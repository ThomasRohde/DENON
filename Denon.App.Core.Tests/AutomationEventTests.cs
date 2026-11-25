using Denon.App.Core.Heos;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>The live room event bus, the RoomEvent and Webhook triggers, and POST /api/hooks/{name}.</summary>
public sealed class AutomationEventTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-automation-" + Guid.NewGuid().ToString("N")[..8]);

    public AutomationEventTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static RoomEvent Event(string name, string origin = "external", string stream = "avr", string device = "avr", params (string Key, object? Value)[] data) =>
        new(DateTimeOffset.Now, stream, device, name, origin, data.ToDictionary(static d => d.Key, static d => d.Value));

    private static async Task<RoomEvent> WaitForAsync(TaskCompletionSource<RoomEvent> tcs)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(done == tcs.Task, "The event did not reach the bus within 5 s.");
        return await tcs.Task;
    }

    [Fact]
    public async Task Record_PublishesOnTheBus_EvenWhenEventLoggingIsOff()
    {
        var log = new AppLogStore(Path.Combine(_tempDir, "app.log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, "settings.json"));
        await store.InitializeAsync();
        await store.UpdateAsync(s => s.Telemetry.Enabled = false);

        using var bus = new RoomEventBus(log);
        var eventsDir = Path.Combine(_tempDir, "events");
        await using var telemetry = new EventTelemetryLog(store, log, bus, eventsDir);

        var seen = new TaskCompletionSource<RoomEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = bus.Subscribe(e => { if (e.Event == "power.changed") seen.TrySetResult(e); });

        telemetry.Record(EventTelemetryLog.StreamAvr, "avr", "power.changed", "external", new Dictionary<string, object?> { ["to"] = "on" });

        var received = await WaitForAsync(seen);
        Assert.Equal("on", received.GetText("to"));
        Assert.Equal("avr", received.Stream);
        Assert.Contains(bus.GetRecent(), e => e.Event == "power.changed");

        await Task.Delay(300);
        Assert.Empty(Directory.EnumerateFiles(eventsDir, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public void Bus_KeepsDispatchingWhenAHandlerThrows()
    {
        var log = new AppLogStore(Path.Combine(_tempDir, "bus.log"));
        using var bus = new RoomEventBus(log);
        var count = 0;
        using var bad = bus.Subscribe(_ => throw new InvalidOperationException("boom"));
        using var good = bus.Subscribe(_ => Interlocked.Increment(ref count));

        bus.Publish(Event("a"));
        bus.Publish(Event("b"));

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref count) == 2, TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("arrive-home", "arrive-home")]
    [InlineData("  Sleep_Focus ", "sleep_focus")]
    [InlineData("", null)]
    [InlineData("-starts-with-dash", null)]
    [InlineData("has space", null)]
    [InlineData("a/b", null)]
    public void HookNames_AreNormalized(string raw, string? expected) =>
        Assert.Equal(expected, AutomationTriggers.NormalizeHookName(raw));

    [Fact]
    public void RoomEventRule_MatchesNameAndFilter_AndIgnoresAutomationOrigin()
    {
        var rule = new AutomationRule { TriggerType = AutomationTriggerType.RoomEvent, EventName = "power.changed", DataFilter = "stream=avr, to=on" };

        Assert.True(AutomationTriggers.Matches(rule, Event("power.changed", data: ("to", "on"))));
        Assert.True(AutomationTriggers.Matches(rule, Event("POWER.CHANGED", data: ("to", "ON"))));
        Assert.False(AutomationTriggers.Matches(rule, Event("power.changed", data: ("to", "standby"))));
        Assert.False(AutomationTriggers.Matches(rule, Event("power.changed", stream: "tv", device: "lg-tv", data: ("to", "on"))));
        Assert.False(AutomationTriggers.Matches(rule, Event("power.changed", origin: "automation", data: ("to", "on"))));
        Assert.False(AutomationTriggers.Matches(rule, Event("input.changed", data: ("to", "on"))));
    }

    [Fact]
    public void RoomEventRule_SupportsWildcardsNegationAndBooleans()
    {
        var cast = new AutomationRule { TriggerType = AutomationTriggerType.RoomEvent, EventName = "cast.*", DataFilter = "origin!=desktop_ui" };
        Assert.True(AutomationTriggers.Matches(cast, Event("cast.app_started", stream: "plugin:chromecast", device: "chromecast")));
        Assert.False(AutomationTriggers.Matches(cast, Event("cast.app_started", origin: "desktop_ui")));
        Assert.False(AutomationTriggers.Matches(cast, Event("appletv.woke")));

        var muted = new AutomationRule { TriggerType = AutomationTriggerType.RoomEvent, EventName = "mute.changed", DataFilter = "muted=true" };
        Assert.True(AutomationTriggers.Matches(muted, Event("mute.changed", data: ("muted", true))));
        Assert.False(AutomationTriggers.Matches(muted, Event("mute.changed", data: ("muted", false))));
        Assert.False(AutomationTriggers.Matches(muted, Event("mute.changed")));
    }

    [Fact]
    public void WebhookRule_MatchesOnlyItsHookAndFilter()
    {
        var rule = new AutomationRule { TriggerType = AutomationTriggerType.Webhook, WebhookName = "Focus", DataFilter = "focus=Sleep" };
        RoomEvent Hook(string name, string focus) =>
            Event(AutomationTriggers.WebhookEventName, origin: "webhook", stream: "control", device: "webhook", ("hook", name), ("focus", focus));

        Assert.True(AutomationTriggers.Matches(rule, Hook("focus", "sleep")));
        Assert.False(AutomationTriggers.Matches(rule, Hook("focus", "Work")));
        Assert.False(AutomationTriggers.Matches(rule, Hook("other", "Sleep")));
        Assert.False(AutomationTriggers.Matches(rule, Event("focus", "webhook", "control", "webhook", ("hook", "focus"), ("focus", "Sleep"))));
    }

    private sealed record Rig(AppSettingsStore Store, RoomEventBus Bus, RemoteWebApiHandler Handler, AutomationEngineService Engine, IDisposable Cleanup);

    private async Task<Rig> CreateRigAsync()
    {
        var log = new AppLogStore(Path.Combine(_tempDir, "rig.log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, "rig-settings.json"));
        await store.InitializeAsync();

        var bus = new RoomEventBus(log);
        var telemetry = new EventTelemetryLog(store, log, bus, Path.Combine(_tempDir, "rig-events"));
        var stateStore = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(), store, stateStore, telemetry, log);
        var ytMedia = new YouTubeMediaService(store, log);
        var relay = new LocalAudioRelayServer(ytMedia, store, log);
        var ytQueue = new YouTubeQueueService(relay, controller, stateStore, log);
        var jukebox = new LocalMediaJukeboxService(relay, controller, stateStore, log);
        var plugins = new PluginHostService([], store, stateStore, controller, telemetry, log);
        var handler = new RemoteWebApiHandler(controller, stateStore, store, ytMedia, ytQueue, jukebox, plugins, telemetry, log);
        var engine = new AutomationEngineService(store, controller, plugins, telemetry, bus, log);

        var cleanup = new Cleanup(() =>
        {
            engine.Dispose();
            jukebox.Dispose();
            ytQueue.Dispose();
            relay.Dispose();
            telemetry.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            bus.Dispose();
        });
        return new Rig(store, bus, handler, engine, cleanup);
    }

    private sealed class Cleanup(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    [Fact]
    public async Task WebhookEndpoint_PublishesTheEvent_AndNamesTheListeningRules()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;
        await rig.Store.UpdateAsync(s => s.Automations =
        [
            new AutomationRule { Name = "Home", IsEnabled = true, TriggerType = AutomationTriggerType.Webhook, WebhookName = "arrive-home" },
            new AutomationRule { Name = "Paused", IsEnabled = false, TriggerType = AutomationTriggerType.Webhook, WebhookName = "arrive-home" },
        ]);
        var owner = rig.Store.Current.Remote.OwnerToken;

        var seen = new TaskCompletionSource<RoomEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = rig.Bus.Subscribe(e => { if (e.Event == AutomationTriggers.WebhookEventName) seen.TrySetResult(e); });

        var ok = await rig.Handler.HandleAsync("POST", "/api/hooks/Arrive-Home", null, "{\"focus\":\"Sleep\",\"battery\":42,\"hook\":\"spoof\"}", owner, CancellationToken.None);
        Assert.Equal(200, ok.StatusCode);
        Assert.Contains("\"hook\":\"arrive-home\"", ok.Body);
        Assert.Contains("\"rules\":[\"Home\"]", ok.Body);

        var e = await WaitForAsync(seen);
        Assert.Equal("arrive-home", e.GetText("hook"));
        Assert.Equal("Sleep", e.GetText("focus"));
        Assert.Equal("42", e.GetText("battery"));
        Assert.Equal(AutomationTriggers.WebhookOrigin, e.Origin);

        var none = await rig.Handler.HandleAsync("POST", "/api/hooks/nobody", null, "plain text", owner, CancellationToken.None);
        Assert.Equal(200, none.StatusCode);
        Assert.Contains("no enabled automation rule", none.Body);

        var bad = await rig.Handler.HandleAsync("POST", "/api/hooks/has%20space", null, "", owner, CancellationToken.None);
        Assert.Equal(400, bad.StatusCode);

        var guest = await rig.Handler.HandleAsync("POST", "/api/hooks/arrive-home", null, "", rig.Store.Current.Remote.GuestToken, CancellationToken.None);
        Assert.Equal(403, guest.StatusCode);

        var anonymous = await rig.Handler.HandleAsync("POST", "/api/hooks/arrive-home", null, "", null, CancellationToken.None);
        Assert.Equal(401, anonymous.StatusCode);
    }

    [Fact]
    public async Task Engine_RunsMatchingRules_WithCooldownAndLoopGuard()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;
        await rig.Store.UpdateAsync(s => s.Automations =
        [
            new AutomationRule { Id = "hook", Name = "Hook", IsEnabled = true, TriggerType = AutomationTriggerType.Webhook, WebhookName = "tap", CooldownSeconds = 0 },
            new AutomationRule { Id = "on", Name = "On", IsEnabled = true, TriggerType = AutomationTriggerType.RoomEvent, EventName = "power.changed", DataFilter = "to=on", CooldownSeconds = 0 },
            new AutomationRule { Id = "off", Name = "Off", IsEnabled = false, TriggerType = AutomationTriggerType.RoomEvent, EventName = "power.changed" },
        ]);

        var t0 = DateTimeOffset.Now;
        var tap = Event(AutomationTriggers.WebhookEventName, origin: "webhook", stream: "control", device: "webhook", ("hook", "tap"));
        Assert.Equal(new[] { "Hook" }, await rig.Engine.HandleRoomEventAsync(tap, t0));
        Assert.Equal(new[] { "Hook" }, await rig.Engine.HandleRoomEventAsync(tap, t0.AddMilliseconds(100))); // webhooks may repeat at once

        var powerOn = Event("power.changed", data: ("to", "on"));
        Assert.Equal(new[] { "On" }, await rig.Engine.HandleRoomEventAsync(powerOn, t0));
        Assert.Empty(await rig.Engine.HandleRoomEventAsync(powerOn, t0.AddSeconds(5)));   // room events wait at least 10 s
        Assert.Equal(new[] { "On" }, await rig.Engine.HandleRoomEventAsync(powerOn, t0.AddSeconds(11)));
        Assert.Empty(await rig.Engine.HandleRoomEventAsync(Event("power.changed", origin: "automation", data: ("to", "on")), t0.AddSeconds(30)));
    }

    [Fact]
    public async Task RunningEngine_RunsAWebhookRule_FromTheHttpCallThroughTheBus()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;
        await rig.Store.UpdateAsync(s => s.Automations =
        [
            new AutomationRule { Id = "home", Name = "Home", IsEnabled = true, TriggerType = AutomationTriggerType.Webhook, WebhookName = "arrive-home", DataFilter = "who=owner", CooldownSeconds = 0 },
        ]);

        var ran = new TaskCompletionSource<AutomationActivityEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Engine.ActivityLogged += entry => ran.TrySetResult(entry);
        await rig.Engine.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(200); // the engine subscribes when ExecuteAsync starts
            var reply = await rig.Handler.HandleAsync("POST", "/api/hooks/arrive-home", null, "{\"who\":\"owner\"}", rig.Store.Current.Remote.OwnerToken, CancellationToken.None);
            Assert.Equal(200, reply.StatusCode);

            var done = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(done == ran.Task, "The webhook rule did not run within 5 s.");
            var entry = await ran.Task;
            Assert.Equal("Home", entry.RuleName);
            Assert.Contains("Webhook 'arrive-home'", entry.TriggerDetail);
        }
        finally
        {
            await rig.Engine.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Engine_LoopGuard_PausesEventTriggersAfterTooManyRuns()
    {
        var rig = await CreateRigAsync();
        using var _ = rig.Cleanup;
        // Four rules on one event: each event runs four rules, so five events make 20 runs within a minute.
        await rig.Store.UpdateAsync(s => s.Automations = Enumerable.Range(1, 4)
            .Select(i => new AutomationRule { Id = $"r{i}", Name = $"R{i}", IsEnabled = true, TriggerType = AutomationTriggerType.RoomEvent, EventName = "input.changed", CooldownSeconds = 0 })
            .Append(new AutomationRule { Id = "hook", Name = "Hook", IsEnabled = true, TriggerType = AutomationTriggerType.Webhook, WebhookName = "tap", CooldownSeconds = 0 })
            .ToList());

        var input = Event("input.changed", data: ("to", "TV"));
        var t0 = DateTimeOffset.Now;
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(4, (await rig.Engine.HandleRoomEventAsync(input, t0.AddSeconds(i * 10.5))).Count);
        }

        Assert.Equal(20, AutomationEngineService.LoopRunLimit);
        var t5 = t0.AddSeconds(52.5);
        Assert.Empty(await rig.Engine.HandleRoomEventAsync(input, t5));                   // 21st run: the guard trips
        Assert.Empty(await rig.Engine.HandleRoomEventAsync(input, t5.AddSeconds(30)));    // still paused

        var tap = Event(AutomationTriggers.WebhookEventName, origin: "webhook", stream: "control", device: "webhook", ("hook", "tap"));
        Assert.Equal(new[] { "Hook" }, await rig.Engine.HandleRoomEventAsync(tap, t5.AddSeconds(31))); // webhooks are not paused

        Assert.Equal(4, (await rig.Engine.HandleRoomEventAsync(input, t5.AddSeconds(61))).Count); // pause over
    }
}
