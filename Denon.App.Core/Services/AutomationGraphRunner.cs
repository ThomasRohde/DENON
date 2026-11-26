using System.Globalization;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

/// <summary>What a run did at one node. <see cref="Outcome"/>: ran, yes, no, failed, skipped or would-run.</summary>
public sealed record AutomationStep(string NodeId, string Outcome, string Message);

public sealed record AutomationRun(
    DateTimeOffset At,
    string GraphId,
    string GraphName,
    string Trigger,
    bool DryRun,
    bool Success,
    string Summary,
    IReadOnlyList<AutomationStep> Steps);

/// <summary>The receiver as a condition sees it. Null values are unknown (no connection yet).</summary>
public sealed record ReceiverFacts(bool? IsOn, string? Input, bool? IsMuted);

/// <summary>What a run can read and change. The engine passes the real room; tests pass a fake.</summary>
public interface IAutomationEffects
{
    ReceiverFacts GetReceiverFacts();

    string DescribeScene(string sceneId);

    string DescribePluginAction(string pluginId, string actionId);

    /// <summary>Runs a Smart Scene. Throws when it fails.</summary>
    Task RunSceneAsync(string sceneId, CancellationToken ct);

    Task<(bool Success, string Message)> RunPluginActionAsync(string pluginId, string actionId, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct);

    /// <summary>Sends one receiver command and returns what it did. Throws when it fails.</summary>
    Task<string> RunReceiverCommandAsync(string command, string value, CancellationToken ct);

    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

/// <summary>Walks an automation graph from one trigger. See docs/automation-graphs.md, "How a run works".</summary>
public static class AutomationGraphRunner
{
    public const int MaxSteps = 100;

