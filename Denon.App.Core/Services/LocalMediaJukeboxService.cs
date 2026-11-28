using Denon.App.Core.Media;
using Denon.App.Core.Models;
using Denon.App.Core.Services.Metering;

namespace Denon.App.Core.Services;

public sealed class LocalJukeboxSnapshot
{
    public static LocalJukeboxSnapshot Empty { get; } = new([], -1, false, false, YouTubeRepeatMode.Off);

    public LocalJukeboxSnapshot(
        IReadOnlyList<LocalAudioTrackItem> tracks,
        int currentIndex,
        bool isActive,
        bool isShuffleEnabled,
        YouTubeRepeatMode repeatMode)
    {
        Tracks = tracks;
        CurrentIndex = currentIndex;
        IsActive = isActive;
        IsShuffleEnabled = isShuffleEnabled;
        RepeatMode = repeatMode;
    }

    public IReadOnlyList<LocalAudioTrackItem> Tracks { get; }

    public int CurrentIndex { get; }

    public bool IsActive { get; }

    public bool IsShuffleEnabled { get; }

    public YouTubeRepeatMode RepeatMode { get; }

    public LocalAudioTrackItem? CurrentTrack =>
        IsActive && CurrentIndex >= 0 && CurrentIndex < Tracks.Count ? Tracks[CurrentIndex] : null;
}

