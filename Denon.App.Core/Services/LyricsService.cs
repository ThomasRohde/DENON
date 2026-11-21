using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Denon.App.Core.Services;

public sealed record LyricLine(double TimestampSeconds, string Text);

public sealed record LyricsDocument(
    string QueryKey,
    string TrackName,
    string ArtistName,
    bool IsSynced,
    IReadOnlyList<LyricLine> Lines);

/// <summary>
/// Free, zero-API-key live synced lyrics fetcher powered by LRCLIB (https://lrclib.net).
/// Parses LRC [mm:ss.xx] timestamps with automatic fallback to plain lyrics and in-memory caching.
/// </summary>
public sealed partial class LyricsService : IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, LyricsDocument?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LyricsService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DenonDesktop/1.3 (https://github.com/ThomasRohde/DENON)");
    }

    public async Task<LyricsDocument?> FetchLyricsAsync(string title, string? artist = null, CancellationToken cancellationToken = default)
    {
        var cleanTitle = CleanTrackTitle(title);
        if (string.IsNullOrWhiteSpace(cleanTitle) ||
            cleanTitle.Equals("Ready for Playback", StringComparison.OrdinalIgnoreCase) ||
            cleanTitle.Equals("Receiver in Standby", StringComparison.OrdinalIgnoreCase) ||
            cleanTitle.Equals("HEOS Stream", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cleanArtist = CleanArtistName(artist);
        var key = $"{cleanArtist}||{cleanTitle}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var query = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTitle : $"{cleanArtist} {cleanTitle}";
            var url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
            using var resp = await _http.GetAsync(url, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _cache[key] = null;
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                _cache[key] = null;
                return null;
            }

            JsonElement? bestSynced = null;
            JsonElement? bestPlain = null;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var synced = item.TryGetProperty("syncedLyrics", out var sEl) ? sEl.GetString() : null;
                var plain = item.TryGetProperty("plainLyrics", out var pEl) ? pEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(synced))
                {
                    bestSynced = item;
                    break;
                }

                if (bestPlain is null && !string.IsNullOrWhiteSpace(plain))
                {
                    bestPlain = item;
                }
            }

            var chosen = bestSynced ?? bestPlain;
            if (chosen is null)
            {
                _cache[key] = null;
                return null;
            }

            var chosenEl = chosen.Value;
            var trackName = chosenEl.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? cleanTitle : cleanTitle;
            var artistName = chosenEl.TryGetProperty("artistName", out var an) ? an.GetString() ?? cleanArtist : cleanArtist;
            var syncedRaw = chosenEl.TryGetProperty("syncedLyrics", out var sr) ? sr.GetString() : null;
            var plainRaw = chosenEl.TryGetProperty("plainLyrics", out var pr) ? pr.GetString() : null;

            LyricsDocument result;
            if (!string.IsNullOrWhiteSpace(syncedRaw))
            {
                var lines = ParseLrc(syncedRaw);
                result = new LyricsDocument(key, trackName, artistName, lines.Count > 0, lines);
            }
            else
            {
                var lines = (plainRaw ?? string.Empty)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select((t, idx) => new LyricLine(idx * 4.0, t))
                    .ToList();
                result = new LyricsDocument(key, trackName, artistName, IsSynced: false, lines);
            }

            _cache[key] = result;
            return result;
        }
        catch
        {
            return null;
        }
    }

    public static (string Current, string Next) GetDisplayLines(LyricsDocument? doc, double positionSeconds)
    {
        if (doc is null || doc.Lines.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (!doc.IsSynced)
        {
            var idx = Math.Clamp((int)(positionSeconds / 4.0), 0, doc.Lines.Count - 1);
            var cur = doc.Lines[idx].Text;
            var nxt = idx + 1 < doc.Lines.Count ? doc.Lines[idx + 1].Text : string.Empty;
            return (cur, nxt);
        }

        var activeIdx = -1;
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            if (doc.Lines[i].TimestampSeconds <= positionSeconds + 0.35)
            {
                activeIdx = i;
            }
            else
            {
                break;
            }
        }

        if (activeIdx < 0)
        {
            return ("♪  " + doc.Lines[0].Text, doc.Lines.Count > 1 ? doc.Lines[1].Text : string.Empty);
        }

        var current = doc.Lines[activeIdx].Text;
        var next = activeIdx + 1 < doc.Lines.Count ? doc.Lines[activeIdx + 1].Text : string.Empty;
        return (current, next);
    }

    public void Dispose() => _http.Dispose();

    private static List<LyricLine> ParseLrc(string raw)
    {
        var lines = new List<LyricLine>();
        foreach (var rawLine in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = LrcTimestampRegex().Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            var text = LrcTimestampRegex().Replace(rawLine, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (Match m in matches)
            {
                if (int.TryParse(m.Groups[1].Value, out var min) &&
                    double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
                {
                    lines.Add(new LyricLine((min * 60.0) + sec, text));
                }
            }
        }

        return lines.OrderBy(static l => l.TimestampSeconds).ToList();
    }

    private static string CleanTrackTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var cleaned = NoiseTagRegex().Replace(raw, string.Empty).Trim();
        return cleaned.Length > 0 ? cleaned : raw.Trim();
    }

    private static string CleanArtistName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var first = raw.Split(['•', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return first.Replace(" - Topic", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
    }

    [GeneratedRegex(@"\[(\d{1,2}):(\d{2}(?:\.\d{1,3})?)\]")]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex(@"\s*[\(\[](?:official\s*(?:music\s*)?video|official\s*audio|lyrics?|hd|4k|remaster(?:ed)?|live(?:\s+in\s+concert)?|visualizer)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseTagRegex();
}
