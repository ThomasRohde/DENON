using Denon.App.Core.Models;

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

    public LocalMediaJukeboxService(
        LocalAudioRelayServer relayServer,
        ReceiverController receiverController,
        ReceiverStateStore stateStore,
        AppLogStore logStore)
    {
        _relayServer = relayServer;
        _receiverController = receiverController;
        _stateStore = stateStore;
        _logStore = logStore;

        _relayServer.LocalTrackStreamCompleted += OnLocalTrackCompleted;
        _receiverController.NonRelaySourceActivated += OnExternalSourceActivated;
    }

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
                _tracks.Add(new LocalAudioTrackItem(
                    TrackId: Guid.NewGuid().ToString("N")[..10],
                    FilePath: file,
                    FileName: info.Name,
                    Title: title,
                    Artist: artist,
                    Extension: info.Extension,
                    FileSizeBytes: info.Exists ? info.Length : 0));
                added++;
            }
        }

        if (added > 0)
        {
            _logStore.Info($"Added {added} local audio track(s) to Hi-Res Jukebox{(duplicates > 0 ? $" ({duplicates} duplicate(s) skipped)" : string.Empty)}.");
            NotifyChanged();
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

    public async Task<bool> PlayNextAsync(CancellationToken cancellationToken = default)
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

        await PlayAtIndexAsync(nextIndex, useLowVolume: false, unmute: true, cancellationToken);
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
        lock (_sync)
        {
            _tracks.Clear();
            _currentIndex = -1;
            _isActive = false;
            _activeRelayToken = null;
        }

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

        try
        {
            var (relayUrl, token) = await _relayServer.RegisterLocalTrackAsync(track, cancellationToken);
            lock (_sync)
            {
                _activeRelayToken = token;
            }

            await _receiverController.PlayRelayStreamAsync(relayUrl, $"{track.Title} ({track.CodecBadge})", useLowVolume, unmute, cancellationToken);

            lock (_sync)
            {
                var confirmedIdx = _tracks.FindIndex(t => string.Equals(t.TrackId, track.TrackId, StringComparison.Ordinal));
                _currentIndex = confirmedIdx >= 0 ? confirmedIdx : Math.Clamp(index, 0, Math.Max(0, _tracks.Count - 1));
                _isActive = _tracks.Count > 0;
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

        CancelPendingAutoAdvance();
        var cts = new CancellationTokenSource();
        _autoAdvanceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for HEOS to finish draining its prefetched audio buffer (watch playState == "stop" up to 45s)
                var minGrace = DateTimeOffset.UtcNow.AddSeconds(2.5);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(45.0);
                var sawPlaying = string.Equals(_stateStore.Snapshot.Status?.PlayState, "play", StringComparison.OrdinalIgnoreCase);

                while (DateTimeOffset.UtcNow < deadline && !cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(500, cts.Token);
                    var state = _stateStore.Snapshot.Status?.PlayState;
                    if (string.Equals(state, "play", StringComparison.OrdinalIgnoreCase))
                    {
                        sawPlaying = true;
                    }
                    else if (DateTimeOffset.UtcNow >= minGrace &&
                             string.Equals(state, "stop", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    else if (!sawPlaying && DateTimeOffset.UtcNow >= minGrace.AddSeconds(1.5))
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

                // IMPORTANT: Use a fresh CancellationToken so PlayAtIndexAsync's CancelPendingAutoAdvance()
                // does not self-cancel the transition to the next local track!
                var advanced = await PlayNextAsync(CancellationToken.None);
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
        }, cts.Token);
    }

    private void OnExternalSourceActivated()
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
            NotifyChanged();
        }
    }

    private void CancelPendingAutoAdvance()
    {
        try { _autoAdvanceCts?.Cancel(); } catch { }
        _autoAdvanceCts?.Dispose();
        _autoAdvanceCts = null;
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

    private void NotifyChanged() => JukeboxChanged?.Invoke(Snapshot);

    public void Dispose()
    {
        CancelPendingAutoAdvance();
        _relayServer.LocalTrackStreamCompleted -= OnLocalTrackCompleted;
        _receiverController.NonRelaySourceActivated -= OnExternalSourceActivated;
    }
}
