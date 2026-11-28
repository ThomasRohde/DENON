using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Denon.App.Core.Mcp;

/// <summary>The outcome of one MCP tool call in the audit log.</summary>
public static class McpAuditOutcome
{
    public const string Ok = "ok";
    public const string Refused = "refused";
    public const string Error = "error";
}

/// <summary>
/// One MCP tool call. It holds the argument names only: never their values, and never a token.
/// </summary>
public sealed record McpAuditEntry(
    DateTimeOffset Timestamp,
    string? RemoteIp,
    string? Client,
    string Tool,
    IReadOnlyList<string> ArgumentKeys,
    string Outcome,
    long DurationMs);

/// <summary>
/// The MCP audit log: one JSON line per tool call in <c>logs\mcp-audit-yyyyMMdd-HHmmss-NN.jsonl</c>. A new file starts
/// on a new day (UTC) or when the file reaches <see cref="MaxFileBytes"/>; only the newest <see cref="MaxFiles"/>
/// files are kept. A write that fails is dropped: the audit log never stops a tool call.
/// </summary>
public sealed class McpAuditLog
{
    public const string FilePrefix = "mcp-audit-";
    public const string FileExtension = ".jsonl";

    /// <summary>The most argument names kept per line, and the longest name.</summary>
    internal const int MaxKeys = 32;
    internal const int MaxKeyLength = 64;

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
    };

    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();
    private string? _currentPath;
    private DateTime _currentDay;

    public McpAuditLog(string directory, Func<DateTimeOffset>? clock = null, long maxFileBytes = 1024 * 1024, int maxFiles = 7)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A directory is required.", nameof(directory));
        }

        Directory = directory;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        MaxFileBytes = Math.Max(1024, maxFileBytes);
        MaxFiles = Math.Max(1, maxFiles);
    }

    /// <summary>The folder of the audit files.</summary>
    public string Directory { get; }

    public long MaxFileBytes { get; }

    public int MaxFiles { get; }

    /// <summary>The file the next line goes to (null before the first write).</summary>
    public string? CurrentFile
    {
        get
        {
            lock (_gate)
            {
                return _currentPath;
            }
        }
    }

    /// <summary>The argument names of a call, in their order: no values, at most 32, each at most 64 characters.</summary>
    public static IReadOnlyList<string> ArgumentKeysOf(IEnumerable<string>? keys) =>
        (keys ?? [])
            .Where(static k => !string.IsNullOrEmpty(k))
            .Select(static k => k.Length <= MaxKeyLength ? k : k[..MaxKeyLength])
            .Distinct(StringComparer.Ordinal)
            .Take(MaxKeys)
            .ToList();

    /// <summary>The JSON line of an entry (no line break).</summary>
    public static string FormatLine(McpAuditEntry entry) =>
        JsonSerializer.Serialize(new Line(
            entry.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            entry.RemoteIp,
            entry.Client,
            entry.Tool,
            ArgumentKeysOf(entry.ArgumentKeys),
            entry.Outcome,
            Math.Max(0, entry.DurationMs)), LineOptions);

    /// <summary>Adds a line. Returns false when the write failed (the line is dropped).</summary>
    public bool Write(McpAuditEntry entry)
    {
        var line = FormatLine(entry) + "\n";
        lock (_gate)
        {
            try
            {
                var path = PickFileLocked(_clock());
                File.AppendAllText(path, line);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _currentPath = null;
                return false;
            }
        }
    }

    /// <summary>The audit files, oldest first.</summary>
    public IReadOnlyList<string> GetFiles()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.GetFiles(Directory, FilePrefix + "*" + FileExtension)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private string PickFileLocked(DateTimeOffset now)
    {
        var day = now.UtcDateTime.Date;
        if (_currentPath is null)
        {
            // After a restart, go on with today's newest file while it has room.
            var newest = GetFiles().LastOrDefault();
            if (newest is not null && DayOf(newest) == day && Length(newest) < MaxFileBytes)
            {
                _currentPath = newest;
                _currentDay = day;
            }
        }

        if (_currentPath is null || _currentDay != day || Length(_currentPath) >= MaxFileBytes)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            // The two-digit part keeps names unique within a second and sorts them in order.
            var path = Path.Combine(Directory, $"{FilePrefix}{stamp}-00{FileExtension}");
            for (var n = 1; File.Exists(path); n++)
            {
                path = Path.Combine(Directory, $"{FilePrefix}{stamp}-{n:D2}{FileExtension}");
            }

            _currentPath = path;
            _currentDay = day;
            Prune(keepNewest: MaxFiles - 1);
        }

        return _currentPath;
    }

    /// <summary>Deletes the oldest files so that at most <paramref name="keepNewest"/> are left (the new one comes next).</summary>
    private void Prune(int keepNewest)
    {
        var files = GetFiles();
        foreach (var old in files.Take(Math.Max(0, files.Count - keepNewest)))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try again at the next new file.
            }
        }
    }

    private static long Length(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>The UTC day in a file name (mcp-audit-yyyyMMdd-...), or MinValue.</summary>
    private static DateTime DayOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length >= FilePrefix.Length + 8 &&
               DateTime.TryParseExact(name.AsSpan(FilePrefix.Length, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : DateTime.MinValue;
    }

    private sealed record Line(
        [property: JsonPropertyName("ts")] string Timestamp,
        [property: JsonPropertyName("ip")] string? Ip,
        [property: JsonPropertyName("client")] string? Client,
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("argKeys")] IReadOnlyList<string> ArgKeys,
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("ms")] long Ms);
}
