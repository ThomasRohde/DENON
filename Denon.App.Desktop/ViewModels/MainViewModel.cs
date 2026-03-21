using System.Collections.ObjectModel;
using System.Windows.Media;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Denon.App.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ReceiverController _receiverController;
    private readonly AppSettingsStore _settingsStore;
    private readonly ReceiverStateStore _stateStore;
    private readonly AppLogStore _logStore;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _playCommand;
    private readonly AsyncRelayCommand _pauseCommand;
    private readonly AsyncRelayCommand _stopCommand;
    private readonly AsyncRelayCommand _toggleMuteCommand;
    private readonly AsyncRelayCommand _setLowVolumeCommand;
    private readonly AsyncRelayCommand _applyVolumeCommand;
    private readonly AsyncRelayCommand _volumeUpCommand;
    private readonly AsyncRelayCommand _volumeDownCommand;
    private readonly AsyncRelayCommand _playCustomStreamCommand;
    private readonly AsyncRelayCommand _playPresetCommand;
    private readonly AsyncRelayCommand _playPresetLowCommand;
    private readonly AsyncRelayCommand _saveSettingsCommand;
    private readonly RelayCommand _addStationCommand;
    private readonly RelayCommand _removeSelectedStationCommand;
    private readonly RelayCommand _resetDefaultsCommand;

    private string _connectionBadgeText = "Offline";
    private Brush _connectionBadgeBrush = new SolidColorBrush(Color.FromRgb(138, 43, 43));
    private string _statusMessage = "Waiting for receiver status.";
    private string _receiverTitle = "Denon AVR-X2400H";
    private string _receiverDetails = "No receiver data yet.";
    private string _nowPlayingTitle = "Nothing playing";
    private string _nowPlayingDetail = "Use a preset station or custom stream to start playback.";
    private string _playState = "Unknown";
    private int _targetVolume = 10;
    private int _liveVolume;
    private bool _isMuted;
    private bool _isBusy;
    private string _lastUpdatedText = "No successful refresh yet.";
    private string _lastError = string.Empty;
    private string _customStreamUrl = string.Empty;
    private bool _useLowVolumeForStreams = true;
    private bool _unmuteBeforePlay = true;
    private string _settingsHost = "192.168.1.3";
    private string _settingsPort = "1255";
    private string _settingsPlayerName = "Denon AVR-X2400H";
    private string _settingsPollIntervalSeconds = "5";
    private string _settingsLowVolume = "10";
    private bool _settingsStartMinimizedToTray = true;
    private bool _settingsMinimizeToTray = true;
    private bool _settingsCloseToTray = true;
    private bool _settingsShowNotifications = true;
    private StationPresetEditor? _selectedStationPreset;

    public MainViewModel(
        ReceiverController receiverController,
        AppSettingsStore settingsStore,
        ReceiverStateStore stateStore,
        AppLogStore logStore)
    {
        _receiverController = receiverController;
        _settingsStore = settingsStore;
        _stateStore = stateStore;
        _logStore = logStore;
        _dispatcher = System.Windows.Application.Current.Dispatcher;

        StationPresets = new ObservableCollection<StationPresetEditor>();
        LogEntries = new ObservableCollection<AppLogEntry>();

        _refreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), CanControlReceiver);
        _playCommand = new AsyncRelayCommand(_ => SetPlayStateAsync("play"), CanControlReceiver);
        _pauseCommand = new AsyncRelayCommand(_ => SetPlayStateAsync("pause"), CanControlReceiver);
        _stopCommand = new AsyncRelayCommand(_ => SetPlayStateAsync("stop"), CanControlReceiver);
        _toggleMuteCommand = new AsyncRelayCommand(_ => ToggleMuteAsync(), CanControlReceiver);
        _setLowVolumeCommand = new AsyncRelayCommand(_ => SetLowVolumeAsync(), CanControlReceiver);
        _applyVolumeCommand = new AsyncRelayCommand(_ => ApplyVolumeAsync(), CanControlReceiver);
        _volumeUpCommand = new AsyncRelayCommand(_ => StepVolumeAsync(5), CanControlReceiver);
        _volumeDownCommand = new AsyncRelayCommand(_ => StepVolumeAsync(-5), CanControlReceiver);
        _playCustomStreamCommand = new AsyncRelayCommand(_ => PlayCustomStreamAsync(), CanControlReceiver);
        _playPresetCommand = new AsyncRelayCommand(PlayPresetAsync, CanPlayPreset);
        _playPresetLowCommand = new AsyncRelayCommand(PlayPresetLowAsync, CanPlayPreset);
        _saveSettingsCommand = new AsyncRelayCommand(_ => SaveSettingsAsync());
        _addStationCommand = new RelayCommand(_ => AddStation());
        _removeSelectedStationCommand = new RelayCommand(_ => RemoveSelectedStation(), _ => SelectedStationPreset is not null);
        _resetDefaultsCommand = new RelayCommand(_ => LoadDefaultSettingsIntoEditor());

        LoadSettings(_settingsStore.Current);
        ApplyStateSnapshot(_stateStore.Snapshot);
        foreach (var entry in _logStore.Snapshot())
        {
            LogEntries.Add(entry);
        }

        _stateStore.StateChanged += snapshot => _ = _dispatcher.InvokeAsync(() => ApplyStateSnapshot(snapshot));
        _settingsStore.SettingsChanged += settings => _ = _dispatcher.InvokeAsync(() => LoadSettings(settings));
        _logStore.EntryAdded += entry => _ = _dispatcher.InvokeAsync(() =>
        {
            LogEntries.Add(entry);
            if (LogEntries.Count > 500)
            {
                LogEntries.RemoveAt(0);
            }
        });
    }

    public ObservableCollection<StationPresetEditor> StationPresets { get; }

    public ObservableCollection<AppLogEntry> LogEntries { get; }

    public AsyncRelayCommand RefreshCommand => _refreshCommand;

    public AsyncRelayCommand PlayCommand => _playCommand;

    public AsyncRelayCommand PauseCommand => _pauseCommand;

    public AsyncRelayCommand StopCommand => _stopCommand;

    public AsyncRelayCommand ToggleMuteCommand => _toggleMuteCommand;

    public AsyncRelayCommand SetLowVolumeCommand => _setLowVolumeCommand;

    public AsyncRelayCommand ApplyVolumeCommand => _applyVolumeCommand;

    public AsyncRelayCommand VolumeUpCommand => _volumeUpCommand;

    public AsyncRelayCommand VolumeDownCommand => _volumeDownCommand;

    public AsyncRelayCommand PlayCustomStreamCommand => _playCustomStreamCommand;

    public AsyncRelayCommand PlayPresetCommand => _playPresetCommand;

    public AsyncRelayCommand PlayPresetLowCommand => _playPresetLowCommand;

    public AsyncRelayCommand SaveSettingsCommand => _saveSettingsCommand;

    public RelayCommand AddStationCommand => _addStationCommand;

    public RelayCommand RemoveSelectedStationCommand => _removeSelectedStationCommand;

    public RelayCommand ResetDefaultsCommand => _resetDefaultsCommand;

    public string ConnectionBadgeText
    {
        get => _connectionBadgeText;
        private set => SetProperty(ref _connectionBadgeText, value);
    }

    public Brush ConnectionBadgeBrush
    {
        get => _connectionBadgeBrush;
        private set => SetProperty(ref _connectionBadgeBrush, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ReceiverTitle
    {
        get => _receiverTitle;
        private set => SetProperty(ref _receiverTitle, value);
    }

    public string ReceiverDetails
    {
        get => _receiverDetails;
        private set => SetProperty(ref _receiverDetails, value);
    }

    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set => SetProperty(ref _nowPlayingTitle, value);
    }

    public string NowPlayingDetail
    {
        get => _nowPlayingDetail;
        private set => SetProperty(ref _nowPlayingDetail, value);
    }

    public string PlayState
    {
        get => _playState;
        private set => SetProperty(ref _playState, value);
    }

    public int TargetVolume
    {
        get => _targetVolume;
        set
        {
            if (SetProperty(ref _targetVolume, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(VolumeLabel));
            }
        }
    }

    public int LiveVolume
    {
        get => _liveVolume;
        private set => SetProperty(ref _liveVolume, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(MuteButtonText));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    public string LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public string CustomStreamUrl
    {
        get => _customStreamUrl;
        set => SetProperty(ref _customStreamUrl, value);
    }

    public bool UseLowVolumeForStreams
    {
        get => _useLowVolumeForStreams;
        set => SetProperty(ref _useLowVolumeForStreams, value);
    }

    public bool UnmuteBeforePlay
    {
        get => _unmuteBeforePlay;
        set => SetProperty(ref _unmuteBeforePlay, value);
    }

    public string SettingsHost
    {
        get => _settingsHost;
        set => SetProperty(ref _settingsHost, value);
    }

    public string SettingsPort
    {
        get => _settingsPort;
        set => SetProperty(ref _settingsPort, value);
    }

    public string SettingsPlayerName
    {
        get => _settingsPlayerName;
        set => SetProperty(ref _settingsPlayerName, value);
    }

    public string SettingsPollIntervalSeconds
    {
        get => _settingsPollIntervalSeconds;
        set => SetProperty(ref _settingsPollIntervalSeconds, value);
    }

    public string SettingsLowVolume
    {
        get => _settingsLowVolume;
        set => SetProperty(ref _settingsLowVolume, value);
    }

    public bool SettingsStartMinimizedToTray
    {
        get => _settingsStartMinimizedToTray;
        set => SetProperty(ref _settingsStartMinimizedToTray, value);
    }

    public bool SettingsMinimizeToTray
    {
        get => _settingsMinimizeToTray;
        set => SetProperty(ref _settingsMinimizeToTray, value);
    }

    public bool SettingsCloseToTray
    {
        get => _settingsCloseToTray;
        set => SetProperty(ref _settingsCloseToTray, value);
    }

    public bool SettingsShowNotifications
    {
        get => _settingsShowNotifications;
        set => SetProperty(ref _settingsShowNotifications, value);
    }

    public StationPresetEditor? SelectedStationPreset
    {
        get => _selectedStationPreset;
        set
        {
            if (SetProperty(ref _selectedStationPreset, value))
            {
                _removeSelectedStationCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string VolumeLabel => $"{TargetVolume}%";

    public string MuteButtonText => IsMuted ? "Unmute" : "Mute";

    private bool CanControlReceiver(object? _) => !IsBusy;

    private bool CanPlayPreset(object? parameter) => !IsBusy && parameter is StationPresetEditor;

    private void NotifyCommandStateChanged()
    {
        _refreshCommand.NotifyCanExecuteChanged();
        _playCommand.NotifyCanExecuteChanged();
        _pauseCommand.NotifyCanExecuteChanged();
        _stopCommand.NotifyCanExecuteChanged();
        _toggleMuteCommand.NotifyCanExecuteChanged();
        _setLowVolumeCommand.NotifyCanExecuteChanged();
        _applyVolumeCommand.NotifyCanExecuteChanged();
        _volumeUpCommand.NotifyCanExecuteChanged();
        _volumeDownCommand.NotifyCanExecuteChanged();
        _playCustomStreamCommand.NotifyCanExecuteChanged();
        _playPresetCommand.NotifyCanExecuteChanged();
        _playPresetLowCommand.NotifyCanExecuteChanged();
        _removeSelectedStationCommand.NotifyCanExecuteChanged();
    }

    private void ApplyStateSnapshot(ReceiverStateSnapshot snapshot)
    {
        IsBusy = snapshot.IsBusy;

        if (snapshot.Status is { } status)
        {
            ReceiverTitle = status.Player.Name;
            var powerText = status.IsPoweredOn && status.IsMainZoneOn ? "Main Zone On" : "Standby";
            var inputText = string.IsNullOrWhiteSpace(status.InputSource) ? "Unknown Input" : status.InputSource;
            ReceiverDetails = $"{status.Player.Model} on {status.Player.IpAddress} via {status.Player.Network} • {powerText} • {inputText}";
            NowPlayingTitle = FirstNonEmpty(status.NowPlaying.Song, "Url Stream");
            NowPlayingDetail = BuildNowPlayingDetail(status.NowPlaying);
            PlayState = ToTitleCase(status.PlayState);
            LiveVolume = status.Volume;
            TargetVolume = status.Volume;
            IsMuted = status.IsMuted;
        }

        if (snapshot.IsConnected)
        {
            ConnectionBadgeText = "Online";
            ConnectionBadgeBrush = new SolidColorBrush(Color.FromRgb(26, 110, 79));
            StatusMessage = snapshot.Status is not null
                ? $"{(snapshot.Status.IsPoweredOn && snapshot.Status.IsMainZoneOn ? PlayState : "Standby")} on {snapshot.Status.InputSource} at volume {LiveVolume}%."
                : "Receiver connected.";
        }
        else
        {
            ConnectionBadgeText = "Offline";
            ConnectionBadgeBrush = new SolidColorBrush(Color.FromRgb(138, 43, 43));
        }

        LastUpdatedText = snapshot.LastSuccessfulRefresh.HasValue
            ? $"Last successful refresh: {snapshot.LastSuccessfulRefresh.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
            : "No successful refresh yet.";

        LastError = snapshot.LastError ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            StatusMessage = snapshot.LastError;
        }
    }

    private void LoadSettings(AppSettings settings)
    {
        SettingsHost = settings.Receiver.Host;
        SettingsPort = settings.Receiver.Port.ToString();
        SettingsPlayerName = settings.Receiver.PlayerName;
        SettingsPollIntervalSeconds = settings.Receiver.PollIntervalSeconds.ToString();
        SettingsLowVolume = settings.Receiver.DefaultLowVolume.ToString();
        SettingsStartMinimizedToTray = settings.Ui.StartMinimizedToTray;
        SettingsMinimizeToTray = settings.Ui.MinimizeToTray;
        SettingsCloseToTray = settings.Ui.CloseToTray;
        SettingsShowNotifications = settings.Ui.ShowNotifications;

        StationPresets.Clear();
        foreach (var station in settings.Receiver.Stations)
        {
            StationPresets.Add(StationPresetEditor.FromModel(station));
        }

        if (StationPresets.Count > 0 && SelectedStationPreset is null)
        {
            SelectedStationPreset = StationPresets[0];
        }
    }

    private async Task RefreshAsync() =>
        await ExecuteReceiverActionAsync(
            () => _receiverController.RefreshAsync(),
            "Receiver refreshed.");

    private async Task SetPlayStateAsync(string state) =>
        await ExecuteReceiverActionAsync(
            () => _receiverController.SetPlayStateAsync(state),
            $"Play state set to {ToTitleCase(state)}.");

    private async Task ToggleMuteAsync() =>
        await ExecuteReceiverActionAsync(
            () => _receiverController.ToggleMuteAsync(),
            "Mute state updated.");

    private async Task SetLowVolumeAsync()
    {
        if (int.TryParse(SettingsLowVolume, out var lowVolume))
        {
            TargetVolume = lowVolume;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.SetLowVolumeAsync(),
            "Receiver set to low volume.");
    }

    private async Task ApplyVolumeAsync() =>
        await ExecuteReceiverActionAsync(
            () => _receiverController.SetVolumeAsync(TargetVolume),
            $"Volume set to {TargetVolume}%.");

    private async Task StepVolumeAsync(int delta)
    {
        TargetVolume = Math.Clamp(TargetVolume + delta, 0, 100);
        await ApplyVolumeAsync();
    }

    private async Task PlayCustomStreamAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomStreamUrl))
        {
            StatusMessage = "Enter a stream URL before starting custom playback.";
            return;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.PlayCustomUrlAsync(CustomStreamUrl, UseLowVolumeForStreams, UnmuteBeforePlay),
            "Custom stream started.");
    }

    private async Task PlayPresetAsync(object? parameter)
    {
        if (parameter is not StationPresetEditor preset)
        {
            return;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.PlayPresetAsync(preset.ToModel(), UseLowVolumeForStreams, UnmuteBeforePlay),
            $"Playing {preset.DisplayName}.");
    }

    private async Task PlayPresetLowAsync(object? parameter)
    {
        if (parameter is not StationPresetEditor preset)
        {
            return;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.PlayPresetAsync(preset.ToModel(), useLowVolume: true, unmute: true),
            $"Playing {preset.DisplayName} at low volume.");
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var settings = new AppSettings
            {
                Receiver = new ReceiverSettings
                {
                    Host = SettingsHost,
                    Port = ParseInt(SettingsPort, 1255),
                    PlayerName = SettingsPlayerName,
                    PollIntervalSeconds = ParseInt(SettingsPollIntervalSeconds, 5),
                    DefaultLowVolume = ParseInt(SettingsLowVolume, 10),
                    Stations = StationPresets.Select(static station => station.ToModel()).ToList(),
                },
                Ui = new UiSettings
                {
                    StartMinimizedToTray = SettingsStartMinimizedToTray,
                    MinimizeToTray = SettingsMinimizeToTray,
                    CloseToTray = SettingsCloseToTray,
                    ShowNotifications = SettingsShowNotifications,
                },
            };

            await _settingsStore.SaveAsync(settings);
            StatusMessage = "Settings saved. Refreshing receiver with the new connection profile.";
            await _receiverController.RefreshAsync();
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            StatusMessage = exception.Message;
        }
    }

    private void AddStation()
    {
        var editor = new StationPresetEditor
        {
            Alias = $"station-{StationPresets.Count + 1}",
            DisplayName = "New Station",
            Url = "http://",
        };

        StationPresets.Add(editor);
        SelectedStationPreset = editor;
        StatusMessage = "Added a new station row. Save settings to persist it.";
    }

    private void RemoveSelectedStation()
    {
        if (SelectedStationPreset is null)
        {
            return;
        }

        var removedName = SelectedStationPreset.DisplayName;
        StationPresets.Remove(SelectedStationPreset);
        SelectedStationPreset = StationPresets.FirstOrDefault();
        StatusMessage = $"Removed station '{removedName}'. Save settings to persist the change.";
    }

    private void LoadDefaultSettingsIntoEditor()
    {
        LoadSettings(AppSettings.CreateDefault());
        StatusMessage = "Default settings loaded into the editor. Save to apply them.";
    }

    private async Task ExecuteReceiverActionAsync(Func<Task<ReceiverStatus>> action, string successMessage)
    {
        try
        {
            await action();
            StatusMessage = successMessage;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            StatusMessage = exception.Message;
        }
    }

    private static string FirstNonEmpty(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string BuildNowPlayingDetail(NowPlayingMedia nowPlaying)
    {
        var pieces = new[]
        {
            nowPlaying.Artist,
            nowPlaying.Album,
            nowPlaying.Type,
        }.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();

        return pieces.Length == 0 ? "No media metadata available." : string.Join(" • ", pieces);
    }

    private static string ToTitleCase(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Unknown"
            : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;
}
