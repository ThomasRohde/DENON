using System.Collections.Concurrent;
using System.Text;
using Denon.App.Core.Infrastructure;

namespace Denon.App.Core.Services;

public enum AppLogLevel
{
    Info,
    Warning,
    Error,
}

public sealed record AppLogEntry(DateTimeOffset Timestamp, AppLogLevel Level, string Message);

public sealed class AppLogStore
{
    private const int MaxEntries = 500;
    private const long MaxLogFileBytes = 5 * 1024 * 1024; // 5 MB rotation cap
    private const int MaxRotatedFiles = 4;
    private readonly ConcurrentQueue<AppLogEntry> _entries = new();
    private readonly object _fileLock = new();

    public event Action<AppLogEntry>? EntryAdded;

    public AppLogStore()
    {
        AppDataPaths.EnsureCreated();
    }

    public IReadOnlyList<AppLogEntry> Snapshot() => _entries.ToArray();

    public void Info(string message) => Add(AppLogLevel.Info, message);

    public void Warning(string message) => Add(AppLogLevel.Warning, message);

    public void Error(string message) => Add(AppLogLevel.Error, message);

    public void Error(string message, Exception exception) => Add(AppLogLevel.Error, $"{message} {exception.Message}");

    private void Add(AppLogLevel level, string message)
    {
        var entry = new AppLogEntry(DateTimeOffset.Now, level, message.Trim());
        _entries.Enqueue(entry);

        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }

        AppendToFile(entry);
        EntryAdded?.Invoke(entry);
    }

    private static string FormatEntry(AppLogEntry entry) =>
        $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} [{entry.Level}] {entry.Message}";

    private void AppendToFile(AppLogEntry entry)
    {
        lock (_fileLock)
        {
            try
            {
                RotateIfNeededLocked();
                File.AppendAllText(
                    AppDataPaths.LogFilePath,
                    FormatEntry(entry) + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                // Never crash app on transient file lock.
            }
        }
    }

    private static void RotateIfNeededLocked()
    {
        var path = AppDataPaths.LogFilePath;
        if (!File.Exists(path))
        {
            return;
        }

        var info = new FileInfo(path);
        if (info.Length < MaxLogFileBytes)
        {
            return;
        }

        for (var i = MaxRotatedFiles; i >= 1; i--)
        {
            var src = i == 1 ? path : $"{path}.{i - 1}";
            var dst = $"{path}.{i}";
            if (File.Exists(src))
            {
                File.Move(src, dst, overwrite: true);
            }
        }
    }
}
