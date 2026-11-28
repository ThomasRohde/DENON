using System.Collections.ObjectModel;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private YouTubeMediaService _youtubeService = null!;
    private LocalAudioRelayServer _relayServer = null!;
    private YouTubeQueueService _youtubeQueueService = null!;
    private YouTubeCinemaWindow _cinemaWindow = null!;

    private string _youtubeSearchInput = string.Empty;
    private YouTubeSearchMode _youtubeSearchMode = YouTubeSearchMode.Music;
    private bool _isSearchingYouTube;
    private string _youtubeStatusText = "Search YouTube Music, videos, or paste any YouTube / playlist URL to stream directly to your Denon.";
    private bool _isYouTubeActive;
    private string _currentYouTubeTitle = string.Empty;
    private string _currentYouTubeAuthor = string.Empty;
    private string _relayEndpointText = "Local Audio Relay: Standby (auto-starts on play)";
    private string _relayCodecBadge = "READY";
    private string _relayStreamStateText = "Idle";
    private bool _isYouTubeShuffleEnabled;
    private YouTubeRepeatMode _youtubeRepeatMode = YouTubeRepeatMode.Off;

    // Settings editor fields for Media Relay
    private string _settingsRelayPort = "18925";
    private string _settingsFfmpegPath = string.Empty;
    private bool _settingsPreferFfmpegMp3 = true;
    private string _settingsCinemaSyncOffsetMs = "1800";

    public ObservableCollection<YouTubeTrackItem> YouTubeSearchResults { get; } = [];
    public ObservableCollection<YouTubePlaylistSummary> YouTubePlaylistResults { get; } = [];
    public ObservableCollection<YouTubeQueueItemViewModel> YouTubeQueue { get; } = [];
    public ObservableCollection<YouTubeTrackItem> YouTubeHistory { get; } = [];

    public AsyncRelayCommand SearchYouTubeCommand { get; private set; } = null!;
    public RelayCommand SetYouTubeSearchModeCommand { get; private set; } = null!;
    public AsyncRelayCommand PlayYouTubeTrackCommand { get; private set; } = null!;
    public AsyncRelayCommand PlayYouTubeTrackAndWatchCommand { get; private set; } = null!;
    public RelayCommand EnqueueYouTubeTrackCommand { get; private set; } = null!;
    public AsyncRelayCommand PlayAllYouTubeResultsCommand { get; private set; } = null!;
    public RelayCommand EnqueueAllYouTubeResultsCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenYouTubePlaylistCommand { get; private set; } = null!;
    public AsyncRelayCommand PlayYouTubeQueueItemCommand { get; private set; } = null!;
    public RelayCommand RemoveYouTubeQueueItemCommand { get; private set; } = null!;
    public RelayCommand MoveYouTubeQueueUpCommand { get; private set; } = null!;
    public RelayCommand MoveYouTubeQueueDownCommand { get; private set; } = null!;
    public RelayCommand ClearYouTubeQueueCommand { get; private set; } = null!;
    public RelayCommand ToggleYouTubeShuffleCommand { get; private set; } = null!;
    public RelayCommand CycleYouTubeRepeatCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenCinemaForCurrentTrackCommand { get; private set; } = null!;
    public RelayCommand PasteAndSearchYouTubeCommand { get; private set; } = null!;
    public AsyncRelayCommand AllowRelayFirewallCommand { get; private set; } = null!;

    private void InitializeYouTube(
        YouTubeMediaService youtubeService,
        LocalAudioRelayServer relayServer,
        YouTubeQueueService youtubeQueueService,
        YouTubeCinemaWindow cinemaWindow)
    {
        _youtubeService = youtubeService;
        _relayServer = relayServer;
        _youtubeQueueService = youtubeQueueService;
        _cinemaWindow = cinemaWindow;

        SearchYouTubeCommand = new AsyncRelayCommand(_ => SearchYouTubeAsync(), _ => !IsSearchingYouTube);
        SetYouTubeSearchModeCommand = new RelayCommand(param =>
        {
            if (param is string modeStr && Enum.TryParse<YouTubeSearchMode>(modeStr, ignoreCase: true, out var parsed) &&
                parsed != YouTubeSearchMode)
            {
                YouTubeSearchMode = parsed;

                // A tab switch after a search shows that tab's results at once (the old results stayed until Search).
                var query = YouTubeSearchInput.Trim();
                if (query.Length > 0 && !YouTubeMediaService.IsYouTubeUrl(query) && YouTubeSearchResults.Count > 0 &&
                    SearchYouTubeCommand.CanExecute(null))
                {
                    SearchYouTubeCommand.Execute(null);
                }
            }
        });
        PlayYouTubeTrackCommand = new AsyncRelayCommand(PlayYouTubeTrackAsync, CanControlReceiver);
        PlayYouTubeTrackAndWatchCommand = new AsyncRelayCommand(PlayYouTubeTrackAndWatchAsync, CanControlReceiver);
        EnqueueYouTubeTrackCommand = new RelayCommand(EnqueueYouTubeTrack);
        PlayAllYouTubeResultsCommand = new AsyncRelayCommand(_ => PlayAllYouTubeResultsAsync(), _ => !IsBusy && YouTubeSearchResults.Count > 0);
        EnqueueAllYouTubeResultsCommand = new RelayCommand(_ => EnqueueAllYouTubeResults(), _ => YouTubeSearchResults.Count > 0);
        OpenYouTubePlaylistCommand = new AsyncRelayCommand(OpenYouTubePlaylistAsync, _ => !IsSearchingYouTube);
        PlayYouTubeQueueItemCommand = new AsyncRelayCommand(PlayYouTubeQueueItemAsync, CanControlReceiver);
        RemoveYouTubeQueueItemCommand = new RelayCommand(RemoveYouTubeQueueItem);
        MoveYouTubeQueueUpCommand = new RelayCommand(MoveYouTubeQueueUp);
        MoveYouTubeQueueDownCommand = new RelayCommand(MoveYouTubeQueueDown);
        ClearYouTubeQueueCommand = new RelayCommand(_ => _youtubeQueueService.ClearQueue());
        ToggleYouTubeShuffleCommand = new RelayCommand(_ => _youtubeQueueService.ToggleShuffle());
        CycleYouTubeRepeatCommand = new RelayCommand(_ => _youtubeQueueService.CycleRepeatMode());
        OpenCinemaForCurrentTrackCommand = new AsyncRelayCommand(_ => OpenCinemaForCurrentTrackAsync(), _ => IsYouTubeActive);
        PasteAndSearchYouTubeCommand = new RelayCommand(_ => PasteFromClipboardAndSearch());
        AllowRelayFirewallCommand = new AsyncRelayCommand(_ => AllowRelayFirewallAsync());

        _relayServer.StatusChanged += snapshot => _ = _dispatcher.InvokeAsync(() => ApplyRelayStatus(snapshot));
        _youtubeQueueService.QueueChanged += snapshot => _ = _dispatcher.InvokeAsync(() => ApplyYouTubeQueueSnapshot(snapshot));
        _cinemaWindow.SyncOffsetChanged += offsetMs => _ = SaveCinemaSyncOffsetAsync(offsetMs);
        _youtubeQueueService.TrackStarted += track => _ = _dispatcher.InvokeAsync(async () =>
        {
            if (_cinemaWindow.IsVisible)
            {
                await _cinemaWindow.LoadTrackAsync(
                    track,
                    _settingsStore.Current.Media.CinemaSyncOffsetMs,
                    offsetMs => _youtubeQueueService.GetEstimatedPlaybackPositionSeconds(offsetMs));
            }
        });

        ApplyRelayStatus(_relayServer.CurrentStatus);
        ApplyYouTubeQueueSnapshot(_youtubeQueueService.Snapshot);
        RefreshFfmpegBadge();
    }

    public string YouTubeSearchInput
    {
        get => _youtubeSearchInput;
        set => SetProperty(ref _youtubeSearchInput, value);
    }

    public YouTubeSearchMode YouTubeSearchMode
    {
        get => _youtubeSearchMode;
        set
        {
            if (SetProperty(ref _youtubeSearchMode, value))
            {
                OnPropertyChanged(nameof(YouTubeSearchModeName));
                OnPropertyChanged(nameof(IsPlaylistResultsVisible));
            }
        }
    }

    public string YouTubeSearchModeName => YouTubeSearchMode.ToString();

    public bool IsPlaylistResultsVisible => YouTubePlaylistResults.Count > 0;

    public bool IsSearchingYouTube
    {
        get => _isSearchingYouTube;
        private set
        {
            if (SetProperty(ref _isSearchingYouTube, value))
            {
                SearchYouTubeCommand.NotifyCanExecuteChanged();
                OpenYouTubePlaylistCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string YouTubeStatusText
    {
        get => _youtubeStatusText;
        private set => SetProperty(ref _youtubeStatusText, value);
    }

    public bool IsYouTubeActive
    {
        get => _isYouTubeActive;
        private set
        {
            if (SetProperty(ref _isYouTubeActive, value))
            {
                OpenCinemaForCurrentTrackCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    public string CurrentYouTubeTitle
    {
        get => _currentYouTubeTitle;
        private set => SetProperty(ref _currentYouTubeTitle, value);
    }

    public string CurrentYouTubeAuthor
    {
        get => _currentYouTubeAuthor;
        private set => SetProperty(ref _currentYouTubeAuthor, value);
    }

    public string RelayEndpointText
    {
        get => _relayEndpointText;
        private set => SetProperty(ref _relayEndpointText, value);
    }

    public string RelayCodecBadge
    {
        get => _relayCodecBadge;
        private set => SetProperty(ref _relayCodecBadge, value);
    }

    public string RelayStreamStateText
    {
        get => _relayStreamStateText;
        private set => SetProperty(ref _relayStreamStateText, value);
    }

    public bool IsYouTubeShuffleEnabled
    {
        get => _isYouTubeShuffleEnabled;
        private set => SetProperty(ref _isYouTubeShuffleEnabled, value);
    }

    public YouTubeRepeatMode YouTubeRepeatMode
    {
        get => _youtubeRepeatMode;
        private set
        {
            if (SetProperty(ref _youtubeRepeatMode, value))
            {
                OnPropertyChanged(nameof(YouTubeRepeatLabel));
            }
        }
    }

    public string YouTubeRepeatLabel => YouTubeRepeatMode switch
    {
        YouTubeRepeatMode.All => "Repeat: All",
        YouTubeRepeatMode.One => "Repeat: One",
        _ => "Repeat: Off",
    };

    public string YouTubeQueueCountLabel => YouTubeQueue.Count == 1 ? "1 track" : $"{YouTubeQueue.Count} tracks";

    public bool HasYouTubeHistory => YouTubeHistory.Count > 0;

    public string SettingsRelayPort
    {
        get => _settingsRelayPort;
        set => SetProperty(ref _settingsRelayPort, value);
    }

    public string SettingsFfmpegPath
    {
        get => _settingsFfmpegPath;
        set => SetProperty(ref _settingsFfmpegPath, value);
    }

    public bool SettingsPreferFfmpegMp3
    {
        get => _settingsPreferFfmpegMp3;
        set => SetProperty(ref _settingsPreferFfmpegMp3, value);
    }

    public string SettingsCinemaSyncOffsetMs
    {
        get => _settingsCinemaSyncOffsetMs;
        set => SetProperty(ref _settingsCinemaSyncOffsetMs, value);
    }

    private void RefreshFfmpegBadge()
    {
        var ffmpeg = _youtubeService.FindFfmpegPath();
        var preferMp3 = _settingsStore.Current.Media.PreferFfmpegMp3Transcode;
        if (!string.IsNullOrWhiteSpace(ffmpeg) && preferMp3)
        {
            RelayCodecBadge = "MP3 320K (FFMPEG)";
        }
        else
        {
            RelayCodecBadge = "DIRECT M4A PROXY";
        }
    }

    private void PasteFromClipboardAndSearch()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText().Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    YouTubeSearchInput = text;
                    if (SearchYouTubeCommand.CanExecute(null))
                    {
                        SearchYouTubeCommand.Execute(null);
                    }
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// A Cinema nudge saves only the offset, quietly: a full settings change would reload the Settings, Scenes and
    /// Stations editors and drop edits that are not saved yet.
    /// </summary>
    private async Task SaveCinemaSyncOffsetAsync(int offsetMs)
    {
        try
        {
            var (updated, _) = await _settingsStore.UpdateQuietAsync(s => s.Media.CinemaSyncOffsetMs = offsetMs);
            SettingsCinemaSyncOffsetMs = updated.Media.CinemaSyncOffsetMs.ToString();
        }
        catch (Exception ex)
        {
            _logStore.Warning($"Cinema sync offset not saved: {ex.Message}");
        }
    }

    private const string FirewallRuleName = "Denon Desktop Audio Relay";

    private static string NetshPath => System.IO.Path.Combine(Environment.SystemDirectory, "netsh.exe");

    /// <summary>
    /// "set" when the rule exists (so a second click does not add a second rule), else "add". Scoped to this exe
    /// when its path is known. The check runs without elevation; only the change asks for it.
    /// </summary>
    private static async Task<string> BuildFirewallRuleArgumentsAsync(int port)
    {
        var program = Environment.ProcessPath is { Length: > 0 } path ? $" program=\"{path}\"" : string.Empty;
        var exists = false;
        try
        {
            using var check = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = NetshPath,
                Arguments = $"advfirewall firewall show rule name=\"{FirewallRuleName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (check is not null)
            {
                await check.WaitForExitAsync();
                exists = check.ExitCode == 0;
            }
        }
        catch
        {
            // Add a rule then.
        }

        return exists
            ? $"advfirewall firewall set rule name=\"{FirewallRuleName}\" new dir=in action=allow protocol=TCP localport={port} profile=private{program}"
            : $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port} profile=private{program}";
    }

    private async Task AllowRelayFirewallAsync()
    {
        try
        {
            await _relayServer.EnsureStartedAsync();
            var port = _relayServer.BoundPort > 0 ? _relayServer.BoundPort : _settingsStore.Current.Media.RelayPort;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                // The full path: a bare "netsh" would run a netsh.exe planted in the app's folder, elevated.
                FileName = NetshPath,
                Arguments = await BuildFirewallRuleArgumentsAsync(port),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0)
                {
                    YouTubeStatusText = $"Windows Firewall rule added for Private LAN TCP port {port}.";
                    StatusMessage = $"Firewall opened for LAN Audio Relay (port {port}).";
                    _logStore.Info($"Added Windows Firewall inbound rule for TCP port {port}.");
                }
                else
                {
                    YouTubeStatusText = $"Firewall command exited with code {proc.ExitCode}.";
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            YouTubeStatusText = "Firewall prompt canceled.";
        }
        catch (Exception ex)
        {
            YouTubeStatusText = $"Could not configure firewall: {ex.Message}";
            _logStore.Warning($"Firewall rule setup failed: {ex.Message}");
        }
    }

    private async Task SearchYouTubeAsync()
    {
        var query = YouTubeSearchInput.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            YouTubeStatusText = "Enter a song, artist, video title, or YouTube URL.";
            return;
        }

        IsSearchingYouTube = true;
        YouTubeStatusText = YouTubeMediaService.IsYouTubeUrl(query)
            ? "Resolving YouTube link..."
            : $"Searching YouTube ({YouTubeSearchMode}) for \"{query}\"...";

        try
        {
            var response = await _youtubeService.SearchOrResolveAsync(query, YouTubeSearchMode);
            YouTubeSearchResults.Clear();
            foreach (var track in response.Tracks)
            {
                YouTubeSearchResults.Add(track);
            }

            YouTubePlaylistResults.Clear();
            foreach (var pl in response.Playlists)
            {
                YouTubePlaylistResults.Add(pl);
            }

            YouTubeStatusText = response.SummaryText;
            OnPropertyChanged(nameof(IsPlaylistResultsVisible));
            PlayAllYouTubeResultsCommand.NotifyCanExecuteChanged();
            EnqueueAllYouTubeResultsCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            YouTubeStatusText = $"YouTube lookup failed: {ex.Message}";
            _logStore.Error("YouTube search failed.", ex);
        }
        finally
        {
            IsSearchingYouTube = false;
        }
    }

    private async Task OpenYouTubePlaylistAsync(object? parameter)
    {
        if (parameter is not YouTubePlaylistSummary playlist)
        {
            return;
        }

        IsSearchingYouTube = true;
        YouTubeStatusText = $"Loading playlist \"{playlist.Title}\"...";

        try
        {
            var tracks = await _youtubeService.LoadPlaylistTracksAsync(playlist.PlaylistUrl, 40);
            YouTubePlaylistResults.Clear();
            YouTubeSearchResults.Clear();
            foreach (var track in tracks)
            {
                YouTubeSearchResults.Add(track);
            }

            YouTubeStatusText = $"Loaded {tracks.Count} tracks from playlist \"{playlist.Title}\". Click 'Play All' or pick any track.";
            OnPropertyChanged(nameof(IsPlaylistResultsVisible));
            PlayAllYouTubeResultsCommand.NotifyCanExecuteChanged();
            EnqueueAllYouTubeResultsCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            YouTubeStatusText = $"Failed to open playlist: {ex.Message}";
            _logStore.Error("Failed to load YouTube playlist.", ex);
        }
        finally
        {
            IsSearchingYouTube = false;
        }
    }

    private async Task PlayYouTubeTrackAsync(object? parameter) => await TryPlayYouTubeTrackAsync(parameter);

    /// <returns>True when the cast started.</returns>
    private async Task<bool> TryPlayYouTubeTrackAsync(object? parameter)
    {
        if (parameter is not YouTubeTrackItem track)
        {
            return false;
        }

        try
        {
            _localJukeboxService?.StopSession();
            if (_pcAudioCastService is { IsCasting: true })
            {
                await _pcAudioCastService.StopCastingAsync();
            }

            YouTubeStatusText = $"Casting \"{track.Title}\" to Denon AVR...";
            StatusMessage = $"Casting YouTube: {track.Title}";
            await _youtubeQueueService.PlayNowAsync(track, useLowVolume: false, UnmuteBeforePlay);
            YouTubeStatusText = $"Now casting \"{track.Title}\" on Denon AVR.";
            StatusMessage = $"Playing YouTube: {track.Title}";
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
            YouTubeStatusText = $"Playback failed: {ex.Message}";
            return false;
        }
    }

    private async Task PlayYouTubeTrackAndWatchAsync(object? parameter)
    {
        if (parameter is not YouTubeTrackItem track)
        {
            return;
        }

        var wasOpen = _cinemaWindow.IsVisible;
        if (!await TryPlayYouTubeTrackAsync(track) || wasOpen)
        {
            return; // Failed: no silent video. Already open: the TrackStarted handler loads the new track.
        }

        await _cinemaWindow.LoadTrackAsync(
            track,
            _settingsStore.Current.Media.CinemaSyncOffsetMs,
            offsetMs => _youtubeQueueService.GetEstimatedPlaybackPositionSeconds(offsetMs));
    }

    private async Task OpenCinemaForCurrentTrackAsync()
    {
        var track = _youtubeQueueService.Snapshot.CurrentTrack;
        if (track is null)
        {
            return;
        }

        await _cinemaWindow.LoadTrackAsync(
            track,
            _settingsStore.Current.Media.CinemaSyncOffsetMs,
            offsetMs => _youtubeQueueService.GetEstimatedPlaybackPositionSeconds(offsetMs));
    }

    private void EnqueueYouTubeTrack(object? parameter)
    {
        if (parameter is not YouTubeTrackItem track)
        {
            return;
        }

        _youtubeQueueService.Enqueue(track);
        YouTubeStatusText = $"Queued \"{track.Title}\".";
    }

    private async Task PlayAllYouTubeResultsAsync()
    {
        if (YouTubeSearchResults.Count == 0)
        {
            return;
        }

        try
        {
            var items = YouTubeSearchResults.ToList();
            YouTubeStatusText = $"Starting queue with {items.Count} tracks...";
            await _youtubeQueueService.PlayAllAsync(items, useLowVolume: false, UnmuteBeforePlay);
            YouTubeStatusText = $"Playing queue ({items.Count} tracks) on Denon AVR.";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
            YouTubeStatusText = $"Queue playback failed: {ex.Message}";
        }
    }

    private void EnqueueAllYouTubeResults()
    {
        if (YouTubeSearchResults.Count == 0)
        {
            return;
        }

        _youtubeQueueService.EnqueueRange(YouTubeSearchResults);
        YouTubeStatusText = $"Added {YouTubeSearchResults.Count} tracks to Up Next queue.";
    }

    private async Task PlayYouTubeQueueItemAsync(object? parameter)
    {
        if (parameter is not YouTubeQueueItemViewModel item)
        {
            return;
        }

        try
        {
            YouTubeStatusText = $"Jumping to \"{item.Title}\"...";
            await _youtubeQueueService.PlayQueueEntryAsync(item.EntryId, useLowVolume: false, UnmuteBeforePlay);
            YouTubeStatusText = $"Now casting \"{item.Title}\".";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
            YouTubeStatusText = $"Failed to switch track: {ex.Message}";
        }
    }

    private void RemoveYouTubeQueueItem(object? parameter)
    {
        if (parameter is YouTubeQueueItemViewModel item)
        {
            _youtubeQueueService.RemoveFromQueue(item.EntryId);
        }
    }

    private void MoveYouTubeQueueUp(object? parameter)
    {
        if (parameter is YouTubeQueueItemViewModel item)
        {
            _youtubeQueueService.MoveQueueItem(item.EntryId, -1);
        }
    }

    private void MoveYouTubeQueueDown(object? parameter)
    {
        if (parameter is YouTubeQueueItemViewModel item)
        {
            _youtubeQueueService.MoveQueueItem(item.EntryId, 1);
        }
    }

    private void ApplyRelayStatus(RelayStatusSnapshot relay)
    {
        RelayCodecBadge = relay.HasFfmpeg && _settingsStore.Current.Media.PreferFfmpegMp3Transcode
            ? "MP3 320K (FFMPEG)"
            : "DIRECT M4A PROXY";

        if (!relay.IsRunning)
        {
            RelayEndpointText = "Local Audio Relay: Standby (auto-starts on play)";
            RelayStreamStateText = "Standby";
            return;
        }

        var mb = relay.BytesStreamed / (1024.0 * 1024.0);
        var transferred = relay.BytesStreamed > 0 ? $" • {mb:0.0} MB sent" : string.Empty;
        RelayEndpointText = $"LAN Relay: {relay.EndpointBaseUrl} • {relay.TranscodeMode}";
        RelayStreamStateText = $"{relay.StreamState}{transferred}";
    }

    private void ApplyYouTubeQueueSnapshot(YouTubeQueueSnapshot snapshot)
    {
        IsYouTubeActive = snapshot.IsActive && snapshot.CurrentTrack is not null;
        CurrentYouTubeTitle = snapshot.CurrentTrack?.Title ?? string.Empty;
        CurrentYouTubeAuthor = snapshot.CurrentTrack?.Author ?? string.Empty;
        IsYouTubeShuffleEnabled = snapshot.IsShuffleEnabled;
        YouTubeRepeatMode = snapshot.RepeatMode;

        YouTubeQueue.Clear();
        for (var i = 0; i < snapshot.Queue.Count; i++)
        {
            YouTubeQueue.Add(new YouTubeQueueItemViewModel(
                snapshot.Queue[i],
                position: i + 1,
                isCurrent: snapshot.IsActive && i == snapshot.CurrentIndex));
        }

        YouTubeHistory.Clear();
        foreach (var item in snapshot.History.Take(10))
        {
            YouTubeHistory.Add(item);
        }

        OnPropertyChanged(nameof(YouTubeQueueCountLabel));
        OnPropertyChanged(nameof(HasYouTubeHistory));

        // Immediately reflect active YouTube metadata on the Hero OLED Now Playing card
        ApplyStateSnapshot(_stateStore.Snapshot);
    }
}
