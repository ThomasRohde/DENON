namespace Denon.App.Core.Services;

/// <summary>
/// Stops double counting of watch time on the <c>media</c> stream.
///
/// One viewing can produce several parallel sessions: a Chromecast or Apple TV program (kind "cast" or "appletv"),
/// the LG TV on the HDMI input it is plugged into (kind "tv_app"), and the Denon on "TV Audio"
/// via ARC (kind "avr_input"). Each <c>session.ended</c> gets:
/// <list type="bullet">
/// <item><c>coveredSec</c>: seconds during which a more specific session ran at the same time,</item>
/// <item><c>coveredBy</c>: the kinds/devices of those sessions (only when coveredSec &gt; 0),</item>
/// <item><c>exclusiveSec</c>: <c>durationSec - coveredSec</c>. Summing exclusiveSec gives real time.</item>
/// </list>
/// Covering rules (more specific covers less specific):
/// (Apple TV sessions follow the same rules as cast sessions.)
/// cast → tv_app on an HDMI input; cast → avr_input "TV" (ARC) or the cast's own receiver input;
/// tv_app/tv_channel → avr_input "TV" (ARC); tv_channel or a non-HDMI tv_app → cast (the TV shows something
/// else, so the cast plays unseen). Sessions with <c>watchedSec</c> (cast) cap exclusiveSec at watchedSec:
/// paused time is not watching.
/// </summary>
public sealed class MediaSessionOverlapTracker
{
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromHours(24);
    private const int MaxHistory = 500;

    private readonly object _sync = new();
    private readonly Dictionary<string, SessionInterval> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SessionInterval> _closed = [];

