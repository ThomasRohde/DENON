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

        /// <summary>Newest first, as the bus gives them.</summary>
        public List<RoomEvent> Recent { get; } = [];

        public IReadOnlyList<RoomEvent> GetRecentEvents() => Recent;

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

        /// <summary>When set, a wait lasts until the test completes this.</summary>
        public TaskCompletionSource? DelayGate { get; set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"wait:{delay.TotalSeconds}");
            OnDelay?.Invoke();
            return DelayGate?.Task ?? Task.CompletedTask;
        }

        public Task<string> SetLearnedVolumeAsync(CancellationToken ct)
        {
            lock (Calls) Calls.Add("learned-volume");
            return Task.FromResult("Volume 42 for DR1 (learned)");
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

    [Fact]
    public void Condition_RecentEvent_SeesACastThatStartedJustBefore()
    {
        var effects = new FakeEffects();
        var now = new DateTimeOffset(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);
        var tvOn = new RoomEvent(now, "tv", "smart-tv", "app.changed", "external", new Dictionary<string, object?> { ["fromAppId"] = "" });
        var recentCast = Node("r", AutomationNodeTypes.ConditionRecentEvent,
            ("event", "cast.app_started, appletv.woke"), ("seconds", "30"), ("filter", "origin=external"));

        Assert.False(AutomationGraphRunner.Evaluate(recentCast, tvOn, effects, now).Pass);

        // A cast the user started 12 s ago: yes.
        effects.Recent.Add(new RoomEvent(now.AddSeconds(-12), "plugin:chromecast", "chromecast", "cast.app_started", "external", null));
        var yes = AutomationGraphRunner.Evaluate(recentCast, tvOn, effects, now);
        Assert.True(yes.Pass);
        Assert.Contains("cast.app_started from chromecast 12 s ago", yes.Why);

        // Too old, or started by the app itself (the TV plugin's HDMI-CEC wake): no.
        effects.Recent.Clear();
        effects.Recent.Add(new RoomEvent(now.AddSeconds(-45), "plugin:chromecast", "chromecast", "cast.app_started", "external", null));
        effects.Recent.Add(new RoomEvent(now.AddSeconds(-5), "plugin:chromecast", "chromecast", "cast.app_started", "desktop_ui", null));
        Assert.False(AutomationGraphRunner.Evaluate(recentCast, tvOn, effects, now).Pass);

        // Wildcards, and the triggering event itself never counts.
        var anyTv = Node("r", AutomationNodeTypes.ConditionRecentEvent, ("event", "app.*"), ("seconds", "30"), ("filter", ""));
        effects.Recent.Insert(0, tvOn);
        Assert.False(AutomationGraphRunner.Evaluate(anyTv, tvOn, effects, now).Pass);
        effects.Recent.Insert(0, new RoomEvent(now.AddSeconds(-1), "tv", "smart-tv", "app.changed", "external", null));
        Assert.True(AutomationGraphRunner.Evaluate(anyTv, tvOn, effects, now).Pass);
    }

    [Fact]
    public void The_live_tv_example_switches_only_when_the_tv_comes_on_elsewhere()
    {
        var graph = AutomationGraph.CreateDefaults().Single(g => g.Id == "auto-tv-live");
        Assert.Empty(AutomationGraphValidator.Validate(graph));
        var trigger = graph.Nodes.Single(n => n.Type == AutomationNodeTypes.TriggerEvent);
        DateTimeOffset now = DateTimeOffset.Now;
        RoomEvent App(string from, string to) => new(now, "tv", "smart-tv", "app.changed", "external",
            new Dictionary<string, object?> { ["fromAppId"] = from, ["toAppId"] = to });

        Assert.True(AutomationTriggers.Matches(trigger, App("", "com.webos.app.hdmi1")));
        Assert.False(AutomationTriggers.Matches(trigger, App("", "com.webos.app.livetv")));
        Assert.False(AutomationTriggers.Matches(trigger, App("com.webos.app.livetv", "com.webos.app.hdmi1")));
    }

    [Fact]
    public void Condition_StandbyOrIdle_PassesWhenHeosPlaysNothing()
    {
        var effects = new FakeEffects();
        var idle = Node("r", AutomationNodeTypes.ConditionReceiver, ("power", "idle"), ("input", ""), ("muted", "any"));
        var e = Event("x");
        var noon = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        (bool Pass, string Message) Check(ReceiverFacts facts)
        {
            effects.Facts = facts;
            return AutomationGraphRunner.Evaluate(idle, e, effects, noon);
        }

        Assert.True(Check(new ReceiverFacts(false, "NET", false, false)).Pass);
        // Left on after a stream was stopped or paused: idle.
        var stopped = Check(new ReceiverFacts(true, "NET", false, false));
        Assert.True(stopped.Pass);
        Assert.Contains("HEOS plays nothing", stopped.Message);

        var playing = Check(new ReceiverFacts(true, "NET", false, true));
        Assert.False(playing.Pass);
        Assert.Contains("HEOS is playing", playing.Message);
        // HEOS state not read yet (or the AVR did not confirm the input): not idle.
        var unknown = Check(new ReceiverFacts(true, "NET", false, null));
        Assert.False(unknown.Pass);
        Assert.Contains("not known", unknown.Message);
        // On the TV input the TV may be on: never idle.
        var tv = Check(new ReceiverFacts(true, "TV", false, false));
        Assert.False(tv.Pass);
        Assert.Contains("TV input", tv.Message);
        Assert.False(Check(new ReceiverFacts(null, null, null)).Pass);
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
        var effects = new FakeEffects();
        var engine = new AutomationEngineService(store, controller, stateStore, plugins, telemetry, bus, log) { EffectsOverride = effects };
        var handler = new RemoteWebApiHandler(controller, stateStore, store, ytMedia, ytQueue, jukebox, plugins, telemetry, engine, log);

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
    public async Task Engine_ARunThatChangesNothing_LeavesTheOriginsAlone()
    {
        var graph = new AutomationGraph
        {
            Id = "never", Name = "never", Enabled = true,
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "never")),
                Node("c", AutomationNodeTypes.ConditionEvent, ("filter", "who=nobody")),
                Node("a", AutomationNodeTypes.ActionScene, ("sceneId", "movie-night")),
            ],
            Edges = [Edge("t", "c"), Edge("c", "a", "yes")],
        };
        var rig = await CreateRigAsync(graph);
        using var _ = rig.Cleanup;

        Assert.Single(await rig.Engine.HandleRoomEventAsync(Hook("never"), DateTimeOffset.Now));
        Assert.Empty(rig.Effects.Calls);

        // The user's own power-on right after still counts as external, so a receiver-on automation can fire.
        Assert.Equal("external", rig.Telemetry.ResolveOrigin("avr"));
        Assert.Equal("external", rig.Telemetry.ResolveOrigin("tv"));
        Assert.Equal("external", rig.Telemetry.ResolveOrigin("lighting"));
    }

    [Fact]
    public async Task Engine_AHueActionMarksOnlyTheLights()
    {
        var graph = new AutomationGraph
        {
            Id = "lights", Name = "lights", Enabled = true,
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "lights")),
                Node("a", AutomationNodeTypes.ActionPlugin, ("pluginId", "philips-hue"), ("actionId", "hue.all_on")),
            ],
            Edges = [Edge("t", "a")],
        };
        var rig = await CreateRigAsync(graph);
        using var _ = rig.Cleanup;

        Assert.Single(await rig.Engine.HandleRoomEventAsync(Hook("lights"), DateTimeOffset.Now));
        Assert.Equal("automation", rig.Telemetry.ResolveOrigin("lighting"));
        Assert.Equal("external", rig.Telemetry.ResolveOrigin("avr")); // the user's receiver power-on still counts
        Assert.Equal("external", rig.Telemetry.ResolveOrigin("tv"));
    }

    [Fact]
    public async Task Engine_OnlyARunThatActsUsesTheCooldown_AndSkipsAreLogged()
    {
        var graph = new AutomationGraph
        {
            Id = "home", Name = "home", Enabled = true, CooldownSeconds = 3600,
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "home")),
                Node("c", AutomationNodeTypes.ConditionEvent, ("filter", "who=owner")),
                Node("a", AutomationNodeTypes.ActionScene, ("sceneId", "morning-coffee")),
            ],
            Edges = [Edge("t", "c"), Edge("c", "a", "yes")],
        };
        var rig = await CreateRigAsync(graph);
        using var _ = rig.Cleanup;
        var log = new List<AutomationActivityEntry>();
        rig.Engine.ActivityLogged += e => { lock (log) log.Add(e); };

        var t0 = DateTimeOffset.Now;
        // 1. The check says no: nothing happens, and the cooldown is given back.
        Assert.Single(await rig.Engine.HandleRoomEventAsync(Hook("home", ("who", "guest")), t0));
        Assert.Empty(rig.Effects.Calls);

        // 2. A minute later the check says yes: it runs although the cooldown is an hour.
        Assert.Single(await rig.Engine.HandleRoomEventAsync(Hook("home", ("who", "owner")), t0.AddMinutes(1)));
        Assert.Equal(new[] { "scene:morning-coffee" }, rig.Effects.Calls);

        // 3. That run acted, so the next call is in the cooldown: skipped, and the log says so (once a minute at most).
        Assert.Empty(await rig.Engine.HandleRoomEventAsync(Hook("home", ("who", "owner")), t0.AddMinutes(2)));
        Assert.Empty(await rig.Engine.HandleRoomEventAsync(Hook("home", ("who", "owner")), t0.AddMinutes(2.5)));
        var skips = log.Where(e => e.TriggerDetail.Contains("skipped")).ToList();
        var skip = Assert.Single(skips);
        Assert.True(skip.Skipped);
        Assert.False(skip.Success); // shown as skipped, not as a successful run
        Assert.Contains("Webhook 'home'", skip.TriggerDetail);
        Assert.Contains($"until {t0.AddMinutes(61).LocalDateTime:HH:mm:ss}", skip.TriggerDetail);
        Assert.Equal(new[] { "scene:morning-coffee" }, rig.Effects.Calls);

        // 4. After the hour it runs again.
        Assert.Single(await rig.Engine.HandleRoomEventAsync(Hook("home", ("who", "owner")), t0.AddMinutes(62)));
        Assert.Equal(new[] { "scene:morning-coffee", "scene:morning-coffee" }, rig.Effects.Calls);
    }

    [Fact]
    public async Task Engine_RunsThatChangeNothing_DoNotTripTheLoopGuard()
    {
        // A room-event automation whose check keeps saying no, and an unrelated one that acts.
        var picky = new AutomationGraph
        {
            Id = "picky", Name = "picky", Enabled = true, CooldownSeconds = 0,
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerEvent, ("event", "volume.settled")),
                Node("c", AutomationNodeTypes.ConditionEvent, ("filter", "to=never")),
                Node("a", AutomationNodeTypes.ActionScene, ("sceneId", "movie-night")),
            ],
            Edges = [Edge("t", "c"), Edge("c", "a", "yes")],
        };
        var lights = SceneGraph("lights", Node("t", AutomationNodeTypes.TriggerEvent, ("event", "power.changed")), "late-night");
        var rig = await CreateRigAsync(picky, lights);
        using var _ = rig.Cleanup;

        // 30 rejected runs within a minute (each gives back its cooldown, so each is tried).
        var t0 = DateTimeOffset.Now;
        for (var i = 0; i < 30; i++)
        {
            Assert.Single(await rig.Engine.HandleRoomEventAsync(Event("volume.settled", data: ("to", "40")), t0.AddSeconds(i)));
        }

        Assert.Empty(rig.Effects.Calls);
        Assert.False(rig.Engine.EventTriggersPaused);

        // The loop guard was not tripped: the other automation still runs.
        Assert.Single(await rig.Engine.HandleRoomEventAsync(Event("power.changed", data: ("to", "on")), t0.AddSeconds(31)));
        Assert.Equal(new[] { "scene:late-night" }, rig.Effects.Calls);
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
    public async Task Engine_AWaitingGraphDoesNotHoldBackTheNextGraph()
    {
        var slow = new AutomationGraph
        {
            Id = "slow", Name = "slow", Enabled = true,
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerEvent, ("event", "input.changed")),
                Node("w", AutomationNodeTypes.ActionDelay, ("seconds", "120")),
                Node("a", AutomationNodeTypes.ActionScene, ("sceneId", "movie-night")),
            ],
            Edges = [Edge("t", "w"), Edge("w", "a")],
        };
        var quick = SceneGraph("quick", Node("t", AutomationNodeTypes.TriggerEvent, ("event", "input.changed")), "late-night");
        var rig = await CreateRigAsync(slow, quick);
        using var _ = rig.Cleanup;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Effects.DelayGate = gate;

        var handling = rig.Engine.HandleRoomEventAsync(Event("input.changed", data: ("to", "TV")), DateTimeOffset.Now);
        Assert.Contains("scene:late-night", rig.Effects.Calls);   // ran while "slow" still waits
        Assert.False(handling.IsCompleted);

        gate.SetResult();
        var runs = await handling;
        Assert.Equal(2, runs.Count);
        Assert.Contains("scene:movie-night", rig.Effects.Calls);
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
    public async Task RunningEngine_RunsAWebhookAutomation_OnceFromTheHttpCall()
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
            Assert.Contains("\"outcome\":\"ran\"", reply.Body);

            var done = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(done == ran.Task, "The webhook automation did not run within 5 s.");
            var entry = await ran.Task;
            Assert.Equal("home", entry.RuleName);
            Assert.Contains("Webhook 'arrive-home'", entry.TriggerDetail);
            await Task.Delay(300); // the bus copy of the event must not start it a second time
            Assert.Equal(new[] { "scene:movie-night" }, rig.Effects.Calls);
        }
        finally
        {
            await rig.Engine.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Webhook_Reply_SaysWhatEachAutomationDid()
    {
        // ran: no conditions. nothing: needs the receiver in standby, but it is on. skipped: a run 5 minutes ago, 1 h cooldown.
        var ran = SceneGraph("ran", Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "home")));
        var nothing = new AutomationGraph
        {
            Id = "nothing", Name = "nothing", Enabled = true, CooldownSeconds = 3600,
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "home")),
                Node("c", AutomationNodeTypes.ConditionReceiver, ("power", "standby"), ("input", ""), ("muted", "any")),
                Node("a", AutomationNodeTypes.ActionScene, ("sceneId", "jazz")),
            ],
            Edges = [Edge("t", "c"), Edge("c", "a", AutomationNodeTypes.PortYes)],
        };
        var skipped = SceneGraph("skipped", Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "home")), cooldown: 3600);
        var rig = await CreateRigAsync(ran, nothing, skipped);
        using var _ = rig.Cleanup;
        rig.Effects.Facts = new ReceiverFacts(true, "TV", false);
        // All three ran 5 minutes ago. Only "skipped" acted with a cooldown; "nothing" changed nothing and keeps no cooldown.
        Assert.Equal(3, (await rig.Engine.HandleRoomEventAsync(Hook("home"), DateTimeOffset.Now.AddMinutes(-5))).Count);
        rig.Effects.Calls.Clear();

        var reply = await rig.Handler.HandleAsync("POST", "/api/hooks/home", null, "", rig.Store.Current.Remote.OwnerToken, CancellationToken.None);

        Assert.Equal(200, reply.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(reply.Body);
        var root = doc.RootElement.TryGetProperty("data", out var data) ? data : doc.RootElement;
        var outcomes = root.GetProperty("results").EnumerateArray()
            .ToDictionary(static r => r.GetProperty("automation").GetString()!, static r => r.GetProperty("outcome").GetString());
        Assert.Equal("ran", outcomes["ran"]);
        Assert.Equal("nothing", outcomes["nothing"]);
        Assert.Equal("skipped", outcomes["skipped"]);
        var message = root.GetProperty("message").GetString()!;
        Assert.Contains("ran: ran Scene 'movie-night'", message);
        Assert.Contains("nothing: nothing to do", message);
        Assert.Contains("skipped: skipped, in its cooldown until", message);
        Assert.Equal(new[] { "scene:movie-night" }, rig.Effects.Calls);
    }

    [Fact]
    public async Task Webhook_Reply_SaysStillRunning_WhenARunTakesLonger()
    {
        var slow = new AutomationGraph
        {
            Id = "slow", Name = "slow", Enabled = true, CooldownSeconds = 0,
            Nodes = [Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "slow")), Node("w", AutomationNodeTypes.ActionDelay, ("seconds", "5"))],
            Edges = [Edge("t", "w")],
        };
        var rig = await CreateRigAsync(slow);
        using var _ = rig.Cleanup;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Effects.DelayGate = gate;
        var wait = RemoteWebApiHandler.WebhookReplyWait;
        RemoteWebApiHandler.WebhookReplyWait = TimeSpan.FromMilliseconds(200);
        try
        {
            var reply = await rig.Handler.HandleAsync("POST", "/api/hooks/slow", null, "", rig.Store.Current.Remote.OwnerToken, CancellationToken.None);
            Assert.Contains("\"outcome\":\"running\"", reply.Body);
            Assert.Contains("slow: still running.", reply.Body);
        }
        finally
        {
            RemoteWebApiHandler.WebhookReplyWait = wait;
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_source_change_runs_the_learned_volume_action()
    {
        var graph = new AutomationGraph
        {
            Id = "dr1-level", Name = "DR1 level", Enabled = true, CooldownSeconds = 0,
            Nodes =
            [
                Node("t", AutomationNodeTypes.TriggerSource, ("source", "DR1"), ("filter", "")),
                Node("a", AutomationNodeTypes.ActionLearnedVolume),
            ],
            Edges = [Edge("t", "a")],
        };
        Assert.Empty(AutomationGraphValidator.Validate(graph));
        var rig = await CreateRigAsync(graph);
        using var _ = rig.Cleanup;

        RoomEvent Changed(string source, string origin = "external") =>
            Event(VolumeMemoryService.SourceChangedEvent, origin, "control", "source", ("source", source), ("kind", "tv_channel"));

        Assert.Empty(await rig.Engine.HandleRoomEventAsync(Changed("TV 2"), DateTimeOffset.Now));
        Assert.Empty(await rig.Engine.HandleRoomEventAsync(Changed("DR1", origin: "automation"), DateTimeOffset.Now)); // its own change
        var run = Assert.Single(await rig.Engine.HandleRoomEventAsync(Changed("DR1"), DateTimeOffset.Now.AddMinutes(1)));
        Assert.Contains("Volume 42 for DR1 (learned)", run.Summary);
        Assert.Equal(new[] { "learned-volume" }, rig.Effects.Calls);
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