    public static async Task<AutomationRun> RunAsync(
        AutomationGraph graph,
        AutomationNode trigger,
        RoomEvent? roomEvent,
        string triggerDetail,
        IAutomationEffects effects,
        bool dryRun,
        DateTimeOffset now,
        CancellationToken ct,
        Func<TimeSpan>? elapsed = null)
    {
        // Conditions are checked when they are reached: a Wait or a slow step before a time window moves the clock.
        // `now` is the run's start; `elapsed` is the real time since then (tests can pass their own).
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        elapsed ??= () => System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var steps = new List<AutomationStep> { new(trigger.Id, dryRun ? "would-run" : "ran", triggerDetail) };
        var done = new List<string>();
        var success = true;
        var stoppedAtLimit = false;
        var eventForConditions = roomEvent ?? new RoomEvent(now, "control", "automation", "manual", "desktop_ui", null);

        async Task FollowAsync(AutomationNode node, string port)
        {
            foreach (var edge in graph.Edges.Where(e => e.From == node.Id && e.Port == port).ToList())
            {
                if (graph.FindNode(edge.To) is { } next)
                {
                    await VisitAsync(next);
                }
            }
        }

        async Task VisitAsync(AutomationNode node)
        {
            ct.ThrowIfCancellationRequested();
            if (steps.Count >= MaxSteps)
            {
                stoppedAtLimit = true;
                return;
            }

            if (AutomationNodeTypes.IsCondition(node.Type))
            {
                var (pass, why) = Evaluate(node, eventForConditions, effects, now + elapsed());
                steps.Add(new(node.Id, pass ? "yes" : "no", why));
                await FollowAsync(node, pass ? AutomationNodeTypes.PortYes : AutomationNodeTypes.PortNo);
                return;
            }

            if (AutomationNodeTypes.Find(node.Type)?.Group != AutomationNodeTypes.GroupAction)
            {
                steps.Add(new(node.Id, "skipped", "Not an action or a condition."));
                return;
            }

            try
            {
                var message = dryRun ? Describe(node, effects) : await ExecuteAsync(node, effects, ct);
                steps.Add(new(node.Id, dryRun ? "would-run" : "ran", message));
                if (node.Type != AutomationNodeTypes.ActionDelay)
                {
                    done.Add(message);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                success = false;
                steps.Add(new(node.Id, "failed", ex.Message));
                return; // A failed step ends its branch.
            }

            await FollowAsync(node, AutomationNodeTypes.PortOut);
        }

        await FollowAsync(trigger, AutomationNodeTypes.PortOut);

        if (stoppedAtLimit)
        {
            success = false;
            steps.Add(new(trigger.Id, "failed", $"Stopped after {MaxSteps} steps."));
        }

        var failed = steps.Where(static s => s.Outcome == "failed").Select(static s => s.Message).FirstOrDefault();
        // Nothing ran: the last check that answered says why (a "no", or a "yes" with nothing connected to it).
        var stoppedBy = done.Count == 0 ? steps.LastOrDefault(static s => s.Outcome is "no" or "yes") : null;
        var summary = failed is not null
            ? $"{triggerDetail} → failed: {failed}"
            : done.Count > 0
                ? $"{triggerDetail} → {(dryRun ? "would run " : string.Empty)}{string.Join(" + ", done)}"
                : stoppedBy is not null
                    ? $"{triggerDetail} → nothing to do: {stoppedBy.Message}"
                    : $"{triggerDetail} → no actions connected";

        return new AutomationRun(now, graph.Id, graph.Name, triggerDetail, dryRun, success, summary, steps);
    }

    /// <summary>Checks a condition node. Returns the answer and a short reason.</summary>
    public static (bool Pass, string Why) Evaluate(AutomationNode node, RoomEvent roomEvent, IAutomationEffects effects, DateTimeOffset now)
    {
        switch (node.Type)
        {
            case AutomationNodeTypes.ConditionEvent:
            {
                var filter = node.Get("filter");
                var pass = AutomationTriggers.FilterMatches(roomEvent, AutomationTriggers.ParseFilter(filter));
                return (pass, pass ? $"The event data matches {filter}." : $"The event data does not match {filter}.");
            }

            case AutomationNodeTypes.ConditionTime:
            {
                var local = now.LocalDateTime;
                if (!AutomationTriggers.MatchesDays(node.Get("days"), local.DayOfWeek))
                {
                    return (false, $"Not on {node.Get("days")}.");
                }

                if (!AutomationGraphValidator.TryParseTime(node.Get("from"), out var from) ||
                    !AutomationGraphValidator.TryParseTime(node.Get("to"), out var to))
                {
                    return (false, "The time window is not set.");
                }

                var time = TimeOnly.FromDateTime(local);
                var inside = from <= to ? time >= from && time < to : time >= from || time < to;
                var window = $"{from:HH\\:mm}–{to:HH\\:mm}";
                return (inside, inside ? $"{time:HH\\:mm} is inside {window}." : $"{time:HH\\:mm} is outside {window}.");
            }

            case AutomationNodeTypes.ConditionReceiver:
            {
                var facts = effects.GetReceiverFacts();
                var reasons = new List<string>();
                var pass = true;

                switch (node.Get("power"))
                {
                    case "on" or "standby" when facts.IsOn is null:
                        return (false, "The receiver state is not known yet.");
                    case "on" when facts.IsOn == false:
                        pass = false; reasons.Add("the receiver is in standby");
                        break;
                    case "standby" when facts.IsOn == true:
                        pass = false; reasons.Add("the receiver is on");
                        break;
                }

                var input = node.Get("input");
                if (input.Length > 0 && !DenonCatalog.InputMatches(facts.Input, input))
                {
                    pass = false; reasons.Add($"the input is {facts.Input ?? "unknown"}, not {input}");
                }

                switch (node.Get("muted"))
                {
                    case "yes" when facts.IsMuted != true:
                        pass = false; reasons.Add(facts.IsMuted is null ? "the mute state is not known" : "the receiver is not muted");
                        break;
                    case "no" when facts.IsMuted != false:
                        // Unknown (no connection yet) is not "not muted", as unknown is not "muted" above.
                        pass = false; reasons.Add(facts.IsMuted is null ? "the mute state is not known" : "the receiver is muted");
                        break;
                }

                if (pass)
                {
                    // Say what matched, so "nothing to do" after a yes with nothing connected reads well.
                    var matched = new List<string>();
                    if (node.Get("power") is "on" or "standby")
                    {
                        matched.Add(facts.IsOn == true ? "the receiver is on" : "the receiver is in standby");
                    }

                    if (input.Length > 0)
                    {
                        matched.Add($"the input is {facts.Input}");
                    }

                    if (node.Get("muted") is "yes" or "no")
                    {
                        matched.Add(facts.IsMuted == true ? "it is muted" : "it is not muted");
                    }

                    return (true, matched.Count == 0 ? "The receiver matches." : "Yes: " + string.Join(", ", matched) + ".");
                }

                return (false, "No: " + string.Join(", ", reasons) + ".");
            }

            default:
                return (false, $"Unknown condition '{node.Type}'.");
        }
    }

    private static string Describe(AutomationNode node, IAutomationEffects effects) =>
        node.Type switch
        {
            AutomationNodeTypes.ActionScene => $"Scene '{effects.DescribeScene(node.Get("sceneId"))}'",
            AutomationNodeTypes.ActionPlugin => effects.DescribePluginAction(node.Get("pluginId"), node.Get("actionId")),
            AutomationNodeTypes.ActionReceiver => $"Receiver {node.Get("command").Replace('_', ' ')} {node.Get("value")}".TrimEnd(),
            AutomationNodeTypes.ActionDelay => $"Wait {node.Get("seconds")} s",
            _ => node.Type,
        };

    private static async Task<string> ExecuteAsync(AutomationNode node, IAutomationEffects effects, CancellationToken ct)
    {
        switch (node.Type)
        {
            case AutomationNodeTypes.ActionScene:
                await effects.RunSceneAsync(node.Get("sceneId"), ct);
                return $"Scene '{effects.DescribeScene(node.Get("sceneId"))}'";

            case AutomationNodeTypes.ActionPlugin:
            {
                var parameters = node.Config
                    .Where(static kv => kv.Key.StartsWith(AutomationNodeTypes.ParameterPrefix, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(kv.Value))
                    .ToDictionary(static kv => kv.Key[AutomationNodeTypes.ParameterPrefix.Length..], static kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);
                var (ok, message) = await effects.RunPluginActionAsync(node.Get("pluginId"), node.Get("actionId"), parameters.Count > 0 ? parameters : null, ct);
                var label = effects.DescribePluginAction(node.Get("pluginId"), node.Get("actionId"));
                return ok ? label : throw new InvalidOperationException($"{label}: {message}");
            }

            case AutomationNodeTypes.ActionReceiver:
                return await effects.RunReceiverCommandAsync(node.Get("command"), node.Get("value"), ct);

            case AutomationNodeTypes.ActionDelay:
            {
                var seconds = Math.Clamp(int.TryParse(node.Get("seconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 1, 1, 600);
                await effects.DelayAsync(TimeSpan.FromSeconds(seconds), ct);
                return $"Waited {seconds} s";
            }

            default:
                throw new InvalidOperationException($"Unknown action '{node.Type}'.");
        }
    }
}
