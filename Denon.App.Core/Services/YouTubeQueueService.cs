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

    public YouTubeQueueService(
        LocalAudioRelayServer relayServer,
        ReceiverController receiverController,
        AppLogStore logStore)
    {
        _relayServer = relayServer;
        _receiverController = receiverController;
        _logStore = logStore;

        _relayServer.TrackStreamCompleted += OnRelayTrackStreamCompleted;
        _receiverController.NonRelaySourceActivated += OnNonRelaySourceActivated;
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

    public async Task<bool> PlayNextAsync(CancellationToken cancellationToken = default)
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
            unmute = _lastUnmute;

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
        lock (_sync)
        {
            _queue.Clear();
            _currentIndex = -1;
            _isActive = false;
            _activeRelayToken = null;
        }

        _relayServer.CancelAllActiveStreams();
        NotifyChanged();
    }

    public void StopSession()
    {
        CancelPendingAutoAdvance();
        lock (_sync)
        {
            _isActive = false;
            _activeRelayToken = null;
        }

        _relayServer.CancelAllActiveStreams();
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
        lock (_sync)
        {
            if (index < 0 || index >= _queue.Count)
            {
                return;
            }

            _currentIndex = index;
            _isActive = true;
            track = _queue[index];

            _history.RemoveAll(t => string.Equals(t.VideoId, track.VideoId, StringComparison.Ordinal));
            _history.Insert(0, track);
            if (_history.Count > 20)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }

        NotifyChanged();

        var relayUrl = await _relayServer.RegisterTrackAsync(track, cancellationToken);
        var token = ExtractTokenFromUrl(relayUrl);
        lock (_sync)
        {
            _activeRelayToken = token;
        }

        await _receiverController.PlayRelayStreamAsync(relayUrl, track.Title, useLowVolume, unmute, cancellationToken);
        TrackStarted?.Invoke(track);
        NotifyChanged();
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
                // Allow HEOS internal audio buffer (~2.5s) to finish playing the final audio frames
                await Task.Delay(TimeSpan.FromSeconds(2.6), cts.Token);

                var advanced = await PlayNextAsync(cts.Token);
                if (advanced)
                {
                    _logStore.Info($"YouTube queue auto-advanced after '{track.Title}'.");
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
        lock (_sync)
        {
            if (_isActive)
            {
                _isActive = false;
                _activeRelayToken = null;
                wasActive = true;
            }
        }

        if (wasActive)
        {
            CancelPendingAutoAdvance();
            _relayServer.CancelAllActiveStreams();
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
        _relayServer.TrackStreamCompleted -= OnRelayTrackStreamCompleted;
        _receiverController.NonRelaySourceActivated -= OnNonRelaySourceActivated;
    }
}
