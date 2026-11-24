using System.Globalization;
using System.Text.Json;
using Denon.App.Core.Services;

namespace Denon.App.Core.Insights;

/// <summary>
/// The Insights page report: watch and listen time, the weekly rhythm, the receiver, who is in control, lighting and
/// a short list of plain-language observations. Built from the habit event log (all streams) for one period, with the
/// period before it for comparison. Watch time uses <c>exclusiveSec</c> as <see cref="WatchTimeReport"/> does (no
/// double counting of parallel sessions); receiver time uses the receiver-input sessions' <c>durationSec</c>.
/// </summary>
public static class InsightsReport
{
    public static readonly string[] Blocks = ["Morning", "Afternoon", "Evening", "LateNight"];

    public sealed record Period(int Days, string From, string To, string PreviousFrom, string Label);

    /// <summary>
    /// A ranked row. <paramref name="Logo"/> is a channel logo's file name in the Smart TV plugin's channel-logos folder
    /// (the page loads it from <c>/logo/&lt;file&gt;</c>).
    /// </summary>
    public sealed record Slice(string Key, string Label, string? Kind, int Seconds, string Text, int Sessions, double Share, string? Detail = null, string? Logo = null);

    public sealed record Day(string Date, string Weekday, int Seconds, string Text, IReadOnlyDictionary<string, int> ByKind);

    public sealed record LongestSession(string Title, string App, string Kind, int Seconds, string Text, string Start);

    public sealed record Watch(
        int TotalSeconds,
        string TotalText,
        int Sessions,
        int ActiveDays,
        string AveragePerActiveDayText,
        int PreviousSeconds,
        string PreviousText,
        int? ChangePercent,
        int VideoSeconds,
        int AudioSeconds,
        IReadOnlyList<Day> ByDay,
        IReadOnlyList<Slice> ByKind,
        IReadOnlyList<Slice> TopApps,
        IReadOnlyList<Slice> TopTitles,
        LongestSession? Longest,
        int StreakDays,
        IReadOnlyList<Slice> TopChannels);

    public sealed record BlockShare(string Block, string Label, int Seconds, string Text, double Share);

    /// <summary>Cells[day][hour] in seconds; day 0 is Monday.</summary>
    public sealed record Rhythm(int[][] Cells, int MaxCell, int? PeakDay, int? PeakHour, string PeakText, IReadOnlyList<BlockShare> ByBlock);

    public sealed record VolumePoint(string Ts, double Volume, string Db);

    public sealed record BlockVolume(string Block, string Label, double? Average, string? AverageDb, int Samples);

    public sealed record Volume(
        double? Average,
        string? AverageDb,
        double? Min,
        double? Max,
        int Changes,
        IReadOnlyList<VolumePoint> Points,
        IReadOnlyList<BlockVolume> ByBlock);

    public sealed record ReceiverDay(string Date, int Seconds);

    public sealed record Receiver(
        int InUseSeconds,
        string InUseText,
        int PowerOns,
        IReadOnlyList<ReceiverDay> ByDay,
        IReadOnlyList<Slice> Inputs,
        IReadOnlyList<Slice> SoundModes,
        Volume Volume);

    public sealed record OriginCount(string Key, string Label, int Count, double Share);

    public sealed record Control(int Total, IReadOnlyList<OriginCount> Origins, int FromApp, int FromOutside);

    public sealed record Lighting(int Changes, int MusicSyncStarts, string? LastState);

    public sealed record Device(string Key, string Stream, int Events, string LastSeen);

    public sealed record Insight(string Tone, string Icon, string Title, string Text);

    public sealed record Report(
        string GeneratedAt,
        Period Period,
        int EventCount,
        Watch Watch,
        Rhythm Rhythm,
        Receiver Receiver,
        Control Control,
        Lighting Lighting,
        IReadOnlyList<Device> Devices,
        IReadOnlyList<Insight> Insights);

    private sealed record Session(
        DateTimeOffset Start,
        string Kind,
        string App,
        string Title,
        int Exclusive,
        int Duration,
        string? Input,
        string? SoundMode,
        string? Logo);

