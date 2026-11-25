using System.Text.RegularExpressions;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

/// <summary>
/// Matching rules for the RoomEvent and Webhook triggers of <see cref="AutomationRule"/>.
/// </summary>
public static partial class AutomationTriggers
{
    /// <summary>The event that POST /api/hooks/{name} publishes on the control stream.</summary>
    public const string WebhookEventName = "webhook.received";

    public const string WebhookDevice = "webhook";

    public const string WebhookOrigin = "webhook";

    /// <summary>Origin of changes that the automation engine made itself.</summary>
    public const string AutomationOrigin = "automation";

    public sealed record FilterTerm(string Key, string Value, bool Negate);

    /// <summary>
    /// A hook name in lower case: letters, digits, '-' and '_', starting with a letter or digit, at most 64.
    /// Returns null when <paramref name="raw"/> is not a valid name.
    /// </summary>
    public static string? NormalizeHookName(string? raw)
    {
        var name = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        return HookNamePattern().IsMatch(name) ? name : null;
    }

    /// <summary>Parses "key=value, key!=value" terms. Terms without a key are ignored.</summary>
    public static IReadOnlyList<FilterTerm> ParseFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var terms = new List<FilterTerm>();
        foreach (var part in raw.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var notEq = part.IndexOf("!=", StringComparison.Ordinal);
            var eq = part.IndexOf('=');
            if (notEq > 0)
            {
                terms.Add(new FilterTerm(part[..notEq].Trim(), part[(notEq + 2)..].Trim(), Negate: true));
            }
            else if (eq > 0)
            {
                terms.Add(new FilterTerm(part[..eq].Trim(), part[(eq + 1)..].Trim(), Negate: false));
            }
        }

        return terms;
    }

    /// <summary>
    /// True when every term matches. "device", "origin" and "stream" match the event itself; other keys match
    /// its data. A missing value never equals anything.
    /// </summary>
    public static bool FilterMatches(RoomEvent roomEvent, IReadOnlyList<FilterTerm> terms)
    {
        foreach (var term in terms)
        {
            var actual = term.Key.ToLowerInvariant() switch
            {
                "device" => roomEvent.Device,
                "origin" => roomEvent.Origin,
                "stream" => roomEvent.Stream,
                _ => roomEvent.GetText(term.Key),
            };

            var equal = actual is not null && string.Equals(actual.Trim(), term.Value, StringComparison.OrdinalIgnoreCase);
            if (equal == term.Negate)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="rule"/> fires on <paramref name="roomEvent"/>. The enabled state and the
    /// cooldown are checked by the engine, not here. RoomEvent rules never fire on changes made by an automation, so a rule cannot
    /// trigger itself. An event name ending in "*" matches every event that starts with the rest, e.g. "cast.*".
    /// </summary>
    public static bool Matches(AutomationRule rule, RoomEvent roomEvent)
    {
        switch (rule.TriggerType)
        {
            case AutomationTriggerType.RoomEvent:
            {
                var wanted = rule.EventName?.Trim() ?? string.Empty;
                if (wanted.Length == 0 ||
                    string.Equals(roomEvent.Origin, AutomationOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var nameMatches = wanted.EndsWith('*')
                    ? roomEvent.Event.StartsWith(wanted[..^1], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(roomEvent.Event, wanted, StringComparison.OrdinalIgnoreCase);
                return nameMatches && FilterMatches(roomEvent, ParseFilter(rule.DataFilter));
            }

            case AutomationTriggerType.Webhook:
            {
                var hook = NormalizeHookName(rule.WebhookName);
                return hook is not null &&
                       string.Equals(roomEvent.Event, WebhookEventName, StringComparison.Ordinal) &&
                       string.Equals(roomEvent.GetText("hook"), hook, StringComparison.Ordinal) &&
                       FilterMatches(roomEvent, ParseFilter(rule.DataFilter));
            }

            default:
                return false;
        }
    }

    /// <summary>A short line for the activity log, e.g. "power.changed (from=standby, to=on)".</summary>
    public static string Describe(RoomEvent roomEvent)
    {
        var pairs = (roomEvent.Data ?? new Dictionary<string, object?>())
            .Keys
            .Take(4)
            .Select(k => $"{k}={Truncate(roomEvent.GetText(k), 40)}");
        var detail = string.Join(", ", pairs);
        return detail.Length > 0 ? $"{roomEvent.Event} ({detail})" : roomEvent.Event;
    }

    private static string Truncate(string? text, int max) =>
        text is null ? "" : text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex HookNamePattern();
}
