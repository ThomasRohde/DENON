using System.Collections.Concurrent;
using Denon.App.Core.Heos;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.PluginSdk;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The plugin host with fake plugins: a slow plugin does not hold up the others, events keep their order, volume
/// changes reach plugins as one VolumeChanged (#45), a plugin that did not start is not run, a toggle at the same
/// moment starts a plugin once, and all plugins stop at the same time, once (#45). Plus the LAN rule for
/// self-signed certificates (#64).
/// </summary>
public sealed class PluginHostLifecycleTests : IDisposable
{
    private static readonly ReceiverPlayerInfo Player = new("Test AVR", 1, "Test AVR", "1", "127.0.0.1", "wired", 1, "S");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-plugin-host-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];

    public PluginHostLifecycleTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { }
        }

        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task<(PluginHostService Host, ReceiverStateStore State)> CreateHostAsync(params IDenonPlugin[] plugins)
    {
        var log = new AppLogStore(Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".json"));
        await store.InitializeAsync();
        var telemetry = new EventTelemetryLog(store, log, new RoomEventBus(log), Path.Combine(_tempDir, "events-" + Guid.NewGuid().ToString("N")));
        _disposables.Add(telemetry);
        var state = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(), store, state, telemetry, log);
        var host = new PluginHostService(plugins, store, state, controller, telemetry, log) { VolumeEventDelay = TimeSpan.FromMilliseconds(100) };
        _disposables.Add(host);
        state.UpdateStatus(Status(power: false, input: "TV", volume: 30));
        await host.InitializeAndStartAsync();
        return (host, state);
    }

    private static ReceiverStatus Status(bool power, string input, int volume, bool muted = false) =>
        new(Player, "stop", volume, muted, new NowPlayingMedia("", "", "", "", "", "", "", null, null), DateTimeOffset.Now,
            IsPoweredOn: power, IsMainZoneOn: power, InputSource: input, SoundMode: "STEREO");

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_slow_plugin_does_not_hold_up_the_others()
    {
        var slow = new FakePlugin("slow") { EventDelay = TimeSpan.FromSeconds(3) };
        var fast = new FakePlugin("fast");
        var (_, state) = await CreateHostAsync(slow, fast);

        state.UpdateStatus(Status(power: true, input: "TV", volume: 30)); // Like the TV wake: the slow plugin takes 3 s.

        await WaitUntilAsync(() => fast.Events.Any(e => e == PluginReceiverEventType.PowerChanged), 1000);
        Assert.Contains(PluginReceiverEventType.PowerChanged, fast.Events);
        Assert.Empty(slow.Events); // Still busy with its first event.
    }

    [Fact]
    public async Task A_plugin_gets_its_events_in_order()
    {
        var plugin = new FakePlugin("ordered") { EventDelay = TimeSpan.FromMilliseconds(30) };
        var (_, state) = await CreateHostAsync(plugin);

        state.UpdateStatus(Status(power: true, input: "TV", volume: 30));
        state.UpdateStatus(Status(power: true, input: "GAME", volume: 30));
        state.UpdateStatus(Status(power: false, input: "GAME", volume: 30));

        await WaitUntilAsync(() => plugin.Events.Count >= 3);
        Assert.Equal(
            [PluginReceiverEventType.PowerChanged, PluginReceiverEventType.InputChanged, PluginReceiverEventType.PowerChanged],
            plugin.Events.Take(3).ToArray());
    }

    [Fact]
    public async Task Volume_steps_reach_plugins_as_one_volume_changed_event_with_the_last_level()
    {
        var plugin = new FakePlugin("volume");
        var (_, state) = await CreateHostAsync(plugin);

        foreach (var level in new[] { 31, 32, 33, 34, 35 })
        {
            state.UpdateStatus(Status(power: false, input: "TV", volume: level));
            await Task.Delay(10);
        }

        await WaitUntilAsync(() => plugin.Events.Contains(PluginReceiverEventType.VolumeChanged));
        await Task.Delay(250); // No second one comes.
        Assert.Single(plugin.Events, e => e == PluginReceiverEventType.VolumeChanged);
        Assert.Equal(35, plugin.LastSnapshot?.Volume);
    }

    [Fact]
    public async Task A_plugin_that_did_not_start_is_not_run()
    {
        var broken = new FakePlugin("broken") { FailStart = true };
        var (host, _) = await CreateHostAsync(broken);

        var result = await host.ExecuteActionAsync("broken", "fake.do");

        Assert.False(result.Success);
        Assert.Contains("not running", result.Message);
        Assert.Equal(0, broken.ActionsRun);
    }

    [Fact]
    public async Task Two_toggles_at_once_start_the_plugin_once()
    {
        var plugin = new FakePlugin("toggle") { StartDelay = TimeSpan.FromMilliseconds(200) };
        var (host, _) = await CreateHostAsync(plugin);
        await host.SetPluginEnabledAsync("toggle", false);
        var startsBefore = plugin.Starts;

        await Task.WhenAll(host.SetPluginEnabledAsync("toggle", true), host.SetPluginEnabledAsync("toggle", true));

        Assert.Equal(startsBefore + 1, plugin.Starts);
    }

    [Fact]
    public async Task All_plugins_stop_at_the_same_time_and_only_once()
    {
        var slow = new FakePlugin("slow-stop") { StopDelay = TimeSpan.FromSeconds(2) };
        var fast = new FakePlugin("fast-stop");
        var (host, _) = await CreateHostAsync(slow, fast);

        var stopping = host.StopAllAsync();
        await WaitUntilAsync(() => fast.Stops > 0, 1000);
        Assert.Equal(1, fast.Stops); // Not after the slow one.

        Assert.Same(stopping, host.StopAllAsync());
        await host.DisposeAsync();
        Assert.Equal(1, slow.Stops);
        Assert.Equal(1, fast.Stops);
    }

    [Theory]
    [InlineData("192.168.1.16", true)]
    [InlineData("10.0.0.2", true)]
    [InlineData("169.254.10.1", true)]
    [InlineData("localhost", true)]
    [InlineData("hue-bridge.local", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("fd00::5", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("example.com", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("", false)]
    public void Only_lan_hosts_may_use_a_self_signed_certificate(string host, bool allowed) =>
        Assert.Equal(allowed, PluginHostService.IsLanHost(host));

    [Theory]
    [InlineData("denon://room/state", true)]
    [InlineData("denon://memory/habits", true)]
    [InlineData("denon://plugins/philips-hue", true)]
    [InlineData("denon://logs/events/plugin:chromecast", true)]
    [InlineData("denon://anything/else", false)]
    [InlineData("file:///c:/secret.txt", false)]
    [InlineData("denon://plugins/a/b", false)]
    public void Mcp_subscriptions_take_only_known_resources(string uri, bool known) =>
        Assert.Equal(known, Denon.App.Core.Mcp.DenonMcpServer.IsKnownResourceUri(uri));

    private sealed class FakePlugin(string id) : IDenonPlugin
    {
        public PluginManifest Manifest { get; } = new(id, id, "1.0", "test", "", "Test", "#000000", "*", PluginCapabilities.ReceiverEvents | PluginCapabilities.Actions);

        public TimeSpan EventDelay { get; init; }

        public TimeSpan StartDelay { get; init; }

        public TimeSpan StopDelay { get; init; }

        public bool FailStart { get; init; }

        public ConcurrentQueue<PluginReceiverEventType> EventQueue { get; } = new();

        public IReadOnlyCollection<PluginReceiverEventType> Events => EventQueue;

        public PluginReceiverSnapshot? LastSnapshot { get; private set; }

        public int Starts;
        public int Stops;
        public int ActionsRun;

        public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Starts);
            await Task.Delay(StartDelay, cancellationToken);
            if (FailStart)
            {
                throw new InvalidOperationException("start failed");
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(StopDelay, CancellationToken.None);
            Interlocked.Increment(ref Stops);
        }

        public IReadOnlyList<PluginActionDescriptor> GetActions() => [new("fake.do", "Do", "Does it.", "Test")];

        public Task<PluginActionResult> ExecuteActionAsync(string actionId, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ActionsRun);
            return Task.FromResult(PluginActionResult.Ok("done"));
        }

        public async Task OnReceiverEventAsync(PluginReceiverEvent receiverEvent, CancellationToken cancellationToken = default)
        {
            await Task.Delay(EventDelay, cancellationToken);
            LastSnapshot = receiverEvent.Snapshot;
            EventQueue.Enqueue(receiverEvent.EventType);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
