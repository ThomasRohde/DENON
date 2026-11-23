namespace Denon.App.Core.Services;

/// <summary>
/// Stops double counting of watch time on the <c>media</c> stream.
///
/// One viewing can produce several parallel sessions: a Chromecast program (kind "cast"),
/// the LG TV on the HDMI input it is plugged into (kind "tv_app"), and the Denon on "TV Audio"
/// via ARC (kind "avr_input"). Each <c>session.ended</c> gets:
/// <list type="bullet">
/// <item><c>coveredSec</c>: seconds during which a more specific session ran at the same time,</item>
/// <item><c>coveredBy</c>: the kinds/devices of those sessions (only when coveredSec &gt; 0),</item>
/// <item><c>exclusiveSec</c>: <c>durationSec - coveredSec</c>. Summing exclusiveSec gives real time.</item>
/// </list>
/// Covering rules (more specific covers less specific):
/// cast → tv_app on an HDMI input; cast → avr_input "TV" (ARC) or the cast's own receiver input;
/// tv_app/tv_channel → avr_input "TV" (ARC).
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

        var key = $"{device}|{GetString(data, "castDevice")}";
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
            var self = SessionInterval.From(device, kind, data, now.AddSeconds(-durationSec), now);

            var coverers = _open.Values.Concat(_closed).Where(c => Covers(c, self)).ToList();
            var covered = UnionSeconds(coverers.Select(c => (Start: Max(c.Start, self.Start), End: Min(c.End ?? now, self.End!.Value))));
            var coveredSec = (int)Math.Round(Math.Min(covered, durationSec));

            var result = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase)
            {
                ["coveredSec"] = coveredSec,
                ["exclusiveSec"] = (int)Math.Max(0, Math.Round(durationSec) - coveredSec),
            };
            if (coveredSec > 0)
            {
                result["coveredBy"] = coverers
                    .Where(c => Min(c.End ?? now, self.End!.Value) > Max(c.Start, self.Start))
                    .Select(c => c.Label)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
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

    /// <summary>True when <paramref name="coverer"/> is a more specific view of the same watching as <paramref name="target"/>.</summary>
    internal static bool Covers(SessionInterval coverer, SessionInterval target)
    {
        if (ReferenceEquals(coverer, target))
        {
            return false;
        }

        if (coverer.Kind == "cast")
        {
            return (target.Kind == "tv_app" && target.Title.Contains("HDMI", StringComparison.OrdinalIgnoreCase)) ||
                   (target.Kind == "avr_input" && (IsTvInput(target.Input) ||
                       (!string.IsNullOrEmpty(coverer.Input) && string.Equals(coverer.Input, target.Input, StringComparison.OrdinalIgnoreCase))));
        }

        if (coverer.Kind is "tv_app" or "tv_channel")
        {
            return target.Kind == "avr_input" && IsTvInput(target.Input);
        }

        return false;
    }

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
            // Cast sessions carry the Denon input as receiverInput; Denon sessions carry it as input.
            var input = kind == "cast" ? GetString(data, "receiverInput") : GetString(data, "input");
            var castDevice = GetString(data, "castDevice");
            var label = kind == "cast" && castDevice.Length > 0 ? $"cast:{castDevice}" : $"{kind}:{device}";
            return new SessionInterval(label, kind, GetString(data, "title"), input, start, end);
        }
    }
}
