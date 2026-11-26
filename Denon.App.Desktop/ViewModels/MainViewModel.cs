using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows.Data;
using System.Windows.Media;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Mcp;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.Core.Services.Metering;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Infrastructure.Theming;
using Denon.App.Desktop.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Denon.App.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    // Tokens, not brushes: a theme switch rebuilds the brush from the token (docs/theming.md).
    private const string OnlineTone = "AccentEmerald";
    private const string StandbyTone = "AccentAmber";
    private const string OfflineTone = "AccentCrimson";

    private readonly ReceiverController _receiverController;
    private readonly AppSettingsStore _settingsStore;
    private readonly ReceiverStateStore _stateStore;
    private readonly AppLogStore _logStore;
    private readonly EventTelemetryLog _telemetryLog;
    private readonly DenonDiscoveryService _discoveryService;
    private readonly StreamProbeService _streamProbeService;
    private readonly WindowsStartupService _startupService;
    private readonly LiveAudioAnalyzerService _liveAudioAnalyzer;
    private readonly AppStreamMeterService _appStreamMeters;
    private readonly AlbumArtPaletteService _albumArtPaletteService;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    private CancellationTokenSource? _volumeDebounceCts;
    private bool _isDraggingVolume;
    private bool _suppressVolumeSend;
    private readonly VolumeCommandGuard _volumeGuard = new();

    // Navigation
    private string _activeSection = "Control";

    // Receiver Telemetry
    private bool _isConnected;
    private bool _isPoweredOn;
    private bool _isMainZoneOn;
    private bool _isZone2On;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _isMuted;
    private bool _isBusy;
    private string _connectionBadgeText = "OFFLINE";
    private string _connectionBadgeTone = OfflineTone;
    private string _statusMessage = "Discovering receiver on local network...";
    private string _receiverTitle = "Denon / Marantz AVR";
    private string _detectedModelName = string.Empty;
    private string _receiverDetails = "Waiting for telemetry...";
    private string _firmwareVersion = string.Empty;
    private string _inputSourceCode = string.Empty;
    private string _inputSourceDisplay = "—";
    private string _soundMode = "—";
    private string _ecoMode = "—";
    private int? _sleepTimerMinutes;
    private string _nowPlayingTitle = "Ready for Playback";
    private string _nowPlayingDetail = "Select a preset station or input source to begin.";
    private string _nowPlayingImageUrl = string.Empty;
    private string _playState = "Stopped";
    private int _targetVolume = 20;
    private int _liveVolume;
    private double _liveVolumeExact;
    private int _maxVolume = 98;
    private bool _displayVolumeInDb;
    private string _lastUpdatedText = "Not synced yet";
    private string _lastError = string.Empty;

    // Custom stream & stations
    private string _customStreamUrl = string.Empty;
    private bool _useLowVolumeForStreams = false;
    private bool _unmuteBeforePlay = true;
    private string _customStreamProbeStatus = string.Empty;
    private bool? _customStreamProbeSuccess;
    private bool _isProbingCustomStream;
    private string _stationSearchText = string.Empty;
    private StationPresetEditor? _selectedStationPreset;
    private bool _isLoadingFavorites;
    private string _favoritesStatusText = "Click 'Load from Receiver' to fetch saved HEOS Favorites.";

    // Discovery
    private bool _isScanningNetwork;
    private string _discoveryStatusText = "Scan your local network to detect Denon & Marantz HEOS receivers.";

    // Settings editor fields
    private string _settingsHost = string.Empty;
    private string _settingsPort = "1255";
    private string _settingsPlayerName = string.Empty;
    private string _settingsPollIntervalSeconds = "5";
    private string _settingsLowVolume = "20";
    private string _settingsVolumePresetsText = "15, 28, 42, 55";
    private bool _settingsStartMinimizedToTray;
    private bool _settingsMinimizeToTray = true;
    private bool _settingsCloseToTray = true;
    private bool _settingsShowNotifications = true;
    private bool _settingsDisplayVolumeInDb;
    private bool _settingsLaunchAtWindowsStartup;
    private bool _settingsTelemetryEnabled = true;
    private string _settingsTelemetryRetentionDays = "90";
    private string _settingsTelemetryMediaDwellSeconds = "15";
    private string _telemetryStatsSummaryText = "Smart event logging active (90-day retention • 15s media dwell filter).";

    // Diagnostics & Console
    private string _logFilterLevel = "All";
    private string _logSearchText = string.Empty;
    private string _consoleProtocol = "AVR";
    private string _consoleCommandInput = "SI?";
    private string _consoleOutput = "Ready. Choose AVR (Telnet :23) or HEOS (:1255) and send any command.";
    private bool _isRunningConsoleCommand;
    private bool _isRunningHealthCheck;
    private string _healthSummaryText = "Click 'Run Health Check' to test your receiver connection, audio readiness, and internet radio streams.";
    private string _healthSummaryTone = "TextMuted";

    public MainViewModel(
        ReceiverController receiverController,
        AppSettingsStore settingsStore,
        ReceiverStateStore stateStore,
        AppLogStore logStore,
        DenonDiscoveryService discoveryService,
        StreamProbeService streamProbeService,
        WindowsStartupService startupService,
        YouTubeMediaService youtubeService,
        LocalAudioRelayServer relayServer,
        YouTubeQueueService youtubeQueueService,
        YouTubeCinemaWindow cinemaWindow,
        RadioBrowserService radioBrowserService,
        LocalMediaJukeboxService localJukeboxService,
        PcAudioCastService pcAudioCastService,
        HeosEventListenerService heosEventListener,
        GlobalHotkeyService globalHotkeyService,
        PluginHostService pluginHostService,
        AutomationEngineService automationEngineService,
        LiveAudioAnalyzerService liveAudioAnalyzerService,
        AppStreamMeterService appStreamMeterService,
        AlbumArtPaletteService albumArtPaletteService,
        LyricsService lyricsService,
        AcousticSyncCalibrationService calibrationService,
        RoomIntelligenceService roomIntelligenceService,
        DenonMcpServer mcpServer,
        EventTelemetryLog telemetryLog)
    {
        _receiverController = receiverController;
        _settingsStore = settingsStore;
        _stateStore = stateStore;
        _logStore = logStore;
        _telemetryLog = telemetryLog;
        _discoveryService = discoveryService;
        _streamProbeService = streamProbeService;
        _startupService = startupService;
        _liveAudioAnalyzer = liveAudioAnalyzerService;
        _appStreamMeters = appStreamMeterService;
        _albumArtPaletteService = albumArtPaletteService;
        _dispatcher = System.Windows.Application.Current.Dispatcher;

        StationPresets = [];
        HeosFavorites = [];
        DiscoveredReceivers = [];
        LogEntries = [];
        VolumePresets = [];
        DiagnosticChecks =
        [
            new DiagnosticCheckItem("Network Connection", "Checks if your Denon receiver IP responds on the local network."),
            new DiagnosticCheckItem("AVR Control Interface", "Checks power, volume, mute, and input switching (Port 23)."),
            new DiagnosticCheckItem("HEOS Streaming Engine", "Checks HEOS audio service and player name match (Port 1255)."),
            new DiagnosticCheckItem("Audio Output Readiness", "Checks power state, mute status, and audible volume level."),
            new DiagnosticCheckItem("Internet Radio Stream", "Tests whether your radio station URL is live and reachable."),
        ];

        InputSources = new ObservableCollection<InputSourceItem>(
            DenonCatalog.InputSources.Select(static opt => new InputSourceItem(opt.Code, opt.DisplayName, opt.ShortLabel)));

        SoundModes = new ObservableCollection<SoundModeItem>(
            DenonCatalog.SoundModes.Select(static opt => new SoundModeItem(opt.Code, opt.DisplayName)));

        SleepTimerOptions =
        [
            new SleepTimerOption(null, "Off"),
            new SleepTimerOption(30, "30m"),
            new SleepTimerOption(60, "60m"),
            new SleepTimerOption(90, "90m"),
            new SleepTimerOption(120, "120m"),
        ];

        FilteredStationPresets = CollectionViewSource.GetDefaultView(StationPresets);
        FilteredStationPresets.Filter = FilterStation;

        FilteredLogEntries = CollectionViewSource.GetDefaultView(LogEntries);
        FilteredLogEntries.Filter = FilterLogEntry;

        // Navigation
        NavigateCommand = new RelayCommand(param =>
        {
            if (param is string section && !string.IsNullOrWhiteSpace(section))
            {
                ActiveSection = section;
            }
        });

        // Hardware & Transport Commands
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), CanControlReceiver);
        TogglePowerCommand = new AsyncRelayCommand(_ => TogglePowerAsync(), CanControlReceiver);
        ToggleZone2Command = new AsyncRelayCommand(_ => ToggleZone2Async(), CanControlReceiver);
        PlayPauseCommand = new AsyncRelayCommand(_ => PlayPauseAsync(), p => CanControlReceiver(p) && (!IsTransportPlaying || CanPauseTransport));
        PlayCommand = new AsyncRelayCommand(_ => SetPlayStateAsync("play"), CanControlReceiver);
        PauseCommand = new AsyncRelayCommand(_ => SetPlayStateAsync("pause"), CanControlReceiver);
        StopCommand = new AsyncRelayCommand(_ => SetPlayStateAsync("stop"), CanControlReceiver);
        NextTrackCommand = new AsyncRelayCommand(_ => NextTrackAsync(), CanControlReceiver);
        PreviousTrackCommand = new AsyncRelayCommand(_ => PreviousTrackAsync(), CanControlReceiver);
        ToggleMuteCommand = new AsyncRelayCommand(_ => ToggleMuteAsync(), CanControlReceiver);
        SetLowVolumeCommand = new AsyncRelayCommand(_ => SetLowVolumeAsync(), CanControlReceiver);
        ApplyVolumeCommand = new AsyncRelayCommand(_ => ApplyVolumeAsync(), CanControlReceiver);
        VolumeUpCommand = new AsyncRelayCommand(_ => StepVolumeAsync(5), CanControlReceiver);
        VolumeDownCommand = new AsyncRelayCommand(_ => StepVolumeAsync(-5), CanControlReceiver);
        VolumeFineUpCommand = new AsyncRelayCommand(_ => StepVolumeAsync(1), CanControlReceiver);
        VolumeFineDownCommand = new AsyncRelayCommand(_ => StepVolumeAsync(-1), CanControlReceiver);
        SetVolumePresetCommand = new AsyncRelayCommand(SetVolumePresetAsync, CanControlReceiver);
        ToggleVolumeDisplayModeCommand = new RelayCommand(_ =>
        {
            DisplayVolumeInDb = !DisplayVolumeInDb;
            SettingsDisplayVolumeInDb = DisplayVolumeInDb;
            var newVal = DisplayVolumeInDb;
            _ = SaveVolumeDisplayModeAsync(newVal);
        });

        SelectInputCommand = new AsyncRelayCommand(SelectInputAsync, CanControlReceiver);
        SelectSoundModeCommand = new AsyncRelayCommand(SelectSoundModeAsync, CanControlReceiver);
        SetEcoModeCommand = new AsyncRelayCommand(SetEcoModeAsync, CanControlReceiver);
        SetSleepTimerCommand = new AsyncRelayCommand(SetSleepTimerAsync, CanControlReceiver);
        ThemeManager.ThemeChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ThemeSummaryText)); // "Follow Windows" can change it
            RefreshContentBadgeBrushes();
        };

        // Stations & Streams
        PlayCustomStreamCommand = new AsyncRelayCommand(_ => PlayCustomStreamAsync(), CanControlReceiver);
        TestCustomStreamCommand = new AsyncRelayCommand(_ => TestCustomStreamAsync());
        PlayPresetCommand = new AsyncRelayCommand(PlayPresetAsync, CanPlayPreset);
        PlayPresetLowCommand = new AsyncRelayCommand(PlayPresetLowAsync, CanPlayPreset);
        TestPresetCommand = new AsyncRelayCommand(TestPresetAsync);
        AddStationCommand = new RelayCommand(_ => AddStation());
        RemoveStationCommand = new RelayCommand(RemoveStation);
        MoveStationUpCommand = new RelayCommand(MoveStationUp);
        MoveStationDownCommand = new RelayCommand(MoveStationDown);
        LoadFavoritesCommand = new AsyncRelayCommand(_ => LoadFavoritesAsync(), _ => !IsLoadingFavorites);
        PlayFavoriteCommand = new AsyncRelayCommand(PlayFavoriteAsync, CanControlReceiver);

        // Discovery & Settings
        ScanNetworkCommand = new AsyncRelayCommand(_ => ScanNetworkAsync(), _ => !IsScanningNetwork);
        UseDiscoveredReceiverCommand = new RelayCommand(UseDiscoveredReceiver);
        SaveSettingsCommand = new AsyncRelayCommand(_ => SaveSettingsAsync());
        ResetDefaultsCommand = new RelayCommand(_ => LoadDefaultSettingsIntoEditor());
        ExportSettingsCommand = new RelayCommand(_ => ExportSettings());
        ImportSettingsCommand = new AsyncRelayCommand(_ => ImportSettingsAsync());
        OpenDataFolderCommand = new RelayCommand(_ => OpenFolder(AppDataPaths.RootDirectory));
        OpenEventsFolderCommand = new RelayCommand(_ => OpenFolder(AppDataPaths.EventsLogsDirectory));
        ClearTelemetryHistoryCommand = new AsyncRelayCommand(_ => ClearTelemetryHistoryAsync());

        // Diagnostics & Console
        SetLogFilterCommand = new RelayCommand(param =>
        {
            if (param is string level)
            {
                LogFilterLevel = level;
            }
        });
        ClearLogsCommand = new RelayCommand(_ => LogEntries.Clear());
        CopyLogsCommand = new RelayCommand(_ => CopyLogsToClipboard());
        OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(AppDataPaths.LogsDirectory));
        SetConsoleProtocolCommand = new RelayCommand(param =>
        {
            if (param is string proto)
            {
                ConsoleProtocol = proto;
                ConsoleCommandInput = proto == "HEOS" ? "heos://player/get_players" : "SI?";
            }
        });
        SendConsoleCommand = new AsyncRelayCommand(_ => RunConsoleCommandAsync(ConsoleCommandInput), _ => !IsRunningConsoleCommand);
        RunQuickDiagnosticCommand = new AsyncRelayCommand(RunQuickDiagnosticAsync, _ => !IsRunningConsoleCommand);
        RunHealthCheckCommand = new AsyncRelayCommand(_ => RunHealthCheckAsync(), _ => !IsRunningHealthCheck);
        FixSilentAudioCommand = new AsyncRelayCommand(_ => FixSilentAudioAsync(), CanControlReceiver);
        ResetConnectionsCommand = new AsyncRelayCommand(_ => ResetAndRefreshAsync(), CanControlReceiver);
        CopyDiagnosticReportCommand = new RelayCommand(_ => CopyDiagnosticReportToClipboard());

        InitializeYouTube(youtubeService, relayServer, youtubeQueueService, cinemaWindow);
        InitializeScenesAcoustics();
        InitializeCastRemoteRadio(radioBrowserService, localJukeboxService, pcAudioCastService, heosEventListener);
        InitializeShortcuts(globalHotkeyService);
        InitializePlugins(pluginHostService);
        InitializeAutomations(automationEngineService);
        InitializeStudioAndSync(lyricsService, calibrationService);
        InitializeMcpAndIntelligence(roomIntelligenceService, mcpServer);
        InitializeHelpGuide();
        UpdateMeterCaptureRequest();

        LoadSettings(_settingsStore.Current);
        if (!string.IsNullOrWhiteSpace(_settingsStore.LastLoadWarning))
        {
            LastError = _settingsStore.LastLoadWarning;
            StatusMessage = $"{_settingsStore.LastLoadWarning} ({_settingsStore.SettingsFilePath})";
        }

        ApplyStateSnapshot(_stateStore.Snapshot);
        foreach (var entry in _logStore.Snapshot())
        {
            LogEntries.Add(entry);
        }

        _stateStore.StateChanged += snapshot => _ = _dispatcher.InvokeAsync(() => ApplyStateSnapshot(snapshot));
        _settingsStore.SettingsChanged += settings => _ = _dispatcher.InvokeAsync(() => LoadSettings(settings));
        _telemetryLog.StatsChanged += () => _ = _dispatcher.InvokeAsync(RefreshTelemetryStats);
        RefreshTelemetryStats();
        _logStore.EntryAdded += entry => _ = _dispatcher.InvokeAsync(() =>
        {
            LogEntries.Add(entry);
            if (LogEntries.Count > 500)
            {
                LogEntries.RemoveAt(0);
            }
        });
    }

    // Collections
    public ObservableCollection<StationPresetEditor> StationPresets { get; }
    public ICollectionView FilteredStationPresets { get; }
    public ObservableCollection<HeosFavoriteItem> HeosFavorites { get; }
    public ObservableCollection<DiscoveredReceiver> DiscoveredReceivers { get; }
    public ObservableCollection<InputSourceItem> InputSources { get; }
    public ObservableCollection<SoundModeItem> SoundModes { get; }
    public ObservableCollection<VolumePresetItem> VolumePresets { get; }
    public ObservableCollection<SleepTimerOption> SleepTimerOptions { get; }
    public ObservableCollection<AppLogEntry> LogEntries { get; }
    public ICollectionView FilteredLogEntries { get; }
    public ObservableCollection<DiagnosticCheckItem> DiagnosticChecks { get; }

    // Commands
    public RelayCommand NavigateCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand TogglePowerCommand { get; }
    public AsyncRelayCommand ToggleZone2Command { get; }
    public AsyncRelayCommand PlayPauseCommand { get; }
    public AsyncRelayCommand PlayCommand { get; }
    public AsyncRelayCommand PauseCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand NextTrackCommand { get; }
    public AsyncRelayCommand PreviousTrackCommand { get; }
    public AsyncRelayCommand ToggleMuteCommand { get; }
    public AsyncRelayCommand SetLowVolumeCommand { get; }
    public AsyncRelayCommand ApplyVolumeCommand { get; }
    public AsyncRelayCommand VolumeUpCommand { get; }
    public AsyncRelayCommand VolumeDownCommand { get; }
    public AsyncRelayCommand VolumeFineUpCommand { get; }
    public AsyncRelayCommand VolumeFineDownCommand { get; }
    public AsyncRelayCommand SetVolumePresetCommand { get; }
    public RelayCommand ToggleVolumeDisplayModeCommand { get; }
    public AsyncRelayCommand SelectInputCommand { get; }
    public AsyncRelayCommand SelectSoundModeCommand { get; }
    public AsyncRelayCommand SetEcoModeCommand { get; }
    public AsyncRelayCommand SetSleepTimerCommand { get; }
    public AsyncRelayCommand PlayCustomStreamCommand { get; }
    public AsyncRelayCommand TestCustomStreamCommand { get; }
    public AsyncRelayCommand PlayPresetCommand { get; }
    public AsyncRelayCommand PlayPresetLowCommand { get; }
    public AsyncRelayCommand TestPresetCommand { get; }
    public RelayCommand AddStationCommand { get; }
    public RelayCommand RemoveStationCommand { get; }
    public RelayCommand MoveStationUpCommand { get; }
    public RelayCommand MoveStationDownCommand { get; }
    public AsyncRelayCommand LoadFavoritesCommand { get; }
    public AsyncRelayCommand PlayFavoriteCommand { get; }
    public AsyncRelayCommand ScanNetworkCommand { get; }
    public RelayCommand UseDiscoveredReceiverCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public RelayCommand ResetDefaultsCommand { get; }
    public RelayCommand ExportSettingsCommand { get; }
    public AsyncRelayCommand ImportSettingsCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenEventsFolderCommand { get; }
    public AsyncRelayCommand ClearTelemetryHistoryCommand { get; }
    public RelayCommand SetLogFilterCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public RelayCommand CopyLogsCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand SetConsoleProtocolCommand { get; }
    public AsyncRelayCommand SendConsoleCommand { get; }
    public AsyncRelayCommand RunQuickDiagnosticCommand { get; }
    public AsyncRelayCommand RunHealthCheckCommand { get; }
    public AsyncRelayCommand FixSilentAudioCommand { get; }
    public AsyncRelayCommand ResetConnectionsCommand { get; }
    public RelayCommand CopyDiagnosticReportCommand { get; }

    // Properties
    public string ActiveSection
    {
        get => _activeSection;
        set
        {
            if (SetProperty(ref _activeSection, value))
            {
                HideSecrets();
                UpdateMeterCaptureRequest();
                if (string.Equals(value, "Acoustics", StringComparison.OrdinalIgnoreCase))
                {
                    _ = SyncAcousticsIfNeededAsync();
                }
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public bool IsPoweredOn
    {
        get => _isPoweredOn;
        private set
        {
            if (SetProperty(ref _isPoweredOn, value))
            {
                OnPropertyChanged(nameof(PowerStateLabel));
                OnPropertyChanged(nameof(IsActiveAndPlaying));
            }
        }
    }

    public bool IsMainZoneOn
    {
        get => _isMainZoneOn;
        private set
        {
            if (SetProperty(ref _isMainZoneOn, value))
            {
                OnPropertyChanged(nameof(PowerStateLabel));
                OnPropertyChanged(nameof(IsActiveAndPlaying));
            }
        }
    }

    public bool IsZone2On
    {
        get => _isZone2On;
        private set => SetProperty(ref _isZone2On, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(IsActiveAndPlaying));
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set => SetProperty(ref _isPaused, value);
    }

    public bool IsActiveAndPlaying => IsConnected && IsPoweredOn && IsMainZoneOn && IsPlaying && !IsMuted;

    public string PowerStateLabel => IsPoweredOn && IsMainZoneOn ? "ACTIVE" : "STANDBY";

    public string ConnectionBadgeText
    {
        get => _connectionBadgeText;
        private set => SetProperty(ref _connectionBadgeText, value);
    }

    public Brush ConnectionBadgeBrush => ThemeResources.Brush(_connectionBadgeTone);

    private string ConnectionBadgeTone
    {
        set
        {
            if (_connectionBadgeTone != value)
            {
                _connectionBadgeTone = value;
                OnPropertyChanged(nameof(ConnectionBadgeBrush));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ReceiverTitle
    {
        get => _receiverTitle;
        private set
        {
            if (SetProperty(ref _receiverTitle, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public string DetectedModelName
    {
        get => _detectedModelName;
        private set
        {
            if (SetProperty(ref _detectedModelName, value))
            {
                OnPropertyChanged(nameof(BrandName));
                OnPropertyChanged(nameof(BrandMonogram));
                OnPropertyChanged(nameof(DetectedModelBadge));
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public string BrandName =>
        DetectedModelName.Contains("Marantz", StringComparison.OrdinalIgnoreCase) ||
        ReceiverTitle.Contains("Marantz", StringComparison.OrdinalIgnoreCase)
            ? "MARANTZ"
            : "DENON";

    public string BrandMonogram => BrandName[..1];

    public string DetectedModelBadge
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DetectedModelName))
            {
                return "PRECISION";
            }

            var cleaned = DetectedModelName
                .Replace("Denon ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("Marantz ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();

            return string.IsNullOrWhiteSpace(cleaned) ? "PRECISION" : cleaned.ToUpperInvariant();
        }
    }

    public string WindowTitle =>
        string.IsNullOrWhiteSpace(DetectedModelName)
            ? "Denon Desktop"
            : $"{ReceiverTitle} — Denon Desktop";

    public string ReceiverDetails
    {
        get => _receiverDetails;
        private set => SetProperty(ref _receiverDetails, value);
    }

    public string FirmwareVersion
    {
        get => _firmwareVersion;
        private set => SetProperty(ref _firmwareVersion, value);
    }

    public string InputSourceCode
    {
        get => _inputSourceCode;
        private set => SetProperty(ref _inputSourceCode, value);
    }

    public string InputSourceDisplay
    {
        get => _inputSourceDisplay;
        private set => SetProperty(ref _inputSourceDisplay, value);
    }

    public string SoundMode
    {
        get => _soundMode;
        private set => SetProperty(ref _soundMode, value);
    }

    public string EcoMode
    {
        get => _ecoMode;
        private set => SetProperty(ref _ecoMode, value);
    }

    public int? SleepTimerMinutes
    {
        get => _sleepTimerMinutes;
        private set
        {
            if (SetProperty(ref _sleepTimerMinutes, value))
            {
                OnPropertyChanged(nameof(SleepTimerDisplay));
                OnPropertyChanged(nameof(HasSleepTimer));
            }
        }
    }

    public bool HasSleepTimer => SleepTimerMinutes is > 0;

    public string SleepTimerDisplay => SleepTimerMinutes is > 0 ? $"Sleep {SleepTimerMinutes}m" : "Sleep Off";

    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set
        {
            if (SetProperty(ref _nowPlayingTitle, value))
            {
                NotifyTrackChangedForLyrics(value, NowPlayingDetail);
            }
        }
    }

    public string NowPlayingDetail
    {
        get => _nowPlayingDetail;
        private set => SetProperty(ref _nowPlayingDetail, value);
    }

    public string NowPlayingImageUrl
    {
        get => _nowPlayingImageUrl;
        private set
        {
            if (SetProperty(ref _nowPlayingImageUrl, value))
            {
                OnPropertyChanged(nameof(HasArtwork));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _ = _albumArtPaletteService.ProcessArtworkAsync(value, NowPlayingTitle);
                }
            }
        }
    }

    public bool HasArtwork => !string.IsNullOrWhiteSpace(NowPlayingImageUrl);

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
            var clamped = Math.Clamp(value, 0, MaxVolume);
            if (SetProperty(ref _targetVolume, clamped))
            {
                OnPropertyChanged(nameof(PrimaryVolumeText));
                OnPropertyChanged(nameof(SecondaryVolumeText));
                OnPropertyChanged(nameof(VolumeDbLabel));
                OnPropertyChanged(nameof(VolumeAbsoluteLabel));
                UpdateActiveVolumePreset(clamped);

                if (!_suppressVolumeSend && !_isDraggingVolume)
                {
                    ScheduleDebouncedVolumeApply(clamped);
                }
            }
        }
    }

    public int LiveVolume
    {
        get => _liveVolume;
        private set => SetProperty(ref _liveVolume, value);
    }

    public double LiveVolumeExact
    {
        get => _liveVolumeExact;
        private set
        {
            if (SetProperty(ref _liveVolumeExact, value))
            {
                OnPropertyChanged(nameof(PrimaryVolumeText));
            }
        }
    }

    public int MaxVolume
    {
        get => _maxVolume;
        private set => SetProperty(ref _maxVolume, Math.Clamp(value, 20, 98));
    }

    public bool DisplayVolumeInDb
    {
        get => _displayVolumeInDb;
        set
        {
            if (SetProperty(ref _displayVolumeInDb, value))
            {
                OnPropertyChanged(nameof(PrimaryVolumeText));
                OnPropertyChanged(nameof(SecondaryVolumeText));
            }
        }
    }

    public string VolumeDbLabel => DenonVolumeMath.FormatDecibels(TargetVolume);

    public string VolumeAbsoluteLabel => $"{TargetVolume} / {MaxVolume}";

    public string PrimaryVolumeText
    {
        get
        {
            if (DisplayVolumeInDb)
            {
                return VolumeDbLabel;
            }

            var displayVal = Math.Abs(LiveVolumeExact - TargetVolume) < 1.0 && LiveVolumeExact > 0
                ? LiveVolumeExact
                : TargetVolume;
            return displayVal.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The line under the big readout: the other unit, and MUTED while muted. The receiver's MVMAX is not shown: it
    /// follows the current volume (41 → 69, 44 → 72), so "41 / 69" changed with every step and meant nothing.
    /// </summary>
    public string SecondaryVolumeText
    {
        get
        {
            var other = DisplayVolumeInDb ? $"VOL {TargetVolume}" : VolumeDbLabel;
            return IsMuted ? $"MUTED • {other}" : other;
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(MuteButtonText));
                OnPropertyChanged(nameof(IsActiveAndPlaying));
                OnPropertyChanged(nameof(SecondaryVolumeText));
            }
        }
    }

    public string MuteButtonText => IsMuted ? "MUTED" : "MUTE";

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

    public string CustomStreamProbeStatus
    {
        get => _customStreamProbeStatus;
        private set => SetProperty(ref _customStreamProbeStatus, value);
    }

    public bool? CustomStreamProbeSuccess
    {
        get => _customStreamProbeSuccess;
        private set => SetProperty(ref _customStreamProbeSuccess, value);
    }

    public bool IsProbingCustomStream
    {
        get => _isProbingCustomStream;
        private set => SetProperty(ref _isProbingCustomStream, value);
    }

    public string StationSearchText
    {
        get => _stationSearchText;
        set
        {
            if (SetProperty(ref _stationSearchText, value))
            {
                FilteredStationPresets.Refresh();
            }
        }
    }

    public StationPresetEditor? SelectedStationPreset
    {
        get => _selectedStationPreset;
        set => SetProperty(ref _selectedStationPreset, value);
    }

    public bool IsLoadingFavorites
    {
        get => _isLoadingFavorites;
        private set
        {
            if (SetProperty(ref _isLoadingFavorites, value))
            {
                LoadFavoritesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string FavoritesStatusText
    {
        get => _favoritesStatusText;
        private set => SetProperty(ref _favoritesStatusText, value);
    }

    public bool IsScanningNetwork
    {
        get => _isScanningNetwork;
        private set
        {
            if (SetProperty(ref _isScanningNetwork, value))
            {
                ScanNetworkCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string DiscoveryStatusText
    {
        get => _discoveryStatusText;
        private set => SetProperty(ref _discoveryStatusText, value);
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

    public string SettingsVolumePresetsText
    {
        get => _settingsVolumePresetsText;
        set => SetProperty(ref _settingsVolumePresetsText, value);
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

    public bool SettingsDisplayVolumeInDb
    {
        get => _settingsDisplayVolumeInDb;
        set => SetProperty(ref _settingsDisplayVolumeInDb, value);
    }

    public bool SettingsLaunchAtWindowsStartup
    {
        get => _settingsLaunchAtWindowsStartup;
        set => SetProperty(ref _settingsLaunchAtWindowsStartup, value);
    }

    public bool SettingsTelemetryEnabled
    {
        get => _settingsTelemetryEnabled;
        set => SetProperty(ref _settingsTelemetryEnabled, value);
    }

    public string SettingsTelemetryRetentionDays
    {
        get => _settingsTelemetryRetentionDays;
        set => SetProperty(ref _settingsTelemetryRetentionDays, value);
    }

    public string SettingsTelemetryMediaDwellSeconds
    {
        get => _settingsTelemetryMediaDwellSeconds;
        set => SetProperty(ref _settingsTelemetryMediaDwellSeconds, value);
    }

    public string TelemetryStatsSummaryText
    {
        get => _telemetryStatsSummaryText;
        private set => SetProperty(ref _telemetryStatsSummaryText, value);
    }

    public string LogFilterLevel
    {
        get => _logFilterLevel;
        set
        {
            if (SetProperty(ref _logFilterLevel, value))
            {
                FilteredLogEntries.Refresh();
            }
        }
    }

    public string LogSearchText
    {
        get => _logSearchText;
        set
        {
            if (SetProperty(ref _logSearchText, value))
            {
                FilteredLogEntries.Refresh();
            }
        }
    }

    public string ConsoleProtocol
    {
        get => _consoleProtocol;
        set => SetProperty(ref _consoleProtocol, value);
    }

    public string ConsoleCommandInput
    {
        get => _consoleCommandInput;
        set => SetProperty(ref _consoleCommandInput, value);
    }

    public string ConsoleOutput
    {
        get => _consoleOutput;
        private set => SetProperty(ref _consoleOutput, value);
    }

    public bool IsRunningConsoleCommand
    {
        get => _isRunningConsoleCommand;
        private set
        {
            if (SetProperty(ref _isRunningConsoleCommand, value))
            {
                SendConsoleCommand.NotifyCanExecuteChanged();
                RunQuickDiagnosticCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRunningHealthCheck
    {
        get => _isRunningHealthCheck;
        private set
        {
            if (SetProperty(ref _isRunningHealthCheck, value))
            {
                RunHealthCheckCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string HealthSummaryText
    {
        get => _healthSummaryText;
        private set => SetProperty(ref _healthSummaryText, value);
    }

    public Brush HealthSummaryBrush => ThemeResources.Brush(_healthSummaryTone);

    private string HealthSummaryTone
    {
        set
        {
            if (_healthSummaryTone != value)
            {
                _healthSummaryTone = value;
                OnPropertyChanged(nameof(HealthSummaryBrush));
            }
        }
    }

    public void BeginVolumeDrag()
    {
        _isDraggingVolume = true;
        _volumeDebounceCts?.Cancel();
    }

    public void EndVolumeDrag()
    {
        _isDraggingVolume = false;
        ScheduleDebouncedVolumeApply(TargetVolume, immediate: true);
    }

    public void AdjustVolumeByWheel(int deltaSteps)
    {
        if (IsBusy)
        {
            return;
        }

        TargetVolume = Math.Clamp(TargetVolume + deltaSteps, 0, MaxVolume);
    }

    private bool CanControlReceiver(object? _) => !IsBusy;

    private bool CanPlayPreset(object? parameter) => !IsBusy && parameter is StationPresetEditor;

    private void NotifyCommandStateChanged()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        TogglePowerCommand.NotifyCanExecuteChanged();
        ToggleZone2Command.NotifyCanExecuteChanged();
        PlayPauseCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NextTrackCommand.NotifyCanExecuteChanged();
        PreviousTrackCommand.NotifyCanExecuteChanged();
        ToggleMuteCommand.NotifyCanExecuteChanged();
        SetLowVolumeCommand.NotifyCanExecuteChanged();
        ApplyVolumeCommand.NotifyCanExecuteChanged();
        VolumeUpCommand.NotifyCanExecuteChanged();
        VolumeDownCommand.NotifyCanExecuteChanged();
        VolumeFineUpCommand.NotifyCanExecuteChanged();
        VolumeFineDownCommand.NotifyCanExecuteChanged();
        SetVolumePresetCommand.NotifyCanExecuteChanged();
        SelectInputCommand.NotifyCanExecuteChanged();
        SelectSoundModeCommand.NotifyCanExecuteChanged();
        SetEcoModeCommand.NotifyCanExecuteChanged();
        SetSleepTimerCommand.NotifyCanExecuteChanged();
        PlayCustomStreamCommand.NotifyCanExecuteChanged();
        PlayPresetCommand.NotifyCanExecuteChanged();
        PlayPresetLowCommand.NotifyCanExecuteChanged();
        PlayFavoriteCommand.NotifyCanExecuteChanged();
        FixSilentAudioCommand.NotifyCanExecuteChanged();
        ResetConnectionsCommand.NotifyCanExecuteChanged();

        PlayYouTubeTrackCommand?.NotifyCanExecuteChanged();
        PlayYouTubeTrackAndWatchCommand?.NotifyCanExecuteChanged();
        PlayAllYouTubeResultsCommand?.NotifyCanExecuteChanged();
        PlayYouTubeQueueItemCommand?.NotifyCanExecuteChanged();
        ActivateSceneCommand?.NotifyCanExecuteChanged();
        RefreshAcousticsCommand?.NotifyCanExecuteChanged();
        SetMultEqCommand?.NotifyCanExecuteChanged();
        ToggleDynamicEqCommand?.NotifyCanExecuteChanged();
        SetDynamicVolumeCommand?.NotifyCanExecuteChanged();
        SetRestorerCommand?.NotifyCanExecuteChanged();
        SetDimmerCommand?.NotifyCanExecuteChanged();
        ApplySubwooferTrimCommand?.NotifyCanExecuteChanged();
        StepSubwooferTrimCommand?.NotifyCanExecuteChanged();
        ToggleSubwooferTrimCommand?.NotifyCanExecuteChanged();
        ApplyDialogTrimCommand?.NotifyCanExecuteChanged();
        StepDialogTrimCommand?.NotifyCanExecuteChanged();
        ToggleDialogTrimCommand?.NotifyCanExecuteChanged();
        ToggleToneControlCommand?.NotifyCanExecuteChanged();
        StepBassCommand?.NotifyCanExecuteChanged();
        StepTrebleCommand?.NotifyCanExecuteChanged();
        ResetAcousticTrimsCommand?.NotifyCanExecuteChanged();
        TogglePcCastCommand?.NotifyCanExecuteChanged();
        PlayLocalTrackCommand?.NotifyCanExecuteChanged();
        PlayAllLocalTracksCommand?.NotifyCanExecuteChanged();
        PlayRadioBrowserStationCommand?.NotifyCanExecuteChanged();
    }

    private void ApplyStateSnapshot(ReceiverStateSnapshot snapshot)
    {
        IsBusy = snapshot.IsBusy;
        IsConnected = snapshot.IsConnected;

        if (snapshot.Status is { } status)
        {
            var configuredName = _settingsStore.Current.Receiver.PlayerName;
            ReceiverTitle = !string.IsNullOrWhiteSpace(status.Player.Name)
                ? status.Player.Name
                : (!string.IsNullOrWhiteSpace(status.Player.Model)
                    ? status.Player.Model
                    : (string.IsNullOrWhiteSpace(configuredName) ? "Denon / Marantz AVR" : configuredName));
            DetectedModelName = status.Player.Model ?? string.Empty;
            FirmwareVersion = string.IsNullOrWhiteSpace(status.Player.Version) ? string.Empty : $"FW {status.Player.Version}";
            IsPoweredOn = status.IsPoweredOn;
            IsMainZoneOn = status.IsMainZoneOn;
            IsZone2On = status.IsZone2On;

            InputSourceCode = status.InputSource;
            InputSourceDisplay = DenonCatalog.GetFriendlyInputName(status.InputSource);
            SoundMode = string.IsNullOrWhiteSpace(status.SoundMode) ? "—" : status.SoundMode;
            EcoMode = string.IsNullOrWhiteSpace(status.EcoMode) ? "—" : status.EcoMode;
            SleepTimerMinutes = status.SleepTimerMinutes;

            var powerText = status.IsPoweredOn && status.IsMainZoneOn ? "Active" : "Standby";
            var netBadge = string.IsNullOrWhiteSpace(status.Player.Network) ? "LAN" : status.Player.Network.ToUpperInvariant();
            ReceiverDetails = $"{status.Player.Model} • {status.Player.IpAddress} ({netBadge}) • {powerText}";

            var wasPaused = IsPaused;
            IsPlaying = string.Equals(status.PlayState, "play", StringComparison.OrdinalIgnoreCase);
            IsPaused = string.Equals(status.PlayState, "pause", StringComparison.OrdinalIgnoreCase);

            if (wasPaused != IsPaused && _youtubeQueueService is { IsYouTubePlaybackActive: true } && _cinemaWindow is { IsVisible: true })
            {
                _ = _cinemaWindow.SyncPlaybackStateAsync(IsPaused);
            }

            var activeStation = !string.IsNullOrWhiteSpace(status.ActiveStationAlias)
                ? StationPresets.FirstOrDefault(s => string.Equals(s.Alias, status.ActiveStationAlias, StringComparison.OrdinalIgnoreCase))
                : null;

            foreach (var preset in StationPresets)
            {
                preset.IsCurrentlyPlaying = activeStation is not null && ReferenceEquals(preset, activeStation);
            }

            var isPcCasting = _pcAudioCastService is not null && _pcAudioCastService.IsCasting;
            var pcCastDetail = isPcCasting
                ? $"{SelectedAudioDevice?.FriendlyName ?? "System Output"} • {(SelectedPcCastFormat == PcCastFormat.Wav_Lossless ? "PCM 16-bit WAV" : "MP3 320 kbps")}"
                : null;
            var activeLocalTrack = _localJukeboxService is { IsLocalPlaybackActive: true }
                ? _localJukeboxService.Snapshot.CurrentTrack
                : null;
            var activeYouTubeTrack = _youtubeQueueService is { IsYouTubePlaybackActive: true }
                ? _youtubeQueueService.Snapshot.CurrentTrack
                : null;

            var resolved = ApplyHeroCast(NowPlayingResolver.Resolve(
                status,
                activeStationName: activeStation?.DisplayName,
                plugins: _pluginHost?.GetPlugins(),
                isPcCasting: isPcCasting,
                pcCastDetail: pcCastDetail,
                activeLocalTrack: activeLocalTrack,
                activeYouTubeTrack: activeYouTubeTrack));

            PlayState = resolved.PlayStateLabel;
            NowPlayingTitle = resolved.Title;
            NowPlayingDetail = resolved.Detail;
            NowPlayingImageUrl = resolved.ImageUrl;

            MaxVolume = status.MaxVolume > 0 ? status.MaxVolume : 98;
            LiveVolume = status.Volume;
            LiveVolumeExact = status.VolumeExact;
            IsMuted = status.IsMuted;

            // Never fight the user while they are actively dragging the volume slider, and keep a volume the app just
            // sent until the receiver reports it (a stale update must not become the base of the next step).
            if (!_isDraggingVolume && !_volumeGuard.ShouldHold(status.Volume))
            {
                _suppressVolumeSend = true;
                try
                {
                    TargetVolume = status.Volume;
                }
                finally
                {
                    _suppressVolumeSend = false;
                }
            }

            foreach (var inputItem in InputSources)
            {
                inputItem.IsActive = status.IsPoweredOn && status.IsMainZoneOn && DenonCatalog.InputMatches(status.InputSource, inputItem.Code);
            }

            var normalizedSoundMode = (status.SoundMode ?? string.Empty).Trim().ToUpperInvariant();
            foreach (var modeItem in SoundModes)
            {
                modeItem.IsActive = SoundModeMatches(normalizedSoundMode, modeItem.Code);
            }

            foreach (var sleepOption in SleepTimerOptions)
            {
                sleepOption.IsActive = sleepOption.Minutes == status.SleepTimerMinutes;
            }
        }

        if (snapshot.IsConnected)
        {
            var isAwake = snapshot.Status is { IsPoweredOn: true, IsMainZoneOn: true };
            ConnectionBadgeText = isAwake ? "ONLINE" : "STANDBY";
            ConnectionBadgeTone = isAwake ? OnlineTone : StandbyTone;
            StatusMessage = snapshot.Status is not null
                ? $"{(isAwake ? InputSourceDisplay : "Standby")} • {DenonVolumeMath.FormatDecibels(LiveVolume)} ({LiveVolume}){(IsMuted ? " • Muted" : "")}"
                : "Receiver connected.";
        }
        else
        {
            ConnectionBadgeText = "OFFLINE";
            ConnectionBadgeTone = OfflineTone;
        }

        LastUpdatedText = snapshot.LastSuccessfulRefresh.HasValue
            ? $"Synced {snapshot.LastSuccessfulRefresh.Value.LocalDateTime:HH:mm:ss}"
            : "Not synced yet";

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
        SettingsVolumePresetsText = string.Join(", ", settings.Receiver.VolumePresets);
        SettingsStartMinimizedToTray = settings.Ui.StartMinimizedToTray;
        SettingsMinimizeToTray = settings.Ui.MinimizeToTray;
        SettingsCloseToTray = settings.Ui.CloseToTray;
        SettingsShowNotifications = settings.Ui.ShowNotifications;
        SettingsDisplayVolumeInDb = settings.Ui.DisplayVolumeInDb;
        SettingsLaunchAtWindowsStartup = settings.Ui.LaunchAtWindowsStartup || _startupService.IsEnabled();
        SettingsCheckForUpdates = settings.Ui.CheckForUpdates;
        LoadRoomMicSettings(settings.Ui);
        DisplayVolumeInDb = settings.Ui.DisplayVolumeInDb;
        MarkActiveTheme(settings.Ui.Theme);
        MarkActiveSkin(settings.Ui.Skin);

        SettingsTelemetryEnabled = settings.Telemetry.Enabled;
        SettingsTelemetryRetentionDays = settings.Telemetry.RetentionDays.ToString();
        SettingsTelemetryMediaDwellSeconds = settings.Telemetry.MediaDwellSeconds.ToString();
        RefreshTelemetryStats();

        SettingsRelayPort = settings.Media.RelayPort.ToString();
        SettingsFfmpegPath = settings.Media.FfmpegPath;
        SettingsPreferFfmpegMp3 = settings.Media.PreferFfmpegMp3Transcode;
        SettingsCinemaSyncOffsetMs = settings.Media.CinemaSyncOffsetMs.ToString();
        RefreshFfmpegBadge();

        LoadScenesFromSettings(settings);
        LoadAutomationsFromSettings(settings);
        LoadShortcutsFromSettings(settings);

        RebuildVolumePresets(settings.Receiver.VolumePresets);

        StationPresets.Clear();
        foreach (var station in settings.Receiver.Stations)
        {
            StationPresets.Add(StationPresetEditor.FromModel(station));
        }

        if (StationPresets.Count > 0 && SelectedStationPreset is null)
        {
            SelectedStationPreset = StationPresets[0];
        }

        FilteredStationPresets.Refresh();
    }

    private void RebuildVolumePresets(IReadOnlyList<int> presets)
    {
        string[] defaultNames = ["Night", "Chill", "Normal", "Boost", "Party", "Max"];
        VolumePresets.Clear();
        for (var i = 0; i < presets.Count; i++)
        {
            var label = i < defaultNames.Length ? defaultNames[i] : $"Preset {i + 1}";
            VolumePresets.Add(new VolumePresetItem(presets[i], label));
        }

        UpdateActiveVolumePreset(TargetVolume);
    }

    private void UpdateActiveVolumePreset(int currentLevel)
    {
        foreach (var item in VolumePresets)
        {
            item.IsActive = Math.Abs(item.Level - currentLevel) <= 1;
        }
    }

    private void ScheduleDebouncedVolumeApply(int level, bool immediate = false)
    {
        _volumeGuard.Sent(level);
        _volumeDebounceCts?.Cancel();
        _volumeDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _volumeDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediate)
                {
                    await Task.Delay(170, cts.Token);
                }

                await _receiverController.SetVolumeAsync(level, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Superseded by newer drag/step
            }
            catch (Exception exception)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    LastError = exception.Message;
                    StatusMessage = exception.Message;
                });
            }
        });
    }

    private Task RefreshAsync() =>
        ExecuteReceiverActionAsync(() => _receiverController.RefreshAsync(), "Receiver telemetry refreshed.");

    private Task TogglePowerAsync() =>
        ExecuteReceiverActionAsync(() => _receiverController.TogglePowerAsync(), IsPoweredOn && IsMainZoneOn ? "Receiver entering standby." : "Receiver powering on.");

    private Task ToggleZone2Async() =>
        ExecuteReceiverActionAsync(() => _receiverController.SetZone2PowerAsync(!IsZone2On), IsZone2On ? "Zone 2 turned off." : "Zone 2 turned on.");

    private Task PlayPauseAsync() =>
        _heroCast is { } cast
            ? PlayPauseCastAsync(cast) // The hero shows a cast: control the Chromecast, not HEOS.
            : SetPlayStateAsync(IsPlaying ? "pause" : "play");

    private async Task SetPlayStateAsync(string state)
    {
        if (string.Equals(state, "stop", StringComparison.OrdinalIgnoreCase))
        {
            if (_youtubeQueueService.IsYouTubePlaybackActive)
            {
                _youtubeQueueService.StopSession();
            }

            if (_localJukeboxService.IsLocalPlaybackActive)
            {
                _localJukeboxService.StopSession();
            }

            if (_pcAudioCastService.IsCasting)
            {
                await _pcAudioCastService.StopCastingAsync();
            }
        }

        await ExecuteReceiverActionAsync(() => _receiverController.SetPlayStateAsync(state), $"Transport set to {ToTitleCase(state)}.");
    }

    private async Task NextTrackAsync()
    {
        if (_heroCast is { PluginId: NowPlayingResolver.HomePodPluginId } homePod)
        {
            await SkipCastAsync(homePod, "next");
            return;
        }

        if (_youtubeQueueService.IsYouTubePlaybackActive)
        {
            try
            {
                var skipped = await _youtubeQueueService.PlayNextAsync();
                StatusMessage = skipped ? "Skipped to next YouTube track." : "Reached end of YouTube queue.";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                StatusMessage = ex.Message;
            }

            return;
        }

        if (_localJukeboxService.IsLocalPlaybackActive)
        {
            try
            {
                var skipped = await _localJukeboxService.PlayNextAsync();
                StatusMessage = skipped ? "Skipped to next local audio file." : "Reached end of local playlist.";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                StatusMessage = ex.Message;
            }

            return;
        }

        await ExecuteReceiverActionAsync(() => _receiverController.PlayNextAsync(), "Skipped to next track.");
    }

    private async Task PreviousTrackAsync()
    {
        if (_heroCast is { PluginId: NowPlayingResolver.HomePodPluginId } homePod)
        {
            await SkipCastAsync(homePod, "previous");
            return;
        }

        if (_youtubeQueueService.IsYouTubePlaybackActive)
        {
            try
            {
                var skipped = await _youtubeQueueService.PlayPreviousAsync();
                StatusMessage = skipped ? "Returned to previous YouTube track." : "No previous YouTube track in queue.";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                StatusMessage = ex.Message;
            }

            return;
        }

        if (_localJukeboxService.IsLocalPlaybackActive)
        {
            try
            {
                var skipped = await _localJukeboxService.PlayPreviousAsync();
                StatusMessage = skipped ? "Returned to previous local audio file." : "No previous local track.";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                StatusMessage = ex.Message;
            }

            return;
        }

        await ExecuteReceiverActionAsync(() => _receiverController.PlayPreviousAsync(), "Returned to previous track.");
    }

    private Task ToggleMuteAsync() =>
        ExecuteReceiverActionAsync(() => _receiverController.ToggleMuteAsync(), IsMuted ? "Receiver unmuted." : "Receiver muted.");

    private async Task SetLowVolumeAsync()
    {
        if (int.TryParse(SettingsLowVolume, out var lowVolume))
        {
            _suppressVolumeSend = true;
            TargetVolume = lowVolume;
            _suppressVolumeSend = false;
            _volumeGuard.Sent(lowVolume);
        }

        await ExecuteReceiverActionAsync(() => _receiverController.SetLowVolumeAsync(), "Safety low volume applied.");
    }

    private Task ApplyVolumeAsync()
    {
        var level = TargetVolume;
        _volumeGuard.Sent(level);
        return ExecuteReceiverActionAsync(() => _receiverController.SetVolumeAsync(level), $"Volume set to {DenonVolumeMath.FormatDecibels(level)} ({level}).");
    }

    private async Task StepVolumeAsync(int delta)
    {
        var next = Math.Clamp(TargetVolume + delta, 0, MaxVolume);
        _suppressVolumeSend = true;
        TargetVolume = next;
        _suppressVolumeSend = false;
        _volumeGuard.Sent(next);
        await ExecuteReceiverActionAsync(() => _receiverController.SetVolumeAsync(next), $"Volume set to {DenonVolumeMath.FormatDecibels(next)} ({next}).");
    }

    private async Task SetVolumePresetAsync(object? parameter)
    {
        if (parameter is not VolumePresetItem preset)
        {
            return;
        }

        _suppressVolumeSend = true;
        TargetVolume = preset.Level;
        _suppressVolumeSend = false;
        _volumeGuard.Sent(preset.Level);
        await ExecuteReceiverActionAsync(() => _receiverController.SetVolumeAsync(preset.Level), $"{preset.Name} volume ({preset.DbLabel}) applied.");
    }

    private async Task SelectInputAsync(object? parameter)
    {
        var code = parameter switch
        {
            InputSourceItem item => item.Code,
            string str => str,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.SetInputSourceAsync(code),
            $"Switched input to {DenonCatalog.GetFriendlyInputName(code)}.");
    }

    private async Task SelectSoundModeAsync(object? parameter)
    {
        var code = parameter switch
        {
            SoundModeItem item => item.Code,
            string str => str,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.SetSoundModeAsync(code),
            $"Sound mode set to {code}.");
    }

    private async Task SetEcoModeAsync(object? parameter)
    {
        if (parameter is not string mode || string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.SetEcoModeAsync(mode),
            $"ECO mode set to {mode}.");
    }

    private async Task SetSleepTimerAsync(object? parameter)
    {
        int? minutes = parameter switch
        {
            SleepTimerOption opt => opt.Minutes,
            int m => m,
            _ => null,
        };

        await ExecuteReceiverActionAsync(
            () => _receiverController.SetSleepTimerAsync(minutes),
            minutes is > 0 ? $"Sleep timer set for {minutes} minutes." : "Sleep timer turned off.");
    }

    private async Task PlayCustomStreamAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomStreamUrl))
        {
            StatusMessage = "Enter a stream URL before starting playback.";
            return;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.PlayCustomUrlAsync(CustomStreamUrl, UseLowVolumeForStreams, UnmuteBeforePlay),
            "Custom stream started.");
    }

    private async Task TestCustomStreamAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomStreamUrl))
        {
            CustomStreamProbeStatus = "Enter a URL to test.";
            CustomStreamProbeSuccess = false;
            return;
        }

        IsProbingCustomStream = true;
        CustomStreamProbeStatus = "Probing stream endpoint...";
        CustomStreamProbeSuccess = null;

        try
        {
            var result = await _streamProbeService.ProbeAsync(CustomStreamUrl);
            CustomStreamProbeSuccess = result.IsReachable;
            CustomStreamProbeStatus = result.IsReachable ? $"OK • {result.Message}" : $"Failed • {result.Message}";
        }
        finally
        {
            IsProbingCustomStream = false;
        }
    }

    private async Task PlayPresetAsync(object? parameter)
    {
        if (parameter is not StationPresetEditor preset)
        {
            return;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.PlayPresetAsync(preset.ToModel(), useLowVolume: false, unmute: UnmuteBeforePlay),
            $"Tuning to {preset.DisplayName}...");
    }

    private async Task PlayPresetLowAsync(object? parameter)
    {
        if (parameter is not StationPresetEditor preset)
        {
            return;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.PlayPresetAsync(preset.ToModel(), useLowVolume: true, unmute: true),
            $"Tuning to {preset.DisplayName} at safety low volume...");
    }

    private async Task TestPresetAsync(object? parameter)
    {
        if (parameter is not StationPresetEditor preset)
        {
            return;
        }

        preset.IsProbing = true;
        preset.ProbeStatusText = "Probing...";
        preset.ProbeSucceeded = null;

        try
        {
            var result = await _streamProbeService.ProbeAsync(preset.Url);
            preset.ProbeSucceeded = result.IsReachable;
            preset.ProbeStatusText = result.IsReachable ? $"OK • {result.Message}" : $"Error • {result.Message}";
        }
        finally
        {
            preset.IsProbing = false;
        }
    }

    private async Task LoadFavoritesAsync()
    {
        IsLoadingFavorites = true;
        FavoritesStatusText = "Querying HEOS Favorites from receiver...";

        try
        {
            var favorites = await _receiverController.GetFavoritesAsync();
            HeosFavorites.Clear();
            foreach (var fav in favorites)
            {
                HeosFavorites.Add(fav);
            }

            FavoritesStatusText = favorites.Count > 0
                ? $"Loaded {favorites.Count} HEOS Favorites from your receiver."
                : "No HEOS Favorites found on this receiver.";
        }
        catch (Exception exception)
        {
            FavoritesStatusText = $"Could not load favorites: {exception.Message}";
        }
        finally
        {
            IsLoadingFavorites = false;
        }
    }

    private async Task PlayFavoriteAsync(object? parameter)
    {
        if (parameter is not HeosFavoriteItem favorite)
        {
            return;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.PlayFavoriteAsync(favorite, UseLowVolumeForStreams, UnmuteBeforePlay),
            $"Playing HEOS Favorite: {favorite.Name}");
    }

    private async Task ScanNetworkAsync()
    {
        IsScanningNetwork = true;
        DiscoveryStatusText = "Scanning LAN via SSDP & HEOS port 1255...";
        DiscoveredReceivers.Clear();

        try
        {
            var found = await _discoveryService.DiscoverAsync();
            foreach (var receiver in found)
            {
                DiscoveredReceivers.Add(receiver);
            }

            DiscoveryStatusText = found.Count > 0
                ? $"Found {found.Count} HEOS receiver(s). Click one to populate your connection settings."
                : "No HEOS receivers responded to SSDP discovery. Verify your receiver is powered or in Network Standby.";
        }
        catch (Exception exception)
        {
            DiscoveryStatusText = $"Discovery failed: {exception.Message}";
        }
        finally
        {
            IsScanningNetwork = false;
        }
    }

    private void UseDiscoveredReceiver(object? parameter)
    {
        if (parameter is not DiscoveredReceiver receiver)
        {
            return;
        }

        SettingsHost = receiver.IpAddress;
        SettingsPort = receiver.Port.ToString();
        SettingsPlayerName = receiver.Name;
        StatusMessage = $"Selected {receiver.Name} ({receiver.IpAddress}). Click 'Save Settings' to apply.";
    }

    private void AddStation()
    {
        var editor = new StationPresetEditor
        {
            Alias = $"station-{StationPresets.Count + 1}",
            DisplayName = "New Radio Station",
            Url = "http://",
        };

        StationPresets.Add(editor);
        SelectedStationPreset = editor;
        FilteredStationPresets.Refresh();
        StatusMessage = "Added new station preset. Edit details and click Save.";
    }

    private void RemoveStation(object? parameter)
    {
        var target = parameter as StationPresetEditor ?? SelectedStationPreset;
        if (target is null)
        {
            return;
        }

        var removedName = target.DisplayName;
        StationPresets.Remove(target);
        SelectedStationPreset = StationPresets.FirstOrDefault();
        FilteredStationPresets.Refresh();
        StatusMessage = $"Removed '{removedName}'. Click Save to persist changes.";
    }

    private void MoveStationUp(object? parameter)
    {
        if (parameter is not StationPresetEditor station)
        {
            return;
        }

        var index = StationPresets.IndexOf(station);
        if (index > 0)
        {
            StationPresets.Move(index, index - 1);
            FilteredStationPresets.Refresh();
        }
    }

    private void MoveStationDown(object? parameter)
    {
        if (parameter is not StationPresetEditor station)
        {
            return;
        }

        var index = StationPresets.IndexOf(station);
        if (index >= 0 && index < StationPresets.Count - 1)
        {
            StationPresets.Move(index, index + 1);
            FilteredStationPresets.Refresh();
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            if (!ValidateShortcutCollisions(reportClean: false))
            {
                StatusMessage = "Cannot save: resolve highlighted shortcut collisions in Settings first.";
                return;
            }

            var volumePresets = ParseVolumePresets(SettingsVolumePresetsText);
            var settings = _settingsStore.Current.Clone();
            settings.Receiver = new ReceiverSettings
            {
                Host = SettingsHost,
                Port = ParseInt(SettingsPort, 1255),
                PlayerName = SettingsPlayerName,
                PollIntervalSeconds = ParseInt(SettingsPollIntervalSeconds, 5),
                DefaultLowVolume = ParseInt(SettingsLowVolume, 20),
                VolumePresets = volumePresets,
                Stations = StationPresets.Select(static station => station.ToModel()).ToList(),
                Scenes = ScenePresets.Select(static scene => scene.ToModel()).ToList(),
            };
            var editorUi = new UiSettings
            {
                StartMinimizedToTray = SettingsStartMinimizedToTray,
                MinimizeToTray = SettingsMinimizeToTray,
                CloseToTray = SettingsCloseToTray,
                ShowNotifications = SettingsShowNotifications,
                DisplayVolumeInDb = SettingsDisplayVolumeInDb,
                LaunchAtWindowsStartup = SettingsLaunchAtWindowsStartup,
                CheckForUpdates = SettingsCheckForUpdates,
                AllowRoomMicMeter = SettingsAllowRoomMicMeter,
                MeterMicDeviceId = SettingsMeterMicDeviceId,
            };
            settings.Media = new MediaRelaySettings
            {
                RelayPort = ParseInt(SettingsRelayPort, 18925),
                FfmpegPath = SettingsFfmpegPath?.Trim() ?? string.Empty,
                PreferFfmpegMp3Transcode = SettingsPreferFfmpegMp3,
                CinemaSyncOffsetMs = ParseInt(SettingsCinemaSyncOffsetMs, 1800),
            };
            settings.Shortcuts = new ShortcutSettings
            {
                Bindings = ShortcutEditors.Select(static s => s.ToModel()).ToList(),
            };
            settings.Telemetry.Enabled = SettingsTelemetryEnabled;
            settings.Telemetry.RetentionDays = ParseInt(SettingsTelemetryRetentionDays, 90);
            settings.Telemetry.MediaDwellSeconds = ParseInt(SettingsTelemetryMediaDwellSeconds, 15);

            _startupService.SetEnabled(SettingsLaunchAtWindowsStartup, SettingsStartMinimizedToTray ? true : null);
            _receiverController.ResetConnections();
            await _settingsStore.SaveEditorAsync(s =>
            {
                s.Receiver = settings.Receiver;
                s.Ui.ApplyEditorFields(editorUi); // the latest theme, skin, placement and splash marker stay
                s.Media = settings.Media;
                s.Shortcuts = settings.Shortcuts;
                s.Telemetry = settings.Telemetry;
            });
            RefreshTelemetryStats();
            StatusMessage = "Settings saved. Syncing with receiver...";
            await _receiverController.RefreshAsync();
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            StatusMessage = exception.Message;
        }
    }

    private async Task SaveVolumeDisplayModeAsync(bool displayVolumeInDb)
    {
        try
        {
            await _settingsStore.SaveUiStateAsync(ui => ui.DisplayVolumeInDb = displayVolumeInDb);
        }
        catch (Exception exception)
        {
            LastError = $"Could not save volume display mode: {exception.Message}";
        }
    }

    private void RefreshContentBadgeBrushes()
    {
        foreach (var scene in ScenePresets)
        {
            scene.RefreshThemeBrushes();
        }

        foreach (var station in StationPresets)
        {
            station.RefreshThemeBrushes();
        }

        foreach (var topic in _allHelpTopics)
        {
            topic.RefreshThemeBrushes();
        }

        foreach (var plugin in InstalledPlugins)
        {
            plugin.RefreshThemeBrushes();
        }
    }

    private void RefreshTelemetryStats()
    {
        var stats = _telemetryLog.GetStats();
        var sizeLabel = stats.TotalBytes >= 1024 * 1024
            ? $"{stats.TotalBytes / (1024.0 * 1024.0):0.0} MB"
            : $"{Math.Max(0, (int)Math.Round(stats.TotalBytes / 1024.0))} KB";

        TelemetryStatsSummaryText = stats.Enabled
            ? $"{stats.EventsRecordedThisSession} events this session • {stats.Streams.Count} stream(s) • {sizeLabel} on disk • {stats.RetentionDays}d retention"
            : $"Paused • {stats.Streams.Count} stream(s) • {sizeLabel} on disk";
    }

    private async Task ClearTelemetryHistoryAsync()
    {
        await _telemetryLog.ClearAllAsync();
        RefreshTelemetryStats();
        StatusMessage = "Cleared all structured event history files.";
    }

    private void LoadDefaultSettingsIntoEditor()
    {
        LoadSettings(AppSettings.CreateDefault());
        StatusMessage = "Factory defaults loaded into editor. Click Save Settings to apply.";
    }

    private void ExportSettings()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Denon Desktop Settings",
            Filter = "JSON Files (*.json)|*.json",
            FileName = "denon-desktop-settings.json",
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, _settingsStore.ExportJson());
            StatusMessage = $"Settings exported to {Path.GetFileName(dialog.FileName)}.";
            _logStore.Info($"Exported settings to {dialog.FileName}");
        }
    }

    private async Task ImportSettingsAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Denon Desktop Settings",
            Filter = "JSON Files (*.json)|*.json",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            await _settingsStore.ImportJsonAsync(json);
            _receiverController.ResetConnections();
            StatusMessage = $"Settings imported from {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            StatusMessage = $"Import failed: {exception.Message}";
        }
    }

    private async Task RunConsoleCommandAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        IsRunningConsoleCommand = true;
        try
        {
            var reply = string.Equals(ConsoleProtocol, "HEOS", StringComparison.OrdinalIgnoreCase)
                ? await _receiverController.SendRawHeosCommandAsync(command)
                : await _receiverController.SendRawAvrCommandAsync(command);

            ConsoleOutput = $"[{DateTime.Now:HH:mm:ss}] ({ConsoleProtocol}) > {command.Trim()}\n{reply}";
        }
        catch (Exception exception)
        {
            ConsoleOutput = $"[{DateTime.Now:HH:mm:ss}] ({ConsoleProtocol}) ERROR: {exception.Message}";
        }
        finally
        {
            IsRunningConsoleCommand = false;
        }
    }

    private async Task RunQuickDiagnosticAsync(object? parameter)
    {
        if (parameter is not string raw || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        // Format: "AVR:MV?" or "HEOS:heos://player/get_players"
        var separator = raw.IndexOf(':');
        if (separator > 0)
        {
            var proto = raw[..separator];
            var cmd = raw[(separator + 1)..];
            ConsoleProtocol = proto;
            ConsoleCommandInput = cmd;
            await RunConsoleCommandAsync(cmd);
        }
    }

    private void CopyLogsToClipboard()
    {
        var lines = LogEntries.Select(static e => $"{e.Timestamp:yyyy-MM-dd HH:mm:ss} [{e.Level}] {e.Message}");
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
        StatusMessage = "Copied activity history to clipboard.";
    }

    private async Task FixSilentAudioAsync() =>
        await ExecuteReceiverActionAsync(
            () => _receiverController.FixSilentAudioAsync(35),
            "Receiver woken, switched to HEOS, unmuted, and set to audible volume.");

    private async Task ResetAndRefreshAsync()
    {
        _receiverController.ResetConnections();
        await ExecuteReceiverActionAsync(
            () => _receiverController.RefreshAsync(),
            "Connections reset and receiver synced.");
    }

    private async Task RunHealthCheckAsync()
    {
        IsRunningHealthCheck = true;
        HealthSummaryText = "Running 5-point receiver health check...";
        HealthSummaryTone = "AccentCyanBright";

        var settings = _settingsStore.Current.Receiver;
        var netCheck = DiagnosticChecks[0];
        var avrCheck = DiagnosticChecks[1];
        var heosCheck = DiagnosticChecks[2];
        var audioCheck = DiagnosticChecks[3];
        var streamCheck = DiagnosticChecks[4];

        foreach (var check in DiagnosticChecks)
        {
            check.SetResult(DiagnosticCheckState.Running, "Testing...");
        }

        try
        {
            // 1. Network Ping
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(settings.Host, 2000);
                if (reply.Status == IPStatus.Success)
                {
                    netCheck.SetResult(
                        DiagnosticCheckState.Pass,
                        $"Receiver at {settings.Host} responded in {reply.RoundtripTime} ms.");
                }
                else
                {
                    netCheck.SetResult(
                        DiagnosticCheckState.Fail,
                        $"Could not reach {settings.Host} ({reply.Status}).",
                        "Make sure your Denon receiver is plugged in and connected to the same Wi-Fi/LAN, or click 'Scan LAN' in Settings.");
                }
            }
            catch (Exception ex)
            {
                netCheck.SetResult(
                    DiagnosticCheckState.Fail,
                    $"Network test failed for '{settings.Host}': {ex.Message}",
                    "Check the Receiver IP in Settings or run 'Scan LAN' to find the receiver automatically.");
            }

            // 2. AVR Control Interface (Port 23)
            AvrDeviceStatus? avrStatus = null;
            try
            {
                avrStatus = await _receiverController.GetAvrStatusOnlyAsync();
                var powerText = avrStatus.IsPoweredOn && avrStatus.IsMainZoneOn ? "Awake" : "Standby";
                avrCheck.SetResult(
                    DiagnosticCheckState.Pass,
                    $"AVR control responded ({powerText}, Input: {DenonCatalog.GetFriendlyInputName(avrStatus.InputSource)}, Volume: {DenonVolumeMath.FormatDecibels(avrStatus.Volume)}).");
            }
            catch (Exception ex)
            {
                avrCheck.SetResult(
                    DiagnosticCheckState.Fail,
                    $"AVR control port (:23) did not respond: {ex.Message}",
                    "Denon receivers only allow 1 control connection at a time. Close other Denon remote apps or click 'Reset Connections'.");
            }

            // 3. HEOS Streaming Engine (Port 1255)
            try
            {
                var players = await _receiverController.GetPlayersAsync();
                if (players.Count == 0)
                {
                    heosCheck.SetResult(
                        DiagnosticCheckState.Fail,
                        "HEOS connected, but no players were reported.",
                        "Restart the receiver's Network / HEOS module or unplug power for 15 seconds.");
                }
                else
                {
                    var targetSlug = SlugNormalizer.Normalize(settings.PlayerName);
                    var matched = players.FirstOrDefault(p =>
                        SlugNormalizer.Normalize(p.Name) == targetSlug ||
                        SlugNormalizer.Normalize(p.Model) == targetSlug ||
                        SlugNormalizer.Normalize(p.IpAddress) == targetSlug);

                    if (matched is not null)
                    {
                        heosCheck.SetResult(
                            DiagnosticCheckState.Pass,
                            $"Matched '{matched.Name}' ({matched.Model}, Firmware {matched.Version}).");
                    }
                    else
                    {
                        var names = string.Join(", ", players.Select(static p => p.Name));
                        heosCheck.SetResult(
                            DiagnosticCheckState.Warning,
                            $"HEOS is online, but player name '{settings.PlayerName}' did not match.",
                            $"Available player(s): {names}. Update 'HEOS Player Name' in Settings.");
                    }
                }
            }
            catch (Exception ex)
            {
                heosCheck.SetResult(
                    DiagnosticCheckState.Fail,
                    $"HEOS port ({settings.Port}) did not respond: {ex.Message}",
                    "Verify Network Control is set to 'Always On' in your Denon receiver's Network menu.");
            }

            // 4. Audio Output Readiness
            if (avrStatus is null)
            {
                audioCheck.SetResult(
                    DiagnosticCheckState.Warning,
                    "Skipped because AVR control did not respond.",
                    "Resolve the AVR Control Interface issue above first.");
            }
            else if (!avrStatus.IsPoweredOn || !avrStatus.IsMainZoneOn)
            {
                audioCheck.SetResult(
                    DiagnosticCheckState.Warning,
                    "Receiver is currently in Standby (powered off).",
                    "Click 'Fix Silent Audio' or the Power button in the title bar to wake the receiver.");
            }
            else if (avrStatus.IsMuted)
            {
                audioCheck.SetResult(
                    DiagnosticCheckState.Warning,
                    "Receiver audio is currently MUTED.",
                    "Click 'Fix Silent Audio' or press M to unmute.");
            }
            else if (avrStatus.Volume < 18)
            {
                audioCheck.SetResult(
                    DiagnosticCheckState.Warning,
                    $"Volume is very quiet ({DenonVolumeMath.FormatDecibels(avrStatus.Volume)} / level {avrStatus.Volume}).",
                    "Levels below 20 (-60 dB) are often inaudible. Click 'Fix Silent Audio' or raise the volume slider.");
            }
            else if (!DenonCatalog.InputMatches(avrStatus.InputSource, "NET"))
            {
                audioCheck.SetResult(
                    DiagnosticCheckState.Pass,
                    $"Audio is ready on {DenonCatalog.GetFriendlyInputName(avrStatus.InputSource)} at {DenonVolumeMath.FormatDecibels(avrStatus.Volume)}.",
                    "Playing any radio station will automatically switch the input to HEOS Music.");
            }
            else
            {
                audioCheck.SetResult(
                    DiagnosticCheckState.Pass,
                    $"Receiver is awake on HEOS Music, unmuted, at audible level {DenonVolumeMath.FormatDecibels(avrStatus.Volume)} ({avrStatus.Volume}).");
            }

            // 5. Internet Radio Stream Reachability
            var sampleStation = StationPresets.FirstOrDefault(static s => s.IsCurrentlyPlaying)
                                ?? StationPresets.FirstOrDefault();
            if (sampleStation is null)
            {
                streamCheck.SetResult(
                    DiagnosticCheckState.Warning,
                    "No station presets configured to test.",
                    "Add a radio station in 'Stations & Streams'.");
            }
            else
            {
                var probe = await _streamProbeService.ProbeAsync(sampleStation.Url);
                if (probe.IsReachable)
                {
                    streamCheck.SetResult(
                        DiagnosticCheckState.Pass,
                        $"'{sampleStation.DisplayName}' stream is live ({probe.Message}).");
                }
                else
                {
                    streamCheck.SetResult(
                        DiagnosticCheckState.Fail,
                        $"'{sampleStation.DisplayName}' stream failed: {probe.Message}",
                        "The radio station URL may have moved or your internet/firewall blocked it. Test individual stations in 'Stations & Streams'.");
                }
            }

            var fails = DiagnosticChecks.Count(static c => c.State == DiagnosticCheckState.Fail);
            var warns = DiagnosticChecks.Count(static c => c.State == DiagnosticCheckState.Warning);

            if (fails == 0 && warns == 0)
            {
                HealthSummaryText = "All systems healthy! Your Denon receiver and radio streams are ready.";
                HealthSummaryTone = OnlineTone;
            }
            else if (fails == 0)
            {
                HealthSummaryText = $"Connected with {warns} item(s) to check — see tips below or click 'Fix Silent Audio'.";
                HealthSummaryTone = StandbyTone;
            }
            else
            {
                HealthSummaryText = $"Found {fails} connection/stream issue(s) — follow the plain-English fixes below.";
                HealthSummaryTone = OfflineTone;
            }

            _logStore.Info($"Health check completed ({5 - fails - warns} OK, {warns} warnings, {fails} issues).");
        }
        finally
        {
            IsRunningHealthCheck = false;
        }
    }

    private void CopyDiagnosticReportToClipboard()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Denon Desktop Health Report ===");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Receiver: {ReceiverTitle} ({SettingsHost}:{SettingsPort})");
        sb.AppendLine($"Status: {ConnectionBadgeText} | Input: {InputSourceDisplay} | Volume: {DenonVolumeMath.FormatDecibels(LiveVolume)} ({LiveVolume}) | Muted: {IsMuted}");
        sb.AppendLine();
        sb.AppendLine("--- Health Checks ---");
        foreach (var check in DiagnosticChecks)
        {
            sb.AppendLine($"[{check.StatusLabel}] {check.Title}: {check.Summary}");
            if (!string.IsNullOrWhiteSpace(check.Advice))
            {
                sb.AppendLine($"    Tip: {check.Advice}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("--- Recent Activity (last 20) ---");
        foreach (var entry in LogEntries.TakeLast(20))
        {
            sb.AppendLine($"{entry.Timestamp:HH:mm:ss} [{entry.Level}] {entry.Message}");
        }

        System.Windows.Clipboard.SetText(sb.ToString());
        StatusMessage = "Copied full health & support report to clipboard.";
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Ignore shell launch errors
        }
    }

    private bool FilterStation(object obj)
    {
        if (obj is not StationPresetEditor station)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(StationSearchText))
        {
            return true;
        }

        return station.DisplayName.Contains(StationSearchText, StringComparison.OrdinalIgnoreCase) ||
               station.Alias.Contains(StationSearchText, StringComparison.OrdinalIgnoreCase) ||
               station.Url.Contains(StationSearchText, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterLogEntry(object obj)
    {
        if (obj is not AppLogEntry entry)
        {
            return false;
        }

        if (!string.Equals(LogFilterLevel, "All", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.Level.ToString(), LogFilterLevel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(LogSearchText) &&
            !entry.Message.Contains(LogSearchText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
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

    private static bool SoundModeMatches(string currentMode, string optionCode)
    {
        if (string.IsNullOrWhiteSpace(currentMode))
        {
            return false;
        }

        if (string.Equals(currentMode, optionCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Prevent "MCH STEREO" from lighting up "STEREO", and "PURE DIRECT" from lighting up "DIRECT"
        if (string.Equals(optionCode, "STEREO", StringComparison.OrdinalIgnoreCase))
        {
            return currentMode.Contains("STEREO", StringComparison.OrdinalIgnoreCase) &&
                   !currentMode.Contains("MCH", StringComparison.OrdinalIgnoreCase) &&
                   !currentMode.Contains("MULTI", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(optionCode, "DIRECT", StringComparison.OrdinalIgnoreCase))
        {
            return currentMode.Contains("DIRECT", StringComparison.OrdinalIgnoreCase) &&
                   !currentMode.Contains("PURE", StringComparison.OrdinalIgnoreCase);
        }

        return currentMode.Contains(optionCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildNowPlayingDetail(NowPlayingMedia nowPlaying, StationPresetEditor? activeStation)
    {
        var pieces = new List<string>();
        if (!string.IsNullOrWhiteSpace(nowPlaying.Artist))
        {
            pieces.Add(nowPlaying.Artist);
        }

        if (!string.IsNullOrWhiteSpace(nowPlaying.Album))
        {
            pieces.Add(nowPlaying.Album);
        }

        if (activeStation is not null &&
            !pieces.Any(p => p.Contains(activeStation.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            pieces.Add(activeStation.DisplayName);
        }

        if (pieces.Count == 0 && !string.IsNullOrWhiteSpace(nowPlaying.Type))
        {
            pieces.Add(ToTitleCase(nowPlaying.Type));
        }

        return pieces.Count == 0 ? "HEOS Network Audio Stream" : string.Join(" • ", pieces);
    }

    private static string ToTitleCase(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Unknown"
            : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static List<int> ParseVolumePresets(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [15, 28, 42, 55];
        }

        var list = raw
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static s => int.TryParse(s, out var v) ? (int?)Math.Clamp(v, 0, 98) : null)
            .Where(static v => v.HasValue)
            .Select(static v => v!.Value)
            .Distinct()
            .OrderBy(static v => v)
            .Take(6)
            .ToList();

        return list.Count == 0 ? [15, 28, 42, 55] : list;
    }
}
