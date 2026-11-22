using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

public sealed class YouTubeQueueSnapshot
{
    public static YouTubeQueueSnapshot Empty { get; } = new([], -1, false, false, YouTubeRepeatMode.Off, []);

    public YouTubeQueueSnapshot(
        IReadOnlyList<YouTubeTrackItem> queue,
        int currentIndex,
        bool isActive,
        bool isShuffleEnabled,
        YouTubeRepeatMode repeatMode,
        IReadOnlyList<YouTubeTrackItem> history)
    {
        Queue = queue;
        CurrentIndex = currentIndex;
        IsActive = isActive;
        IsShuffleEnabled = isShuffleEnabled;
        RepeatMode = repeatMode;
        History = history;
    }

    public IReadOnlyList<YouTubeTrackItem> Queue { get; }

    public int CurrentIndex { get; }

    public bool IsActive { get; }

    public bool IsShuffleEnabled { get; }

    public YouTubeRepeatMode RepeatMode { get; }

    public IReadOnlyList<YouTubeTrackItem> History { get; }

    public YouTubeTrackItem? CurrentTrack =>
        IsActive && CurrentIndex >= 0 && CurrentIndex < Queue.Count ? Queue[CurrentIndex] : null;
}

public sealed class YouTubeQueueService : IDisposable
{
    private readonly LocalAudioRelayServer _relayServer;
    private readonly ReceiverController _receiverController;
    private readonly ReceiverStateStore _stateStore;
    private readonly AppLogStore _logStore;
    private readonly object _sync = new();
    private readonly List<YouTubeTrackItem> _queue = [];
    private readonly List<YouTubeTrackItem> _history = [];
    private readonly Random _random = new();

    private int _currentIndex = -1;
    private bool _isActive;
    private bool _isShuffleEnabled;
    private YouTubeRepeatMode _repeatMode = YouTubeRepeatMode.Off;
    private string? _activeRelayToken;
    private bool _lastUseLowVolume;
    private bool _lastUnmute = true;
    private CancellationTokenSource? _autoAdvanceCts;

    // Precision playback clock for Cinema A/V sync
    private DateTimeOffset? _clockRunningSinceUtc;
    private TimeSpan _clockAccumulated = TimeSpan.Zero;
    private bool _clockPaused;
    private DateTimeOffset _sessionStartedUtc = DateTimeOffset.MinValue;

    public YouTubeQueueService(
        LocalAudioRelayServer relayServer,
        ReceiverController receiverController,
        ReceiverStateStore stateStore,
        AppLogStore logStore)
    {
        _relayServer = relayServer;
        _receiverController = receiverController;
        _stateStore = stateStore;
        _logStore = logStore;

        _relayServer.TrackPlaybackStarted += OnRelayTrackPlaybackStarted;
        _relayServer.TrackStreamCompleted += OnRelayTrackStreamCompleted;
        _receiverController.NonRelaySourceActivated += OnNonRelaySourceActivated;
        _stateStore.StateChanged += OnReceiverStateChanged;
    }

    public event Action<YouTubeQueueSnapshot>? QueueChanged;

    public event Action<YouTubeTrackItem>? TrackStarted;

