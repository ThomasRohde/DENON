using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Microsoft.Extensions.Hosting;

namespace Denon.App.Core.Services;

public sealed record AutomationActivityEntry(DateTimeOffset Timestamp, string RuleName, string TriggerDetail, bool Success);

public sealed class AutomationEngineService : BackgroundService
{
    /// <summary>RoomEvent rules wait at least this long between runs, whatever their cooldown says.</summary>
    public static readonly TimeSpan MinRoomEventCooldown = TimeSpan.FromSeconds(10);

    /// <summary>More event-triggered runs than this within <see cref="LoopWindow"/> pauses event triggers.</summary>
    public const int LoopRunLimit = 20;

    public static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(1);

    private readonly AppSettingsStore _settingsStore;
    private readonly ReceiverController _receiverController;
    private readonly PluginHostService _pluginHost;
    private readonly EventTelemetryLog _telemetry;
    private readonly RoomEventBus _eventBus;
    private readonly AppLogStore _logStore;

    private readonly Dictionary<string, DateTimeOffset> _lastAppTriggerByRule = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateOnly> _lastScheduleDayByRule = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastEventTriggerByRule = new(StringComparer.OrdinalIgnoreCase);
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
        PluginHostService pluginHost,
        EventTelemetryLog telemetry,
        RoomEventBus eventBus,
        AppLogStore logStore)
    {
        _settingsStore = settingsStore;
        _receiverController = receiverController;
        _pluginHost = pluginHost;
        _telemetry = telemetry;
        _eventBus = eventBus;
        _logStore = logStore;
    }

    public event Action<AutomationActivityEntry>? ActivityLogged;

    /// <summary>The latest room events from the live bus, newest first, for the rule editor.</summary>
    public IReadOnlyList<RoomEvent> GetRecentEvents(int max = 20) => _eventBus.GetRecent(max);

    public async Task<string> TriggerRuleNowAsync(
        AutomationRule rule,
        string triggerDetail = "Manual test",
        CancellationToken cancellationToken = default)
    {
        var executedParts = new List<string>();
        var success = true;

        try
        {
            _telemetry.MarkOrigin("avr", "automation", "automation.triggered", new Dictionary<string, object?>
            {
                ["ruleName"] = rule.Name,
                ["trigger"] = triggerDetail,
            });
            _telemetry.MarkOrigin("tv", "automation");
            _telemetry.MarkOrigin("lighting", "automation");

            ScenePreset? targetScene = null;
            if (!string.IsNullOrWhiteSpace(rule.TargetSceneId))
            {
                targetScene = _settingsStore.Current.Receiver.Scenes
                    .FirstOrDefault(s => string.Equals(s.Id, rule.TargetSceneId, StringComparison.OrdinalIgnoreCase));
            }

            if (targetScene is not null)
            {
                try
                {
                    await _receiverController.ApplySceneAsync(targetScene, cancellationToken);
                    executedParts.Add($"Scene '{targetScene.Name}'");
                }
                catch (Exception ex)
                {
                    success = false;
                    executedParts.Add($"Scene '{targetScene.Name}' skipped ({ex.Message})");
                    _logStore.Warning($"[Automation:{rule.Name}] Scene '{targetScene.Name}' failed: {ex.Message}");
                }
            }

            if (rule.PluginActions is { Count: > 0 })
            {
                // Deduplicate only IDENTICAL (pluginId, actionId, parameters) steps already run by targetScene
                // so distinct parameterized calls to the same action (e.g. two different Hue rooms) are preserved!
                static string BuildStepKey(ScenePluginActionStep step)
                {
                    var paramKey = step.Parameters is { Count: > 0 }
                        ? string.Join("&", step.Parameters
                            .OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(static kv => $"{kv.Key.Trim().ToLowerInvariant()}={kv.Value?.Trim()}"))
                        : string.Empty;
                    return $"{step.PluginId}:{step.ActionId}:{paramKey}";
                }

                var sceneActionKeys = new HashSet<string>(
                    (targetScene?.PluginActions ?? []).Select(BuildStepKey),
                    StringComparer.OrdinalIgnoreCase);

                var extraSteps = rule.PluginActions
                    .Where(a => !sceneActionKeys.Contains(BuildStepKey(a)))
                    .ToList();

                if (extraSteps.Count > 0)
                {
                    var outcomes = await _pluginHost.ExecuteActionChainAsync(extraSteps, cancellationToken);
                    var okCount = outcomes.Count(static o => o.Result.Success);
                    var failCount = outcomes.Count - okCount;
                    if (failCount > 0)
                    {
                        success = false;
                        var firstErr = outcomes.First(static o => !o.Result.Success).Result.Message;
                        executedParts.Add($"{okCount}/{outcomes.Count} plugin step(s) ok ({failCount} failed: {firstErr})");
                    }
                    else
                    {
                        executedParts.Add($"{okCount} plugin step(s)");
                    }
                }
            }

            var summary = executedParts.Count > 0
                ? $"{triggerDetail} → {string.Join(" + ", executedParts)}"
                : $"{triggerDetail} → no actions configured";

            _logStore.Info($"[Automation] {rule.Name}: {summary}");
            ActivityLogged?.Invoke(new AutomationActivityEntry(DateTimeOffset.Now, rule.Name, summary, success));
            return summary;
        }
        catch (Exception ex)
        {
            var err = $"{triggerDetail} failed: {ex.Message}";
            _logStore.Warning($"[Automation] {rule.Name}: {err}");
            ActivityLogged?.Invoke(new AutomationActivityEntry(DateTimeOffset.Now, rule.Name, err, false));
            return err;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Room events and webhooks: the bus handler only queues; one loop runs the rules in order.
        using var subscription = _eventBus.Subscribe(e => _pendingEvents.Writer.TryWrite(e));
        var eventLoop = RunEventTriggersAsync(stoppingToken);

        try
        {
            // Let app finish startup and establish an initial process baseline so already-running apps don't fire immediately
            await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
            _previousProcessNames = SnapshotRunningProcessNames();
            _hasBaselineSnapshot = true;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await EvaluateRulesTickAsync(stoppingToken);
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
            try
            {
                await HandleRoomEventAsync(roomEvent, DateTimeOffset.Now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logStore.Warning($"[Automation] Event trigger failed on {roomEvent.Event}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Runs every enabled RoomEvent or Webhook rule that matches <paramref name="roomEvent"/> and is out of its
    /// cooldown. Returns the names of the rules it ran. Public for tests.
    /// </summary>
    public async Task<IReadOnlyList<string>> HandleRoomEventAsync(RoomEvent roomEvent, DateTimeOffset now, CancellationToken ct = default)
    {
        var rules = (_settingsStore.Current.Automations ?? [])
            .Where(r => r.IsEnabled &&
                        r.TriggerType is AutomationTriggerType.RoomEvent or AutomationTriggerType.Webhook &&
                        AutomationTriggers.Matches(r, roomEvent))
            .ToList();
        if (rules.Count == 0)
        {
            return [];
        }

        var ran = new List<string>();
        foreach (var rule in rules)
        {
            var isWebhook = rule.TriggerType == AutomationTriggerType.Webhook;
            var cooldown = TimeSpan.FromSeconds(Math.Clamp(rule.CooldownSeconds, 0, 86400));
            if (!isWebhook && cooldown < MinRoomEventCooldown)
            {
                cooldown = MinRoomEventCooldown;
            }

            if (_lastEventTriggerByRule.TryGetValue(rule.Id, out var last) && now - last < cooldown)
            {
                continue;
            }

            if (!isWebhook && !AllowEventRun(now, rule.Name))
            {
                continue;
            }

            _lastEventTriggerByRule[rule.Id] = now;
            var detail = isWebhook
                ? $"Webhook '{roomEvent.GetText("hook")}'"
                : $"Event {AutomationTriggers.Describe(roomEvent)}";
            await TriggerRuleNowAsync(rule, detail, ct);
            ran.Add(rule.Name);
        }

        return ran;
    }

    /// <summary>
    /// Loop breaker: rules that react to room events can set off each other (scene → power.changed → scene).
    /// After <see cref="LoopRunLimit"/> event-triggered runs within <see cref="LoopWindow"/>, event triggers
    /// pause for one window. Webhooks are not counted: each one is a call from outside.
    /// </summary>
    private bool AllowEventRun(DateTimeOffset now, string ruleName)
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
            var message = $"Paused event triggers for {LoopWindow.TotalSeconds:0} s: {LoopRunLimit} runs in {LoopWindow.TotalSeconds:0} s looks like a loop (last: '{ruleName}').";
            _logStore.Warning($"[Automation] {message}");
            ActivityLogged?.Invoke(new AutomationActivityEntry(now, "Loop guard", message, false));
            return false;
        }

        _recentEventRuns.Enqueue(now);
        return true;
    }

    private async Task EvaluateRulesTickAsync(CancellationToken ct)
    {
        var rules = _settingsStore.Current.Automations;
        if (rules is null || rules.Count == 0)
        {
            return;
        }

        var enabledRules = rules.Where(static r => r.IsEnabled).ToList();
        if (enabledRules.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);

        // 1. Evaluate AppLaunch triggers if any are enabled
        var appRules = enabledRules.Where(static r => r.TriggerType == AutomationTriggerType.AppLaunch).ToList();
        if (appRules.Count > 0)
        {
            var currentProcesses = SnapshotRunningProcessNames();
            if (_hasBaselineSnapshot)
            {
                foreach (var rule in appRules)
                {
                    var watched = ParseProcessList(rule.ProcessNames);
                    if (watched.Count == 0)
                    {
                        continue;
                    }

                    var newlyLaunched = watched.FirstOrDefault(proc =>
                        currentProcesses.Contains(proc) && !_previousProcessNames.Contains(proc));

                    if (newlyLaunched is null)
                    {
                        continue;
                    }

                    var cooldown = TimeSpan.FromSeconds(Math.Clamp(rule.CooldownSeconds, 10, 86400));
                    if (_lastAppTriggerByRule.TryGetValue(rule.Id, out var lastFired) &&
                        (now - lastFired) < cooldown)
                    {
                        continue;
                    }

                    _lastAppTriggerByRule[rule.Id] = now;
                    _ = TriggerRuleNowAsync(rule, $"Detected '{newlyLaunched}.exe' launch", ct);
                }
            }

            _previousProcessNames = currentProcesses;
            _hasBaselineSnapshot = true;
        }

        // 2. Evaluate TimeOfDay triggers
        var scheduleRules = enabledRules.Where(static r => r.TriggerType == AutomationTriggerType.TimeOfDay).ToList();
        foreach (var rule in scheduleRules)
        {
            if (!MatchesDaysFilter(rule.DaysFilter, now.DayOfWeek))
            {
                continue;
            }

            if (!TimeOnly.TryParseExact(rule.ScheduleTime?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var scheduledTime))
            {
                continue;
            }

            var currentTime = TimeOnly.FromDateTime(now.LocalDateTime);
            if (currentTime.Hour == scheduledTime.Hour && currentTime.Minute == scheduledTime.Minute)
            {
                if (_lastScheduleDayByRule.TryGetValue(rule.Id, out var lastDay) && lastDay == today)
                {
                    continue;
                }

                _lastScheduleDayByRule[rule.Id] = today;
                await TriggerRuleNowAsync(rule, $"Scheduled {rule.ScheduleTime} ({rule.DaysFilter})", ct);
            }
        }
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

    private static List<string> ParseProcessList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesDaysFilter(string? filter, DayOfWeek day) =>
        (filter?.Trim().ToLowerInvariant()) switch
        {
            "weekdays" => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
            "weekends" => day is DayOfWeek.Saturday or DayOfWeek.Sunday,
            _ => true,
        };
}
