using System.Globalization;
using System.Text.RegularExpressions;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

public sealed record AutomationProblem(string? NodeId, string Message);

/// <summary>Checks an automation graph before it is saved. See docs/automation-graphs.md.</summary>
public static partial class AutomationGraphValidator
{
    public const int MaxNodes = 60;

    /// <summary>
    /// Trims and normalizes <paramref name="graph"/> in place (name, hook names, cooldown) and returns what is wrong
    /// with it. An empty list means it can be saved.
    /// </summary>
    public static IReadOnlyList<AutomationProblem> Validate(AutomationGraph graph)
    {
        var problems = new List<AutomationProblem>();
        graph.Name = graph.Name?.Trim() ?? string.Empty;
        graph.Nodes ??= [];
        graph.Edges ??= [];
        graph.CooldownSeconds = Math.Clamp(graph.CooldownSeconds, 0, 86400);

        if (!IdPattern().IsMatch(graph.Id ?? string.Empty))
        {
            problems.Add(new(null, "The automation id must be 1–64 letters, digits, '-' or '_'."));
        }

        if (graph.Name.Length == 0)
        {
            problems.Add(new(null, "Give the automation a name."));
        }

        if (graph.Nodes.Count > MaxNodes)
        {
            problems.Add(new(null, $"An automation can have at most {MaxNodes} nodes."));
        }

        if (!graph.Nodes.Any(static n => AutomationNodeTypes.IsTrigger(n.Type)))
        {
            problems.Add(new(null, "Add a trigger: a room event, a webhook, a schedule or an app start."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            node.Config ??= [];
            if (!IdPattern().IsMatch(node.Id ?? string.Empty) || !ids.Add(node.Id!))
            {
                problems.Add(new(node.Id, "Each node needs its own id (letters, digits, '-' or '_')."));
                continue;
            }

            var info = AutomationNodeTypes.Find(node.Type);
            if (info is null)
            {
                problems.Add(new(node.Id, $"Unknown node type '{node.Type}'."));
                continue;
            }

            CheckFields(node, info, problems);
        }

        CheckEdges(graph, problems);
        return problems;
    }

    private static void CheckFields(AutomationNode node, AutomationNodeTypeInfo info, List<AutomationProblem> problems)
    {
        foreach (var field in info.Fields.Where(static f => f.Required))
        {
            var missing = field.Kind == "pluginAction"
                ? node.Get("pluginId").Length == 0 || node.Get("actionId").Length == 0
                : node.Get(field.Key).Length == 0;
            if (missing)
            {
                problems.Add(new(node.Id, $"{info.Title}: fill in {field.Label}."));
            }
        }

        foreach (var field in info.Fields.Where(static f => f.Kind == "time"))
        {
            var value = node.Get(field.Key);
            if (value.Length > 0 && !TryParseTime(value, out _))
            {
                problems.Add(new(node.Id, $"{info.Title}: {field.Label} must be a time like 22:00."));
            }
        }

        switch (node.Type)
        {
            case AutomationNodeTypes.TriggerWebhook:
            {
                var raw = node.Get("hook");
                if (raw.Length > 0)
                {
                    var hook = AutomationTriggers.NormalizeHookName(raw);
                    if (hook is null)
                    {
                        problems.Add(new(node.Id, "Webhook: the hook name may only have letters, digits, '-' or '_' (at most 64)."));
                    }
                    else
                    {
                        node.Config["hook"] = hook;
                    }
                }

                break;
            }

            case AutomationNodeTypes.ActionDelay:
                if (!int.TryParse(node.Get("seconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 600)
                {
                    problems.Add(new(node.Id, "Wait: seconds must be a whole number from 1 to 600."));
                }

                break;

            case AutomationNodeTypes.ActionReceiver:
            {
                var command = node.Get("command");
                var value = node.Get("value");
                switch (command)
                {
                    case "volume" when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume) || volume is < 0 or > 98:
                        problems.Add(new(node.Id, "Receiver: the volume must be a whole number from 0 to 98."));
                        break;
                    case "input" or "sound_mode" when value.Length == 0:
                        problems.Add(new(node.Id, $"Receiver: fill in the {(command == "input" ? "input code" : "sound mode")}."));
                        break;
                    case "" or "power_on" or "power_off" or "mute" or "unmute" or "volume" or "input" or "sound_mode":
                        break;
                    default:
                        problems.Add(new(node.Id, $"Receiver: unknown command '{command}'."));
                        break;
                }

                break;
            }
        }
    }

    private static void CheckEdges(AutomationGraph graph, List<AutomationProblem> problems)
    {
        var seen = new HashSet<(string, string, string)>();
        foreach (var edge in graph.Edges)
        {
            var from = graph.FindNode(edge.From);
            var to = graph.FindNode(edge.To);
            if (from is null || to is null)
            {
                problems.Add(new(from?.Id ?? to?.Id, "A connection points to a node that does not exist."));
                continue;
            }

            var fromInfo = AutomationNodeTypes.Find(from.Type);
            var toInfo = AutomationNodeTypes.Find(to.Type);
            if (fromInfo is null || toInfo is null)
            {
                continue; // Reported as an unknown type.
            }

            if (!fromInfo.Ports.Contains(edge.Port))
            {
                problems.Add(new(from.Id, $"{fromInfo.Title} has no '{edge.Port}' output."));
            }

            if (!toInfo.HasInput)
            {
                problems.Add(new(to.Id, $"Nothing can connect into a trigger ({toInfo.Title})."));
            }

            if (from.Id == to.Id)
            {
                problems.Add(new(from.Id, "A node cannot connect to itself."));
            }

            if (!seen.Add((edge.From, edge.Port, edge.To)))
            {
                problems.Add(new(from.Id, "The same connection is there twice."));
            }
        }

        if (FindCycle(graph) is { } nodeInCycle)
        {
            problems.Add(new(nodeInCycle, "The connections make a loop. An automation must not lead back to an earlier step."));
        }
    }

    /// <summary>A node on a cycle, or null when the graph has none.</summary>
    public static string? FindCycle(AutomationGraph graph)
    {
        var next = graph.Edges
            .GroupBy(static e => e.From, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.Select(static e => e.To).ToList(), StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on the path, 2 = done

        string? Visit(string id)
        {
            state[id] = 1;
            foreach (var to in next.GetValueOrDefault(id) ?? [])
            {
                if (state.GetValueOrDefault(to) == 1)
                {
                    return to;
                }

                if (state.GetValueOrDefault(to) == 0 && Visit(to) is { } found)
                {
                    return found;
                }
            }

            state[id] = 2;
            return null;
        }

        foreach (var node in graph.Nodes)
        {
            if (state.GetValueOrDefault(node.Id) == 0 && Visit(node.Id) is { } cycleNode)
            {
                return cycleNode;
            }
        }

        return null;
    }

    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();
}
