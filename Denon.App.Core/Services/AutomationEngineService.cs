using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Microsoft.Extensions.Hosting;

namespace Denon.App.Core.Services;

public sealed record AutomationActivityEntry(DateTimeOffset Timestamp, string RuleName, string TriggerDetail, bool Success);

/// <summary>
/// Runs the automation graphs in settings: room events and webhooks from the <see cref="RoomEventBus"/>, schedules
/// and app starts from a 3-second tick. See docs/automation-graphs.md.
/// </summary>
public sealed class AutomationEngineService : BackgroundService
{
    /// <summary>Event triggers start a graph at most this often, whatever its cooldown says.</summary>
    public static readonly TimeSpan MinEventCooldown = TimeSpan.FromSeconds(10);

    /// <summary>App-start triggers start a graph at most this often.</summary>
    public static readonly TimeSpan MinAppLaunchCooldown = TimeSpan.FromSeconds(10);

    /// <summary>More event-started runs than this within <see cref="LoopWindow"/> pauses event triggers.</summary>
    public const int LoopRunLimit = 20;

    public static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(1);

    private readonly AppSettingsStore _settingsStore;
    private readonly ReceiverController _receiverController;
    private readonly ReceiverStateStore _stateStore;
    private readonly PluginHostService _pluginHost;
    private readonly EventTelemetryLog _telemetry;
    private readonly RoomEventBus _eventBus;
    private readonly AppLogStore _logStore;
    private readonly RoomEffects _effects;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRunByGraph = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateOnly> _lastScheduleDayByNode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AutomationRun> _lastRunResult = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _recentEventRuns = new();
    private readonly Channel<RoomEvent> _pendingEvents = Channel.CreateBounded<RoomEvent>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private HashSet<string> _previousProcessNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasBaselineSnapshot;
    private DateTimeOffset _eventTriggersPausedUntil = DateTimeOffset.MinValue;

    public AutomationEngineService(
        AppSettingsStore settingsStore,
        ReceiverController receiverController,
        ReceiverStateStore stateStore,
        PluginHostService pluginHost,
        EventTelemetryLog telemetry,
        RoomEventBus eventBus,
        AppLogStore logStore)
    {
        _settingsStore = settingsStore;
        _receiverController = receiverController;
        _stateStore = stateStore;
        _pluginHost = pluginHost;
        _telemetry = telemetry;
        _eventBus = eventBus;
        _logStore = logStore;
        _effects = new RoomEffects(this);
    }

    /// <summary>A short line per finished run (not dry runs), for the activity log in the app.</summary>
    public event Action<AutomationActivityEntry>? ActivityLogged;

    /// <summary>Every finished run, dry runs included.</summary>
    public event Action<AutomationRun>? RunCompleted;

    /// <summary>Lets tests replace the room. Null: the real receiver, scenes and plugins.</summary>
    public IAutomationEffects? EffectsOverride { get; set; }

    /// <summary>True while the loop guard pauses event triggers.</summary>
    public bool EventTriggersPaused => DateTimeOffset.Now < _eventTriggersPausedUntil;

    /// <summary>The latest room events from the live bus, newest first.</summary>
    public IReadOnlyList<RoomEvent> GetRecentEvents(int max = 20) => _eventBus.GetRecent(max);

