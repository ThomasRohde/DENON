using System.Globalization;
using System.Text.Json;

namespace Denon.Plugin.SmartTv;

/// <summary>
/// Reads <c>ssap://tv/getChannelProgramInfo</c>: the current channel's guide (DVB EIT, usually the previous, current
/// and next few programs). Times come as "yyyy,MM,dd,HH,mm,ss" in UTC (<c>startTime</c>) and TV-local time
/// (<c>localStartTime</c>).
/// </summary>
public static class LgTvProgramGuide
{
    /// <summary>The current and next program, and the whole days the TV's guide was off by (0 when correct).</summary>
    public sealed record Pick(LgTvProgram? Current, LgTvProgram? Next, int DayShift);

    /// <summary>
    /// The programs in the reply, sorted by start (UTC instants with the TV's local offset). Programs without a name
    /// or with an unreadable time are skipped.
    /// </summary>
    public static IReadOnlyList<LgTvProgram> Parse(JsonElement payload)
    {
        var list = new List<LgTvProgram>();
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("programList", out var programs) ||
            programs.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var p in programs.EnumerateArray())
        {
            var name = Str(p, "programName")?.Trim();
            if (string.IsNullOrEmpty(name) ||
                ParseTime(Str(p, "startTime")) is not { } startUtc ||
                ParseTime(Str(p, "endTime")) is not { } endUtc ||
                endUtc <= startUtc)
            {
                continue;
            }

            // The TV's offset from UTC, from its local start time (whole minutes, within ±14 h).
            var offset = TimeSpan.Zero;
            if (ParseTime(Str(p, "localStartTime")) is { } localStart)
            {
                var minutes = Math.Round((localStart - startUtc).TotalMinutes);
                if (Math.Abs(minutes) <= 14 * 60)
                {
                    offset = TimeSpan.FromMinutes(minutes);
                }
            }

            list.Add(new LgTvProgram(
                name,
                new DateTimeOffset(startUtc + offset, offset),
                new DateTimeOffset(endUtc + offset, offset)));
        }

        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return list;
    }

    /// <summary>
    /// The program on air at <paramref name="now"/> and the one after it. Some TVs date their guide a day wrong
    /// (seen: one day ahead with the right time of day), so when no program covers <paramref name="now"/> the guide is
    /// tried one day earlier or later; the result is in real time.
    /// </summary>
    public static Pick PickCurrent(IReadOnlyList<LgTvProgram> programs, DateTimeOffset now)
    {
        foreach (var shift in (int[])[0, -1, 1])
        {
            var delta = TimeSpan.FromDays(shift);
            for (var i = 0; i < programs.Count; i++)
            {
                var p = programs[i];
                if (p.Start + delta <= now && now < p.End + delta)
                {
                    var next = i + 1 < programs.Count ? Shift(programs[i + 1], delta) : null;
                    return new Pick(Shift(p, delta), next, -shift);
                }
            }
        }

        return new Pick(null, null, 0);
    }

    private static LgTvProgram Shift(LgTvProgram p, TimeSpan delta) =>
        delta == TimeSpan.Zero ? p : p with { Start = p.Start + delta, End = p.End + delta };

    /// <summary>"2026,11,25,13,55,00" as a <see cref="DateTime"/> (kind unspecified), or null.</summary>
    internal static DateTime? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split(',');
        if (parts.Length != 6)
        {
            return null;
        }

        var n = new int[6];
        for (var i = 0; i < 6; i++)
        {
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i]))
            {
                return null;
            }
        }

        try
        {
            return new DateTime(n[0], n[1], n[2], n[3], n[4], n[5], DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
