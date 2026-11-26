using System.Text.Json;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>Volume memory: learning a level per source, starting from the logs, and setting it when the source changes.</summary>
public sealed class VolumeMemoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-volmem-" + Guid.NewGuid().ToString("N")[..8]);

    public VolumeMemoryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly ReceiverPlayerInfo Player = new("Denon", 1, "Denon AVR-X2400H", "1.0", "192.168.1.3", "wired", 1, "SER");
    private static readonly NowPlayingMedia Media = new("station", "", "", "", "", "", "", null, null);

    private static ReceiverStatus Status(string input, int volume, bool on = true, bool muted = false) =>
        new(Player, "play", volume, muted, Media, DateTimeOffset.Now, IsPoweredOn: on, IsMainZoneOn: on, InputSource: input, VolumeExact: volume);

    // ---- learning ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_level_follows_the_listening_volume_by_time()
    {
        var store = new VolumeMemoryStore(new AppLogStore(Path.Combine(_tempDir, "a.log")), Path.Combine(_tempDir, "vm.json"));
        var dr1 = ListeningSource.Create("tv_channel", "DR1");

        store.Learn(dr1, 42, 60, T0);
        Assert.Equal(42, store.Find(dr1.Key)!.Level); // a new source starts where it is heard

        // 15 minutes at 32 moves it about 63 % of the way (the time constant).
        store.Learn(dr1, 32, 15 * 60, T0.AddMinutes(15));
        Assert.InRange(store.Find(dr1.Key)!.Level, 35.5, 36.5);

        // A moment at 60 hardly moves it.
        store.Learn(dr1, 60, 10, T0.AddMinutes(16));
        Assert.InRange(store.Find(dr1.Key)!.Level, 35.5, 37);
    }

    [Fact]
    public void Levels_start_from_the_logged_sessions_and_survive_a_restart()
    {
        var path = Path.Combine(_tempDir, "vm.json");
        var log = new AppLogStore(Path.Combine(_tempDir, "a.log"));
        var store = new VolumeMemoryStore(log, path);
        store.Load();
        Assert.True(store.IsNew);

        JsonElement Ended(string kind, string title, int seconds, double volume, object? extra = null)
        {
            var data = new Dictionary<string, object?> { ["kind"] = kind, ["title"] = title, ["durationSec"] = seconds, ["exclusiveSec"] = seconds, ["avgVolume"] = volume };
            if (extra is (string k, string v))
            {
                data[k] = v;
            }

            return JsonSerializer.SerializeToElement(new { @event = "session.ended", data });
        }

        var added = store.Seed(
        [
            Ended("tv_channel", "Live TV (Ch 1 • DR1)", 3600, 42, ("channelName", "DR1")),
            Ended("tv_channel", "Nyheder", 1800, 45, ("channelName", "DR1")),
            Ended("cast", "Netflix", 7200, 56, ("app", "Netflix")),
            Ended("cast", "Short", 60, 30, ("app", "YouTube")), // too short to learn from
        ], TimeSpan.FromMinutes(10), T0);
        store.SaveIfChanged();

        Assert.Equal(2, added);
        Assert.Equal(43, store.Find("tv_channel:dr1")!.Level); // (42 × 60 + 45 × 30) / 90
        Assert.Equal(56, store.Find("cast:netflix")!.Level);
        Assert.Null(store.Find("cast:youtube"));

        var again = new VolumeMemoryStore(log, path);
        again.Load();
        Assert.False(again.IsNew);
        Assert.Equal(56, again.Find("cast:netflix")!.Level);
    }

    [Fact]
    public void A_broken_file_is_kept_aside_and_learning_starts_again()
    {
        var path = Path.Combine(_tempDir, "vm.json");
        File.WriteAllText(path, "{ not json");
        var store = new VolumeMemoryStore(new AppLogStore(Path.Combine(_tempDir, "a.log")), path);
        store.Load();

        Assert.True(store.IsNew); // so the service seeds from the logs
        Assert.True(File.Exists(path + ".bad"));
        Assert.Empty(store.Snapshot());
    }

    // ---- what counts as a source ---------------------------------------------------------------------------------

    [Fact]
    public void A_source_is_the_input_when_no_plugin_says_more()
    {
        Assert.Null(ListeningSource.From(Status("MPLAY", 40, on: false), null));
        var source = ListeningSource.From(Status("MPLAY", 40), null)!;
        Assert.Equal("input", source.Kind);
        Assert.Equal("Media Player", source.Label);
        Assert.Equal("input:media player", source.Key);
    }

    // ---- when to set it ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, 55, 55, false, "external", 42, null)]                        // set to 42
    [InlineData(false, false, 55, 55, false, "external", 0, "off")]
    [InlineData(true, false, 55, 55, true, "external", 0, "muted")]
    [InlineData(true, false, 55, 55, false, "smart_scene", 0, "the smart scene set the source and its volume")]
    [InlineData(true, false, 50, 55, false, "external", 0, "the volume was changed after the switch")]
    [InlineData(true, false, 44, 44, false, "external", 0, "already close")]               // 2 steps: left alone
    [InlineData(true, true, 30, 30, false, "external", 0, "only turning down")]            // 42 would be louder
    public void Decide_sets_the_level_only_when_it_helps(
        bool enabled, bool onlyLower, double volume, double atSwitch, bool muted, string origin, int expectTarget, string? expectWhy)
    {
        var settings = new VolumeMemorySettings { Enabled = enabled, OnlyLower = onlyLower, MinDifference = 3 };
        var learned = new LearnedVolume { Key = "tv_channel:dr1", Label = "DR1", Level = 42.4 };

        var why = VolumeMemoryService.Decide(settings, learned, volume, atSwitch, muted, origin, 65, out var target);

        Assert.Equal(expectWhy, why);
        if (why is null)
        {
            Assert.Equal(expectTarget, target);
        }
    }

    [Fact]
    public void Decide_never_goes_above_the_safe_volume()
    {
        var learned = new LearnedVolume { Key = "cast:netflix", Label = "Netflix", Level = 80 };
        Assert.Null(VolumeMemoryService.Decide(new VolumeMemorySettings(), learned, 40, 40, false, "external", 65, out var target));
        Assert.Equal(65, target);
    }

    // ---- the service -------------------------------------------------------------------------------------------

    private sealed record Rig(VolumeMemoryService Service, VolumeMemoryStore Store, List<(int Level, bool Log)> Set, List<RoomEvent> Seen, ReceiverStateStore State, IDisposable Cleanup)
    {
        /// <summary>Runs during a fade step, after the volume is set (to act as the user would).</summary>
        public Action<int>? OnSet { get; set; }
    }

    private async Task<Rig> CreateRigAsync()
    {
        var log = new AppLogStore(Path.Combine(_tempDir, "app.log"));
        var settings = new AppSettingsStore(log, Path.Combine(_tempDir, "settings.json"));
        await settings.InitializeAsync();
        var bus = new RoomEventBus(log);
        var telemetry = new EventTelemetryLog(settings, log, bus, Path.Combine(_tempDir, "events"));
        var state = new ReceiverStateStore();
        var controller = new ReceiverController(new Heos.HeosClient(), new Heos.DenonAvrClient(), settings, state, telemetry, log);
        var plugins = new PluginHostService([], settings, state, controller, telemetry, log);
        var store = new VolumeMemoryStore(log, Path.Combine(_tempDir, "vm.json"));
        var service = new VolumeMemoryService(settings, state, controller, plugins, telemetry, store, log) { FadeStepDelay = TimeSpan.Zero };
        var set = new List<(int, bool)>();
        Rig? rig = null;
        service.SetVolumeOverride = (level, logIt, _) =>
        {
            lock (set) set.Add((level, logIt));
            rig?.OnSet?.Invoke(level);
            return Task.CompletedTask;
        };
        var seen = new List<RoomEvent>();
        var sub = bus.Subscribe(e => { lock (seen) seen.Add(e); });
        return rig = new Rig(service, store, set, seen, state, new Cleanup(() =>
        {
            sub.Dispose();
            service.Dispose();
            telemetry.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            bus.Dispose();
        }));
    }

    private sealed class Cleanup(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private static Task Tick(Rig rig, DateTimeOffset at, ReceiverStatus status) => rig.Service.TickAsync(at, status, null, CancellationToken.None);

    [Fact]
    public async Task Switching_source_sets_its_learned_volume_after_the_dwell()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;
        rig.Store.Learn(ListeningSource.Create("input", "Media Player"), 55, 60, T0);
        rig.Store.Learn(ListeningSource.Create("input", "TV Audio"), 40, 60, T0);

        await Tick(rig, T0, Status("MPLAY", 55, on: false));     // standby at start
        await Tick(rig, T0.AddSeconds(6), Status("MPLAY", 55, on: false));
        await Tick(rig, T0.AddSeconds(10), Status("MPLAY", 55)); // on, Media Player at its level
        await Tick(rig, T0.AddSeconds(16), Status("MPLAY", 55));
        Assert.Empty(rig.Set);                                    // already at its level

        await Tick(rig, T0.AddSeconds(30), Status("TV", 55));     // switch to TV Audio, still at 55
        await Tick(rig, T0.AddSeconds(33), Status("TV", 55));     // inside the dwell: nothing yet
        Assert.Empty(rig.Set);
        await Tick(rig, T0.AddSeconds(36), Status("TV", 55));     // after 5 s: down to 40 at once
        Assert.Equal([(40, true)], rig.Set);
        Assert.Equal("TV Audio", rig.Service.CurrentSource?.Label);

        rig.Set.Clear();
        await Tick(rig, T0.AddSeconds(60), Status("MPLAY", 40)); // back to Media Player at 40
        await Tick(rig, T0.AddSeconds(66), Status("MPLAY", 40));
        Assert.Equal([(42, false), (44, false), (46, false), (48, false), (50, false), (52, false), (54, false), (55, true)], rig.Set); // up in a fade
    }

    [Fact]
    public async Task A_fade_stops_when_the_user_turns_the_volume()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;
        rig.Store.Learn(ListeningSource.Create("input", "Media Player"), 55, 60, T0);

        await Tick(rig, T0, Status("MPLAY", 40, on: false));
        await Tick(rig, T0.AddSeconds(6), Status("MPLAY", 40, on: false));
        await Tick(rig, T0.AddSeconds(10), Status("MPLAY", 40));

        // The receiver follows each step, until the user turns it down to 30 at 44.
        rig.OnSet = level => rig.State.UpdateStatus(Status("MPLAY", level >= 44 ? 30 : level));
        await Tick(rig, T0.AddSeconds(16), Status("MPLAY", 40));

        Assert.Equal([(42, false), (44, false)], rig.Set); // stopped: nothing after the user's change
    }

    [Fact]
    public async Task Flipping_through_sources_or_the_app_starting_sets_nothing()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;
        rig.Store.Learn(ListeningSource.Create("input", "TV Audio"), 40, 60, T0);
        rig.Store.Learn(ListeningSource.Create("input", "Media Player"), 55, 60, T0);

        // The app starts while TV Audio plays at 50: that is the user's volume, not a switch.
        await Tick(rig, T0, Status("TV", 50));
        await Tick(rig, T0.AddSeconds(6), Status("TV", 50));
        Assert.Empty(rig.Set);

        // Media Player for 2 s, then back: no source held for 5 s.
        await Tick(rig, T0.AddSeconds(20), Status("MPLAY", 50));
        await Tick(rig, T0.AddSeconds(22), Status("TV", 50));
        await Tick(rig, T0.AddSeconds(30), Status("TV", 50));
        Assert.Empty(rig.Set);
    }

    [Fact]
    public async Task A_volume_change_right_after_the_switch_wins()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;
        rig.Store.Learn(ListeningSource.Create("input", "TV Audio"), 40, 60, T0);

        await Tick(rig, T0, Status("MPLAY", 55, on: false));
        await Tick(rig, T0.AddSeconds(6), Status("MPLAY", 55, on: false));
        await Tick(rig, T0.AddSeconds(10), Status("TV", 55));
        await Tick(rig, T0.AddSeconds(12), Status("TV", 48)); // the user turned it down already
        await Tick(rig, T0.AddSeconds(16), Status("TV", 48));
        Assert.Empty(rig.Set);
    }

    [Fact]
    public async Task A_source_change_is_published_for_automations()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;

        await Tick(rig, T0, Status("MPLAY", 40, on: false));
        await Tick(rig, T0.AddSeconds(6), Status("MPLAY", 40, on: false));
        await Tick(rig, T0.AddSeconds(10), Status("TV", 40));
        await Tick(rig, T0.AddSeconds(16), Status("TV", 40));

        var until = DateTime.UtcNow.AddSeconds(5);
        RoomEvent? changed = null;
        while (changed is null && DateTime.UtcNow < until)
        {
            lock (rig.Seen)
            {
                changed = rig.Seen.FirstOrDefault(static e => e.Event == VolumeMemoryService.SourceChangedEvent);
            }

            await Task.Delay(20);
        }

        Assert.NotNull(changed);
        Assert.Equal("TV Audio", changed!.GetText("source"));
        Assert.Equal("input", changed.GetText("kind"));

        var trigger = new AutomationNode { Id = "t", Type = AutomationNodeTypes.TriggerSource, Config = new() { ["source"] = "tv audio" } };
        Assert.True(AutomationTriggers.Matches(trigger, changed));
        trigger.Config["source"] = "DR1";
        Assert.False(AutomationTriggers.Matches(trigger, changed));
    }

    [Fact]
    public async Task Listening_teaches_the_level()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;

        await Tick(rig, T0, Status("TV", 45));
        for (var s = 5; s <= 20 * 60; s += 5)
        {
            await Tick(rig, T0.AddSeconds(s), Status("TV", 45));
        }

        var learned = rig.Store.Find("input:tv audio");
        Assert.NotNull(learned);
        Assert.Equal(45, learned!.Level, 1);
        Assert.InRange(learned.Seconds, 19 * 60, 20 * 60); // the first 20 s after the source started are not learned
    }
}
