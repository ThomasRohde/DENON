using System.Text.Json;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The event monitor writes what really changed: a zone 2 or sound mode flip that goes back at once, and a moment
/// while the app stops, are not changes. Sessions a crash left open are closed at the next start.
/// </summary>
public sealed class EventNoiseTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-noise-" + Guid.NewGuid().ToString("N")[..8]);

    public EventNoiseTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed record Rig(EventTelemetryMonitorService Monitor, ReceiverStateStore State, EventTelemetryLog Telemetry, List<RoomEvent> Seen, IDisposable Cleanup);

    private async Task<Rig> CreateRigAsync()
    {
        var log = new AppLogStore(Path.Combine(_tempDir, "app.log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, "settings.json"));
        await store.InitializeAsync();
        var bus = new RoomEventBus(log);
        var telemetry = new EventTelemetryLog(store, log, bus, Path.Combine(_tempDir, "events"));
        var state = new ReceiverStateStore();
        var controller = new ReceiverController(new Heos.HeosClient(), new Heos.DenonAvrClient(), store, state, telemetry, log);
        var ytMedia = new YouTubeMediaService(store, log);
        var relay = new LocalAudioRelayServer(ytMedia, store, log);
        var ytQueue = new YouTubeQueueService(relay, controller, state, log);
        var plugins = new PluginHostService([], store, state, controller, telemetry, log);
        var monitor = new EventTelemetryMonitorService(telemetry, store, state, ytQueue, plugins, log);
        var seen = new List<RoomEvent>();
        var sub = bus.Subscribe(e => { lock (seen) seen.Add(e); });
        return new Rig(monitor, state, telemetry, seen, new Cleanup(() =>
        {
            sub.Dispose();
            monitor.Dispose();
            ytQueue.Dispose();
            relay.Dispose();
            telemetry.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            bus.Dispose();
        }));
    }

    private sealed class Cleanup(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private static readonly ReceiverPlayerInfo Player = new("Living Room", 1, "Denon AVR-X2400H", "1.0", "192.168.1.3", "wired", 1, "SER");
    private static readonly NowPlayingMedia Media = new("station", "P8 Jazz", "", "", "", "", "", null, null);

    private static void Show(Rig rig, bool zone2 = true, string soundMode = "MCH STEREO", bool muted = false, int volume = 30)
    {
        rig.State.UpdateStatus(new ReceiverStatus(Player, "play", volume, muted, Media, DateTimeOffset.Now,
            IsPoweredOn: true, IsMainZoneOn: true, InputSource: "TV", SoundMode: soundMode, IsZone2On: zone2, VolumeExact: volume));
        rig.Monitor.ProcessReceiverSnapshot(rig.State.Snapshot);
    }

    /// <summary>
    /// The bus delivers on its own task, in order: once a marker written now has arrived, every event before it has too.
    /// </summary>
    private static async Task DeliveredAsync(Rig rig)
    {
        var marker = Guid.NewGuid().ToString("N");
        rig.Telemetry.Record("control", "test", "test.marker", "test", new Dictionary<string, object?> { ["id"] = marker });
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            lock (rig.Seen)
            {
                if (rig.Seen.Any(e => e.Event == "test.marker" && e.GetText("id") == marker))
                {
                    return;
                }
            }

            await Task.Delay(20);
        }

        Assert.Fail("The event bus did not deliver within 10 s.");
    }

    private static List<string> Events(Rig rig)
    {
        lock (rig.Seen)
        {
            return rig.Seen.Select(static e => e.Event).ToList();
        }
    }

    [Fact]
    public async Task A_flip_that_goes_back_at_once_is_not_written()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;

        Show(rig);                                    // the first state
        Show(rig, zone2: false, muted: true);         // the receiver switching inputs
        Show(rig, zone2: true, muted: false);         // …and back
        Show(rig, soundMode: "DOLBY D+");             // an ad in Dolby
        Show(rig, soundMode: "MCH STEREO");           // the programme again
        rig.Monitor.EvaluateHeldChanges(DateTimeOffset.UtcNow.AddMinutes(1));
        await DeliveredAsync(rig);

        Assert.DoesNotContain("zone2.changed", Events(rig));
        Assert.DoesNotContain("mute.changed", Events(rig));
        Assert.DoesNotContain("sound_mode.changed", Events(rig));
    }

    [Fact]
    public async Task A_change_that_holds_is_written_once_from_where_it_started()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;

        Show(rig);
        Show(rig, soundMode: "DOLBY D+");
        Show(rig, soundMode: "DOLBY SURROUND");
        rig.Monitor.EvaluateHeldChanges(DateTimeOffset.UtcNow); // not held long enough yet
        await DeliveredAsync(rig);
        Assert.DoesNotContain("sound_mode.changed", Events(rig));

        rig.Monitor.EvaluateHeldChanges(DateTimeOffset.UtcNow + EventTelemetryMonitorService.SoundModeHold + TimeSpan.FromSeconds(1));
        await DeliveredAsync(rig);

        RoomEvent changed;
        lock (rig.Seen)
        {
            changed = Assert.Single(rig.Seen, e => e.Event == "sound_mode.changed");
        }

        Assert.Equal("MCH STEREO", changed.GetText("from"));
        Assert.Equal("DOLBY SURROUND", changed.GetText("to"));
    }

    [Fact]
    public async Task Sessions_a_crash_left_open_are_closed_at_the_last_time_seen()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;
        var lastSeen = DateTimeOffset.UtcNow.AddHours(-2);
        File.WriteAllText(rig.Telemetry.OpenSessionsPath, JsonSerializer.Serialize(new
        {
            lastSeenUtc = lastSeen,
            sessions = new[]
            {
                new
                {
                    device = "avr",
                    origin = "external",
                    startedUtc = lastSeen.AddHours(-3),
                    data = new Dictionary<string, object?> { ["kind"] = "avr_input", ["title"] = "TV Audio", ["input"] = "TV" },
                },
            },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        rig.Monitor.CloseSessionsLeftOpen();
        await DeliveredAsync(rig);

        RoomEvent ended;
        lock (rig.Seen)
        {
            ended = Assert.Single(rig.Seen, e => e.Event == "session.ended");
        }

        Assert.Equal("app_crashed", ended.GetText("reason"));
        Assert.Equal("TV Audio", ended.GetText("title"));
        Assert.Equal("10800", ended.GetText("durationSec"));
        Assert.Equal(lastSeen, ended.Timestamp);
        Assert.False(File.Exists(rig.Telemetry.OpenSessionsPath));
    }

    [Fact]
    public async Task A_recovered_receiver_input_under_the_tv_is_not_counted_twice()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;
        var lastSeen = DateTimeOffset.UtcNow.AddHours(-1);
        object Open(string device, string kind, string title) => new
        {
            device,
            origin = "external",
            startedUtc = lastSeen.AddHours(-2),
            data = new Dictionary<string, object?> { ["kind"] = kind, ["title"] = title, ["input"] = kind == "avr_input" ? "TV" : null },
        };

        // Saved as the monitor saves them: the receiver first.
        File.WriteAllText(rig.Telemetry.OpenSessionsPath, JsonSerializer.Serialize(new
        {
            lastSeenUtc = lastSeen,
            sessions = new[] { Open("avr", "avr_input", "TV Audio"), Open("lg-tv", "tv_channel", "DR1") },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        rig.Monitor.CloseSessionsLeftOpen();
        await DeliveredAsync(rig);

        List<RoomEvent> ended;
        lock (rig.Seen)
        {
            ended = rig.Seen.Where(static e => e.Event == "session.ended").ToList();
        }

        Assert.Equal(2, ended.Count);
        Assert.Equal("lg-tv", ended[0].Device);
        Assert.Equal("7200", ended[0].GetText("exclusiveSec"));
        Assert.Equal("avr", ended[1].Device);
        Assert.Equal("0", ended[1].GetText("exclusiveSec")); // the TV covered it
    }
}