    private static readonly HashSet<string> VideoKinds = new(StringComparer.Ordinal)
    {
        "cast", "appletv", "tv_app", "tv_channel", "youtube",
    };

    /// <summary>
    /// Builds the report for the <paramref name="days"/> days up to today. <paramref name="events"/> should cover the
    /// period before it too (2 × days), so the change against that period can be shown. <paramref name="logoForChannel"/>
    /// gives a cached logo file for a channel name, for Live TV sessions logged without one (older entries).
    /// </summary>
    public static Report Build(IEnumerable<JsonElement> events, DateTimeOffset now, int days, Func<string, string?>? logoForChannel = null)
    {
        days = Math.Clamp(days, 1, 90);
        var today = DateOnly.FromDateTime(now.LocalDateTime.Date);
        var from = today.AddDays(-(days - 1));
        var previousFrom = from.AddDays(-days);

        var all = new List<(DateTimeOffset Ts, JsonElement Ev)>();
        foreach (var ev in events)
        {
            if (ev.ValueKind == JsonValueKind.Object && ev.TryGetProperty("ts", out var tsEl) &&
                DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
            {
                all.Add((ts, ev));
            }
        }

        all.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        var inPeriod = all.Where(e => LocalDate(e.Ts) >= from).ToList();

        var sessions = all
            .Where(e => Str(e.Ev, "stream") == EventTelemetryLog.StreamMedia && Str(e.Ev, "event") == "session.ended")
            .Select(e => ToSession(e.Ts, e.Ev, logoForChannel))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        var current = sessions.Where(s => LocalDate(s.Start) >= from && LocalDate(s.Start) <= today).ToList();
        var previous = sessions.Where(s => LocalDate(s.Start) >= previousFrom && LocalDate(s.Start) < from).ToList();

        var watch = BuildWatch(current, previous, from, today);
        var rhythm = BuildRhythm(current);
        var receiver = BuildReceiver(current, inPeriod, from, today);
        var control = BuildControl(inPeriod);
        var lighting = BuildLighting(inPeriod);
        var devices = inPeriod
            .GroupBy(e => Str(e.Ev, "device") ?? "unknown")
            .Select(g => new Device(g.Key, Str(g.Last().Ev, "stream") ?? "", g.Count(), g.Last().Ts.ToString("O", CultureInfo.InvariantCulture)))
            .OrderByDescending(d => d.Events)
            .ToList();

        var period = new Period(
            days,
            Iso(from),
            Iso(today),
            Iso(previousFrom),
            days == 1 ? "Today" : $"Last {days} days");

        return new Report(
            now.ToString("O", CultureInfo.InvariantCulture),
            period,
            inPeriod.Count,
            watch,
            rhythm,
            receiver,
            control,
            lighting,
            devices,
            BuildInsights(period, watch, rhythm, receiver, control, lighting));
    }

    public static string KindLabel(string kind) => kind switch
    {
        "cast" => "Chromecast",
        "appletv" => "Apple TV",
        "homepod" => "HomePod",
        "tv_app" => "TV apps & HDMI",
        "tv_channel" => "Live TV",
        "youtube" => "YouTube",
        "avr_input" => "Receiver input",
        "radio" or "tunein_radio" => "Radio",
        "spotify" => "Spotify",
        "tidal" => "TIDAL",
        "amazon_music" => "Amazon Music",
        "local_dlna" => "Music library",
        "airplay" => "AirPlay",
        "heos_stream" => "HEOS",
        _ => kind.Length == 0 ? "Other" : char.ToUpperInvariant(kind[0]) + kind[1..].Replace('_', ' '),
    };

    public static string BlockLabel(string block) => block switch
    {
        "Morning" => "Morning",
        "Afternoon" => "Afternoon",
        "Evening" => "Evening",
        "LateNight" => "Late night",
        _ => block,
    };

    public static string BlockOf(int hour) => hour switch
    {
        >= 6 and < 12 => "Morning",
        >= 12 and < 17 => "Afternoon",
        >= 17 and < 22 => "Evening",
        _ => "LateNight",
    };

    /// <summary>Denon volume 0–98 as the receiver shows it in dB (80 = 0 dB).</summary>
    public static string Db(double volume)
    {
        var db = volume - 80.0;
        return (db >= 0 ? "+" : "−") + Math.Abs(db).ToString("0.#", CultureInfo.InvariantCulture) + " dB";
    }

    // ---------------------------------------------------------------- watch

    private static Watch BuildWatch(List<Session> current, List<Session> previous, DateOnly from, DateOnly today)
    {
        var counted = current.Where(s => s.Exclusive > 0).ToList();
        var total = counted.Sum(s => s.Exclusive);
        var previousTotal = previous.Where(s => s.Exclusive > 0).Sum(s => s.Exclusive);

        var byDay = new List<Day>();
        for (var d = from; d <= today; d = d.AddDays(1))
        {
            var date = d;
            var daySessions = counted.Where(s => LocalDate(s.Start) == date).ToList();
            var byKind = daySessions
                .GroupBy(s => s.Kind)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.Exclusive), StringComparer.Ordinal);
            var seconds = daySessions.Sum(s => s.Exclusive);
            byDay.Add(new Day(Iso(date), date.DayOfWeek.ToString()[..3], seconds, WatchTimeReport.FormatDuration(seconds), byKind));
        }

