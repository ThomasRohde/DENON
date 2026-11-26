using System.Globalization;
using System.Text.Json;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;

namespace Denon.App.Core.Services;

/// <summary>
/// What plays through the receiver, as Volume memory tells sources apart: a live TV channel, a TV app or HDMI input,
/// a Chromecast or Apple TV app, or a receiver input. <see cref="Key"/> is stable and lower case
/// ("tv_channel:dr1"); <see cref="Label"/> is for people ("DR1").
/// </summary>
public sealed record ListeningSource(string Key, string Kind, string Label)
{
    public static ListeningSource Create(string kind, string label)
    {
        var clean = label.Trim();
        return new ListeningSource($"{kind}:{clean.ToLowerInvariant()}", kind, clean);
    }

    /// <summary>
    /// The source that plays now, from the receiver and the plugins (the same reading as the now-playing hero).
    /// Null while the receiver sleeps or its state is not known.
    /// </summary>
    public static ListeningSource? From(ReceiverStatus? status, IReadOnlyList<PluginRecordSnapshot>? plugins)
    {
        if (status is null || !status.IsPoweredOn || !status.IsMainZoneOn || string.IsNullOrWhiteSpace(status.InputSource))
        {
            return null;
        }

        var resolved = NowPlayingResolver.Resolve(status, null, plugins);
        if (resolved.Cast is { } cast && cast.PluginId != NowPlayingResolver.HomePodPluginId && cast.App.Length > 0)
        {
            return Create(cast.IsAppleTv ? "appletv" : "cast", cast.App);
        }

        if (resolved.LiveTv is { } live && live.ChannelName.Length > 0)
        {
            return Create("tv_channel", live.ChannelName);
        }

        if (NowPlayingResolver.IsTvAudioInput(status.InputSource) &&
            NowPlayingResolver.TryExtractSmartTvNowShowing(plugins) is { } tv && !string.IsNullOrWhiteSpace(tv.Showing))
        {
            // The TV's own app or input ("Netflix", "HDMI 2"); Live TV without guide details reads "Live TV (Ch 1 • DR1)".
            return WatchTimeReport.ChannelFromShowing(tv.Showing) is { } channel
                ? Create("tv_channel", channel)
                : Create("tv_app", tv.Showing.Trim());
        }

        return Create("input", DenonCatalog.GetFriendlyInputName(status.InputSource));
    }

    /// <summary>The source a logged session (session.ended) was, with the same keys as <see cref="From"/>; null for others.</summary>
    public static ListeningSource? FromSession(string kind, JsonElement data, string title) => kind switch
    {
        "cast" or "appletv" or "tv_channel" or "tv_app" => Create(kind, WatchTimeReport.AppLabel(kind, data, title)),
        "avr_input" when data.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.String &&
                         input.GetString() is { Length: > 0 } code => Create("input", DenonCatalog.GetFriendlyInputName(code)),
        _ => null,
    };
}

