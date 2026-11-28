using System.Text.Json;
using System.Text.Json.Serialization;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

/// <summary>A Jukebox track as it is saved between app runs.</summary>
public sealed record SavedLocalTrack(
    string TrackId,
    string FilePath,
    string Title,
    string Artist,
    long FileSizeBytes,
    string? Album = null,
    double? DurationSeconds = null,
    int? TrackNumber = null,
    string? CoverArtPath = null,
    bool TagsRead = false);

/// <summary>The Jukebox queue as it is saved between app runs.</summary>
public sealed record SavedJukeboxQueue(
    IReadOnlyList<SavedLocalTrack> Tracks,
    bool IsShuffleEnabled,
    YouTubeRepeatMode RepeatMode);

/// <summary>A YouTube queue entry as it is saved between app runs.</summary>
public sealed record SavedYouTubeTrack(
    string VideoId,
    string Title,
    string Author,
    double? DurationSeconds,
    string? ThumbnailUrl,
    string? QueueEntryId);

/// <summary>The YouTube queue and its history as they are saved between app runs.</summary>
public sealed record SavedYouTubeQueue(
    IReadOnlyList<SavedYouTubeTrack> Queue,
    IReadOnlyList<SavedYouTubeTrack> History,
    bool IsShuffleEnabled,
    YouTubeRepeatMode RepeatMode);

/// <summary>
/// Saves the Jukebox and YouTube queues as JSON files in one folder, so they come back after the app restarts.
/// A save waits <see cref="SaveDelay"/> for more changes and then writes the last state once, off the caller's thread.
/// <see cref="Flush"/> (also on dispose) writes what is still waiting. A bad or missing file loads as null.
/// </summary>
public sealed class MediaQueueStore : IDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    /// <summary>A saved queue with more entries than this is cut to this size.</summary>
    public const int MaxEntries = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppLogStore? _logStore;
    private readonly DebouncedJsonFile<SavedJukeboxQueue> _jukebox;
    private readonly DebouncedJsonFile<SavedYouTubeQueue> _youTube;

    public MediaQueueStore(string directory, AppLogStore? logStore = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A directory is required.", nameof(directory));
        }

        Directory = Path.GetFullPath(directory);
        _logStore = logStore;
        _jukebox = new DebouncedJsonFile<SavedJukeboxQueue>(Path.Combine(Directory, "jukebox-queue.json"), this);
        _youTube = new DebouncedJsonFile<SavedYouTubeQueue>(Path.Combine(Directory, "youtube-queue.json"), this);
    }

    public string Directory { get; }

    public SavedJukeboxQueue? LoadJukebox() => _jukebox.Load();

    public void SaveJukebox(SavedJukeboxQueue queue) => _jukebox.Schedule(queue);

    public SavedYouTubeQueue? LoadYouTube() => _youTube.Load();

    public void SaveYouTube(SavedYouTubeQueue queue) => _youTube.Schedule(queue);

    /// <summary>Writes the saves that still wait, now, on this thread.</summary>
    public void Flush()
    {
        _jukebox.Flush();
        _youTube.Flush();
    }

    public void Dispose()
    {
        Flush();
        _jukebox.Dispose();
        _youTube.Dispose();
    }

    private void Warn(string message) => _logStore?.Warning(message);

    private sealed class DebouncedJsonFile<T> : IDisposable
        where T : class
    {
        private readonly string _path;
        private readonly MediaQueueStore _owner;
        private readonly object _sync = new();
        private readonly object _writeSync = new();
        private readonly Timer _timer;
        private T? _pending;
        private bool _disposed;

        public DebouncedJsonFile(string path, MediaQueueStore owner)
        {
            _path = path;
            _owner = owner;
            _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public T? Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }

                using var stream = File.OpenRead(_path);
                return JsonSerializer.Deserialize<T>(stream, JsonOptions);
            }
            catch (Exception ex)
            {
                _owner.Warn($"Could not read the saved queue '{Path.GetFileName(_path)}': {ex.Message}");
                return null;
            }
        }

        public void Schedule(T value)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _pending = value;
                _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Flush()
        {
            // One writer at a time, so an older state never lands after a newer one.
            lock (_writeSync)
            {
                T? value;
                lock (_sync)
                {
                    value = _pending;
                    _pending = null;
                }

                if (value is null)
                {
                    return;
                }

                var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
                    File.Move(temp, _path, overwrite: true);
                }
                catch (Exception ex)
                {
                    try { File.Delete(temp); } catch { }
                    _owner.Warn($"Could not save the queue '{Path.GetFileName(_path)}': {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
            }

            _timer.Dispose();
        }
    }
}
