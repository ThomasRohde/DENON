using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.PluginSdk;
using Microsoft.Extensions.Hosting;

namespace Denon.App.Core.Services;

/// <summary>
/// A line for the automation activity log. <see cref="Skipped"/>: the trigger fired but the automation did not run
/// (its cooldown), which is neither a success nor a failure.
/// </summary>
public enum TriggerOutcomeKind
{
    /// <summary>The run started; <see cref="TriggerOutcome.Run"/> completes with its result.</summary>
    Started,

    /// <summary>The cooldown held it back until <see cref="TriggerOutcome.Until"/>.</summary>
    Skipped,

    /// <summary>The loop guard has paused event triggers.</summary>
    Paused,
}

/// <summary>What one automation did with one trigger.</summary>
public sealed record TriggerOutcome(AutomationGraph Graph, TriggerOutcomeKind Kind, DateTimeOffset? Until, Task<AutomationRun>? Run);

public sealed record AutomationActivityEntry(DateTimeOffset Timestamp, string RuleName, string TriggerDetail, bool Success, bool Skipped = false);

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
    private readonly Dictionary<string, DateTimeOffset> _lastSkipNoticeByGraph = new(StringComparer.Ordinal);
    /// <summary>One ticket per event-started run in the loop window; under <see cref="_gate"/> (runs finish on other threads).</summary>
    private readonly List<LoopTicket> _recentEventRuns = [];
    private readonly Channel<RoomEvent> _pendingEvents = Channel.CreateBounded<RoomEvent>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private HashSet<string> _previousProcessNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasBaselineSnapshot;
    private DateTimeOffset _eventTriggersPausedUntil = DateTimeOffset.MinValue;
    private CancellationToken _stopping = CancellationToken.None;
    private readonly VolumeMemoryService? _volumeMemory;

    public AutomationEngineService(
        AppSettingsStore settingsStore,
        ReceiverController receiverController,
        ReceiverStateStore stateStore,
        PluginHostService pluginHost,
        EventTelemetryLog telemetry,
        RoomEventBus eventBus,
        AppLogStore logStore,
        VolumeMemoryService? volumeMemory = null)
    {
        _volumeMemory = volumeMemory;
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
    public bool EventTriggersPaused
    {
        get
        {
            lock (_gate)
            {
                return DateTimeOffset.Now < _eventTriggersPausedUntil;
            }
        }
    }

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
        _stopping = stoppingToken;
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
            // A webhook call is started by the webhook API itself (HandleWebhookCall), which answers with the outcome.
            if (roomEvent.Event == AutomationTriggers.WebhookEventName && roomEvent.Origin == AutomationTriggers.WebhookOrigin)
            {
                continue;
            }

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
    public Task<IReadOnlyList<AutomationRun>> HandleRoomEventAsync(RoomEvent roomEvent, DateTimeOffset now, CancellationToken ct = default) =>
        WhenAllRuns(StartForEvent(roomEvent, now, ct).Where(static o => o.Run is not null).Select(static o => o.Run!).ToList());

    /// <summary>
    /// A webhook call (POST /api/hooks/{name}): starts the listening automations at once and says what each one did,
    /// so the caller (an iPhone Shortcut) can show it. The engine does not also take this event from the bus.
    /// </summary>
    public IReadOnlyList<TriggerOutcome> HandleWebhookCall(RoomEvent webhookEvent) =>
        StartForEvent(webhookEvent, DateTimeOffset.Now, _stopping);

    private List<TriggerOutcome> StartForEvent(RoomEvent roomEvent, DateTimeOffset now, CancellationToken ct)
    {
        var outcomes = new List<TriggerOutcome>();
        foreach (var graph in EnabledGraphs())
        {
            var trigger = graph.Nodes.FirstOrDefault(n => AutomationTriggers.Matches(n, roomEvent));
            if (trigger is null)
            {
                continue;
            }

            var isWebhook = trigger.Type == AutomationNodeTypes.TriggerWebhook;
            var minimum = isWebhook ? TimeSpan.Zero : MinEventCooldown;
            var detail = isWebhook
                ? $"Webhook '{roomEvent.GetText("hook")}'"
                : $"Event {AutomationTriggers.Describe(roomEvent)}";
            if (!IsOutOfCooldown(graph, now, minimum))
            {
                ReportCooldownSkip(graph, now, minimum, detail);
                outcomes.Add(new TriggerOutcome(graph, TriggerOutcomeKind.Skipped, CooldownUntil(graph, minimum), null));
                continue;
            }

            LoopTicket? ticket = null;
            if (!isWebhook && (ticket = AllowEventRun(now, graph.Name)) is null)
            {
                outcomes.Add(new TriggerOutcome(graph, TriggerOutcomeKind.Paused, null, null));
                continue;
            }

            if (!TryMarkRun(graph, now, minimum, out var mark))
            {
                ReturnLoopTicket(ticket); // A schedule or app start of this graph got there first: no run, no count.
                ReportCooldownSkip(graph, now, minimum, detail);
                outcomes.Add(new TriggerOutcome(graph, TriggerOutcomeKind.Skipped, CooldownUntil(graph, minimum), null));
                continue;
            }

            outcomes.Add(new TriggerOutcome(graph, TriggerOutcomeKind.Started, null,
                RunAsync(graph, trigger, roomEvent, detail, dryRun: false, ct, now, mark, ticket)));
        }

        return outcomes;
    }

    /// <summary>When the graph's cooldown ends, from its last run (null when it has none).</summary>
    private DateTimeOffset? CooldownUntil(AutomationGraph graph, TimeSpan minimum)
    {
        lock (_gate)
        {
            if (!_lastRunByGraph.TryGetValue(graph.Id, out var last))
            {
                return null;
            }

            var cooldown = TimeSpan.FromSeconds(Math.Clamp(graph.CooldownSeconds, 0, 86400));
            return last + (cooldown < minimum ? minimum : cooldown);
        }
    }

    /// <summary>True when the run changed the room (a scene, plugin or receiver step ran or failed). Public for the webhook reply.</summary>
    public static bool RunActed(AutomationGraph graph, AutomationRun run) => ActedOnTheRoom(graph, run);

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
                    if (started is null)
                    {
                        continue;
                    }

                    var detail = $"'{started}.exe' started";
                    if (TryMarkRun(graph, now, MinAppLaunchCooldown, out var mark))
                    {
                        runs.Add(RunAsync(graph, node, null, detail, dryRun: false, ct, now, mark));
                    }
                    else
                    {
                        ReportCooldownSkip(graph, now, MinAppLaunchCooldown, detail);
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
                var scheduled = $"Scheduled {at:HH\\:mm}";
                if (!TryMarkRun(graph, now, TimeSpan.Zero, out var mark))
                {
                    ReportCooldownSkip(graph, now, TimeSpan.Zero, scheduled);
                    continue;
                }

                runs.Add(RunAsync(graph, node, null, scheduled, dryRun: false, ct, now, mark));
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
    private bool TryMarkRun(AutomationGraph graph, DateTimeOffset now, TimeSpan minimum, out CooldownMark mark)
    {
        lock (_gate)
        {
            mark = new CooldownMark(now, _lastRunByGraph.TryGetValue(graph.Id, out var previous) ? previous : null);
            if (!IsOutOfCooldownLocked(graph, now, minimum))
            {
                return false;
            }

            _lastRunByGraph[graph.Id] = now;
            return true;
        }
    }

    /// <summary>The cooldown mark a run set, and the one before it, so a run that changed nothing can give it back.</summary>
    private readonly record struct CooldownMark(DateTimeOffset At, DateTimeOffset? Previous);

    /// <summary>
    /// Gives the cooldown back after a run that changed nothing (its checks said no, or it only waited): the cooldown
    /// is there to space out actions, so a test or a "no" must not block the next real run. Only undone while the
    /// mark is still this run's own.
    /// </summary>
    private void ReleaseCooldownMark(string graphId, CooldownMark mark)
    {
        lock (_gate)
        {
            if (!_lastRunByGraph.TryGetValue(graphId, out var current) || current != mark.At)
            {
                return;
            }

            if (mark.Previous is { } previous)
            {
                _lastRunByGraph[graphId] = previous;
            }
            else
            {
                _lastRunByGraph.Remove(graphId);
            }
        }
    }

    /// <summary>True when a scene, plugin or receiver step ran (or failed trying). Waits and checks do not count.</summary>
    private static bool ActedOnTheRoom(AutomationGraph graph, AutomationRun run) =>
        run.Steps.Any(step => step.Outcome is "ran" or "failed" &&
                              graph.FindNode(step.NodeId) is { } node &&
                              AutomationNodeTypes.Find(node.Type)?.Group == AutomationNodeTypes.GroupAction &&
                              node.Type != AutomationNodeTypes.ActionDelay);

    /// <summary>
    /// Says in the activity log that the cooldown blocked a trigger, so a webhook or event that seems to do nothing
    /// can be explained. At most once a minute per automation, so a busy event trigger cannot flood the log.
    /// </summary>
    private void ReportCooldownSkip(AutomationGraph graph, DateTimeOffset now, TimeSpan minimum, string detail)
    {
        DateTimeOffset last, until;
        lock (_gate)
        {
            if (!_lastRunByGraph.TryGetValue(graph.Id, out last) ||
                (_lastSkipNoticeByGraph.TryGetValue(graph.Id, out var lastNotice) && now - lastNotice < TimeSpan.FromMinutes(1)))
            {
                return;
            }

            _lastSkipNoticeByGraph[graph.Id] = now;
            var cooldown = TimeSpan.FromSeconds(Math.Clamp(graph.CooldownSeconds, 0, 86400));
            until = last + (cooldown < minimum ? minimum : cooldown);
        }

        var message = $"{detail} → skipped: in its cooldown until {until.LocalDateTime:HH:mm:ss} (last run {last.LocalDateTime:HH:mm:ss}).";
        _logStore.Info($"[Automation] {graph.Name}: {message}");
        ActivityLogged?.Invoke(new AutomationActivityEntry(now, graph.Name, message, Success: false, Skipped: true));
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
    /// one window. Webhooks are not counted: each one is a call from outside. A run that changed nothing gives its
    /// ticket back (<see cref="ReturnLoopTicket"/>): it cannot have caused a loop, and a check that keeps saying no must
    /// not pause every other event automation. Returns the run's ticket, or null when event triggers are paused.
    /// </summary>
    private LoopTicket? AllowEventRun(DateTimeOffset now, string graphName)
    {
        string? pausedMessage = null;
        lock (_gate)
        {
            if (now < _eventTriggersPausedUntil)
            {
                return null;
            }

            _recentEventRuns.RemoveAll(t => now - t.At > LoopWindow);
            if (_recentEventRuns.Count < LoopRunLimit)
            {
                var ticket = new LoopTicket(now);
                _recentEventRuns.Add(ticket);
                return ticket;
            }

            _eventTriggersPausedUntil = now + LoopWindow;
            _recentEventRuns.Clear();
            pausedMessage = $"Paused event triggers for {LoopWindow.TotalSeconds:0} s: {LoopRunLimit} runs in {LoopWindow.TotalSeconds:0} s looks like a loop (last: '{graphName}').";
        }

        _logStore.Warning($"[Automation] {pausedMessage}");
        ActivityLogged?.Invoke(new AutomationActivityEntry(now, "Loop guard", pausedMessage, false));
        return null;
    }

    private void ReturnLoopTicket(LoopTicket? ticket)
    {
        if (ticket is null)
        {
            return;
        }

        lock (_gate)
        {
            _recentEventRuns.Remove(ticket);
        }
    }

    /// <summary>One event-started run counted by the loop guard (a reference, so the same run's count can be returned).</summary>
    private sealed class LoopTicket(DateTimeOffset at)
    {
        public DateTimeOffset At { get; } = at;
    }

    private async Task<AutomationRun> RunAsync(
        AutomationGraph graph,
        AutomationNode trigger,
        RoomEvent? roomEvent,
        string detail,
        bool dryRun,
        CancellationToken ct,
        DateTimeOffset? at = null,
        CooldownMark? mark = null,
        LoopTicket? ticket = null)
    {
        var effects = dryRun ? EffectsOverride ?? _effects : new OriginStampingEffects(EffectsOverride ?? _effects, _telemetry, PluginDomains);

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

        // A run that changed nothing gives back its cooldown and its loop-guard count.
        if (!dryRun && !ActedOnTheRoom(graph, run))
        {
            if (mark is { } own)
            {
                ReleaseCooldownMark(graph.Id, own);
            }

            ReturnLoopTicket(ticket);
        }

        if (!dryRun)
        {
            // The event says what the run did (after it ran), so Insights counts only runs that acted. The "automation"
            // origin is stamped by each step right before it changes a domain (OriginStampingEffects): a run that
            // waits or whose checks say no must not hide the user's own changes from event triggers.
            var acted = ActedOnTheRoom(graph, run);
            _telemetry.Record(EventTelemetryLog.StreamControl, "automation", "automation.triggered", AutomationTriggers.AutomationOrigin, new Dictionary<string, object?>
            {
                ["automation"] = graph.Name,
                ["trigger"] = detail,
                ["outcome"] = !run.Success ? "failed" : acted ? "acted" : "nothing",
                ["summary"] = run.Summary,
            });

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
    /// <summary>
    /// The origin domains a plugin action can change: the plugin's own (tv for the LG TV, lighting for Hue, else
    /// plugin:&lt;id&gt;), plus the receiver only for a plugin that may control it (<see cref="PluginCapabilities.ReceiverControl"/>).
    /// A Hue-only action must not hide the user's own receiver power-on from event triggers.
    /// </summary>
    private IReadOnlyList<string> PluginDomains(string pluginId)
    {
        var domain = PluginHostService.MapPluginToDomain(pluginId);
        var record = _pluginHost.GetPlugins().FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        return record?.Capabilities.HasFlag(PluginCapabilities.ReceiverControl) == true
            ? [domain, EventTelemetryLog.StreamAvr]
            : [domain];
    }

    private sealed class OriginStampingEffects(
        IAutomationEffects inner,
        EventTelemetryLog telemetry,
        Func<string, IReadOnlyList<string>> pluginDomains) : IAutomationEffects
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
            Stamp([.. pluginDomains(pluginId)]);
            return inner.RunPluginActionAsync(pluginId, actionId, parameters, ct);
        }

        public Task<string> RunReceiverCommandAsync(string command, string value, CancellationToken ct)
        {
            Stamp(EventTelemetryLog.StreamAvr);
            return inner.RunReceiverCommandAsync(command, value, ct);
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => inner.DelayAsync(delay, ct);

        public Task<string> SetLearnedVolumeAsync(CancellationToken ct)
        {
            Stamp(EventTelemetryLog.StreamAvr);
            return inner.SetLearnedVolumeAsync(ct);
        }

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
                : new ReceiverFacts(
                    status is { IsPoweredOn: true, IsMainZoneOn: true }, status.InputSource, status.IsMuted,
                    HeosPlaying(status));
        }

        /// <summary>
        /// Null (unknown) without a play state, or when the AVR did not confirm the input: with port 23 down the
        /// status is a synthetic "on, NET" from HEOS alone, while the receiver may play TV audio.
        /// </summary>
        private static bool? HeosPlaying(ReceiverStatus status)
        {
            var playState = status.PlayState?.Trim();
            return !status.IsAvrConfirmed || string.IsNullOrWhiteSpace(playState)
                ? null
                : string.Equals(playState, "play", StringComparison.OrdinalIgnoreCase);
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

        public Task<string> SetLearnedVolumeAsync(CancellationToken ct) =>
            engine._volumeMemory?.ApplyLearnedNowAsync(AutomationTriggers.AutomationOrigin, ct)
            ?? throw new InvalidOperationException("Volume memory is not running.");

        private ScenePreset? FindScene(string sceneId) =>
            engine._settingsStore.Current.Receiver.Scenes.FirstOrDefault(s => string.Equals(s.Id, sceneId, StringComparison.OrdinalIgnoreCase));
    }
}