/// <summary>A learned listening volume: where the user settles for this source, weighted by listening time.</summary>
public sealed class LearnedVolume
{
    public string Key { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>Denon volume 0–98, kept with decimals while it learns.</summary>
    public double Level { get; set; }

    /// <summary>Seconds listened to this source while learning (from the logs at the first start, then live).</summary>
    public double Seconds { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>
/// The learned volumes, in <c>volume-memory.json</c> next to the settings (they change while listening, so they are
/// not in settings.json, whose saves wake every settings listener).
/// </summary>
public sealed class VolumeMemoryStore
{
    /// <summary>
    /// How fast a level follows the volume the user listens at: after this much listening time at a new volume, the
    /// level has moved about 63 % of the way.
    /// </summary>
    public static readonly TimeSpan LearningTimeConstant = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly AppLogStore _log;
    private readonly Lock _gate = new();
    private Dictionary<string, LearnedVolume> _levels = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;
    private bool _loaded;

    public VolumeMemoryStore(AppLogStore log, string? path = null)
    {
        _log = log;
        _path = path ?? Path.Combine(Infrastructure.AppDataPaths.RootDirectory, "volume-memory.json");
    }

    /// <summary>Raised after a level was learned, set, forgotten or seeded.</summary>
    public event Action? Changed;

    /// <summary>True when the file did not exist yet (the first start): <see cref="Seed"/> fills it from the logs.</summary>
    public bool IsNew { get; private set; }

    public void Load()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            try
            {
                if (!File.Exists(_path))
                {
                    IsNew = true;
                    return;
                }

                var list = JsonSerializer.Deserialize<List<LearnedVolume>>(File.ReadAllText(_path), Json) ?? [];
                _levels = list
                    .Where(static l => l.Key.Length > 0)
                    .GroupBy(static l => l.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(static g => g.Key, static g => g.Last(), StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException ex)
            {
                // A broken file: keep it aside and start again from the logs, as on the first start.
                IsNew = true;
                TryMoveAside();
                _log.Warning($"[Volume memory] {Path.GetFileName(_path)} could not be read ({ex.Message}); kept as .bad and learning again from the logs.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning($"[Volume memory] Could not read {Path.GetFileName(_path)}: {ex.Message}. Starting empty.");
            }
        }
    }

    private void TryMoveAside()
    {
        try
        {
            File.Move(_path, _path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public IReadOnlyList<LearnedVolume> Snapshot()
    {
        lock (_gate)
        {
            return _levels.Values
                .Select(static l => new LearnedVolume { Key = l.Key, Kind = l.Kind, Label = l.Label, Level = l.Level, Seconds = l.Seconds, UpdatedUtc = l.UpdatedUtc })
                .OrderByDescending(static l => l.Seconds)
                .ToList();
        }
    }

    public LearnedVolume? Find(string key)
    {
        lock (_gate)
        {
            return _levels.TryGetValue(key, out var level)
                ? new LearnedVolume { Key = level.Key, Kind = level.Kind, Label = level.Label, Level = level.Level, Seconds = level.Seconds, UpdatedUtc = level.UpdatedUtc }
                : null;
        }
    }

    /// <summary>
    /// Learns from <paramref name="seconds"/> of listening to <paramref name="source"/> at <paramref name="volume"/>:
    /// the level moves toward it by 1 − e^(−seconds / <see cref="LearningTimeConstant"/>). A new source starts at it.
    /// </summary>
    public void Learn(ListeningSource source, double volume, double seconds, DateTimeOffset now)
    {
        if (seconds <= 0 || volume <= 0.5)
        {
            return;
        }

        lock (_gate)
        {
            if (!_levels.TryGetValue(source.Key, out var level))
            {
                _levels[source.Key] = new LearnedVolume
                {
                    Key = source.Key, Kind = source.Kind, Label = source.Label, Level = volume, Seconds = seconds, UpdatedUtc = now,
                };
            }
            else
            {
                var weight = 1 - Math.Exp(-seconds / LearningTimeConstant.TotalSeconds);
                level.Level += (volume - level.Level) * weight;
                level.Seconds += seconds;
                level.Label = source.Label;
                level.UpdatedUtc = now;
            }

            _dirty = true;
        }

        Changed?.Invoke();
    }

    /// <summary>Sets a level by hand (the Volume memory page). Learning goes on from it.</summary>
    public void Set(string key, double level, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_levels.TryGetValue(key, out var learned))
            {
                return;
            }

            learned.Level = Math.Clamp(level, 0, 98);
            learned.UpdatedUtc = now;
            _dirty = true;
        }

        Changed?.Invoke();
    }

    public void Forget(string key)
    {
        lock (_gate)
        {
            if (!_levels.Remove(key))
            {
                return;
            }

            _dirty = true;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The first start: each source's level from the logged sessions (session.ended with avgVolume), weighted by the
    /// time it played alone. Sources heard for less than <paramref name="minimum"/> are left to learn live.
    /// </summary>
    public int Seed(IEnumerable<JsonElement> sessionEnds, TimeSpan minimum, DateTimeOffset now)
    {
        var sums = new Dictionary<string, (ListeningSource Source, double Weighted, double Seconds)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in sessionEnds)
        {
            if (!ev.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("kind", out var kindEl) || kindEl.GetString() is not { } kind ||
                Number(data, "avgVolume") is not { } volume || volume <= 0.5)
            {
                continue;
            }

            var title = data.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            if (ListeningSource.FromSession(kind, data, title) is not { } source)
            {
                continue;
            }

            var seconds = Number(data, "exclusiveSec") ?? Number(data, "durationSec") ?? 0;
            if (kind == "avr_input")
            {
                seconds = Number(data, "durationSec") ?? 0; // under a TV or cast session it has no time of its own
            }

            if (seconds <= 0)
            {
                continue;
            }

            sums.TryGetValue(source.Key, out var sum);
            sums[source.Key] = (source, sum.Weighted + volume * seconds, sum.Seconds + seconds);
        }

        var added = 0;
        lock (_gate)
        {
            foreach (var (key, sum) in sums)
            {
                if (sum.Seconds < minimum.TotalSeconds || _levels.ContainsKey(key))
                {
                    continue;
                }

                _levels[key] = new LearnedVolume
                {
                    Key = key, Kind = sum.Source.Kind, Label = sum.Source.Label, Level = sum.Weighted / sum.Seconds, Seconds = sum.Seconds, UpdatedUtc = now,
                };
                added++;
            }

            _dirty = true;
            IsNew = false;
        }

        Changed?.Invoke();
        return added;
    }

    /// <summary>Writes the file when something changed since the last save.</summary>
    public void SaveIfChanged()
    {
        List<LearnedVolume> list;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            list = _levels.Values.OrderBy(static l => l.Key, StringComparer.Ordinal).ToList();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _dirty = true; // Next time.
            }

            _log.Warning($"[Volume memory] Could not save: {ex.Message}");
        }
    }

    private static double? Number(JsonElement data, string name) =>
        data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;

    public static string FormatLevel(double level) => Math.Round(level).ToString(CultureInfo.InvariantCulture);
}
