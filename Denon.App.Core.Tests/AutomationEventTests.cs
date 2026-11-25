using Denon.App.Core.Heos;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The live room event bus, automation graphs (validator, runner, triggers, engine) and POST /api/hooks/{name}.
/// </summary>
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

    private static RoomEvent Hook(string name, params (string Key, object? Value)[] data) =>
        Event(AutomationTriggers.WebhookEventName, "webhook", "control", "webhook", [("hook", name), .. data]);

    private static AutomationNode Node(string id, string type, params (string Key, string Value)[] config) =>
        new() { Id = id, Type = type, Config = config.ToDictionary(static c => c.Key, static c => c.Value) };

    private static AutomationEdge Edge(string from, string to, string port = AutomationNodeTypes.PortOut) =>
        new() { From = from, Port = port, To = to };

    /// <summary>trigger → scene, enabled.</summary>
    private static AutomationGraph SceneGraph(string id, AutomationNode trigger, string sceneId = "movie-night", int cooldown = 0) =>
        new()
        {
            Id = id, Name = id, Enabled = true, CooldownSeconds = cooldown,
            Nodes = [trigger, Node("a", AutomationNodeTypes.ActionScene, ("sceneId", sceneId))],
            Edges = [Edge(trigger.Id, "a")],
        };

    private static async Task<RoomEvent> WaitForAsync(TaskCompletionSource<RoomEvent> tcs)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(done == tcs.Task, "The event did not reach the bus within 5 s.");
        return await tcs.Task;
    }

    /// <summary>Records what a run asks for; changes nothing.</summary>
    private sealed class FakeEffects : IAutomationEffects
    {
        public List<string> Calls { get; } = [];

        public ReceiverFacts Facts { get; set; } = new(true, "TV", false);

        public string? FailPluginAction { get; set; }

        /// <summary>Runs during a wait (to simulate time passing), e.g. to let another origin take over.</summary>
        public Action? OnDelay { get; set; }

        /// <summary>Read when a scene or plugin action runs; the answers go to <see cref="Probed"/>.</summary>
        public Func<string>? Probe { get; set; }

        public List<string> Probed { get; } = [];

        public ReceiverFacts GetReceiverFacts() => Facts;

        public string DescribeScene(string sceneId) => sceneId;

        public string DescribePluginAction(string pluginId, string actionId) => $"{pluginId}:{actionId}";

        public Task RunSceneAsync(string sceneId, CancellationToken ct)
        {
            Calls.Add($"scene:{sceneId}");
            if (Probe is not null) Probed.Add(Probe());
            return Task.CompletedTask;
        }

        public Task<(bool Success, string Message)> RunPluginActionAsync(string pluginId, string actionId, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
        {
            if (Probe is not null) Probed.Add(Probe());
            Calls.Add($"plugin:{pluginId}:{actionId}" + (parameters is null ? "" : "?" + string.Join("&", parameters.Select(p => $"{p.Key}={p.Value}"))));
            return Task.FromResult(actionId == FailPluginAction ? (false, "bridge offline") : (true, "ok"));
        }

        public Task<string> RunReceiverCommandAsync(string command, string value, CancellationToken ct)
        {
            Calls.Add($"receiver:{command}:{value}");
            return Task.FromResult($"Receiver {command}");
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Calls.Add($"wait:{delay.TotalSeconds}");
            OnDelay?.Invoke();
            return Task.CompletedTask;
        }
    }

    // ---- Event bus -----------------------------------------------------------------------------------------

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

    // ---- Trigger matching ----------------------------------------------------------------------------------

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
    public void EventTrigger_MatchesNameAndFilter_AndIgnoresAutomationOrigin()
    {
        var trigger = Node("t", AutomationNodeTypes.TriggerEvent, ("event", "power.changed"), ("filter", "stream=avr, to=on"));

        Assert.True(AutomationTriggers.Matches(trigger, Event("power.changed", data: ("to", "on"))));
        Assert.True(AutomationTriggers.Matches(trigger, Event("POWER.CHANGED", data: ("to", "ON"))));
        Assert.False(AutomationTriggers.Matches(trigger, Event("power.changed", data: ("to", "standby"))));
        Assert.False(AutomationTriggers.Matches(trigger, Event("power.changed", stream: "tv", device: "lg-tv", data: ("to", "on"))));
        Assert.False(AutomationTriggers.Matches(trigger, Event("power.changed", origin: "automation", data: ("to", "on"))));
        Assert.False(AutomationTriggers.Matches(trigger, Event("input.changed", data: ("to", "on"))));
    }

    [Fact]
    public void EventTrigger_SupportsWildcardsNegationAndBooleans()
    {
        var cast = Node("t", AutomationNodeTypes.TriggerEvent, ("event", "cast.*"), ("filter", "origin!=desktop_ui"));
        Assert.True(AutomationTriggers.Matches(cast, Event("cast.app_started", stream: "plugin:chromecast", device: "chromecast")));
        Assert.False(AutomationTriggers.Matches(cast, Event("cast.app_started", origin: "desktop_ui")));
        Assert.False(AutomationTriggers.Matches(cast, Event("appletv.woke")));

        var muted = Node("t", AutomationNodeTypes.TriggerEvent, ("event", "mute.changed"), ("filter", "muted=true"));
        Assert.True(AutomationTriggers.Matches(muted, Event("mute.changed", data: ("muted", true))));
        Assert.False(AutomationTriggers.Matches(muted, Event("mute.changed", data: ("muted", false))));
        Assert.False(AutomationTriggers.Matches(muted, Event("mute.changed")));
    }

    [Fact]
    public void WebhookTrigger_MatchesOnlyItsHookAndFilter()
    {
        var trigger = Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "Focus"), ("filter", "focus=Sleep"));

        Assert.True(AutomationTriggers.Matches(trigger, Hook("focus", ("focus", "sleep"))));
        Assert.False(AutomationTriggers.Matches(trigger, Hook("focus", ("focus", "Work"))));
        Assert.False(AutomationTriggers.Matches(trigger, Hook("other", ("focus", "Sleep"))));
        Assert.False(AutomationTriggers.Matches(trigger, Event("focus", "webhook", "control", "webhook", ("hook", "focus"), ("focus", "Sleep"))));
    }

    // ---- Validator -----------------------------------------------------------------------------------------

    [Fact]
    public void Validator_AcceptsTheDefaults()
    {
        foreach (var graph in AutomationGraph.CreateDefaults())
        {
            Assert.Empty(AutomationGraphValidator.Validate(graph));
            Assert.False(graph.Enabled);
        }
    }

    [Fact]
    public void Validator_FindsMistakes()
    {
        static IReadOnlyList<AutomationProblem> Check(Action<AutomationGraph> change)
        {
            var graph = SceneGraph("g", Node("t", AutomationNodeTypes.TriggerEvent, ("event", "power.changed")));
            change(graph);
            return AutomationGraphValidator.Validate(graph);
        }

        Assert.Contains(Check(g => g.Nodes.RemoveAt(0)), p => p.Message.Contains("trigger"));
        Assert.Contains(Check(g => g.Edges.Add(Edge("a", "t"))), p => p.Message.Contains("into a trigger"));
        Assert.Contains(Check(g => g.Edges.Add(Edge("t", "a"))), p => p.Message.Contains("twice"));
        Assert.Contains(Check(g => g.Edges.Add(Edge("t", "a", "yes"))), p => p.Message.Contains("no 'yes' output"));
        Assert.Contains(Check(g => g.Edges.Add(Edge("t", "missing"))), p => p.Message.Contains("does not exist"));
        Assert.Contains(Check(g => g.Nodes[1].Config["sceneId"] = ""), p => p.NodeId == "a" && p.Message.Contains("Scene"));
        Assert.Contains(Check(g => g.Nodes.Add(Node("w", AutomationNodeTypes.TriggerWebhook, ("hook", "bad name")))), p => p.NodeId == "w");
        Assert.Contains(Check(g => g.Nodes.Add(Node("v", AutomationNodeTypes.ActionReceiver, ("command", "volume"), ("value", "120")))), p => p.NodeId == "v");
        Assert.Contains(Check(g => g.Nodes.Add(Node("d", AutomationNodeTypes.ActionDelay, ("seconds", "0")))), p => p.NodeId == "d");
        Assert.Contains(Check(g => g.Nodes.Add(Node("x", "action.unknown"))), p => p.NodeId == "x");
        Assert.Contains(Check(g => g.Nodes.Add(Node("a", AutomationNodeTypes.ActionDelay, ("seconds", "5")))), p => p.Message.Contains("own id"));

        // A cycle: a → b → a.
        Assert.Contains(Check(g =>
        {
            g.Nodes.Add(Node("b", AutomationNodeTypes.ActionDelay, ("seconds", "1")));
            g.Edges.Add(Edge("a", "b"));
            g.Edges.Add(Edge("b", "a"));
        }), p => p.Message.Contains("loop"));

        // Hook names are stored in lower case.
        var hookGraph = SceneGraph("h", Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "Arrive-Home")));
        Assert.Empty(AutomationGraphValidator.Validate(hookGraph));
        Assert.Equal("arrive-home", hookGraph.Nodes[0].Get("hook"));
    }

    [Fact]
    public async Task OverlappingUpserts_AllSurvive()
    {
        var log = new AppLogStore(Path.Combine(_tempDir, "upsert.log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, "upsert-settings.json"));
        await store.InitializeAsync();
        await store.UpdateAsync(s => s.AutomationGraphs = []);

        var graphs = Enumerable.Range(1, 10)
            .Select(i => SceneGraph($"g{i}", Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", $"h{i}"))))
            .ToList();
        await Task.WhenAll(graphs.Select(g => Task.Run(() => store.UpdateAsync(s => AutomationGraph.Upsert(s.AutomationGraphs, g)))));

        Assert.Equal(10, store.Current.AutomationGraphs.Count);
        var renamed = graphs[0].Clone();
        renamed.Name = "renamed";
        var replaced = false;
        await store.UpdateAsync(s => replaced = AutomationGraph.Upsert(s.AutomationGraphs, renamed));
        Assert.True(replaced);
        Assert.Equal("renamed", store.Current.AutomationGraphs.Single(g => g.Id == "g1").Name);
    }

    // ---- Runner --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Runner_FollowsConditionPorts_InEdgeOrder()
    {
        var graph = new AutomationGraph
        {
            Id = "g", Name = "Branches",
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "go")),
                Node("c", AutomationNodeTypes.ConditionEvent, ("filter", "who=owner")),
                Node("yes1", AutomationNodeTypes.ActionScene, ("sceneId", "movie-night")),
                Node("yes2", AutomationNodeTypes.ActionPlugin, ("pluginId", "philips-hue"), ("actionId", "hue.all_on"), ("p:roomId", "3"), ("p:empty", " ")),
                Node("no", AutomationNodeTypes.ActionReceiver, ("command", "mute"), ("value", "")),
            ],
            Edges = [Edge("t", "c"), Edge("c", "yes1", "yes"), Edge("c", "yes2", "yes"), Edge("c", "no", "no")],
        };

        var owner = new FakeEffects();
        var run = await AutomationGraphRunner.RunAsync(graph, graph.Nodes[0], Hook("go", ("who", "owner")), "Webhook 'go'", owner, false, DateTimeOffset.Now, default);
        Assert.True(run.Success);
        Assert.Equal(new[] { "scene:movie-night", "plugin:philips-hue:hue.all_on?roomId=3" }, owner.Calls);
        Assert.Contains(run.Steps, s => s.NodeId == "c" && s.Outcome == "yes");
        Assert.Contains("movie-night", run.Summary);

        var guest = new FakeEffects();
        var other = await AutomationGraphRunner.RunAsync(graph, graph.Nodes[0], Hook("go", ("who", "guest")), "Webhook 'go'", guest, false, DateTimeOffset.Now, default);
        Assert.Equal(new[] { "receiver:mute:" }, guest.Calls);
        Assert.Contains(other.Steps, s => s.NodeId == "c" && s.Outcome == "no");
    }

    [Fact]
    public async Task Runner_StopsABranchWhenAStepFails_AndDryRunChangesNothing()
    {
        var graph = new AutomationGraph
        {
            Id = "g", Name = "Chain",
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerSchedule, ("time", "22:00")),
                Node("a1", AutomationNodeTypes.ActionPlugin, ("pluginId", "philips-hue"), ("actionId", "hue.broken")),
                Node("a2", AutomationNodeTypes.ActionScene, ("sceneId", "late-night")),
                Node("w", AutomationNodeTypes.ActionDelay, ("seconds", "3")),
            ],
            Edges = [Edge("t", "a1"), Edge("a1", "a2"), Edge("t", "w")],
        };

        var effects = new FakeEffects { FailPluginAction = "hue.broken" };
        var run = await AutomationGraphRunner.RunAsync(graph, graph.Nodes[0], null, "Scheduled 22:00", effects, false, DateTimeOffset.Now, default);
        Assert.False(run.Success);
        Assert.Equal(new[] { "plugin:philips-hue:hue.broken", "wait:3" }, effects.Calls); // a2 never runs; the other branch does
        Assert.Contains(run.Steps, s => s.NodeId == "a1" && s.Outcome == "failed" && s.Message.Contains("bridge offline"));
        Assert.Contains("failed", run.Summary);

        var dry = new FakeEffects();
        var dryRun = await AutomationGraphRunner.RunAsync(graph, graph.Nodes[0], null, "Dry run", dry, true, DateTimeOffset.Now, default);
        Assert.Empty(dry.Calls);
        Assert.All(dryRun.Steps, s => Assert.Equal("would-run", s.Outcome));
    }

    [Fact]
    public async Task Runner_ChecksATimeWindowWhenItIsReached_NotWhenTheRunStarted()
    {
        var graph = new AutomationGraph
        {
            Id = "g", Name = "Late wait",
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "go")),
                Node("w", AutomationNodeTypes.ActionDelay, ("seconds", "120")),
                Node("c", AutomationNodeTypes.ConditionTime, ("from", "17:00"), ("to", "22:00"), ("days", "Daily")),
                Node("a", AutomationNodeTypes.ActionScene, ("sceneId", "movie-night")),
            ],
            Edges = [Edge("t", "w"), Edge("w", "c"), Edge("c", "a", "yes")],
        };

        // The run starts at 21:59; the wait takes 2 minutes, so the window is checked at 22:01.
        var elapsed = TimeSpan.Zero;
        var effects = new FakeEffects { OnDelay = () => elapsed = TimeSpan.FromMinutes(2) };
        var start = new DateTimeOffset(2026, 11, 25, 21, 59, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 25)));
        var run = await AutomationGraphRunner.RunAsync(graph, graph.Nodes[0], Hook("go"), "Webhook 'go'", effects, false, start, default, () => elapsed);

        Assert.Contains(run.Steps, s => s.NodeId == "c" && s.Outcome == "no" && s.Message.Contains("22:01"));
        Assert.Equal(new[] { "wait:120" }, effects.Calls);
    }

    [Fact]
    public void Conditions_TimeWindowAcrossMidnight_AndReceiverState()
    {
        var effects = new FakeEffects();
        var evening = Node("c", AutomationNodeTypes.ConditionTime, ("from", "22:00"), ("to", "02:00"), ("days", "Daily"));
        var e = Event("x");
        DateTimeOffset At(int h, int m) => new(2026, 11, 25, h, m, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 25)));

        Assert.True(AutomationGraphRunner.Evaluate(evening, e, effects, At(23, 30)).Pass);
        Assert.True(AutomationGraphRunner.Evaluate(evening, e, effects, At(1, 59)).Pass);
        Assert.False(AutomationGraphRunner.Evaluate(evening, e, effects, At(2, 0)).Pass);
        Assert.False(AutomationGraphRunner.Evaluate(evening, e, effects, At(12, 0)).Pass);

        var standbyOnTv = Node("r", AutomationNodeTypes.ConditionReceiver, ("power", "standby"), ("input", "TV"), ("muted", "any"));
        effects.Facts = new ReceiverFacts(false, "TV", false);
        Assert.True(AutomationGraphRunner.Evaluate(standbyOnTv, e, effects, At(12, 0)).Pass);
        effects.Facts = new ReceiverFacts(true, "TV", false);
        Assert.False(AutomationGraphRunner.Evaluate(standbyOnTv, e, effects, At(12, 0)).Pass);
        effects.Facts = new ReceiverFacts(null, null, null);
        Assert.False(AutomationGraphRunner.Evaluate(standbyOnTv, e, effects, At(12, 0)).Pass);

        // Unknown mute state passes neither "muted" nor "not muted".
        var notMuted = Node("m", AutomationNodeTypes.ConditionReceiver, ("power", "any"), ("input", ""), ("muted", "no"));
        var muted = Node("m", AutomationNodeTypes.ConditionReceiver, ("power", "any"), ("input", ""), ("muted", "yes"));
        Assert.False(AutomationGraphRunner.Evaluate(notMuted, e, effects, At(12, 0)).Pass);
        Assert.False(AutomationGraphRunner.Evaluate(muted, e, effects, At(12, 0)).Pass);
        effects.Facts = new ReceiverFacts(true, "TV", false);
        Assert.True(AutomationGraphRunner.Evaluate(notMuted, e, effects, At(12, 0)).Pass);
    }

    // ---- Endpoint and engine -------------------------------------------------------------------------------

    private sealed record Rig(AppSettingsStore Store, RoomEventBus Bus, EventTelemetryLog Telemetry, RemoteWebApiHandler Handler, AutomationEngineService Engine, FakeEffects Effects, IDisposable Cleanup);

    private async Task<Rig> CreateRigAsync(params AutomationGraph[] graphs)
    {
        var log = new AppLogStore(Path.Combine(_tempDir, "rig.log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, "rig-settings.json"));
        await store.InitializeAsync();
        await store.UpdateAsync(s => s.AutomationGraphs = [.. graphs]);

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
        var effects = new FakeEffects();
        var engine = new AutomationEngineService(store, controller, stateStore, plugins, telemetry, bus, log) { EffectsOverride = effects };

        var cleanup = new Cleanup(() =>
        {
            engine.Dispose();
            jukebox.Dispose();
            ytQueue.Dispose();
            relay.Dispose();
            telemetry.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            bus.Dispose();
        });
        return new Rig(store, bus, telemetry, handler, engine, effects, cleanup);
    }

    private sealed class Cleanup(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    [Fact]
    public async Task WebhookEndpoint_PublishesTheEvent_AndNamesTheListeningAutomations()
    {
        var home = SceneGraph("home", Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "arrive-home")));
        var paused = SceneGraph("paused", Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "arrive-home")));
        paused.Enabled = false;
        var rig = await CreateRigAsync(home, paused);
        using var _ = rig.Cleanup;
        var owner = rig.Store.Current.Remote.OwnerToken;

        var seen = new TaskCompletionSource<RoomEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = rig.Bus.Subscribe(e => { if (e.Event == AutomationTriggers.WebhookEventName) seen.TrySetResult(e); });

        var ok = await rig.Handler.HandleAsync("POST", "/api/hooks/Arrive-Home", null, "{\"focus\":\"Sleep\",\"battery\":42,\"hook\":\"spoof\"}", owner, CancellationToken.None);
        Assert.Equal(200, ok.StatusCode);
        Assert.Contains("\"hook\":\"arrive-home\"", ok.Body);
        Assert.Contains("\"automations\":[\"home\"]", ok.Body);

        var e = await WaitForAsync(seen);
        Assert.Equal("arrive-home", e.GetText("hook"));
        Assert.Equal("Sleep", e.GetText("focus"));
        Assert.Equal("42", e.GetText("battery"));
        Assert.Equal(AutomationTriggers.WebhookOrigin, e.Origin);

        var none = await rig.Handler.HandleAsync("POST", "/api/hooks/nobody", null, "plain text", owner, CancellationToken.None);
        Assert.Equal(200, none.StatusCode);
        Assert.Contains("no enabled automation", none.Body);

        Assert.Equal(400, (await rig.Handler.HandleAsync("POST", "/api/hooks/has%20space", null, "", owner, CancellationToken.None)).StatusCode);
        Assert.Equal(403, (await rig.Handler.HandleAsync("POST", "/api/hooks/arrive-home", null, "", rig.Store.Current.Remote.GuestToken, CancellationToken.None)).StatusCode);
        Assert.Equal(401, (await rig.Handler.HandleAsync("POST", "/api/hooks/arrive-home", null, "", null, CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Engine_RunsMatchingGraphs_WithCooldown()
    {
        var hook = SceneGraph("hook", Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "tap")), "pure-audiophile");
        var on = SceneGraph("on", Node("t", AutomationNodeTypes.TriggerEvent, ("event", "power.changed"), ("filter", "to=on")));
        var off = SceneGraph("off", Node("t", AutomationNodeTypes.TriggerEvent, ("event", "power.changed")));
        off.Enabled = false;
        var rig = await CreateRigAsync(hook, on, off);
        using var _ = rig.Cleanup;

        var t0 = DateTimeOffset.Now;
        Assert.Single(await rig.Engine.HandleRoomEventAsync(Hook("tap"), t0));
        Assert.Single(await rig.Engine.HandleRoomEventAsync(Hook("tap"), t0.AddMilliseconds(100))); // webhooks may repeat at once

        var powerOn = Event("power.changed", data: ("to", "on"));
        Assert.Equal("on", Assert.Single(await rig.Engine.HandleRoomEventAsync(powerOn, t0)).GraphId);
        Assert.Empty(await rig.Engine.HandleRoomEventAsync(powerOn, t0.AddSeconds(5)));   // event triggers wait at least 10 s
        Assert.Single(await rig.Engine.HandleRoomEventAsync(powerOn, t0.AddSeconds(11)));
        Assert.Empty(await rig.Engine.HandleRoomEventAsync(Event("power.changed", origin: "automation", data: ("to", "on")), t0.AddSeconds(30)));

        Assert.Equal(new[] { "scene:pure-audiophile", "scene:pure-audiophile", "scene:movie-night", "scene:movie-night" }, rig.Effects.Calls);
        Assert.Equal("on", rig.Engine.GetLastRuns()["on"].GraphId);
    }

    [Fact]
    public async Task Engine_StampsTheAutomationOrigin_RightBeforeEachStep()
    {
        var graph = new AutomationGraph
        {
            Id = "slow", Name = "slow", Enabled = true,
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "slow")),
                Node("w", AutomationNodeTypes.ActionDelay, ("seconds", "10")),
                Node("a", AutomationNodeTypes.ActionScene, ("sceneId", "movie-night")),
                Node("p", AutomationNodeTypes.ActionPlugin, ("pluginId", "chromecast"), ("actionId", "cast.pause")),
            ],
            Edges = [Edge("t", "w"), Edge("w", "a"), Edge("a", "p")],
        };
        var rig = await CreateRigAsync(graph);
        using var _ = rig.Cleanup;

        // During the wait the run's first stamp is overwritten (as an expired or newer one would be).
        rig.Effects.OnDelay = () =>
        {
            rig.Telemetry.MarkOrigin("avr", "desktop_ui");
            rig.Telemetry.MarkOrigin("plugin:chromecast", "desktop_ui");
        };
        rig.Effects.Probe = () => $"{rig.Telemetry.ResolveOrigin("avr")}/{rig.Telemetry.ResolveOrigin("plugin:chromecast")}";

        Assert.Single(await rig.Engine.HandleRoomEventAsync(Hook("slow"), DateTimeOffset.Now));
        Assert.Equal(new[] { "automation/desktop_ui", "automation/automation" }, rig.Effects.Probed);
    }

    [Fact]
    public async Task Engine_ScheduleHonoursTheGraphCooldown()
    {
        var graph = SceneGraph("both", Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "home")), cooldown: 120);
        graph.Nodes.Add(Node("s", AutomationNodeTypes.TriggerSchedule, ("time", "22:00"), ("days", "Daily")));
        graph.Edges.Add(Edge("s", "a"));
        var rig = await CreateRigAsync(graph);
        using var _ = rig.Cleanup;

        var offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 25));
        var webhookAt = new DateTimeOffset(2026, 11, 25, 21, 59, 50, offset);
        Assert.Single(await rig.Engine.HandleRoomEventAsync(Hook("home"), webhookAt));
        await rig.Engine.EvaluateTickAsync(webhookAt.AddSeconds(20), default); // 22:00:10, inside the 120 s cooldown
        await rig.Engine.EvaluateTickAsync(webhookAt.AddSeconds(60), default); // 22:00:50, still today's slot
        Assert.Equal(new[] { "scene:movie-night" }, rig.Effects.Calls);
    }

    [Fact]
    public async Task Engine_RunsSchedulesOncePerDay()
    {
        var nightly = SceneGraph("nightly", Node("t", AutomationNodeTypes.TriggerSchedule, ("time", "22:00"), ("days", "Daily")), "late-night");
        var rig = await CreateRigAsync(nightly);
        using var _ = rig.Cleanup;

        var at = new DateTimeOffset(2026, 11, 25, 22, 0, 10, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 25)));
        await rig.Engine.EvaluateTickAsync(at.AddMinutes(-1), default);
        await rig.Engine.EvaluateTickAsync(at, default);
        await rig.Engine.EvaluateTickAsync(at.AddSeconds(3), default);
        Assert.Equal(new[] { "scene:late-night" }, rig.Effects.Calls);
    }

    [Fact]
    public async Task RunningEngine_RunsAWebhookAutomation_FromTheHttpCallThroughTheBus()
    {
        var graph = SceneGraph("home", Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "arrive-home"), ("filter", "who=owner")));
        var rig = await CreateRigAsync(graph);
        using var _ = rig.Cleanup;

        var ran = new TaskCompletionSource<AutomationActivityEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Engine.ActivityLogged += entry => ran.TrySetResult(entry);
        await rig.Engine.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(200); // the engine subscribes when ExecuteAsync starts
            var reply = await rig.Handler.HandleAsync("POST", "/api/hooks/arrive-home", null, "{\"who\":\"owner\"}", rig.Store.Current.Remote.OwnerToken, CancellationToken.None);
            Assert.Equal(200, reply.StatusCode);

            var done = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(done == ran.Task, "The webhook automation did not run within 5 s.");
            var entry = await ran.Task;
            Assert.Equal("home", entry.RuleName);
            Assert.Contains("Webhook 'arrive-home'", entry.TriggerDetail);
            Assert.Equal(new[] { "scene:movie-night" }, rig.Effects.Calls);
        }
        finally
        {
            await rig.Engine.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Engine_LoopGuard_PausesEventTriggersAfterTooManyRuns()
    {
        // Four graphs on one event: each event runs four graphs, so five events make 20 runs within a minute.
        var graphs = Enumerable.Range(1, 4)
            .Select(i => SceneGraph($"r{i}", Node("t", AutomationNodeTypes.TriggerEvent, ("event", "input.changed"))))
            .Append(SceneGraph("hook", Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "tap"))))
            .ToArray();
        var rig = await CreateRigAsync(graphs);
        using var _ = rig.Cleanup;

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
        Assert.Single(await rig.Engine.HandleRoomEventAsync(Hook("tap"), t5.AddSeconds(31))); // webhooks are not paused
        Assert.Equal(4, (await rig.Engine.HandleRoomEventAsync(input, t5.AddSeconds(61))).Count); // pause over
    }

    [Fact]
    public async Task RunGraphNow_DryRunUsesTheGivenData_AndChangesNothing()
    {
        var graph = new AutomationGraph
        {
            Id = "g", Name = "Focus", Enabled = false,
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "focus")),
                Node("c", AutomationNodeTypes.ConditionEvent, ("filter", "focus=Sleep")),
                Node("a", AutomationNodeTypes.ActionReceiver, ("command", "power_off"), ("value", "")),
            ],
            Edges = [Edge("t", "c"), Edge("c", "a", "yes")],
        };
        var rig = await CreateRigAsync(graph);
        using var _ = rig.Cleanup;

        var run = await rig.Engine.RunGraphNowAsync(graph, null, new Dictionary<string, object?> { ["focus"] = "Sleep" }, dryRun: true);
        Assert.True(run.DryRun);
        Assert.Contains(run.Steps, s => s.NodeId == "c" && s.Outcome == "yes");
        Assert.Contains(run.Steps, s => s.NodeId == "a" && s.Outcome == "would-run");
        Assert.Empty(rig.Effects.Calls);
    }
}