        var activeDays = byDay.Count(d => d.Seconds > 0);

        var streak = 0;
        for (var i = byDay.Count - 1; i >= 0; i--)
        {
            if (byDay[i].Seconds > 0)
            {
                streak++;
            }
            else if (i == byDay.Count - 1)
            {
                continue; // Nothing yet today: the streak can still end yesterday.
            }
            else
            {
                break;
            }
        }

        var longest = counted.MaxBy(s => s.Exclusive);
        var liveTv = counted.Where(s => s.Kind == "tv_channel").ToList();
        var liveTvTotal = liveTv.Sum(s => s.Exclusive);

        return new Watch(
            total,
            WatchTimeReport.FormatDuration(total),
            counted.Count,
            activeDays,
            WatchTimeReport.FormatDuration(activeDays == 0 ? 0 : total / activeDays),
            previousTotal,
            WatchTimeReport.FormatDuration(previousTotal),
            previousTotal > 0 ? (int)Math.Round((total - previousTotal) * 100.0 / previousTotal) : null,
            counted.Where(s => VideoKinds.Contains(s.Kind)).Sum(s => s.Exclusive),
            counted.Where(s => !VideoKinds.Contains(s.Kind) && s.Kind != "avr_input").Sum(s => s.Exclusive),
            byDay,
            Slices(counted, s => s.Kind, s => KindLabel(s.Kind), s => s.Kind, total, int.MaxValue),
            Slices(counted, s => s.App, s => s.App, s => s.Kind, total, 8),
            Slices(
                counted.Where(s => WatchTimeReport.IsRealTitle(s.Title, s.App)),
                s => s.Title,
                s => s.Title,
                s => s.Kind,
                total,
                8,
                detail: s => s.App),
            longest is null
                ? null
                : new LongestSession(
                    WatchTimeReport.IsRealTitle(longest.Title, longest.App) ? longest.Title : longest.App,
                    longest.App,
                    longest.Kind,
                    longest.Exclusive,
                    WatchTimeReport.FormatDuration(longest.Exclusive),
                    longest.Start.ToString("O", CultureInfo.InvariantCulture)),
            streak,
            // Live TV by channel; the share is of all live TV time.
            Slices(liveTv, s => s.App, s => s.App, s => s.Kind, liveTvTotal, 12));
    }

    private static List<Slice> Slices(
        IEnumerable<Session> sessions,
        Func<Session, string> key,
        Func<Session, string> label,
        Func<Session, string> kind,
        int total,
        int top,
        Func<Session, string>? detail = null) =>
        sessions
            .GroupBy(key)
            .Select(g =>
            {
                var seconds = g.Sum(s => s.Exclusive);
                var first = g.OrderByDescending(s => s.Exclusive).First();
                return new Slice(
                    g.Key,
                    label(first),
                    kind(first),
                    seconds,
                    WatchTimeReport.FormatDuration(seconds),
                    g.Count(),
                    total > 0 ? Math.Round(seconds / (double)total, 4) : 0,
                    detail?.Invoke(first),
                    g.OrderByDescending(s => s.Start).Select(s => s.Logo).FirstOrDefault(l => l is not null));
            })
            .OrderByDescending(s => s.Seconds)
            .Take(top)
            .ToList();

    // ---------------------------------------------------------------- rhythm

    private static Rhythm BuildRhythm(List<Session> current)
    {
        var cells = Enumerable.Range(0, 7).Select(_ => new double[24]).ToArray();
        foreach (var s in current.Where(s => s.Exclusive > 0))
        {
            // Spread the counted seconds over the hours the session ran.
            var span = Math.Max(s.Duration, s.Exclusive);
            var weight = s.Exclusive / (double)span;
            var t = s.Start.LocalDateTime;
            var end = t.AddSeconds(span);
            while (t < end)
            {
                var hourEnd = t.Date.AddHours(t.Hour + 1);
                var sliceEnd = hourEnd < end ? hourEnd : end;
                cells[DayIndex(t.DayOfWeek)][t.Hour] += (sliceEnd - t).TotalSeconds * weight;
                t = sliceEnd;
            }
        }

        var rounded = cells.Select(r => r.Select(v => (int)Math.Round(v)).ToArray()).ToArray();
        var max = rounded.SelectMany(r => r).DefaultIfEmpty(0).Max();

        int? peakDay = null, peakHour = null;
        if (max > 0)
        {
            // The busiest day, then its busiest hour.
            peakDay = Enumerable.Range(0, 7).MaxBy(d => rounded[d].Sum());
            peakHour = Enumerable.Range(0, 24).MaxBy(h => rounded[peakDay.Value][h]);
        }

        var blockTotals = Blocks.ToDictionary(b => b, _ => 0);
        for (var d = 0; d < 7; d++)
        {
            for (var h = 0; h < 24; h++)
            {
                blockTotals[BlockOf(h)] += rounded[d][h];
            }
        }

        var all = blockTotals.Values.Sum();
        var byBlock = Blocks
            .Select(b => new BlockShare(b, BlockLabel(b), blockTotals[b], WatchTimeReport.FormatDuration(blockTotals[b]), all > 0 ? Math.Round(blockTotals[b] / (double)all, 4) : 0))
            .ToList();

        var peakText = peakDay is { } pd && peakHour is { } ph
            ? $"{WeekdayName(pd)}s around {ph:00}:00"
            : "No viewing yet";

        return new Rhythm(rounded, max, peakDay, peakHour, peakText, byBlock);
    }

    // ---------------------------------------------------------------- receiver

    private static Receiver BuildReceiver(List<Session> current, List<(DateTimeOffset Ts, JsonElement Ev)> inPeriod, DateOnly from, DateOnly today)
    {
        var inputSessions = current.Where(s => s.Kind == "avr_input" && s.Duration > 0).ToList();
        var inUse = inputSessions.Sum(s => s.Duration);

        var byDay = new List<ReceiverDay>();
        for (var d = from; d <= today; d = d.AddDays(1))
        {
            var date = d;
            byDay.Add(new ReceiverDay(Iso(date), inputSessions.Where(s => LocalDate(s.Start) == date).Sum(s => s.Duration)));
        }

        var inputs = inputSessions
            .GroupBy(s => s.Title.Length > 0 ? s.Title : s.Input ?? "Input")
            .Select(g => new Slice(g.Key, g.Key, "avr_input", g.Sum(s => s.Duration), WatchTimeReport.FormatDuration(g.Sum(s => s.Duration)), g.Count(),
                inUse > 0 ? Math.Round(g.Sum(s => s.Duration) / (double)inUse, 4) : 0))
            .OrderByDescending(s => s.Seconds)
            .ToList();

        var modes = inputSessions
            .Where(s => !string.IsNullOrWhiteSpace(s.SoundMode))
            .GroupBy(s => s.SoundMode!)
            .Select(g => new Slice(g.Key, g.Key, null, g.Sum(s => s.Duration), WatchTimeReport.FormatDuration(g.Sum(s => s.Duration)), g.Count(),
                inUse > 0 ? Math.Round(g.Sum(s => s.Duration) / (double)inUse, 4) : 0))
            .OrderByDescending(s => s.Seconds)
            .ToList();

        var avr = inPeriod.Where(e => Str(e.Ev, "stream") == EventTelemetryLog.StreamAvr).ToList();
        var powerOns = avr.Count(e => Str(e.Ev, "event") == "power.changed" && Data(e.Ev, "to") == "on");

        var points = new List<(DateTimeOffset Ts, double Volume, string Block)>();
        foreach (var (ts, ev) in avr.Where(e => Str(e.Ev, "event") == "volume.settled"))
        {
            if (ev.TryGetProperty("data", out var data) && Num(data, "to") is { } to)
            {
                points.Add((ts, to, Str(ev, "block") ?? BlockOf(ts.LocalDateTime.Hour)));
            }
        }

        double? avg = points.Count > 0 ? Math.Round(points.Average(p => p.Volume), 1) : null;
        var byBlock = Blocks
            .Select(b =>
            {
                var inBlock = points.Where(p => p.Block == b).ToList();
                double? blockAvg = inBlock.Count > 0 ? Math.Round(inBlock.Average(p => p.Volume), 1) : null;
                return new BlockVolume(b, BlockLabel(b), blockAvg, blockAvg is { } v ? Db(v) : null, inBlock.Count);
            })
            .ToList();

        var volume = new Volume(
            avg,
            avg is { } a ? Db(a) : null,
            points.Count > 0 ? points.Min(p => p.Volume) : null,
            points.Count > 0 ? points.Max(p => p.Volume) : null,
            points.Count,
            points.Select(p => new VolumePoint(p.Ts.ToString("O", CultureInfo.InvariantCulture), p.Volume, Db(p.Volume))).ToList(),
            byBlock);

        return new Receiver(inUse, WatchTimeReport.FormatDuration(inUse), powerOns, byDay, inputs, modes, volume);
    }

    // ---------------------------------------------------------------- control, lighting

    public static string OriginLabel(string origin) => origin switch
    {
        "external" => "Remotes & devices",
        "desktop_ui" => "Denon Desktop",
        "mcp_agent" => "AI agent (MCP)",
        "phone_remote" => "Phone remote",
        "automation" => "Automations",
        "smart_scene" => "Smart scenes",
        "hotkey" => "Hotkeys",
        "plugin" => "Plugins",
        "system" => "System",
        "app" => "Denon Desktop",
        _ => origin.Length == 0 ? "Unknown" : char.ToUpperInvariant(origin[0]) + origin[1..].Replace('_', ' '),
    };

    private static Control BuildControl(List<(DateTimeOffset Ts, JsonElement Ev)> inPeriod)
    {
        // Changes only: connection and session bookkeeping are not someone's action.
        var actions = inPeriod.Where(e => IsAction(e.Ev)).ToList();

        var total = actions.Count;
        var origins = actions
            .GroupBy(e => Str(e.Ev, "origin") ?? "unknown")
            .Select(g => new OriginCount(g.Key, OriginLabel(g.Key), g.Count(), total > 0 ? Math.Round(g.Count() / (double)total, 4) : 0))
            .OrderByDescending(o => o.Count)
            .ToList();

        var outside = origins.Where(o => o.Key is "external").Sum(o => o.Count);
        return new Control(total, origins, total - outside, outside);
    }

    /// <summary>
    /// A change someone or something made: a state change, a settled volume or an app action. Connection and session
    /// bookkeeping (origin "system") and the first state read after a start (empty "from") are not.
    /// </summary>
    private static bool IsAction(JsonElement ev)
    {
        if (Str(ev, "origin") is not { } origin || origin == "system" || Str(ev, "event") is not { } name)
        {
            return false;
        }

        if (name is "volume.settled" or "plugin.action" or "scene.activated" or "automation.triggered")
        {
            return true;
        }

        return name.EndsWith(".changed", StringComparison.Ordinal) &&
               !(ev.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                 data.TryGetProperty("from", out var fromEl) && fromEl.ValueKind == JsonValueKind.String && fromEl.GetString() is "");
    }

    private static Lighting BuildLighting(List<(DateTimeOffset Ts, JsonElement Ev)> inPeriod)
    {
        var lighting = inPeriod.Where(e => Str(e.Ev, "stream") == EventTelemetryLog.StreamLighting).ToList();
        var changes = lighting.Where(e => Str(e.Ev, "event") == "lighting.state_changed").ToList();
        var syncStarts = lighting.Count(e => Str(e.Ev, "event") == "music_sync.changed" && DataBool(e.Ev, "enabled") == true);
        return new Lighting(changes.Count, syncStarts, changes.Count > 0 ? Data(changes[^1].Ev, "to") : null);
    }

    // ---------------------------------------------------------------- observations

    private static List<Insight> BuildInsights(Period period, Watch watch, Rhythm rhythm, Receiver receiver, Control control, Lighting lighting)
    {
        var list = new List<Insight>();
        var span = period.Days == 1 ? "today" : $"in the last {period.Days} days";

        if (watch.TotalSeconds == 0)
        {
            list.Add(new Insight("info", "clock", "Nothing watched yet",
                $"No watch or listen sessions {span}. Sessions show here when a cast, the Apple TV, the TV or the receiver plays for longer than the dwell filter."));
        }
        else
        {
            if (watch.ChangePercent is { } change && period.Days > 1)
            {
                var more = change >= 0;
                list.Add(new Insight(more ? "up" : "down", more ? "trend-up" : "trend-down",
                    $"{(more ? "+" : "−")}{Math.Abs(change)} % against the {period.Days} days before",
                    $"{watch.TotalText} now, {watch.PreviousText} in the period before."));
            }

            if (rhythm.PeakDay is { } day && rhythm.PeakHour is { } hour)
            {
                var block = rhythm.ByBlock.MaxBy(b => b.Seconds)!;
                list.Add(period.Days == 1
                    ? new Insight("info", "clock", $"Prime time today: {hour:00}:00",
                        $"{Percent(block.Share)} of today's time falls in the {block.Label.ToLowerInvariant()}.")
                    : new Insight("info", "clock", $"Prime time: {rhythm.PeakText}",
                        $"{Percent(block.Share)} of your time falls in the {block.Label.ToLowerInvariant()}. {WeekdayName(day)} is your busiest day."));
            }

            if (watch.TopApps.FirstOrDefault() is { } app)
            {
                list.Add(new Insight("info", "play", $"{app.Label} leads",
                    $"{app.Text} in {app.Sessions} {(app.Sessions == 1 ? "session" : "sessions")}, {Percent(app.Share)} of the total."));
            }

            // The top channel, unless it already leads all apps (then "X leads" says it).
            if (watch.TopChannels.FirstOrDefault() is { } channel && channel.Seconds >= 30 * 60 && channel.Label != "Live TV" &&
                watch.TopApps.FirstOrDefault()?.Label != channel.Label)
            {
                list.Add(new Insight("info", "tv", $"{channel.Label} is your channel",
                    $"{channel.Text} of live TV in {channel.Sessions} {(channel.Sessions == 1 ? "program" : "programs")}, {Percent(channel.Share)} of your live TV."));
            }

            if (watch.Longest is { } longest && longest.Seconds >= 20 * 60)
            {
                var start = DateTimeOffset.Parse(longest.Start, CultureInfo.InvariantCulture);
                var what = longest.Title.Equals(longest.App, StringComparison.OrdinalIgnoreCase) ? longest.Title : $"{longest.Title} ({longest.App})";
                list.Add(new Insight("info", "film", "Longest sitting",
                    $"{what} for {longest.Text}, {start.LocalDateTime.ToString("dddd HH:mm", CultureInfo.InvariantCulture)}."));
            }

            if (watch.StreakDays >= 3)
            {
                list.Add(new Insight("up", "flame", $"{watch.StreakDays}-day streak", "Something played on each of these days in a row."));
            }
        }

        var evening = receiver.Volume.ByBlock.First(b => b.Block == "Evening");
        var late = receiver.Volume.ByBlock.First(b => b.Block == "LateNight");
        if (evening.Average is { } e && late.Average is { } l && Math.Abs(e - l) >= 2)
        {
            var quieter = l < e;
            list.Add(new Insight("info", "volume",
                quieter ? "Quieter late at night" : "Louder late at night",
                $"Late at night the volume is {Math.Abs(e - l).ToString("0.#", CultureInfo.InvariantCulture)} dB {(quieter ? "lower" : "higher")} than in the evening ({late.AverageDb} against {evening.AverageDb})."));
        }

        if (control.Total >= 5)
        {
            var agent = control.Origins.FirstOrDefault(o => o.Key == "mcp_agent");
            var outsideShare = control.Total > 0 ? control.FromOutside / (double)control.Total : 0;
            list.Add(new Insight("info", "remote",
                outsideShare >= 0.5 ? "Mostly the remotes" : "Mostly Denon Desktop",
                $"{Percent(outsideShare)} of {control.Total} changes came from remotes, the TV and other devices; {Percent(1 - outsideShare)} from Denon Desktop, the phone remote, automations or an agent." +
                (agent is { Count: > 0 } ? $" Your AI agent made {agent.Count}." : "")));
        }

        if (lighting.MusicSyncStarts > 0)
        {
            list.Add(new Insight("info", "bulb", "Lights in the groove",
                $"Hue music sync started {lighting.MusicSyncStarts} {(lighting.MusicSyncStarts == 1 ? "time" : "times")} {span}."));
        }

        // The page shows at most six, most telling first.
        return list
            .OrderBy(i => Array.IndexOf(InsightOrder, i.Icon))
            .Take(6)
            .ToList();
    }

    private static readonly string[] InsightOrder = ["clock", "trend-up", "trend-down", "play", "tv", "volume", "remote", "film", "flame", "bulb"];

    // ---------------------------------------------------------------- helpers

    private static Session? ToSession(DateTimeOffset end, JsonElement ev, Func<string, string?>? logoForChannel)
    {
        if (!ev.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var duration = (int)Math.Round(Num(data, "durationSec") ?? 0);
        // Events written before 1.15.0 have no exclusiveSec: they count as their whole duration.
        var exclusive = (int)Math.Round(Num(data, "exclusiveSec") ?? duration);
        var kind = DataStr(data, "kind") ?? "unknown";
        var title = DataStr(data, "title") ?? "";
        var app = WatchTimeReport.AppLabel(kind, data, title);
        var logo = LogoName(DataStr(data, "channelLogo")) ??
                   (kind == "tv_channel" && logoForChannel is not null ? LogoName(logoForChannel(app)) : null);
        return new Session(
            end.AddSeconds(-duration),
            kind,
            app,
            title,
            Math.Max(0, exclusive),
            Math.Max(0, duration),
            DataStr(data, "input"),
            DataStr(data, "soundMode"),
            logo);
    }

    /// <summary>A logo file name as the Smart TV plugin caches it ("dr1-dk.png"), or null for anything else.</summary>
    public static string? LogoName(string? file)
    {
        if (string.IsNullOrEmpty(file) || file.Length > 120)
        {
            return null;
        }

        foreach (var c in file)
        {
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.'))
            {
                return null;
            }
        }

        return file.Contains("..", StringComparison.Ordinal) ||
               !(file.EndsWith(".png", StringComparison.Ordinal) || file.EndsWith(".jpg", StringComparison.Ordinal) ||
                 file.EndsWith(".jpeg", StringComparison.Ordinal) || file.EndsWith(".webp", StringComparison.Ordinal))
            ? null
            : file;
    }

    private static int DayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    private static string WeekdayName(int index) => ((DayOfWeek)((index + 1) % 7)).ToString();

    private static string Percent(double share) => $"{Math.Round(share * 100):0} %";

    private static DateOnly LocalDate(DateTimeOffset ts) => DateOnly.FromDateTime(ts.LocalDateTime.Date);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Str(JsonElement ev, string name) =>
        ev.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? Data(JsonElement ev, string name) =>
        ev.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? DataStr(data, name) : null;

    private static bool? DataBool(JsonElement ev, string name) =>
        ev.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean()
            : null;

    private static string? DataStr(JsonElement data, string name) =>
        data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s ? s : null;

    private static double? Num(JsonElement data, string name) =>
        data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d) ? d : null;
}
