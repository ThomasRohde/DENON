using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Mcp;

namespace Denon.App.Core.Services;

public sealed record TelemetryEventEnvelope(
    [property: JsonPropertyName("ts")] string Timestamp,
    [property: JsonPropertyName("day")] string DayOfWeek,
    [property: JsonPropertyName("block")] string TimeBlock,
    [property: JsonPropertyName("stream")] string Stream,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, object?>? Data);

public sealed record TelemetryStreamSummary(
    string Stream,
    int FileCount,
    long TotalBytes,
    DateTimeOffset? LastModified);

public sealed record TelemetryStatsSnapshot(
    bool Enabled,
    string EventsDirectory,
    int TotalFiles,
    long TotalBytes,
    long EventsRecordedThisSession,
    int RetentionDays,
    int MediaDwellSeconds,
    IReadOnlyList<TelemetryStreamSummary> Streams);

/// <summary>
/// High-performance, zero-UI-lag structured NDJSON (.jsonl) event telemetry log split by domain/device
/// (media, avr, tv, lighting, control, plugins/<id>) in monthly files with automatic 90-day / 50 MB retention
/// and 6-second origin watermarks to distinguish app commands from external remotes / phone streams.
/// </summary>
public sealed class EventTelemetryLog : IAsyncDisposable
{
    public const string StreamMedia = "media";
    public const string StreamAvr = "avr";
    public const string StreamTv = "tv";
    public const string StreamLighting = "lighting";
    public const string StreamControl = "control";

    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] CoreStreams =
    [
        StreamMedia,
        StreamAvr,
        StreamTv,
        StreamLighting,
        StreamControl,
    ];

    private readonly AppSettingsStore _settingsStore;
    private readonly AppLogStore _logStore;
    private readonly RoomEventBus _eventBus;
    private readonly string _eventsDir;
    private readonly string _pluginEventsDir;
    private readonly Channel<TelemetryEventEnvelope> _channel;
    private readonly ConcurrentDictionary<string, OriginWatermark> _originWatermarks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly MediaSessionOverlapTracker _sessionOverlap = new();
    private readonly CancellationTokenSource _writerCts = new();
    private readonly Task _writerTask;

    private long _sessionEventCount;
    private bool _disposed;

    public EventTelemetryLog(AppSettingsStore settingsStore, AppLogStore logStore, RoomEventBus eventBus)
        : this(settingsStore, logStore, eventBus, AppDataPaths.EventsLogsDirectory)
    {
    }

    public EventTelemetryLog(AppSettingsStore settingsStore, AppLogStore logStore, RoomEventBus eventBus, string eventsDirectory)
    {
        _settingsStore = settingsStore;
        _logStore = logStore;
        _eventBus = eventBus;
        _eventsDir = eventsDirectory;
        _pluginEventsDir = Path.Combine(eventsDirectory, "plugins");

        Directory.CreateDirectory(_eventsDir);
        Directory.CreateDirectory(_pluginEventsDir);
        _channel = Channel.CreateBounded<TelemetryEventEnvelope>(new BoundedChannelOptions(2048)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _writerTask = Task.Run(() => WriterLoopAsync(_writerCts.Token));
        PruneOldFiles();
    }

    public event Action? StatsChanged;

    public bool IsEnabled => _settingsStore.Current.Telemetry.Enabled;

    public long EventsRecordedThisSession => Interlocked.Read(ref _sessionEventCount);

    /// <summary>
    /// Stamps a 6-second origin watermark for a device/domain (e.g. "avr", "tv", "lighting", "plugin:smart-tv")
    /// so subsequent hardware state notifications know whether the change came from Desktop UI, MCP Agent,
    /// Phone Remote, Automation, Hotkey, or an external physical remote / phone cast.
    /// </summary>
    public void MarkOrigin(
        string domain,
        string origin,
        string? controlEvent = null,
        IReadOnlyDictionary<string, object?>? controlData = null,
        bool overwriteRecent = true)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(origin))
        {
            return;
        }

        var key = domain.Trim().ToLowerInvariant();
        var nowUtc = DateTimeOffset.UtcNow;

        if (!overwriteRecent &&
            _originWatermarks.TryGetValue(key, out var existing) &&
            (nowUtc - existing.StampedUtc) <= TimeSpan.FromSeconds(2) &&
            !string.Equals(existing.Origin, "desktop_ui", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _originWatermarks[key] = new OriginWatermark(origin.Trim(), nowUtc);

        if (!string.IsNullOrWhiteSpace(controlEvent))
        {
            Record(StreamControl, key, controlEvent, origin, controlData);
        }
    }

    /// <summary>
    /// Resolves the most likely origin for a hardware state change in <paramref name="domain"/>:
    /// returns the recent app command origin if stamped within 6 seconds, otherwise "external".
    /// </summary>
    public string ResolveOrigin(string domain, string fallback = "external")
    {
        if (!string.IsNullOrWhiteSpace(domain) &&
            _originWatermarks.TryGetValue(domain.Trim().ToLowerInvariant(), out var mark) &&
            (DateTimeOffset.UtcNow - mark.StampedUtc) <= TimeSpan.FromSeconds(6))
        {
            return mark.Origin;
        }

        return fallback;
    }

    /// <summary>Ended sessions the overlap tracker knows, ending after <paramref name="since"/>.</summary>
    internal IReadOnlyList<MediaSessionOverlapTracker.SessionInterval> ClosedSessionsSince(DateTimeOffset since) =>
        _sessionOverlap.ClosedSince(since);

    /// <summary>Gives the overlap tracker the ended sessions of an earlier run (crash recovery).</summary>
    internal void RestoreClosedSessions(IEnumerable<MediaSessionOverlapTracker.SessionInterval> intervals) =>
        _sessionOverlap.RestoreClosed(intervals);

    /// <summary>Where the monitor keeps the sessions that are open now, to close them after a crash.</summary>
    public string OpenSessionsPath => Path.Combine(_eventsDir, "open-sessions.json");

    /// <summary>
    /// Publishes the event on the live <see cref="RoomEventBus"/> (always), and when event logging is on, also
    /// enqueues it to the daily JSONL file of its stream without blocking the caller.
    /// </summary>
    /// <param name="at">When it happened, for an event written later (a session closed after a crash); else now.</param>
    /// <returns>The published event, or null after dispose.</returns>
    public RoomEvent? Record(
        string stream,
        string device,
        string eventName,
        string origin,
        IReadOnlyDictionary<string, object?>? data = null,
        DateTimeOffset? at = null)
    {
        if (_disposed)
        {
            return null;
        }

        var cleanStream = NormalizeStreamName(stream);
        var now = at ?? DateTimeOffset.Now;
        if (cleanStream == StreamMedia)
        {
            // Adds coveredSec / exclusiveSec to session.ended so parallel sessions of one viewing
            // (cast + LG HDMI + Denon TV Audio) are not counted twice.
            data = _sessionOverlap.Annotate(device?.Trim() ?? string.Empty, eventName?.Trim() ?? string.Empty, data, now);
        }

        var cleanDevice = string.IsNullOrWhiteSpace(device) ? cleanStream : device.Trim();
        var cleanEvent = string.IsNullOrWhiteSpace(eventName) ? "event" : eventName.Trim();
        var cleanOrigin = string.IsNullOrWhiteSpace(origin) ? "app" : origin.Trim();
        var cleanData = data is { Count: > 0 } ? data : null;

        var published = new RoomEvent(now, cleanStream, cleanDevice, cleanEvent, cleanOrigin, cleanData);
        _eventBus.Publish(published);

        if (!IsEnabled)
        {
            return published;
        }

        var envelope = new TelemetryEventEnvelope(
            Timestamp: now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            DayOfWeek: now.ToString("ddd", CultureInfo.InvariantCulture),
            TimeBlock: RoomIntelligenceService.GetCurrentTimeBlock(),
            Stream: cleanStream,
            Device: cleanDevice,
            Event: cleanEvent,
            Origin: cleanOrigin,
            Data: cleanData);

        if (_channel.Writer.TryWrite(envelope))
        {
            Interlocked.Increment(ref _sessionEventCount);
        }

        return published;
    }

    public IReadOnlyList<string> ListAvailableStreams()
    {
        var streams = new HashSet<string>(CoreStreams, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(_pluginEventsDir))
            {
                foreach (var file in Directory.EnumerateFiles(_pluginEventsDir, "*.jsonl"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var slug = StripDateSuffix(name);
                    if (!string.IsNullOrWhiteSpace(slug))
                    {
                        streams.Add($"plugin:{slug}");
                    }
                }
            }
        }
        catch
        {
            // Ignore directory read errors.
        }

        return streams.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public TelemetryStatsSnapshot GetStats()
    {
        var cfg = _settingsStore.Current.Telemetry;
        var byStream = new Dictionary<string, (int Count, long Bytes, DateTimeOffset? LastMod)>(StringComparer.OrdinalIgnoreCase);
        var totalFiles = 0;
        var totalBytes = 0L;

        try
        {
            if (Directory.Exists(_eventsDir))
            {
                foreach (var file in Directory.EnumerateFiles(_eventsDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(file);
                    totalFiles++;
                    totalBytes += fi.Length;

                    var streamKey = InferStreamKeyFromPath(file);
                    var mod = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
                    if (byStream.TryGetValue(streamKey, out var cur))
                    {
                        byStream[streamKey] = (cur.Count + 1, cur.Bytes + fi.Length, cur.LastMod > mod ? cur.LastMod : mod);
                    }
                    else
                    {
                        byStream[streamKey] = (1, fi.Length, mod);
                    }
                }
            }
        }
        catch
        {
            // Best effort.
        }

        var summaries = byStream
            .OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static kv => new TelemetryStreamSummary(kv.Key, kv.Value.Count, kv.Value.Bytes, kv.Value.LastMod))
            .ToList();

        return new TelemetryStatsSnapshot(
            Enabled: cfg.Enabled,
            EventsDirectory: _eventsDir,
            TotalFiles: totalFiles,
            TotalBytes: totalBytes,
            EventsRecordedThisSession: EventsRecordedThisSession,
            RetentionDays: cfg.RetentionDays,
            MediaDwellSeconds: cfg.MediaDwellSeconds,
            Streams: summaries);
    }

    /// <summary>
    /// Queries recent structured events across one stream (e.g. "media", "avr", "tv", "lighting",
    /// "control", "plugin:home-assistant") or "all", newest first.
    /// </summary>
    public IReadOnlyList<JsonElement> QueryRecentEvents(
        string? stream = null,
        int hours = 24,
        int limit = 80,
        string? originFilter = null)
    {
        var clampedHours = Math.Clamp(hours, 1, 24 * 90);
        var clampedLimit = Math.Clamp(limit, 1, 500);
        var cutoff = DateTimeOffset.Now.AddHours(-clampedHours);
        var cleanStream = string.IsNullOrWhiteSpace(stream) || stream.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : NormalizeStreamName(stream);
        var cleanOrigin = string.IsNullOrWhiteSpace(originFilter) ? null : originFilter.Trim();

        var files = GetCandidateFiles(cleanStream)
            .OrderByDescending(static f => File.GetLastWriteTimeUtc(f))
            .ToList();

        var results = new List<(DateTimeOffset Ts, JsonElement Element)>();

        foreach (var file in files)
        {
            try
            {
                var lines = ReadTailLines(file, maxLines: Math.Min(2500, clampedLimit * 10));
                for (var i = lines.Count - 1; i >= 0; i--)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (root.TryGetProperty("ts", out var tsEl) &&
                        tsEl.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
                    {
                        if (ts < cutoff)
                        {
                            continue;
                        }

                        if (cleanOrigin is not null)
                        {
                            var evOrigin = root.TryGetProperty("origin", out var oEl) ? oEl.GetString() : null;
                            if (!string.Equals(evOrigin, cleanOrigin, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }

                        results.Add((ts, root.Clone()));
                    }
                }
            }
            catch
            {
                // Skip unreadable or partially written file cleanly.
            }
        }

        return results
            .OrderByDescending(static r => r.Ts)
            .Take(clampedLimit)
            .Select(static r => r.Element)
            .ToList();
    }

    /// <summary>
    /// Reads every event of one stream since <paramref name="since"/> (whole files, not only the tail), oldest first.
    /// Optional filter on the event name. Used for reports such as watch time per day.
    /// </summary>
    public IReadOnlyList<JsonElement> ReadEventsSince(string stream, DateTimeOffset since, string? eventName = null)
    {
        var cleanStream = NormalizeStreamName(stream);
        var results = new List<(DateTimeOffset Ts, JsonElement Element)>();

        foreach (var file in GetCandidateFiles(cleanStream))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < since.UtcDateTime)
                {
                    continue; // Nothing new in this file since the cutoff.
                }

                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line) ||
                        (eventName is not null && !line.Contains(eventName, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object ||
                            (eventName is not null && !(root.TryGetProperty("event", out var evEl) && evEl.GetString() == eventName)) ||
                            !root.TryGetProperty("ts", out var tsEl) ||
                            !DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts) ||
                            ts < since)
                        {
                            continue;
                        }

                        results.Add((ts, root.Clone()));
                    }
                    catch (JsonException)
                    {
                        // Skip a partially written line.
                    }
                }
            }
            catch (IOException)
            {
                // Skip a file that is locked or removed.
            }
        }

        return results.OrderBy(static r => r.Ts).Select(static r => r.Element).ToList();
    }

    public void PruneOldFiles()
    {
        var cfg = _settingsStore.Current.Telemetry;
        var retentionCutoff = DateTime.UtcNow.AddDays(-Math.Clamp(cfg.RetentionDays, 7, 365));
        var maxBytes = Math.Clamp(cfg.MaxTotalSizeMb, 5, 500) * 1024L * 1024L;

        try
        {
            if (!Directory.Exists(_eventsDir))
            {
                return;
            }

            var files = Directory
                .EnumerateFiles(_eventsDir, "*.jsonl", SearchOption.AllDirectories)
                .Select(static p => new FileInfo(p))
                .OrderBy(static fi => fi.LastWriteTimeUtc)
                .ToList();

            var remaining = new List<FileInfo>();
            foreach (var fi in files)
            {
                if (fi.LastWriteTimeUtc < retentionCutoff)
                {
                    try { fi.Delete(); } catch { }
                }
                else
                {
                    remaining.Add(fi);
                }
            }

            var totalBytes = remaining.Sum(static f => f.Length);
            foreach (var fi in remaining)
            {
                if (totalBytes <= maxBytes)
                {
                    break;
                }

                // Never delete today's active file unless it alone exceeds the cap.
                var todayTag = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (fi.Name.Contains(todayTag, StringComparison.OrdinalIgnoreCase) && remaining.Count > 1)
                {
                    continue;
                }

                try
                {
                    var len = fi.Length;
                    fi.Delete();
                    totalBytes -= len;
                }
                catch
                {
                }
            }
        }
        catch
        {
            // Best effort.
        }
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct);
        try
        {
            if (Directory.Exists(_eventsDir))
            {
                foreach (var file in Directory.EnumerateFiles(_eventsDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            Interlocked.Exchange(ref _sessionEventCount, 0);
            _logStore.Info("[Telemetry] Cleared all structured event history files.");
        }
        finally
        {
            _ioGate.Release();
        }

        StatsChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        try
        {
            await _writerTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            _writerCts.Cancel();
        }

        _writerCts.Dispose();
        _ioGate.Dispose();
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        var batch = new List<TelemetryEventEnvelope>(64);

        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < 64 && _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                await WriteBatchAsync(batch, ct);
                StatsChanged?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logStore.Warning($"[Telemetry] Event writer warning: {ex.Message}");
        }
    }

    private async Task WriteBatchAsync(List<TelemetryEventEnvelope> batch, CancellationToken ct)
    {
        await _ioGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_eventsDir);
            Directory.CreateDirectory(_pluginEventsDir);
            var byPath = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in batch)
            {
                var path = ResolveMonthlyFilePath(item.Stream, DateTime.Now);
                if (!byPath.TryGetValue(path, out var sb))
                {
                    sb = new StringBuilder(256);
                    byPath[path] = sb;
                }

                sb.AppendLine(JsonSerializer.Serialize(item, JsonLineOptions));
            }

            foreach (var (path, sb) in byPath)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                await File.AppendAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private string ResolveMonthlyFilePath(string stream, DateTime localNow)
    {
        // Daily files ensure RetentionDays is honored at exact day granularity.
        var day = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (stream.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
        {
            var pluginSlug = SlugNormalizer.Normalize(stream["plugin:".Length..]);
            if (string.IsNullOrWhiteSpace(pluginSlug))
            {
                pluginSlug = "plugin";
            }

            return Path.Combine(_pluginEventsDir, $"{pluginSlug}-{day}.jsonl");
        }

        return Path.Combine(_eventsDir, $"{stream}-{day}.jsonl");
    }

    private static string NormalizeStreamName(string? stream)
    {
        var raw = (stream ?? string.Empty).Trim().ToLowerInvariant();
        if (raw is StreamMedia or StreamAvr or StreamTv or StreamLighting or StreamControl)
        {
            return raw;
        }

        if (raw.StartsWith("plugin:", StringComparison.Ordinal))
        {
            var slug = SlugNormalizer.Normalize(raw["plugin:".Length..]);
            return string.IsNullOrWhiteSpace(slug) ? "plugin:custom" : $"plugin:{slug}";
        }

        var normalized = SlugNormalizer.Normalize(raw);
        return string.IsNullOrWhiteSpace(normalized) ? StreamControl : $"plugin:{normalized}";
    }

    private IEnumerable<string> GetCandidateFiles(string? stream)
    {
        if (!Directory.Exists(_eventsDir))
        {
            return [];
        }

        if (stream is null)
        {
            return Directory.EnumerateFiles(_eventsDir, "*.jsonl", SearchOption.AllDirectories);
        }

        if (stream.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(_pluginEventsDir))
            {
                return [];
            }

            var slug = SlugNormalizer.Normalize(stream["plugin:".Length..]);
            return Directory.EnumerateFiles(_pluginEventsDir, $"{slug}-*.jsonl", SearchOption.TopDirectoryOnly);
        }

        return Directory.EnumerateFiles(_eventsDir, $"{stream}-*.jsonl", SearchOption.TopDirectoryOnly);
    }

    private string InferStreamKeyFromPath(string filePath)
    {
        var isPlugin = filePath.StartsWith(_pluginEventsDir, StringComparison.OrdinalIgnoreCase);
        var name = StripDateSuffix(Path.GetFileNameWithoutExtension(filePath));
        return isPlugin ? $"plugin:{name}" : name;
    }

    private static string StripDateSuffix(string baseName)
    {
        // Matches -yyyy-MM-dd at the end of the file name
        if (baseName.Length > 11 &&
            baseName[^11] == '-' && baseName[^6] == '-' && baseName[^3] == '-' &&
            int.TryParse(baseName[^10..^6], out _) &&
            int.TryParse(baseName[^5..^3], out _) &&
            int.TryParse(baseName[^2..], out _))
        {
            return baseName[..^11];
        }

        return baseName;
    }

    private static List<string> ReadTailLines(string path, int maxLines)
    {
        var queue = new Queue<string>(Math.Min(maxLines + 1, 512));
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (queue.Count >= maxLines)
            {
                queue.Dequeue();
            }

            queue.Enqueue(line);
        }

        return [.. queue];
    }

    private sealed record OriginWatermark(string Origin, DateTimeOffset StampedUtc);
}