    public YouTubeQueueSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return BuildSnapshotLocked();
            }
        }
    }

    public bool IsYouTubePlaybackActive
    {
        get
        {
            lock (_sync)
            {
                return _isActive && _currentIndex >= 0 && _currentIndex < _queue.Count;
            }
        }
    }

    public double GetEstimatedPlaybackPositionSeconds(int heosBufferOffsetMs = 1800)
    {
        lock (_sync)
        {
            if (!_isActive || _currentIndex < 0 || _currentIndex >= _queue.Count)
            {
                return 0;
            }

            var elapsed = _clockAccumulated;
            if (!_clockPaused && _clockRunningSinceUtc is { } startedAt)
            {
                elapsed += DateTimeOffset.UtcNow - startedAt;
            }

            var compensatedSeconds = Math.Max(0.0, elapsed.TotalSeconds - (Math.Clamp(heosBufferOffsetMs, 0, 8000) / 1000.0));
            if (_queue[_currentIndex].Duration is { } duration && duration.TotalSeconds > 1)
            {
                compensatedSeconds = Math.Min(compensatedSeconds, Math.Max(0, duration.TotalSeconds - 1));
            }

            return compensatedSeconds;
        }
    }

    public async Task<YouTubeTrackItem> PlayNowAsync(
        YouTubeTrackItem track,
        bool useLowVolume,
        bool unmute,
        CancellationToken cancellationToken = default)
    {
        var queuedItem = track.WithNewQueueEntryId();
        int targetIndex;

        lock (_sync)
        {
            _lastUseLowVolume = useLowVolume;
            _lastUnmute = unmute;

            if (_currentIndex >= 0 && _currentIndex < _queue.Count)
            {
                targetIndex = _currentIndex + 1;
                _queue.Insert(targetIndex, queuedItem);
            }
            else
            {
                _queue.Add(queuedItem);
                targetIndex = _queue.Count - 1;
            }
        }

        await PlayAtIndexAsync(targetIndex, useLowVolume, unmute, cancellationToken);
        return queuedItem;
    }

    public async Task PlayAllAsync(
        IEnumerable<YouTubeTrackItem> tracks,
        bool useLowVolume,
        bool unmute,
        CancellationToken cancellationToken = default)
    {
        var items = tracks.Select(static t => t.WithNewQueueEntryId()).ToList();
        if (items.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            _lastUseLowVolume = useLowVolume;
            _lastUnmute = unmute;
            _queue.Clear();
            _queue.AddRange(items);
            _currentIndex = 0;
        }

        await PlayAtIndexAsync(0, useLowVolume, unmute, cancellationToken);
    }

    public void Enqueue(YouTubeTrackItem track)
    {
        lock (_sync)
        {
            _queue.Add(track.WithNewQueueEntryId());
        }

        NotifyChanged();
    }

    public void EnqueueRange(IEnumerable<YouTubeTrackItem> tracks)
    {
        lock (_sync)
        {
            foreach (var track in tracks)
            {
                _queue.Add(track.WithNewQueueEntryId());
            }
        }

        NotifyChanged();
    }

    public async Task PlayQueueEntryAsync(
        string entryId,
        bool useLowVolume,
        bool unmute,
        CancellationToken cancellationToken = default)
    {
        int index;
        lock (_sync)
        {
            index = _queue.FindIndex(t => string.Equals(t.EntryId, entryId, StringComparison.Ordinal));
        }

        if (index >= 0)
        {
            await PlayAtIndexAsync(index, useLowVolume, unmute, cancellationToken);
        }
    }

    public Task<bool> PlayNextAsync(CancellationToken cancellationToken = default) =>
        PlayNextCoreAsync(overrideUnmute: null, cancellationToken);

    private Task<bool> PlayNextAutoAsync(bool unmute, CancellationToken cancellationToken) =>
        PlayNextCoreAsync(overrideUnmute: unmute, cancellationToken);

    private async Task<bool> PlayNextCoreAsync(bool? overrideUnmute, CancellationToken cancellationToken)
    {
        int nextIndex;
        bool lowVol;
        bool unmute;

        lock (_sync)
        {
            if (_queue.Count == 0)
            {
                return false;
            }

            lowVol = false; // Don't force safety low volume on track skip
            unmute = overrideUnmute ?? _lastUnmute;

            if (_repeatMode == YouTubeRepeatMode.One && _currentIndex >= 0 && _currentIndex < _queue.Count)
            {
                nextIndex = _currentIndex;
            }
            else if (_isShuffleEnabled && _queue.Count > 1)
            {
                do
                {
                    nextIndex = _random.Next(_queue.Count);
                }
                while (nextIndex == _currentIndex);
            }
            else if (_currentIndex + 1 < _queue.Count)
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

        await PlayAtIndexAsync(nextIndex, lowVol, unmute, cancellationToken);
        return true;
    }

    public async Task<bool> PlayPreviousAsync(CancellationToken cancellationToken = default)
    {
        int prevIndex;
        bool unmute;

        lock (_sync)
        {
            if (_queue.Count == 0)
            {
                return false;
            }

            unmute = _lastUnmute;
            if (_currentIndex > 0)
            {
                prevIndex = _currentIndex - 1;
            }
            else if (_repeatMode == YouTubeRepeatMode.All)
            {
                prevIndex = _queue.Count - 1;
            }
            else
            {
                prevIndex = 0;
            }
        }

        await PlayAtIndexAsync(prevIndex, useLowVolume: false, unmute, cancellationToken);
        return true;
    }

    public void RemoveFromQueue(string entryId)
    {
        lock (_sync)
        {
            var idx = _queue.FindIndex(t => string.Equals(t.EntryId, entryId, StringComparison.Ordinal));
            if (idx < 0)
            {
                return;
            }

            _queue.RemoveAt(idx);
            if (idx < _currentIndex)
            {
                _currentIndex--;
            }
            else if (idx == _currentIndex)
            {
                if (_queue.Count == 0)
                {
                    _currentIndex = -1;
                    _isActive = false;
                }
                else
                {
                    _currentIndex = Math.Min(_currentIndex, _queue.Count - 1);
                }
            }
        }

        NotifyChanged();
    }

    public void MoveQueueItem(string entryId, int delta)
    {
        lock (_sync)
        {
            var idx = _queue.FindIndex(t => string.Equals(t.EntryId, entryId, StringComparison.Ordinal));
            var target = idx + delta;
            if (idx < 0 || target < 0 || target >= _queue.Count)
            {
                return;
            }

            var item = _queue[idx];
            _queue.RemoveAt(idx);
            _queue.Insert(target, item);

            if (_currentIndex == idx)
            {
                _currentIndex = target;
            }
            else if (idx < _currentIndex && target >= _currentIndex)
            {
                _currentIndex--;
            }
            else if (idx > _currentIndex && target <= _currentIndex)
            {
                _currentIndex++;
            }
        }

        NotifyChanged();
    }

    public void ClearQueue()
    {
        CancelPendingAutoAdvance();
        string? activeToken;
        lock (_sync)
        {
            activeToken = _isActive ? _activeRelayToken : null;
            _queue.Clear();
            _currentIndex = -1;
            _isActive = false;
            _activeRelayToken = null;
        }

        // Cancel ONLY YouTube's own active stream token so clearing an inactive queue never
        // interrupts an active Local Jukebox or PC Audio Cast session!
        if (!string.IsNullOrWhiteSpace(activeToken))
        {
            _relayServer.CancelStream(activeToken);
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
            _relayServer.CancelStream(activeToken);
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

    private async Task PlayAtIndexAsync(
        int index,
        bool useLowVolume,
        bool unmute,
        CancellationToken cancellationToken)
    {
        CancelPendingAutoAdvance();

        YouTubeTrackItem track;
        int prevIndex;
        bool prevActive;
        string? prevToken;
        lock (_sync)
        {
            if (index < 0 || index >= _queue.Count)
            {
                return;
            }

            prevIndex = _currentIndex;
            prevActive = _isActive;
            prevToken = _activeRelayToken;
            track = _queue[index];
        }

        string relayUrl;
        string token;
        try
        {
            relayUrl = await _relayServer.RegisterTrackAsync(track, cancellationToken);
            token = ExtractTokenFromUrl(relayUrl) ?? string.Empty;

            lock (_sync)
            {
                _activeRelayToken = token;
                _clockAccumulated = TimeSpan.Zero;
                _clockRunningSinceUtc = null;
                _clockPaused = false;
            }

            await _receiverController.PlayRelayStreamAsync(relayUrl, track.Title, useLowVolume, unmute, cancellationToken);
        }
        catch
        {
            // Roll back pending state if relay registration or receiver cast failed so UI & history remain truthful.
            lock (_sync)
            {
                _currentIndex = prevIndex;
                _isActive = prevActive;
                _activeRelayToken = prevToken;
            }

            NotifyChanged();
            throw;
        }

        lock (_sync)
        {
            var confirmedIdx = _queue.FindIndex(t => string.Equals(t.EntryId, track.EntryId, StringComparison.Ordinal));
            _currentIndex = confirmedIdx >= 0 ? confirmedIdx : Math.Clamp(index, 0, Math.Max(0, _queue.Count - 1));
            _isActive = _queue.Count > 0;
            _sessionStartedUtc = DateTimeOffset.UtcNow;

            _history.RemoveAll(t => string.Equals(t.VideoId, track.VideoId, StringComparison.Ordinal));
            _history.Insert(0, track);
            if (_history.Count > 20)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            // Fallback start time if HEOS hasn't crossed the initial 16KB threshold yet
            if (string.Equals(_activeRelayToken, token, StringComparison.Ordinal) && _clockRunningSinceUtc is null)
            {
                _clockRunningSinceUtc = DateTimeOffset.UtcNow;
            }
        }

        TrackStarted?.Invoke(track);
        NotifyChanged();
    }

    private void OnRelayTrackPlaybackStarted(string token, DateTimeOffset startedAtUtc)
    {
        lock (_sync)
        {
            if (!_isActive || !string.Equals(_activeRelayToken, token, StringComparison.Ordinal))
            {
                return;
            }

            // Reset clock cleanly to the exact moment HEOS starts pulling real audio frames
            _clockAccumulated = TimeSpan.Zero;
            _clockRunningSinceUtc = startedAtUtc;
            _clockPaused = false;
        }
    }

    private void OnReceiverStateChanged(ReceiverStateSnapshot snapshot)
    {
        if (!snapshot.IsConnected || snapshot.Status is not { } status)
        {
            return;
        }

        string? externalTakeoverReason = null;
        var playState = status.PlayState;

        lock (_sync)
        {
            if (!_isActive)
            {
                return;
            }

            var pastStartupGrace = (DateTimeOffset.UtcNow - _sessionStartedUtc) >= TimeSpan.FromSeconds(5);
            if (pastStartupGrace)
            {
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
                else if (string.Equals(playState, "play", StringComparison.OrdinalIgnoreCase) &&
                         status.NowPlaying.HasRealMetadata)
                {
                    externalTakeoverReason = $"external HEOS stream started ({status.NowPlaying.Song})";
                }
            }

            if (externalTakeoverReason is null && !string.IsNullOrWhiteSpace(playState))
            {
                var isPausedNow = string.Equals(playState, "pause", StringComparison.OrdinalIgnoreCase);
                var isPlayingNow = string.Equals(playState, "play", StringComparison.OrdinalIgnoreCase);

                if (isPausedNow && !_clockPaused)
                {
                    if (_clockRunningSinceUtc is { } started)
                    {
                        _clockAccumulated += DateTimeOffset.UtcNow - started;
                    }

                    _clockRunningSinceUtc = null;
                    _clockPaused = true;
                }
                else if (isPlayingNow && _clockPaused)
                {
                    _clockRunningSinceUtc = DateTimeOffset.UtcNow;
                    _clockPaused = false;
                }
            }
        }

        if (externalTakeoverReason is not null)
        {
            _logStore.Info($"Deactivating YouTube cast session because {externalTakeoverReason}.");
            StopSession();
        }
    }

    private void OnRelayTrackStreamCompleted(string token, YouTubeTrackItem track)
    {
        lock (_sync)
        {
            if (!_isActive || !string.Equals(_activeRelayToken, token, StringComparison.Ordinal))
            {
                return;
            }
        }

        CancelPendingAutoAdvance();
        var cts = new CancellationTokenSource();
        _autoAdvanceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for HEOS's internal audio buffer to finish draining before advancing or stopping.
                // Suspend the drain timer whenever HEOS is paused so pausing a buffered track never
                // prematurely skips to the next track!
                var elapsedSec = GetEstimatedPlaybackPositionSeconds(heosBufferOffsetMs: 0);
                var expectedDrainSec = track.Duration is { TotalSeconds: > 5 } dur
                    ? Math.Clamp((dur.TotalSeconds - elapsedSec) + 3.5, 4.0, 90.0)
                    : 6.0;

                if (expectedDrainSec > 8.0)
                {
                    _logStore.Info($"Waiting up to {expectedDrainSec:0.0}s for Denon HEOS buffer to finish '{track.Title}'.");
                }

                var playedWaitSec = 0.0;
                while (playedWaitSec < expectedDrainSec && !cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(500, cts.Token);

                    var playState = _stateStore.Snapshot.Status?.PlayState;
                    if (string.Equals(playState, "pause", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    playedWaitSec += 0.5;

                    // Once past initial 2.5s active grace, if HEOS itself transitions to 'stop' then its buffer is empty!
                    if (playedWaitSec >= 2.5 &&
                        string.Equals(playState, "stop", StringComparison.OrdinalIgnoreCase))
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

                // Preserve user's current mute state across automatic queue transitions!
                var keepMuted = _stateStore.Snapshot.Status?.IsMuted == true;
                var advanced = await PlayNextAutoAsync(unmute: !keepMuted, CancellationToken.None);
                if (advanced)
                {
                    _logStore.Info($"YouTube queue auto-advanced after '{track.Title}'.");
                }
                else
                {
                    lock (_sync)
                    {
                        if (string.Equals(_activeRelayToken, token, StringComparison.Ordinal))
                        {
                            _isActive = false;
                            _activeRelayToken = null;
                            _clockRunningSinceUtc = null;
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

                    _logStore.Info($"YouTube queue finished after '{track.Title}'.");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logStore.Warning($"YouTube queue auto-advance failed: {ex.Message}");
            }
        }, cts.Token);
    }

    private void OnNonRelaySourceActivated()
    {
        var wasActive = false;
        string? activeToken = null;
        lock (_sync)
        {
            if (_isActive)
            {
                activeToken = _activeRelayToken;
                _isActive = false;
                _activeRelayToken = null;
                wasActive = true;
            }
        }

        if (wasActive)
        {
            CancelPendingAutoAdvance();
            if (!string.IsNullOrWhiteSpace(activeToken))
            {
                _relayServer.CancelStream(activeToken);
            }

            NotifyChanged();
        }
    }

    private void CancelPendingAutoAdvance()
    {
        try { _autoAdvanceCts?.Cancel(); } catch { }
        _autoAdvanceCts?.Dispose();
        _autoAdvanceCts = null;
    }

    private static string? ExtractTokenFromUrl(string url)
    {
        var idx = url.LastIndexOf("/yt/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var tail = url[(idx + 4)..];
        var dot = tail.IndexOf('.');
        return dot > 0 ? tail[..dot] : tail;
    }

    private YouTubeQueueSnapshot BuildSnapshotLocked() =>
        new([.. _queue], _currentIndex, _isActive, _isShuffleEnabled, _repeatMode, [.. _history]);

    private void NotifyChanged() => QueueChanged?.Invoke(Snapshot);

    public void Dispose()
    {
        CancelPendingAutoAdvance();
        _relayServer.TrackPlaybackStarted -= OnRelayTrackPlaybackStarted;
        _relayServer.TrackStreamCompleted -= OnRelayTrackStreamCompleted;
        _receiverController.NonRelaySourceActivated -= OnNonRelaySourceActivated;
        _stateStore.StateChanged -= OnReceiverStateChanged;
    }
}