    /// <summary>The last run of each graph since the app started.</summary>
    public IReadOnlyDictionary<string, AutomationRun> GetLastRuns()
    {
        lock (_gate)
        {
            return new Dictionary<string, AutomationRun>(_lastRunResult, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Runs <paramref name="graph"/> now from one trigger (the first one when <paramref name="triggerNodeId"/> is
    /// null), as the editor's Dry run and Run now do. <paramref name="data"/> stands in for the event data. Ignores
    /// the enabled state and the cooldown.
    /// </summary>
    public async Task<AutomationRun> RunGraphNowAsync(
        AutomationGraph graph,
        string? triggerNodeId,
        IReadOnlyDictionary<string, object?>? data,
        bool dryRun,
        CancellationToken ct = default)
    {
        var trigger = (triggerNodeId is null ? null : graph.FindNode(triggerNodeId))
                      ?? graph.Nodes.FirstOrDefault(static n => AutomationNodeTypes.IsTrigger(n.Type))
                      ?? throw new InvalidOperationException("The automation has no trigger.");
        var roomEvent = new RoomEvent(DateTimeOffset.Now, "control", "automation", "manual", "desktop_ui", data);
        return await RunAsync(graph, trigger, roomEvent, dryRun ? "Dry run" : "Run now", dryRun, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Room events and webhooks: the bus handler only queues; one loop runs the graphs in order.
        using var subscription = _eventBus.Subscribe(e => _pendingEvents.Writer.TryWrite(e));
        var eventLoop = RunEventTriggersAsync(stoppingToken);

        try
        {
            // Let the app start, and take a process baseline so programs that already run do not fire.
            await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
            _previousProcessNames = SnapshotRunningProcessNames();
            _hasBaselineSnapshot = true;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // Runs go on in the background: a graph that waits must not stall the next tick.
                Observe(EvaluateTickAsync(DateTimeOffset.Now, stoppingToken), "Schedule");
            }
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await eventLoop;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunEventTriggersAsync(CancellationToken ct)
    {
        await foreach (var roomEvent in _pendingEvents.Reader.ReadAllAsync(ct))
        {
            // The graphs are chosen, checked and marked here, in order; their runs go on in the background, so a
            // graph that waits neither holds up the next event nor starts later graphs with a stale clock.
            Observe(HandleRoomEventAsync(roomEvent, DateTimeOffset.Now, ct), $"Event trigger on {roomEvent.Event}");
        }
    }

    private void Observe(Task task, string what) =>
        _ = task.ContinueWith(
            t => _logStore.Warning($"[Automation] {what} failed: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    /// <summary>
    /// Starts every enabled graph with an event or webhook trigger that matches <paramref name="roomEvent"/> and is out
    /// of its cooldown (one run per graph). The choice, the cooldowns and the loop guard are settled before this
    /// returns its task, all at <paramref name="now"/>; the runs then go side by side. The task completes with the
    /// finished runs. Public for tests.
    /// </summary>
    public Task<IReadOnlyList<AutomationRun>> HandleRoomEventAsync(RoomEvent roomEvent, DateTimeOffset now, CancellationToken ct = default)
    {
        var runs = new List<Task<AutomationRun>>();
        foreach (var graph in EnabledGraphs())
        {
            var trigger = graph.Nodes.FirstOrDefault(n => AutomationTriggers.Matches(n, roomEvent));
            if (trigger is null)
            {
                continue;
            }

            var isWebhook = trigger.Type == AutomationNodeTypes.TriggerWebhook;
            if (!IsOutOfCooldown(graph, now, isWebhook ? TimeSpan.Zero : MinEventCooldown) ||
                (!isWebhook && !AllowEventRun(now, graph.Name)))
            {
                continue;
            }

            if (!TryMarkRun(graph, now, isWebhook ? TimeSpan.Zero : MinEventCooldown))
            {
                continue; // A schedule or app start of this graph got there first.
            }

            var detail = isWebhook
                ? $"Webhook '{roomEvent.GetText("hook")}'"
                : $"Event {AutomationTriggers.Describe(roomEvent)}";
            runs.Add(RunAsync(graph, trigger, roomEvent, detail, dryRun: false, ct, now));
        }

        return WhenAllRuns(runs);
    }

    private static async Task<IReadOnlyList<AutomationRun>> WhenAllRuns(List<Task<AutomationRun>> runs) =>
        runs.Count == 0 ? [] : await Task.WhenAll(runs);

    /// <summary>
    /// Schedules and app starts. As for events, the checks happen before the task is returned and the runs go side
    /// by side; the task completes when they have finished. Public for tests.
    /// </summary>
    public Task EvaluateTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var graphs = EnabledGraphs();
        if (graphs.Count == 0)
        {
            return Task.CompletedTask;
        }

        var runs = new List<Task<AutomationRun>>();

        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var currentTime = TimeOnly.FromDateTime(now.LocalDateTime);

        var appTriggers = graphs.SelectMany(g => g.Nodes.Where(static n => n.Type == AutomationNodeTypes.TriggerAppLaunch).Select(n => (g, n))).ToList();
        if (appTriggers.Count > 0)
        {
            var current = SnapshotRunningProcessNames();
            if (_hasBaselineSnapshot)
            {
                foreach (var (graph, node) in appTriggers)
                {
                    var started = ParseProcessList(node.Get("processes"))
                        .FirstOrDefault(p => current.Contains(p) && !_previousProcessNames.Contains(p));
                    if (started is not null && TryMarkRun(graph, now, MinAppLaunchCooldown))
                    {
                        runs.Add(RunAsync(graph, node, null, $"'{started}.exe' started", dryRun: false, ct, now));
                    }
                }
            }

            _previousProcessNames = current;
            _hasBaselineSnapshot = true;
        }

        foreach (var graph in graphs)
        {
            foreach (var node in graph.Nodes.Where(static n => n.Type == AutomationNodeTypes.TriggerSchedule))
            {
                if (!AutomationTriggers.MatchesDays(node.Get("days"), now.LocalDateTime.DayOfWeek) ||
                    !AutomationGraphValidator.TryParseTime(node.Get("time"), out var at) ||
                    currentTime.Hour != at.Hour || currentTime.Minute != at.Minute)
                {
                    continue;
                }

                var key = $"{graph.Id}/{node.Id}";
                if (_lastScheduleDayByNode.TryGetValue(key, out var lastDay) && lastDay == today)
                {
                    continue;
                }

                // Today's slot is used up either way: a run the cooldown blocks is skipped, not retried later
                // in the same minute.
                _lastScheduleDayByNode[key] = today;
                if (!TryMarkRun(graph, now, TimeSpan.Zero))
                {
                    continue;
                }

                runs.Add(RunAsync(graph, node, null, $"Scheduled {at:HH\\:mm}", dryRun: false, ct, now));
            }
        }

        return WhenAllRuns(runs);
    }

    private List<AutomationGraph> EnabledGraphs() =>
        (_settingsStore.Current.AutomationGraphs ?? []).Where(static g => g.Enabled).ToList();

    private bool IsOutOfCooldown(AutomationGraph graph, DateTimeOffset now, TimeSpan minimum)
    {
        lock (_gate)
        {
            return IsOutOfCooldownLocked(graph, now, minimum);
        }
    }

    /// <summary>
    /// Checks the cooldown and marks the run in one step: the event loop and the 3-second tick run on different
    /// threads, and both may try to start the same graph.
    /// </summary>
    private bool TryMarkRun(AutomationGraph graph, DateTimeOffset now, TimeSpan minimum)
    {
        lock (_gate)
        {
            if (!IsOutOfCooldownLocked(graph, now, minimum))
            {
                return false;
            }

            _lastRunByGraph[graph.Id] = now;
            return true;
        }
    }

    private bool IsOutOfCooldownLocked(AutomationGraph graph, DateTimeOffset now, TimeSpan minimum)
    {
        var cooldown = TimeSpan.FromSeconds(Math.Clamp(graph.CooldownSeconds, 0, 86400));
        if (cooldown < minimum)
        {
            cooldown = minimum;
        }

        return !_lastRunByGraph.TryGetValue(graph.Id, out var last) || now - last >= cooldown;
    }

    /// <summary>
    /// Loop breaker: graphs that react to room events can set off each other (scene → power.changed → scene).
    /// After <see cref="LoopRunLimit"/> event-started runs within <see cref="LoopWindow"/>, event triggers pause for
    /// one window. Webhooks are not counted: each one is a call from outside.
    /// </summary>
    private bool AllowEventRun(DateTimeOffset now, string graphName)
    {
        if (now < _eventTriggersPausedUntil)
        {
            return false;
        }

        while (_recentEventRuns.Count > 0 && now - _recentEventRuns.Peek() > LoopWindow)
        {
            _recentEventRuns.Dequeue();
        }

        if (_recentEventRuns.Count >= LoopRunLimit)
        {
            _eventTriggersPausedUntil = now + LoopWindow;
            _recentEventRuns.Clear();
            var message = $"Paused event triggers for {LoopWindow.TotalSeconds:0} s: {LoopRunLimit} runs in {LoopWindow.TotalSeconds:0} s looks like a loop (last: '{graphName}').";
            _logStore.Warning($"[Automation] {message}");
            ActivityLogged?.Invoke(new AutomationActivityEntry(now, "Loop guard", message, false));
            return false;
        }

        _recentEventRuns.Enqueue(now);
        return true;
    }

    private async Task<AutomationRun> RunAsync(
        AutomationGraph graph,
        AutomationNode trigger,
        RoomEvent? roomEvent,
        string detail,
        bool dryRun,
        CancellationToken ct,
        DateTimeOffset? at = null)
    {
        if (!dryRun)
        {
            // Only the log entry here. The "automation" origin is stamped by each step right before it changes a domain
            // (OriginStampingEffects): a run that waits or whose checks say no must not hide the user's own changes
            // (a real power-on) from event triggers for the next seconds.
            _telemetry.Record(EventTelemetryLog.StreamControl, "automation", "automation.triggered", AutomationTriggers.AutomationOrigin, new Dictionary<string, object?>
            {
                ["automation"] = graph.Name,
                ["trigger"] = detail,
            });
        }

        var effects = dryRun ? EffectsOverride ?? _effects : new OriginStampingEffects(EffectsOverride ?? _effects, _telemetry);

        AutomationRun run;
        try
        {
            run = await AutomationGraphRunner.RunAsync(graph, trigger, roomEvent, detail, effects, dryRun, at ?? DateTimeOffset.Now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run = new AutomationRun(at ?? DateTimeOffset.Now, graph.Id, graph.Name, detail, dryRun, false, $"{detail} → failed: {ex.Message}", []);
        }

        lock (_gate)
        {
            _lastRunResult[graph.Id] = run;
        }

        if (!dryRun)
        {
            if (run.Success)
            {
                _logStore.Info($"[Automation] {graph.Name}: {run.Summary}");
            }
            else
            {
                _logStore.Warning($"[Automation] {graph.Name}: {run.Summary}");
            }

            ActivityLogged?.Invoke(new AutomationActivityEntry(run.At, graph.Name, run.Summary, run.Success));
        }

        RunCompleted?.Invoke(run);
        return run;
    }

    private static HashSet<string> SnapshotRunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(proc.ProcessName))
                    {
                        names.Add(proc.ProcessName);
                    }
                }
                catch
                {
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
        }

        return names;
    }

    private static List<string> ParseProcessList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s)
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>
    /// Stamps the "automation" origin on the domains a step changes right before it runs, so the room events it
    /// causes never start an event trigger, however long the run has waited before the step.
    /// </summary>
    private sealed class OriginStampingEffects(IAutomationEffects inner, EventTelemetryLog telemetry) : IAutomationEffects
    {
        public ReceiverFacts GetReceiverFacts() => inner.GetReceiverFacts();

        public string DescribeScene(string sceneId) => inner.DescribeScene(sceneId);

        public string DescribePluginAction(string pluginId, string actionId) => inner.DescribePluginAction(pluginId, actionId);

        public Task RunSceneAsync(string sceneId, CancellationToken ct)
        {
            Stamp(EventTelemetryLog.StreamAvr, EventTelemetryLog.StreamTv, EventTelemetryLog.StreamLighting);
            return inner.RunSceneAsync(sceneId, ct);
        }

        public Task<(bool Success, string Message)> RunPluginActionAsync(
            string pluginId, string actionId, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
        {
            Stamp(EventTelemetryLog.StreamAvr, EventTelemetryLog.StreamTv, EventTelemetryLog.StreamLighting, $"plugin:{pluginId.Trim().ToLowerInvariant()}");
            return inner.RunPluginActionAsync(pluginId, actionId, parameters, ct);
        }

        public Task<string> RunReceiverCommandAsync(string command, string value, CancellationToken ct)
        {
            Stamp(EventTelemetryLog.StreamAvr);
            return inner.RunReceiverCommandAsync(command, value, ct);
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => inner.DelayAsync(delay, ct);

        private void Stamp(params string[] domains)
        {
            foreach (var domain in domains)
            {
                telemetry.MarkOrigin(domain, AutomationTriggers.AutomationOrigin);
            }
        }
    }

    /// <summary>The real room: the receiver, the Smart Scenes and the plugins.</summary>
    private sealed class RoomEffects(AutomationEngineService engine) : IAutomationEffects
    {
        public ReceiverFacts GetReceiverFacts()
        {
            var snapshot = engine._stateStore.Snapshot;
            var status = snapshot.Status;
            return status is null || !snapshot.IsConnected
                ? new ReceiverFacts(null, null, null)
                : new ReceiverFacts(status is { IsPoweredOn: true, IsMainZoneOn: true }, status.InputSource, status.IsMuted);
        }

        public string DescribeScene(string sceneId) =>
            FindScene(sceneId)?.Name ?? sceneId;

        public string DescribePluginAction(string pluginId, string actionId)
        {
            var plugin = engine._pluginHost.GetPlugins().FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            var action = plugin?.Actions.FirstOrDefault(a => string.Equals(a.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
            return plugin is null ? $"{pluginId}: {actionId}" : $"{plugin.Name}: {action?.DisplayName ?? actionId}";
        }

        public async Task RunSceneAsync(string sceneId, CancellationToken ct)
        {
            var scene = FindScene(sceneId) ?? throw new InvalidOperationException($"No Smart Scene '{sceneId}'.");
            await engine._receiverController.ApplySceneAsync(scene, ct);

            // ApplySceneAsync returns after the receiver part; the scene's plugin chain runs in the background.
            // Wait for it, so the next node runs after the whole scene (e.g. a Hue step after the scene's own dim).
            await engine._pluginHost.WhenSceneActionsDoneAsync(scene.Id, ct);
        }

        public async Task<(bool Success, string Message)> RunPluginActionAsync(
            string pluginId, string actionId, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
        {
            var result = await engine._pluginHost.ExecuteActionAsync(pluginId, actionId, parameters, ct);
            return (result.Success, result.Message);
        }

        public async Task<string> RunReceiverCommandAsync(string command, string value, CancellationToken ct)
        {
            var receiver = engine._receiverController;
            switch (command)
            {
                case "power_on":
                    await receiver.SetPowerAsync(true, ct);
                    return "Receiver on";
                case "power_off":
                    await receiver.SetPowerAsync(false, ct);
                    return "Receiver standby";
                case "mute":
                    await receiver.SetMuteAsync(true, ct);
                    return "Receiver muted";
                case "unmute":
                    await receiver.SetMuteAsync(false, ct);
                    return "Receiver unmuted";
                case "volume":
                {
                    var requested = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    var ceiling = Math.Clamp(engine._settingsStore.Current.Mcp.MaxSafeVolume, 15, 98);
                    var level = Math.Clamp(requested, 0, ceiling);
                    await receiver.SetVolumeAsync(level, ct);
                    return level < requested
                        ? $"Receiver volume {DenonVolumeMath.FormatDecibels(level)} (capped from {requested} at the safe volume {ceiling})"
                        : $"Receiver volume {DenonVolumeMath.FormatDecibels(level)}";
                }
                case "input":
                    await receiver.SetInputSourceAsync(value, ct);
                    return $"Receiver input {DenonCatalog.GetFriendlyInputName(value)}";
                case "sound_mode":
                    await receiver.SetSoundModeAsync(value, ct);
                    return $"Receiver sound mode {value}";
                default:
                    throw new InvalidOperationException($"Unknown receiver command '{command}'.");
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

        private ScenePreset? FindScene(string sceneId) =>
            engine._settingsStore.Current.Receiver.Scenes.FirstOrDefault(s => string.Equals(s.Id, sceneId, StringComparison.OrdinalIgnoreCase));
    }
}