    /// <summary>
    /// Tracks a media event and returns the data to write. For <c>session.ended</c> the result is a copy
    /// with the overlap fields added; other events are returned unchanged.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Annotate(
        string device,
        string eventName,
        IReadOnlyDictionary<string, object?>? data,
        DateTimeOffset now)
    {
        if (data is null || !TryGetString(data, "kind", out var kind))
        {
            return data;
        }

        // One open session per physical device: several Chromecasts, Apple TVs or HomePods can play at once.
        var key = $"{device}|{GetString(data, "castDevice")}{GetString(data, "appleTvDevice")}{GetString(data, "homepodDevice")}";
        lock (_sync)
        {
            if (eventName == "session.started")
            {
                var dwell = GetNumber(data, "dwellConfirmedSec") ?? 0;
                _open[key] = SessionInterval.From(device, kind, data, now.AddSeconds(-dwell), end: null);
                return data;
            }

            if (eventName != "session.ended" || GetNumber(data, "durationSec") is not { } durationSec)
            {
                return data;
            }

            _open.Remove(key);

            // A source can write the end a little later than it happened (the Apple TV plugin waits up to 35 s to
            // tell "standby" from "app_closed"); its "endedAt" is the real end, so the interval stays right.
            var end = EndedAt(data, now);
            var self = SessionInterval.From(device, kind, data, end.AddSeconds(-durationSec), end);

            var coverers = _open.Values.Concat(_closed).Where(c => Covers(c, self)).ToList();
            var covered = UnionSeconds(coverers.Select(c => (Start: Max(c.Start, self.Start), End: Min(c.End ?? now, self.End!.Value))));
            var coveredSec = (int)Math.Round(Math.Min(covered, durationSec));

            var exclusiveSec = Math.Max(0, Math.Round(durationSec) - coveredSec);

            // Paused time is not watching: a session that reports play time (cast) never has more exclusive time.
            if (GetNumber(data, "watchedSec") is { } watchedSec)
            {
                exclusiveSec = Math.Min(exclusiveSec, Math.Max(0, Math.Round(watchedSec)));
            }

            var result = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase)
            {
                ["coveredSec"] = coveredSec,
                ["exclusiveSec"] = (int)exclusiveSec,
            };
            if (coveredSec > 0)
            {
                var overlapping = coverers.Where(c => Min(c.End ?? now, self.End!.Value) > Max(c.Start, self.Start)).ToList();
                result["coveredBy"] = overlapping
                    .Select(c => c.Label)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // How long each one covered it: a cast for 20 s of an hour is not what played on that input.
                result["coveredBySec"] = overlapping
                    .GroupBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => (int)Math.Round(Math.Min(durationSec,
                            UnionSeconds(g.Select(c => (Start: Max(c.Start, self.Start), End: Min(c.End ?? now, self.End!.Value)))))),
                        StringComparer.OrdinalIgnoreCase);
            }

            _closed.Add(self);
            _closed.RemoveAll(c => c.End < now - HistoryWindow);
            if (_closed.Count > MaxHistory)
            {
                _closed.RemoveRange(0, _closed.Count - MaxHistory);
            }

            return result;
        }
    }

    /// <summary>The ended sessions that end after <paramref name="since"/>: kept with the open-sessions checkpoint.</summary>
    internal IReadOnlyList<SessionInterval> ClosedSince(DateTimeOffset since)
    {
        lock (_sync)
        {
            return _closed.Where(c => c.End > since).ToList();
        }
    }

    /// <summary>
    /// Puts ended sessions of an earlier run back, so a session closed after a crash is not given time that those
    /// sessions already counted.
    /// </summary>
    internal void RestoreClosed(IEnumerable<SessionInterval> intervals)
    {
        lock (_sync)
        {
            foreach (var interval in intervals)
            {
                if (interval.End is not null && !_closed.Contains(interval))
                {
                    _closed.Add(interval);
                }
            }
        }
    }

    /// <summary>True when <paramref name="coverer"/> is a more specific view of the same watching as <paramref name="target"/>.</summary>
    internal static bool Covers(SessionInterval coverer, SessionInterval target)
    {
        if (ReferenceEquals(coverer, target))
        {
            return false;
        }

        if (IsStreamer(coverer.Kind))
        {
            return (target.Kind == "tv_app" && target.Title.Contains("HDMI", StringComparison.OrdinalIgnoreCase)) ||
                   (target.Kind == "avr_input" && (IsTvInput(target.Input) ||
                       (!string.IsNullOrEmpty(coverer.Input) && string.Equals(coverer.Input, target.Input, StringComparison.OrdinalIgnoreCase))));
        }

        if (coverer.Kind is "tv_app" or "tv_channel")
        {
            // The TV shows live TV or its own app, not the HDMI input: the cast plays unseen in the background.
            var tvShowsOtherSource = coverer.Kind == "tv_channel" || !coverer.Title.Contains("HDMI", StringComparison.OrdinalIgnoreCase);
            return (target.Kind == "avr_input" && IsTvInput(target.Input)) ||
                   (IsStreamer(target.Kind) && tvShowsOtherSource);
        }

        return false;
    }

    /// <summary>A box on an HDMI input of the TV or receiver: Chromecast ("cast") or Apple TV ("appletv").</summary>
    private static bool IsStreamer(string kind) => kind is "cast" or "appletv";

    private static bool IsTvInput(string input) => string.Equals(input, "TV", StringComparison.OrdinalIgnoreCase);

    private static double UnionSeconds(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var total = 0.0;
        DateTimeOffset? runStart = null;
        DateTimeOffset runEnd = default;
        foreach (var (start, end) in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start))
        {
            if (runStart is null || start > runEnd)
            {
                if (runStart is not null)
                {
                    total += (runEnd - runStart.Value).TotalSeconds;
                }

                runStart = start;
                runEnd = end;
            }
            else if (end > runEnd)
            {
                runEnd = end;
            }
        }

        if (runStart is not null)
        {
            total += (runEnd - runStart.Value).TotalSeconds;
        }

        return total;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>The session's real end: a valid "endedAt" from the last 5 minutes, otherwise <paramref name="now"/>.</summary>
    private static DateTimeOffset EndedAt(IReadOnlyDictionary<string, object?> data, DateTimeOffset now)
    {
        var raw = data.TryGetValue("endedAt", out var value) ? value : null;
        var endedAt = raw switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt),
            string s when DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) => parsed,
            _ => (DateTimeOffset?)null,
        };

        return endedAt is { } e && e <= now && e >= now.AddMinutes(-5) ? e : now;
    }

    private static string GetString(IReadOnlyDictionary<string, object?> data, string key) =>
        TryGetString(data, key, out var value) ? value : string.Empty;

    private static bool TryGetString(IReadOnlyDictionary<string, object?> data, string key, out string value)
    {
        value = data.TryGetValue(key, out var raw) ? raw?.ToString()?.Trim() ?? string.Empty : string.Empty;
        return value.Length > 0;
    }

    private static double? GetNumber(IReadOnlyDictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var raw) && raw is not null &&
        double.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    internal sealed record SessionInterval(string Label, string Kind, string Title, string Input, DateTimeOffset Start, DateTimeOffset? End)
    {
        public static SessionInterval From(string device, string kind, IReadOnlyDictionary<string, object?> data, DateTimeOffset start, DateTimeOffset? end)
        {
            // Cast and Apple TV sessions carry the Denon input as receiverInput; Denon sessions carry it as input.
            var input = IsStreamer(kind) ? GetString(data, "receiverInput") : GetString(data, "input");
            var streamer = kind == "appletv" ? GetString(data, "appleTvDevice") : GetString(data, "castDevice");
            var label = IsStreamer(kind) && streamer.Length > 0 ? $"{kind}:{streamer}" : $"{kind}:{device}";
            return new SessionInterval(label, kind, GetString(data, "title"), input, start, end);
        }
    }
}
