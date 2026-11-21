using System.Diagnostics;
using System.Globalization;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Microsoft.Extensions.Hosting;

namespace Denon.App.Core.Services;

public sealed record AutomationActivityEntry(DateTimeOffset Timestamp, string RuleName, string TriggerDetail, bool Success);

public sealed class AutomationEngineService : BackgroundService
{
    private readonly AppSettingsStore _settingsStore;
    private readonly ReceiverController _receiverController;
    private readonly PluginHostService _pluginHost;
    private readonly AppLogStore _logStore;

    private readonly Dictionary<string, DateTimeOffset> _lastAppTriggerByRule = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateOnly> _lastScheduleDayByRule = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _previousProcessNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasBaselineSnapshot;

    public AutomationEngineService(
        AppSettingsStore settingsStore,
        ReceiverController receiverController,
        PluginHostService pluginHost,
        AppLogStore logStore)
    {
        _settingsStore = settingsStore;
        _receiverController = receiverController;
        _pluginHost = pluginHost;
        _logStore = logStore;
    }

    public event Action<AutomationActivityEntry>? ActivityLogged;

    public async Task<string> TriggerRuleNowAsync(
        AutomationRule rule,
        string triggerDetail = "Manual test",
        CancellationToken cancellationToken = default)
    {
        var executedParts = new List<string>();
        var success = true;

        try
        {
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
                // Avoid re-running an identical plugin action if the target scene already executed it
                var sceneActionKeys = new HashSet<string>(
                    (targetScene?.PluginActions ?? []).Select(static a => $"{a.PluginId}:{a.ActionId}"),
                    StringComparer.OrdinalIgnoreCase);

                var extraSteps = rule.PluginActions
                    .Where(a => !sceneActionKeys.Contains($"{a.PluginId}:{a.ActionId}"))
                    .ToList();

                if (extraSteps.Count > 0)
                {
                    await _pluginHost.ExecuteActionChainAsync(extraSteps, cancellationToken);
                    executedParts.Add($"{extraSteps.Count} plugin step(s)");
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
        // Let app finish startup and establish an initial process baseline so already-running apps don't fire immediately
        await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
        _previousProcessNames = SnapshotRunningProcessNames();
        _hasBaselineSnapshot = true;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await EvaluateRulesTickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
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