public sealed class LocalMediaJukeboxService : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wma",
    };

    private readonly LocalAudioRelayServer _relayServer;
    private readonly ReceiverController _receiverController;
    private readonly ReceiverStateStore _stateStore;
    private readonly AppLogStore _logStore;
    private readonly object _sync = new();
    private readonly List<LocalAudioTrackItem> _tracks = [];
    private readonly Random _random = new();

    private int _currentIndex = -1;
    private bool _isActive;
    private bool _isShuffleEnabled;
    private YouTubeRepeatMode _repeatMode = YouTubeRepeatMode.Off;
    private string? _activeRelayToken;
    private CancellationTokenSource? _autoAdvanceCts;
    private DateTimeOffset _sessionStartedUtc = DateTimeOffset.MinValue;

    // Media-time clock for synced meters (same model as the YouTube queue clock).
    private readonly PlaybackClock _clock = new();

    // Tags and cover art are read off the caller's thread, one file at a time (see EnqueueTagReads).
    private readonly MediaQueueStore? _queueStore;
    private readonly LocalCoverArtCache? _coverArtCache;
    private readonly object _tagSync = new();
    private readonly Queue<string> _tagQueue = new();
    private readonly HashSet<string> _tagQueued = new(StringComparer.Ordinal);
    private bool _tagWorkerRunning;
    private Task _tagWorker = Task.CompletedTask;
    private bool _disposed;

    /// <summary>The worker sends one change for many tracks: at most one per this interval.</summary>
    private static readonly TimeSpan TagNotifyInterval = TimeSpan.FromMilliseconds(500);

    /// <param name="queueStore">Saves the queue between app runs. Null: the queue is not saved.</param>
    /// <param name="coverArtCache">Keeps the cover art of the files. Null: no cover art.</param>
    public LocalMediaJukeboxService(
        LocalAudioRelayServer relayServer,
        ReceiverController receiverController,
        ReceiverStateStore stateStore,
        AppLogStore logStore,
        MediaQueueStore? queueStore = null,
        LocalCoverArtCache? coverArtCache = null)
    {
        _relayServer = relayServer;
        _receiverController = receiverController;
        _stateStore = stateStore;
        _logStore = logStore;
        _queueStore = queueStore;
        _coverArtCache = coverArtCache;

        RestoreSavedQueue();

        _relayServer.LocalTrackStreamCompleted += OnLocalTrackCompleted;
        _relayServer.LocalTrackPlaybackStarted += OnLocalTrackPlaybackStarted;
        _receiverController.NonRelaySourceActivated += OnExternalSourceActivated;
        _stateStore.StateChanged += OnReceiverStateChanged;
    }

    /// <summary>Completes when the tag worker has no more files to read (for tests).</summary>
    internal Task WhenTagsReadAsync()
    {
        lock (_tagSync)
        {
            return _tagWorker;
        }
    }

    /// <summary>File path of the track the jukebox plays now, or null.</summary>
    public string? CurrentTrackFilePath
    {
        get
        {
            lock (_sync)
            {
                return _isActive && _currentIndex >= 0 && _currentIndex < _tracks.Count ? _tracks[_currentIndex].FilePath : null;
            }
        }
    }

    /// <summary>
    /// Estimated position (seconds) that the Denon speakers play now: time since the Denon started to read
    /// the track, without pauses, minus the HEOS buffer delay.
    /// </summary>
    public double GetEstimatedPlaybackPositionSeconds(int heosBufferOffsetMs) =>
        IsLocalPlaybackActive && _clock.IsStarted
            ? _clock.GetPositionSeconds(DateTimeOffset.UtcNow, heosBufferOffsetMs)
            : -1;

    public event Action<LocalJukeboxSnapshot>? JukeboxChanged;

    public LocalJukeboxSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new LocalJukeboxSnapshot([.. _tracks], _currentIndex, _isActive, _isShuffleEnabled, _repeatMode);
            }
        }
    }

    public bool IsLocalPlaybackActive
    {
        get
        {
            lock (_sync)
            {
                return _isActive && _currentIndex >= 0 && _currentIndex < _tracks.Count;
            }
        }
    }

    public int AddPaths(IEnumerable<string> paths) => AddPathsDetailed(paths).Added;

    public (int Added, int Duplicates, int Skipped) AddPathsDetailed(IEnumerable<string> paths)
    {
        var discoveredFiles = new List<string>();
        var skipped = 0;
        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        foreach (var rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            if (File.Exists(rawPath))
            {
                if (IsSupportedAudioFile(rawPath))
                {
                    discoveredFiles.Add(rawPath);
                }
                else
                {
                    skipped++;
                }
            }
            else if (Directory.Exists(rawPath))
            {
                try
                {
                    var files = Directory.EnumerateFiles(rawPath, "*.*", enumOpts)
                        .Where(IsSupportedAudioFile)
                        .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase);
                    discoveredFiles.AddRange(files);
                }
                catch (Exception ex)
                {
                    skipped++;
                    _logStore.Warning($"Could not scan folder '{rawPath}': {ex.Message}");
                }
            }
            else
            {
                skipped++;
            }
        }

        if (discoveredFiles.Count == 0)
        {
            return (0, 0, skipped);
        }

        var added = 0;
        var duplicates = 0;
        var addedIds = new List<string>();
        lock (_sync)
        {
            var existingPaths = new HashSet<string>(_tracks.Select(static t => t.FilePath), StringComparer.OrdinalIgnoreCase);
            foreach (var file in discoveredFiles)
            {
                if (!existingPaths.Add(file))
                {
                    duplicates++;
                    continue;
                }

                var info = new FileInfo(file);
                var (artist, title) = ParseMetadataFromPath(file);
                var trackId = NewTrackId();
                _tracks.Add(new LocalAudioTrackItem(
                    TrackId: trackId,
                    FilePath: file,
                    FileName: info.Name,
                    Title: title,
                    Artist: artist,
                    Extension: info.Extension,
                    FileSizeBytes: info.Exists ? info.Length : 0));
                addedIds.Add(trackId);
                added++;
            }
        }

        if (added > 0)
        {
            _logStore.Info($"Added {added} local audio track(s) to Hi-Res Jukebox{(duplicates > 0 ? $" ({duplicates} duplicate(s) skipped)" : string.Empty)}.");
            NotifyChanged();

            // The names from the file paths show at once; the tags replace them when the worker has read them.
            EnqueueTagReads(addedIds);
        }

        return (added, duplicates, skipped);
    }

    public async Task PlayTrackAsync(string trackId, bool useLowVolume, bool unmute, CancellationToken cancellationToken = default)
    {
        int idx;
        lock (_sync)
        {
            idx = _tracks.FindIndex(t => string.Equals(t.TrackId, trackId, StringComparison.Ordinal));
        }

        if (idx >= 0)
        {
            await PlayAtIndexAsync(idx, useLowVolume, unmute, cancellationToken);
        }
    }

    public async Task PlayAllAsync(bool useLowVolume, bool unmute, CancellationToken cancellationToken = default)
    {
        int count;
        lock (_sync)
        {
            count = _tracks.Count;
        }

        if (count > 0)
        {
            await PlayAtIndexAsync(0, useLowVolume, unmute, cancellationToken);
        }
    }

    public Task<bool> PlayNextAsync(CancellationToken cancellationToken = default) =>
        PlayNextCoreAsync(unmute: true, cancellationToken);

    private async Task<bool> PlayNextCoreAsync(bool unmute, CancellationToken cancellationToken)
    {
        int nextIndex;
        lock (_sync)
        {
            if (_tracks.Count == 0)
            {
                return false;
            }

            if (_repeatMode == YouTubeRepeatMode.One && _currentIndex >= 0 && _currentIndex < _tracks.Count)
            {
                nextIndex = _currentIndex;
            }
            else if (_isShuffleEnabled && _tracks.Count > 1)
            {
                do
                {
                    nextIndex = _random.Next(_tracks.Count);
                }
                while (nextIndex == _currentIndex);
            }
            else if (_currentIndex + 1 < _tracks.Count)
            {
                nextIndex = _currentIndex + 1;
            }
            else if (_repeatMode == YouTubeRepeatMode.All)
            {
                nextIndex = 0;
            }
            else
            {
                return false;
            }
        }

        await PlayAtIndexAsync(nextIndex, useLowVolume: false, unmute: unmute, cancellationToken);
        return true;
    }

    public async Task<bool> PlayPreviousAsync(CancellationToken cancellationToken = default)
    {
        int prevIndex;
        lock (_sync)
        {
            if (_tracks.Count == 0)
            {
                return false;
            }

            prevIndex = _currentIndex > 0
                ? _currentIndex - 1
                : (_repeatMode == YouTubeRepeatMode.All ? _tracks.Count - 1 : 0);
        }

        await PlayAtIndexAsync(prevIndex, useLowVolume: false, unmute: true, cancellationToken);
        return true;
    }

    public void RemoveTrack(string trackId)
    {
        lock (_sync)
        {
            var idx = _tracks.FindIndex(t => string.Equals(t.TrackId, trackId, StringComparison.Ordinal));
            if (idx < 0)
            {
                return;
            }

            _tracks.RemoveAt(idx);
            if (idx < _currentIndex)
            {
                _currentIndex--;
            }
            else if (idx == _currentIndex)
            {
                _currentIndex = _tracks.Count == 0 ? -1 : Math.Min(_currentIndex, _tracks.Count - 1);
                if (_tracks.Count == 0)
                {
                    _isActive = false;
                }
            }
        }

        NotifyChanged();
    }

    public void Clear()
    {
        CancelPendingAutoAdvance();
        string? activeToken;
        lock (_sync)
        {
            activeToken = _isActive ? _activeRelayToken : null;
            _tracks.Clear();
            _currentIndex = -1;
            _isActive = false;
            _activeRelayToken = null;
        }

        if (!string.IsNullOrWhiteSpace(activeToken))
        {
            _relayServer.UnregisterLocalTrack(activeToken);
        }

        NotifyChanged();
    }

    public void StopSession()
    {
        CancelPendingAutoAdvance();
        string? activeToken;
        lock (_sync)
        {
            activeToken = _isActive ? _activeRelayToken : null;
            _isActive = false;
            _activeRelayToken = null;
        }

        if (!string.IsNullOrWhiteSpace(activeToken))
        {
            _relayServer.UnregisterLocalTrack(activeToken);
        }

        NotifyChanged();
    }

    public void ToggleShuffle()
    {
        lock (_sync)
        {
            _isShuffleEnabled = !_isShuffleEnabled;
        }

        NotifyChanged();
    }

    public void CycleRepeatMode()
    {
        lock (_sync)
        {
            _repeatMode = _repeatMode switch
            {
                YouTubeRepeatMode.Off => YouTubeRepeatMode.All,
                YouTubeRepeatMode.All => YouTubeRepeatMode.One,
                _ => YouTubeRepeatMode.Off,
            };
        }

        NotifyChanged();
    }

    public static bool IsSupportedAudioFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    private async Task PlayAtIndexAsync(int index, bool useLowVolume, bool unmute, CancellationToken cancellationToken)
    {
        CancelPendingAutoAdvance();

        LocalAudioTrackItem track;
        int prevIndex;
        bool prevActive;
        string? prevToken;
        lock (_sync)
        {
            if (index < 0 || index >= _tracks.Count)
            {
                return;
            }

            prevIndex = _currentIndex;
            prevActive = _isActive;
            prevToken = _activeRelayToken;
            track = _tracks[index];
        }

        // Read the tags first when the worker has not come to this file yet: the title, cover and length must be
        // right before the track starts (Now Playing, the Hue colours and the auto-advance wait use them).
        if (!track.TagsRead)
        {
            var trackId = track.TrackId;
            await Task.Run(() => ReadTagsInto(trackId), cancellationToken);

            // Take the list's record also when this call read nothing: the worker may have read the tags meanwhile.
            lock (_sync)
            {
                track = _tracks.Find(t => string.Equals(t.TrackId, trackId, StringComparison.Ordinal)) ?? track;
            }
        }

        try
        {
            var (relayUrl, token) = await _relayServer.RegisterLocalTrackAsync(track, cancellationToken);
            lock (_sync)
            {
                _activeRelayToken = token;
                _clock.Reset();
            }

            await _receiverController.PlayRelayStreamAsync(relayUrl, $"{track.Title} ({track.CodecBadge})", useLowVolume, unmute, cancellationToken);

            lock (_sync)
            {
                var confirmedIdx = _tracks.FindIndex(t => string.Equals(t.TrackId, track.TrackId, StringComparison.Ordinal));
                _currentIndex = confirmedIdx >= 0 ? confirmedIdx : Math.Clamp(index, 0, Math.Max(0, _tracks.Count - 1));
                _isActive = _tracks.Count > 0;
                _sessionStartedUtc = DateTimeOffset.UtcNow;

                // Fallback start time if the Denon has not requested the stream yet.
                if (string.Equals(_activeRelayToken, token, StringComparison.Ordinal) && !_clock.IsStarted)
                {
                    _clock.Restart(DateTimeOffset.UtcNow);
                }
            }

            NotifyChanged();
        }
        catch
        {
            lock (_sync)
            {
                _currentIndex = prevIndex;
                _isActive = prevActive;
                _activeRelayToken = prevToken;
            }

            NotifyChanged();
            throw;
        }
    }

    private void OnLocalTrackCompleted(string token, LocalAudioTrackItem track)
    {
        lock (_sync)
        {
            if (!_isActive || !string.Equals(_activeRelayToken, token, StringComparison.Ordinal))
            {
                return;
            }
        }

        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_sync)
        {
            previous = _autoAdvanceCts;
            _autoAdvanceCts = cts;
        }

        try { previous?.Cancel(); } catch { }
        var ct = cts.Token;
        var playedAtTransferEnd = _clock.IsStarted ? _clock.GetPositionSeconds(DateTimeOffset.UtcNow, 0) : 0;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for HEOS to finish draining its prefetched audio buffer; suspend the timer
                // whenever HEOS is paused so pausing a buffered file never advances prematurely!
                // HEOS may buffer far ahead, so wait until the track's real end (from its tags), not a fixed time.
                var playedWaitSec = 0.0;
                var maxDrainSec = DrainLimitSeconds(track, playedAtTransferEnd);
                var sawPlaying = string.Equals(_stateStore.Snapshot.Status?.PlayState, "play", StringComparison.OrdinalIgnoreCase);

                while (playedWaitSec < maxDrainSec && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                    var state = _stateStore.Snapshot.Status?.PlayState;
                    if (string.Equals(state, "pause", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    playedWaitSec += 0.5;
                    if (string.Equals(state, "play", StringComparison.OrdinalIgnoreCase))
                    {
                        sawPlaying = true;
                    }
                    else if (playedWaitSec >= 2.5 &&
                             string.Equals(state, "stop", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    else if (!sawPlaying && playedWaitSec >= 4.0)
                    {
                        break;
                    }
                }

                // Discard superseded completions before advancing
                lock (_sync)
                {
                    if (cts.IsCancellationRequested || !_isActive || !string.Equals(_activeRelayToken, token, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                // Unregister finished track now that HEOS finished draining it, then preserve mute on auto-advance!
                _relayServer.UnregisterLocalTrack(token);
                var keepMuted = _stateStore.Snapshot.Status?.IsMuted == true;
                var advanced = await PlayNextCoreAsync(unmute: !keepMuted, CancellationToken.None);
                if (advanced)
                {
                    _logStore.Info($"Local Jukebox auto-advanced after '{track.Title}'.");
                }
                else
                {
                    lock (_sync)
                    {
                        if (string.Equals(_activeRelayToken, token, StringComparison.Ordinal))
                        {
                            _isActive = false;
                            _activeRelayToken = null;
                        }
                    }

                    NotifyChanged();
                    try
                    {
                        var playState = _stateStore.Snapshot.Status?.PlayState;
                        if (!string.Equals(playState, "stop", StringComparison.OrdinalIgnoreCase))
                        {
                            await _receiverController.SetPlayStateAsync("stop", CancellationToken.None);
                        }
                    }
                    catch
                    {
                    }

                    _logStore.Info($"Local Jukebox finished after '{track.Title}'.");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logStore.Warning($"Local Jukebox auto-advance failed: {ex.Message}");
            }
            finally
            {
                // The task owns its source; others only cancel it.
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private void OnLocalTrackPlaybackStarted(string token, DateTimeOffset startedAtUtc)
    {
        lock (_sync)
        {
            if (!string.Equals(_activeRelayToken, token, StringComparison.Ordinal))
            {
                return;
            }

            // Restart the clock at the moment the Denon starts to read real audio.
            _clock.Restart(startedAtUtc);
        }
    }

    private void OnExternalSourceActivated()
    {
        var wasActive = false;
        string? token = null;
        lock (_sync)
        {
            if (_isActive)
            {
                _isActive = false;
                token = _activeRelayToken;
                _activeRelayToken = null;
                wasActive = true;
            }
        }

        if (wasActive)
        {
            CancelPendingAutoAdvance();
            // Another source took over: drop the relay stream too, as YouTube does.
            _relayServer.UnregisterLocalTrack(token);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Cancel only, under the lock. The wait task captured its token, so the source is never disposed under it
    /// (it holds no OS handle).
    /// </summary>
    private void CancelPendingAutoAdvance()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _autoAdvanceCts;
            _autoAdvanceCts = null;
        }

        try { cts?.Cancel(); } catch { }
    }

    /// <summary>Upper limit for the wait after the file was sent: when no "stop" comes, advance after this.</summary>
    internal const double UnknownLengthDrainLimitSeconds = 900;

    /// <summary>
    /// How long to wait after the whole file was sent before the next track: what is left of the track (its
    /// length from the tags minus what has played) plus 5 s. Without a length (no tags): 15 minutes. HEOS
    /// reports "stop" at the real end, and that ends the wait sooner.
    /// </summary>
    internal static double DrainLimitSeconds(TimeSpan? trackLength, double playedSeconds) =>
        trackLength is { } length && length > TimeSpan.Zero
            ? Math.Clamp(length.TotalSeconds - Math.Max(0, playedSeconds) + 5, 10, UnknownLengthDrainLimitSeconds)
            : UnknownLengthDrainLimitSeconds;

    private static double DrainLimitSeconds(LocalAudioTrackItem track, double playedSeconds)
    {
        // The length comes from the tags read before the track started. Only a track that started before its
        // tags could be read (a missing file) reads them here.
        var length = track.TagsRead
            ? track.Duration
            : AudioTagReader.Read(track.FilePath, includeCoverArt: false).Duration;
        return DrainLimitSeconds(length, playedSeconds);
    }

    private static string NewTrackId() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>Puts tracks in the line for the tag worker and starts it when it does not run.</summary>
    private void EnqueueTagReads(IEnumerable<string> trackIds)
    {
        lock (_tagSync)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var id in trackIds)
            {
                if (_tagQueued.Add(id))
                {
                    _tagQueue.Enqueue(id);
                }
            }

            if (!_tagWorkerRunning && _tagQueue.Count > 0)
            {
                _tagWorkerRunning = true;
                _tagWorker = Task.Run(RunTagWorker);
            }
        }
    }

    private void RunTagWorker()
    {
        var changed = false;
        var lastNotify = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            string? id;
            lock (_tagSync)
            {
                if (_disposed || !_tagQueue.TryDequeue(out id))
                {
                    _tagQueue.Clear();
                    _tagQueued.Clear();
                    _tagWorkerRunning = false;
                    break;
                }

                _tagQueued.Remove(id);
            }

            try
            {
                changed |= ReadTagsInto(id);
            }
            catch (Exception ex)
            {
                _logStore.Warning($"Could not read the tags of a Jukebox track: {ex.Message}");
            }

            if (changed && lastNotify.Elapsed >= TagNotifyInterval)
            {
                changed = false;
                lastNotify.Restart();
                NotifyChanged();
            }
        }

        if (changed)
        {
            NotifyChanged();
        }
    }

    /// <summary>
    /// Reads the tags and cover art of one track and puts them in the list. False when there is nothing to do: the
    /// track is gone, its tags and cover are already there, or its file is missing (it is read again next time).
    /// </summary>
    private bool ReadTagsInto(string trackId)
    {
        LocalAudioTrackItem? track;
        lock (_sync)
        {
            track = _tracks.Find(t => string.Equals(t.TrackId, trackId, StringComparison.Ordinal));
        }

        if (track is null || !NeedsTagRead(track) || !File.Exists(track.FilePath))
        {
            return false;
        }

        var updated = WithTags(track, AudioTagReader.Read(track.FilePath, includeCoverArt: _coverArtCache is not null), _coverArtCache);
        lock (_sync)
        {
            var index = _tracks.FindIndex(t => string.Equals(t.TrackId, trackId, StringComparison.Ordinal));
            if (index < 0 || !string.Equals(_tracks[index].FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _tracks[index] = updated;
        }

        return true;
    }

    private bool NeedsTagRead(LocalAudioTrackItem track) =>
        !track.TagsRead || (track.CoverArtPath is not null && _coverArtCache?.Contains(track.CoverArtPath) != true);

    /// <summary>
    /// The track with the title, artist, album, number, length and cover from its tags. A missing title or artist
    /// keeps the name from the file path.
    /// </summary>
    internal static LocalAudioTrackItem WithTags(LocalAudioTrackItem track, AudioTags tags, LocalCoverArtCache? coverArtCache)
    {
        var (pathArtist, pathTitle) = ParseMetadataFromPath(track.FilePath);
        return track with
        {
            Title = tags.Title ?? pathTitle,
            Artist = tags.Artist ?? pathArtist,
            Album = tags.Album,
            Duration = tags.Duration,
            TrackNumber = tags.TrackNumber,
            CoverArtPath = coverArtCache?.Store(tags.CoverArt),
            TagsRead = true,
        };
    }

    /// <summary>
    /// Loads the queue saved by the last run. It never starts playback: the tracks are there, nothing plays. The
    /// tags of tracks that were not read (or whose cover file is gone) are read again in the background.
    /// </summary>
    private void RestoreSavedQueue()
    {
        var saved = _queueStore?.LoadJukebox();
        if (saved?.Tracks is not { Count: > 0 } savedTracks)
        {
            return;
        }

        var restored = new List<LocalAudioTrackItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in savedTracks)
        {
            if (restored.Count >= MediaQueueStore.MaxEntries)
            {
                break;
            }

            if (item is null || string.IsNullOrWhiteSpace(item.FilePath) || !Path.IsPathFullyQualified(item.FilePath) ||
                !IsSupportedAudioFile(item.FilePath) || !paths.Add(item.FilePath))
            {
                continue;
            }

            var id = !string.IsNullOrWhiteSpace(item.TrackId) && ids.Add(item.TrackId) ? item.TrackId : NewTrackId();
            ids.Add(id);
            var (pathArtist, pathTitle) = ParseMetadataFromPath(item.FilePath);
            var cover = _coverArtCache?.IsInCacheFolder(item.CoverArtPath) == true ? item.CoverArtPath : null;
            var duration = item.DurationSeconds is { } seconds && double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.FromDays(7).TotalSeconds
                ? TimeSpan.FromSeconds(seconds)
                : (TimeSpan?)null;
            restored.Add(new LocalAudioTrackItem(
                TrackId: id,
                FilePath: item.FilePath,
                FileName: Path.GetFileName(item.FilePath),
                Title: string.IsNullOrWhiteSpace(item.Title) ? pathTitle : item.Title.Trim(),
                Artist: string.IsNullOrWhiteSpace(item.Artist) ? pathArtist : item.Artist.Trim(),
                Extension: Path.GetExtension(item.FilePath),
                FileSizeBytes: Math.Max(0, item.FileSizeBytes),
                Album: string.IsNullOrWhiteSpace(item.Album) ? null : item.Album.Trim(),
                Duration: duration,
                TrackNumber: item.TrackNumber is > 0 ? item.TrackNumber : null,
                // A saved cover outside the cache folder is dropped; the worker reads the file again.
                CoverArtPath: cover,
                TagsRead: item.TagsRead && (item.CoverArtPath is null || cover is not null)));
        }

        lock (_sync)
        {
            _tracks.AddRange(restored);
            _isShuffleEnabled = saved.IsShuffleEnabled;
            _repeatMode = Enum.IsDefined(saved.RepeatMode) ? saved.RepeatMode : YouTubeRepeatMode.Off;
        }

        if (restored.Count > 0)
        {
            _logStore.Info($"Restored {restored.Count} track(s) to the Hi-Res Jukebox from the last run.");
            EnqueueTagReads(restored.Select(static t => t.TrackId));
        }
    }

    private void SaveQueue(LocalJukeboxSnapshot snapshot)
    {
        if (_queueStore is null)
        {
            return;
        }

        _queueStore.SaveJukebox(new SavedJukeboxQueue(
            [.. snapshot.Tracks.Select(static t => new SavedLocalTrack(
                t.TrackId,
                t.FilePath,
                t.Title,
                t.Artist,
                t.FileSizeBytes,
                t.Album,
                t.Duration?.TotalSeconds,
                t.TrackNumber,
                t.CoverArtPath,
                t.TagsRead))],
            snapshot.IsShuffleEnabled,
            snapshot.RepeatMode));
    }

    private static (string Artist, string Title) ParseMetadataFromPath(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath).Trim();
        var parentFolder = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);

        var dashParts = name.Split(" - ", 2, StringSplitOptions.TrimEntries);
        if (dashParts.Length == 2)
        {
            var left = dashParts[0].TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ');
            if (!string.IsNullOrWhiteSpace(left))
            {
                return (left, dashParts[1]);
            }

            return (string.IsNullOrWhiteSpace(parentFolder) ? "Local Audio" : parentFolder, dashParts[1]);
        }

        return (string.IsNullOrWhiteSpace(parentFolder) ? "Local Audio" : parentFolder, name);
    }

    private void OnReceiverStateChanged(ReceiverStateSnapshot snapshot)
    {
        if (!snapshot.IsConnected || snapshot.Status is not { } status)
        {
            return;
        }

        string? externalTakeoverReason = null;
        lock (_sync)
        {
            if (!_isActive)
            {
                return;
            }

            // Keep the meter clock in step with HEOS pause/resume.
            if (string.Equals(status.PlayState, "pause", StringComparison.OrdinalIgnoreCase))
            {
                _clock.Pause(DateTimeOffset.UtcNow);
            }
            else if (string.Equals(status.PlayState, "play", StringComparison.OrdinalIgnoreCase))
            {
                _clock.Resume(DateTimeOffset.UtcNow);
            }

            if ((DateTimeOffset.UtcNow - _sessionStartedUtc) < TimeSpan.FromSeconds(5))
            {
                return;
            }

            var currentTrack = _currentIndex >= 0 && _currentIndex < _tracks.Count ? _tracks[_currentIndex] : null;
            if (status.IsAvrConfirmed && (!status.IsPoweredOn || !status.IsMainZoneOn))
            {
                externalTakeoverReason = "receiver entered standby";
            }
            else if (status.IsAvrConfirmed &&
                     !string.IsNullOrWhiteSpace(status.InputSource) &&
                     !DenonCatalog.InputMatches(status.InputSource, "NET"))
            {
                externalTakeoverReason = $"input switched externally to {DenonCatalog.GetFriendlyInputName(status.InputSource)}";
            }
            else if (string.Equals(status.PlayState, "play", StringComparison.OrdinalIgnoreCase) &&
                     status.NowPlaying.IsNamedServiceSource &&
                     status.NowPlaying.SourceId is int sid)
            {
                externalTakeoverReason = $"external streaming service started ({DenonCatalog.GetHeosSourceName(sid) ?? $"sid {sid}"})";
            }
            else if (string.Equals(status.PlayState, "play", StringComparison.OrdinalIgnoreCase) &&
                     status.NowPlaying.HasRealMetadata &&
                     currentTrack is not null &&
                     !string.IsNullOrWhiteSpace(status.NowPlaying.Song) &&
                     !status.NowPlaying.Song.Contains(currentTrack.Title, StringComparison.OrdinalIgnoreCase) &&
                     !currentTrack.Title.Contains(status.NowPlaying.Song, StringComparison.OrdinalIgnoreCase))
            {
                externalTakeoverReason = $"external HEOS track started ({status.NowPlaying.Song})";
            }
        }

        if (externalTakeoverReason is not null)
        {
            _logStore.Info($"Deactivating Local Hi-Res Jukebox session because {externalTakeoverReason}.");
            StopSession();
        }
    }

    private void NotifyChanged()
    {
        var snapshot = Snapshot;
        SaveQueue(snapshot);
        JukeboxChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        lock (_tagSync)
        {
            _disposed = true;
        }

        CancelPendingAutoAdvance();
        _relayServer.LocalTrackStreamCompleted -= OnLocalTrackCompleted;
        _relayServer.LocalTrackPlaybackStarted -= OnLocalTrackPlaybackStarted;
        _receiverController.NonRelaySourceActivated -= OnExternalSourceActivated;
        _stateStore.StateChanged -= OnReceiverStateChanged;
    }
}
