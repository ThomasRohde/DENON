using System.Globalization;
using System.Text.Json;

namespace Denon.App.Core.Services;

/// <summary>
/// Watch and listen time from <c>session.ended</c> events on the <c>media</c> stream, without double counting.
/// Each session counts its <c>exclusiveSec</c> (time not covered by a more specific parallel session, and for casts
/// not more than the play time). Sessions without exclusiveSec are skipped. A session counts on the local day it
/// started. Open sessions are not counted.
/// </summary>
public static class WatchTimeReport
{
    public sealed record Entry(string Key, string? App, int Seconds, int Sessions, string Text);

    public sealed record DayEntry(string Date, int Seconds, string Text, IReadOnlyList<Entry> ByKind);

    public sealed record Report(
        int Days,
        string From,
        string To,
        int TotalSeconds,
        string TotalText,
        int Sessions,
        IReadOnlyList<DayEntry> ByDay,
        IReadOnlyList<Entry> ByKind,
        IReadOnlyList<Entry> ByApp,
        IReadOnlyList<Entry> TopTitles,
        string Note);

    private sealed record Session(DateTimeOffset Start, string Kind, string App, string Title, int Seconds);

    /// <summary>
    /// Where to start reading events for the last <paramref name="days"/> local days up to today: midnight of the first
    /// day, minus 12 h for sessions that started the evening before. Uses that day's own UTC offset, so a period that
    /// crosses a summer-time change works.
    /// </summary>
    public static DateTimeOffset ReadFrom(DateTimeOffset now, int days) =>
        new DateTimeOffset(DateTime.SpecifyKind(now.LocalDateTime.Date.AddDays(-(Math.Max(1, days) - 1)), DateTimeKind.Local)).AddHours(-12);

    public static Report Build(IEnumerable<JsonElement> events, DateTimeOffset now, int days, int top = 10)
    {
        days = Math.Clamp(days, 1, 90);
        top = Math.Clamp(top, 1, 50);
        var fromDate = DateOnly.FromDateTime(now.LocalDateTime.Date).AddDays(-(days - 1));

        var sessions = new List<Session>();
        foreach (var ev in events)
        {
            if (!ev.TryGetProperty("event", out var evEl) || evEl.GetString() != "session.ended" ||
                !ev.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !ev.TryGetProperty("ts", out var tsEl) ||
                !DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end))
            {
                continue;
            }

            if (GetInt(data, "exclusiveSec") is not { } seconds)
            {
                continue;
            }

            var duration = GetInt(data, "durationSec") ?? 0;

            var start = end.AddSeconds(-duration);
            if (DateOnly.FromDateTime(start.LocalDateTime.Date) < fromDate || seconds <= 0)
            {
                continue;
            }

            var kind = GetString(data, "kind") ?? "unknown";
            var title = GetString(data, "title") ?? "";
            sessions.Add(new Session(start, kind, AppLabel(kind, data, title), title, seconds));
        }

        var byDay = sessions
            .GroupBy(s => DateOnly.FromDateTime(s.Start.LocalDateTime.Date))
            .OrderBy(g => g.Key)
            .Select(g => new DayEntry(
                g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                g.Sum(s => s.Seconds),
                FormatDuration(g.Sum(s => s.Seconds)),
                Group(g, s => s.Kind, s => null, int.MaxValue)))
            .ToList();

        var total = sessions.Sum(s => s.Seconds);
        return new Report(
            Days: days,
            From: fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            To: DateOnly.FromDateTime(now.LocalDateTime.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TotalSeconds: total,
            TotalText: FormatDuration(total),
            Sessions: sessions.Count,
            ByDay: byDay,
            ByKind: Group(sessions, s => s.Kind, s => null, int.MaxValue),
            ByApp: Group(sessions, s => s.App, s => s.Kind, top),
            TopTitles: Group(sessions.Where(s => IsRealTitle(s.Title, s.App)),
                s => s.Title, s => s.App, top),
            Note: "Seconds are exclusiveSec (no double counting of parallel cast/TV/receiver sessions; paused cast time " +
                  "excluded). Sessions count on the day they started; open sessions are not included.");
    }

    /// <summary>"what was used": the cast app, the TV app or channel, the receiver input or the HEOS source.</summary>
    public static string AppLabel(string kind, JsonElement data, string title) => kind switch
    {
        "cast" => GetString(data, "app") ?? GetString(data, "source") ?? "Cast",
        "appletv" => GetString(data, "app") ?? GetString(data, "source") ?? "Apple TV",
        "tv_channel" => GetString(data, "channelName") ?? ChannelFromShowing(title) ?? "Live TV",
        "tv_app" => title.Length > 0 ? title : "TV app",
        "avr_input" => title.Length > 0 ? title : GetString(data, "input") ?? "Receiver input",
        "youtube" => "YouTube",
        _ => GetString(data, "source") ?? kind,
    };

    /// <summary>A program, episode or track title: not empty, not the app's own name, not "Live TV (Ch 1 • DR1)".</summary>
    public static bool IsRealTitle(string title, string app) =>
        title.Length > 0 && !title.Equals(app, StringComparison.OrdinalIgnoreCase) && ChannelFromShowing(title) is null;

    /// <summary>
    /// The channel in the Smart TV plugin's Live TV text: "Live TV (Ch 1 • DR1)" → "DR1", "Live TV (DR1)" → "DR1".
    /// Null for other text (for example a program title).
    /// </summary>
    public static string? ChannelFromShowing(string? showing)
    {
        if (showing is null || !showing.StartsWith("Live TV (", StringComparison.OrdinalIgnoreCase) || !showing.EndsWith(')'))
        {
            return null;
        }

        var inner = showing["Live TV (".Length..^1];
        var name = inner.Split(" • ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(p => !p.StartsWith("Ch ", StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static List<Entry> Group(IEnumerable<Session> sessions, Func<Session, string> key, Func<Session, string?> app, int top) =>
        sessions
            .GroupBy(s => (Key: key(s), App: app(s)))
            .Select(g => new Entry(g.Key.Key, g.Key.App, g.Sum(s => s.Seconds), g.Count(), FormatDuration(g.Sum(s => s.Seconds))))
            .OrderByDescending(e => e.Seconds)
            .Take(top)
            .ToList();

    public static string FormatDuration(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
            : t.TotalMinutes >= 1 ? $"{t.Minutes} min"
            : $"{t.Seconds} s";
    }

    private static string? GetString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s ? s : null;

    private static int? GetInt(JsonElement data, string name) =>
        data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d) ? (int)Math.Round(d) : null;
}
