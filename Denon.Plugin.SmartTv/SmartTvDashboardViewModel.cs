using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Denon.Plugin.SmartTv;

public sealed class SmartTvDashboardViewModel : INotifyPropertyChanged
{
    // Tokens, not brushes: a theme switch rebuilds the brush from the token.
    private const string LiveTone = "AccentEmerald";
    private const string DemoTone = "AccentCyanBright";
    private const string WarnTone = "AccentAmber";
    private const string MutedTone = "TextFaint";

    private static readonly IReadOnlyList<LgTvInputInfo> DefaultOfflineInputs =
    [
        new("HDMI_1", "com.webos.app.hdmi1", "HDMI 1", false, false, "Offline"),
        new("HDMI_2", "com.webos.app.hdmi2", "HDMI 2 (eARC)", false, false, "Offline"),
        new("HDMI_3", "com.webos.app.hdmi3", "HDMI 3", false, false, "Offline"),
        new("HDMI_4", "com.webos.app.hdmi4", "HDMI 4", false, false, "Offline"),
    ];

    private static readonly IReadOnlyList<LgTvAppInfo> DefaultOfflineApps =
    [
        new("youtube.leanback.v4", "YouTube", "Google", false, false, "YT"),
        new("netflix", "Netflix", "Netflix", false, false, "NF"),
        new("cdp-30", "Plex", "Plex", false, false, "PX"),
        new("com.disney.disneyplus-prod", "Disney+", "Disney", false, false, "D+"),
        new("spotify-beehive", "Spotify", "Spotify", false, false, "SP"),
        new("amazon", "Prime Video", "Amazon", false, false, "PV"),
        new("com.webos.app.livetv", "Live TV", "LG", true, false, "TV"),
    ];

    private readonly SmartTvPlugin _plugin;
    private IReadOnlyList<LgTvAppInfo> _allApps = DefaultOfflineApps;
    private CancellationTokenSource? _volumeDebounceCts;
    private CancellationTokenSource? _previewLoopCts;
    private bool _suppressVolumeSend;
    private bool _isViewVisible;
    private int _isCapturingPreview;
    private DateTimeOffset _lastUserInteractionUtc = DateTimeOffset.MinValue;

    private ImageSource? _previewImage;
    private bool _autoRefreshPreview;
    private string _previewStatusText = "Ready — click Snap Now or enable Auto-Refresh";
    private string _browserUrlText = string.Empty;

    private string _tvName = "Living Room OLED TV";
    private string _tvIp = string.Empty;
    private string _macAddress = string.Empty;
    private bool _demoMode;
    private bool _wakeWithDenon;
    private bool _offWithStandby;
    private bool _showSceneToast = true;
    private bool _channelLogos = true;
    private bool _switchToArcOnDenon;
    private bool _muteSpeakersOnDenon;
    private bool _screenOffOnMusicScene;
    private string _cecWakeCastDevice = string.Empty;
    private bool _isLoadingCastDevices;

    private bool _isLiveConnected;
    private string _connectionBadgeText = "STANDBY / OFFLINE";
    private string _connectionBadgeTone = MutedTone;
    private string _modelSummary = "Not paired yet";
    private string _nowShowingText = "TV Standby / Offline";
    private string _soundOutputText = "—";
    private string _soundOutputRaw = string.Empty;
    private string _screenStateText = "Panel Ready";
    private bool _isScreenOff;
    private int _tvVolume;
    private bool _isTvMuted;
    private string _appSearchQuery = string.Empty;
    private string _keyboardText = string.Empty;
    private string _youtubeUrlOrId = string.Empty;
    private string _toastText = "Denon Desktop • Home Theater Ready";
    /// <summary>The first-run hint; a paired TV starts with no message.</summary>
    private const string NotPairedHint = "Open Settings, then click Auto-Discover TV, or enter the TV's IP address and click Pair with LG TV.";

    private string _statusMessage = NotPairedHint;
    private string _appsCountBadge = "7 quick apps";
    private bool _isBusy;
    private string _selectedTab = SmartTvConfig.WatchTab;
    private string _remotePanel = SmartTvConfig.ButtonsPanel;
    private bool _suppressAutoSave;

    public SmartTvDashboardViewModel(SmartTvPlugin plugin)
    {
        _plugin = plugin;

        DiscoverCommand = new AsyncRelayCommand(_ => DiscoverAsync());
        AutoMacCommand = new AsyncRelayCommand(_ => ResolveMacAsync());
        PairCommand = new AsyncRelayCommand(_ => PairAsync(forcePairing: false));
        RePairCommand = new AsyncRelayCommand(_ => PairAsync(forcePairing: true));
        RefreshCommand = new AsyncRelayCommand(_ => RefreshStateAsync());
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        ToggleDemoCommand = new AsyncRelayCommand(_ => ToggleDemoAsync());
        RunActionCommand = new AsyncRelayCommand(p => p is string act ? RunActionAsync(act) : Task.CompletedTask);
        SwitchInputCommand = new AsyncRelayCommand(p => p is string input ? SwitchInputAsync(input) : Task.CompletedTask);
        LaunchAppCommand = new AsyncRelayCommand(p => p is string appId ? LaunchAppAsync(appId) : Task.CompletedTask);
        SendButtonCommand = new AsyncRelayCommand(p => p is string btn ? SendButtonAsync(btn) : Task.CompletedTask);
        SendMediaCommand = new AsyncRelayCommand(p => p is string cmd ? SendMediaAsync(cmd) : Task.CompletedTask);
        SetAudioOutputCommand = new AsyncRelayCommand(p => p is string outId ? SetAudioOutputAsync(outId) : Task.CompletedTask);
        ToggleTvMuteCommand = new AsyncRelayCommand(_ => ToggleTvMuteAsync());
        StepTvVolumeCommand = new AsyncRelayCommand(p => StepTvVolumeAsync(p?.ToString() == "+"));
        SendKeyboardCommand = new AsyncRelayCommand(p => SendKeyboardAsync(p?.ToString() != "noenter"));
        BackspaceKeyboardCommand = new AsyncRelayCommand(_ => BackspaceKeyboardAsync());
        CastYouTubeCommand = new AsyncRelayCommand(_ => CastYouTubeAsync());
        SendCustomToastCommand = new AsyncRelayCommand(_ => SendCustomToastAsync());
        CapturePreviewCommand = new AsyncRelayCommand(_ => CapturePreviewAsync(showCursor: true));
        OpenBrowserUrlCommand = new AsyncRelayCommand(_ => OpenBrowserUrlAsync());
        RecalibrateCursorCommand = new AsyncRelayCommand(_ => RecalibrateCursorAsync());
        PointerClickCommand = new AsyncRelayCommand(_ => ClickPointerAsync());
        PointerScrollCommand = new AsyncRelayCommand(p => ScrollPointerAsync(0, p?.ToString() == "up" ? 3 : -3));

        foreach (var inp in DefaultOfflineInputs) Inputs.Add(inp);
        RebuildFilteredApps();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LgTvInputInfo> Inputs { get; } = [];
    public ObservableCollection<LgTvAppInfo> FilteredApps { get; } = [];

    /// <summary>
    /// "Automatic" and the Cast devices the Chromecast plugin knows, for "Chromecast on this TV". A load updates the
    /// entries in place: an entry that stays keeps its object, so the box does not lose the chosen one.
    /// </summary>
    public ObservableCollection<CastDeviceOption> CastDeviceOptions { get; } = [new(string.Empty, "Automatic")];

    public ICommand DiscoverCommand { get; }
    public ICommand AutoMacCommand { get; }
    public ICommand PairCommand { get; }
    public ICommand RePairCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ToggleDemoCommand { get; }
    public ICommand RunActionCommand { get; }
    public ICommand SwitchInputCommand { get; }
    public ICommand LaunchAppCommand { get; }
    public ICommand SendButtonCommand { get; }
    public ICommand SendMediaCommand { get; }
    public ICommand SetAudioOutputCommand { get; }
    public ICommand ToggleTvMuteCommand { get; }
    public ICommand StepTvVolumeCommand { get; }
    public ICommand SendKeyboardCommand { get; }
    public ICommand BackspaceKeyboardCommand { get; }
    public ICommand CastYouTubeCommand { get; }
    public ICommand SendCustomToastCommand { get; }
    public ICommand CapturePreviewCommand { get; }
    public ICommand OpenBrowserUrlCommand { get; }
    public ICommand RecalibrateCursorCommand { get; }
    public ICommand PointerClickCommand { get; }
    public ICommand PointerScrollCommand { get; }

    public string TvName { get => _tvName; set => Set(ref _tvName, value); }
    public string TvIp { get => _tvIp; set => Set(ref _tvIp, value); }
    public string MacAddress { get => _macAddress; set => Set(ref _macAddress, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public bool DemoMode
    {
        get => _demoMode;
        private set
        {
            if (Set(ref _demoMode, value))
            {
                Raise(nameof(DemoButtonLabel));
            }
        }
    }

    public string DemoButtonLabel => DemoMode ? "Exit Demo" : "Try Demo";

    // The page's tabs, by how often each control is used: Watch (power, inputs, sound), Remote, Apps, Settings.
    // A RadioButton sets its own to true; the false for the one it replaces changes nothing.
    public bool IsWatchTab { get => _selectedTab == SmartTvConfig.WatchTab; set { if (value) SelectTab(SmartTvConfig.WatchTab); } }

    public bool IsRemoteTab { get => _selectedTab == SmartTvConfig.RemoteTab; set { if (value) SelectTab(SmartTvConfig.RemoteTab); } }

    public bool IsAppsTab { get => _selectedTab == SmartTvConfig.AppsTab; set { if (value) SelectTab(SmartTvConfig.AppsTab); } }

    public bool IsSettingsTab { get => _selectedTab == SmartTvConfig.SettingsTab; set { if (value) SelectTab(SmartTvConfig.SettingsTab); } }

    // The Remote tab's right side: the remote buttons or the trackpad. The keyboard line serves both.
    public bool IsButtonsPanel { get => _remotePanel == SmartTvConfig.ButtonsPanel; set { if (value) SelectRemotePanel(SmartTvConfig.ButtonsPanel); } }

    public bool IsTrackpadPanel { get => _remotePanel == SmartTvConfig.TrackpadPanel; set { if (value) SelectRemotePanel(SmartTvConfig.TrackpadPanel); } }

    private void SelectRemotePanel(string panel, bool remember = true)
    {
        panel = SmartTvConfig.NormalizeRemotePanel(panel);
        if (remember)
        {
            _ = _plugin.SavePageChoiceAsync(remotePanel: panel);
        }

        if (_remotePanel == panel)
        {
            return;
        }

        _remotePanel = panel;
        Raise(nameof(IsButtonsPanel));
        Raise(nameof(IsTrackpadPanel));
    }

    /// <summary>
    /// Shows a tab. A tab the person picks is kept for the next visit (<paramref name="remember"/>); one the page
    /// opens by itself (Settings while the TV is not paired) is not. The screen preview refreshes only on Remote.
    /// </summary>
    private void SelectTab(string tab, bool remember = true)
    {
        tab = SmartTvConfig.NormalizeTab(tab);
        if (remember)
        {
            _ = _plugin.SavePageChoiceAsync(tab: tab);
        }

        if (_selectedTab == tab)
        {
            return;
        }

        _selectedTab = tab;
        Raise(nameof(IsWatchTab));
        Raise(nameof(IsRemoteTab));
        Raise(nameof(IsAppsTab));
        Raise(nameof(IsSettingsTab));
        UpdatePreviewLoop();
    }

    public bool WakeWithDenon
    {
        get => _wakeWithDenon;
        set { if (Set(ref _wakeWithDenon, value)) TriggerAutoSave(); }
    }

    public bool OffWithStandby
    {
        get => _offWithStandby;
        set { if (Set(ref _offWithStandby, value)) TriggerAutoSave(); }
    }

    public bool ShowSceneToast
    {
        get => _showSceneToast;
        set { if (Set(ref _showSceneToast, value)) TriggerAutoSave(); }
    }

    public bool ChannelLogos
    {
        get => _channelLogos;
        set { if (Set(ref _channelLogos, value)) TriggerAutoSave(); }
    }

    /// <summary>
    /// The entry of the Chromecast that wakes the TV over HDMI-CEC; the empty id is "Automatic". A null from the box
    /// while the list reloads is not a choice, so it changes nothing.
    /// </summary>
    public CastDeviceOption? SelectedCastDevice
    {
        get => CastDeviceOptions.FirstOrDefault(o => o.Id.Equals(_cecWakeCastDevice, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null || _isLoadingCastDevices || value.Id.Equals(_cecWakeCastDevice, StringComparison.Ordinal))
            {
                return;
            }

            _cecWakeCastDevice = value.Id;
            Raise(nameof(SelectedCastDevice));
            TriggerAutoSave();
        }
    }

    public bool SwitchToArcOnDenon
    {
        get => _switchToArcOnDenon;
        set { if (Set(ref _switchToArcOnDenon, value)) TriggerAutoSave(); }
    }

    public bool MuteSpeakersOnDenon
    {
        get => _muteSpeakersOnDenon;
        set { if (Set(ref _muteSpeakersOnDenon, value)) TriggerAutoSave(); }
    }

    public bool ScreenOffOnMusicScene
    {
        get => _screenOffOnMusicScene;
        set { if (Set(ref _screenOffOnMusicScene, value)) TriggerAutoSave(); }
    }

    public bool IsLiveConnected { get => _isLiveConnected; private set => Set(ref _isLiveConnected, value); }
    public string ConnectionBadgeText { get => _connectionBadgeText; private set => Set(ref _connectionBadgeText, value); }
    public Brush ConnectionBadgeBrush => HostTheme.Brush(_connectionBadgeTone);

    private string ConnectionBadgeTone
    {
        set
        {
            if (_connectionBadgeTone != value)
            {
                _connectionBadgeTone = value;
                Raise(nameof(ConnectionBadgeBrush));
            }
        }
    }
    public string ModelSummary { get => _modelSummary; private set => Set(ref _modelSummary, value); }
    public string NowShowingText { get => _nowShowingText; private set => Set(ref _nowShowingText, value); }

    private string _nowShowingProgramText = string.Empty;

    /// <summary>On Live TV: the program from the TV's guide and its times, for example "Deadline 17:30–18:00".</summary>
    public string NowShowingProgramText { get => _nowShowingProgramText; private set => Set(ref _nowShowingProgramText, value); }
    public string SoundOutputText { get => _soundOutputText; private set => Set(ref _soundOutputText, value); }
    public string ScreenStateText { get => _screenStateText; private set => Set(ref _screenStateText, value); }
    public bool IsScreenOff { get => _isScreenOff; private set => Set(ref _isScreenOff, value); }
    public string AppsCountBadge { get => _appsCountBadge; private set => Set(ref _appsCountBadge, value); }

    public bool IsAudioArcActive => _soundOutputRaw.Equals("external_arc", StringComparison.OrdinalIgnoreCase);
    public bool IsAudioSpeakersActive => _soundOutputRaw.Equals("tv_speaker", StringComparison.OrdinalIgnoreCase);
    public bool IsAudioOpticalActive => _soundOutputRaw.Equals("external_optical", StringComparison.OrdinalIgnoreCase);
    public bool IsAudioBluetoothActive => _soundOutputRaw.Contains("bt", StringComparison.OrdinalIgnoreCase) || _soundOutputRaw.Contains("bluetooth", StringComparison.OrdinalIgnoreCase);

    public int TvVolume
    {
        get => _tvVolume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (Set(ref _tvVolume, clamped))
            {
                Raise(nameof(TvVolumeLabel));
                if (!_suppressVolumeSend)
                {
                    ScheduleVolumeSend(clamped);
                }
            }
        }
    }

    public bool IsTvMuted
    {
        get => _isTvMuted;
        private set
        {
            if (Set(ref _isTvMuted, value))
            {
                Raise(nameof(TvVolumeLabel));
                Raise(nameof(MuteButtonLabel));
            }
        }
    }

    public string TvVolumeLabel => IsTvMuted ? $"MUTED ({TvVolume})" : $"{TvVolume}";
    public string MuteButtonLabel => IsTvMuted ? "Unmute" : "Mute";

    public string AppSearchQuery
    {
        get => _appSearchQuery;
        set
        {
            if (Set(ref _appSearchQuery, value))
            {
                RebuildFilteredApps();
            }
        }
    }

    public string KeyboardText { get => _keyboardText; set => Set(ref _keyboardText, value); }
    public string YouTubeUrlOrId { get => _youtubeUrlOrId; set => Set(ref _youtubeUrlOrId, value); }
    public string ToastText { get => _toastText; set => Set(ref _toastText, value); }
    public string BrowserUrlText { get => _browserUrlText; set => Set(ref _browserUrlText, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public ImageSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (Set(ref _previewImage, value))
            {
                Raise(nameof(HasPreviewImage));
                Raise(nameof(ShowPreviewPlaceholder));
            }
        }
    }

    public bool HasPreviewImage => _previewImage is not null;

    public bool ShowPreviewPlaceholder => _previewImage is null;

    public string PreviewStatusText
    {
        get => _previewStatusText;
        private set => Set(ref _previewStatusText, value);
    }

    public bool AutoRefreshPreview
    {
        get => _autoRefreshPreview;
        set
        {
            if (Set(ref _autoRefreshPreview, value))
            {
                UpdatePreviewLoop();
                TriggerAutoSave();
            }
        }
    }

    public void NotifyUserInteraction()
    {
        _lastUserInteractionUtc = DateTimeOffset.UtcNow;
    }

    public void SetViewVisible(bool isVisible)
    {
        _isViewVisible = isVisible;
        UpdatePreviewLoop();
        if (isVisible)
        {
            _ = LoadCastDevicesAsync();
        }
    }

    /// <summary>
    /// Makes the list match <paramref name="options"/> in order. An entry with the same id keeps its object and only
    /// its label changes; others are removed or added.
    /// </summary>
    private void SyncCastDeviceOptions(IReadOnlyList<(string Id, string Label)> options)
    {
        for (var i = CastDeviceOptions.Count - 1; i >= 0; i--)
        {
            if (!options.Any(o => o.Id.Equals(CastDeviceOptions[i].Id, StringComparison.OrdinalIgnoreCase)))
            {
                CastDeviceOptions.RemoveAt(i);
            }
        }

        for (var i = 0; i < options.Count; i++)
        {
            var (id, label) = options[i];
            var at = -1;
            for (var j = 0; j < CastDeviceOptions.Count; j++)
            {
                if (CastDeviceOptions[j].Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    at = j;
                    break;
                }
            }

            if (at < 0)
            {
                CastDeviceOptions.Insert(i, new CastDeviceOption(id, label));
                continue;
            }

            CastDeviceOptions[at].Label = label;
            if (at != i)
            {
                CastDeviceOptions.Move(at, i);
            }
        }
    }

    /// <summary>
    /// Fills "Chromecast on this TV" from the Chromecast plugin. "Automatic" names the device it picks now. A chosen
    /// device the plugin no longer knows stays in the list, so the choice is not lost.
    /// </summary>
    private async Task LoadCastDevicesAsync()
    {
        IReadOnlyList<CastDeviceChoice>? devices;
        try
        {
            devices = await _plugin.GetCastDevicesAsync();
        }
        catch (Exception)
        {
            devices = null;
        }

        RunOnUi(() =>
        {
            var chosen = _cecWakeCastDevice;
            var (auto, _) = SmartTvPlugin.PickCecCastDevice(null, devices);
            var autoName = devices?.FirstOrDefault(d => d.Id == auto)?.Name;
            var options = new List<(string Id, string Label)>
            {
                (string.Empty, autoName is null ? "Automatic" : $"Automatic ({autoName})"),
            };
            options.AddRange((devices ?? []).Select(d => (d.Id, d.IsConnected ? d.Name : $"{d.Name} (offline)")));

            // The setting may hold a name or an IP (the plugin takes those too): it shows as its device.
            var device = chosen.Length == 0
                ? null
                : devices?.FirstOrDefault(d => d.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase)) ??
                  devices?.FirstOrDefault(d => d.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase));
            if (device is not null)
            {
                chosen = device.Id;
            }
            else if (chosen.Length > 0)
            {
                options.Add((chosen, $"{chosen} (not found)"));
            }

            _isLoadingCastDevices = true;
            try
            {
                SyncCastDeviceOptions(options);
            }
            finally
            {
                _isLoadingCastDevices = false;
            }

            _cecWakeCastDevice = chosen;
            Raise(nameof(SelectedCastDevice));
        });
    }

    public void SyncFromConfig(SmartTvConfig cfg)
    {
        void Apply()
        {
            _suppressAutoSave = true;
            try
            {
                TvName = cfg.TvName;
                TvIp = cfg.TvIp;
                MacAddress = cfg.MacAddress;
                DemoMode = cfg.DemoMode;
                WakeWithDenon = cfg.WakeWithDenonPowerOn;
                OffWithStandby = cfg.TurnOffWithDenonStandby;
                ShowSceneToast = cfg.ShowSceneToastOnTv;
                ChannelLogos = cfg.ChannelLogos;
                SwitchToArcOnDenon = cfg.SwitchToArcOnDenonPowerOn;
                MuteSpeakersOnDenon = cfg.MuteTvSpeakersOnDenonPowerOn;
                ScreenOffOnMusicScene = cfg.ScreenOffOnMusicScene;
                AutoRefreshPreview = cfg.AutoRefreshPreview;
                _cecWakeCastDevice = cfg.CecWakeCastDevice.Trim();
                Raise(nameof(SelectedCastDevice));

                var isConfigured = cfg.DemoMode || (!string.IsNullOrWhiteSpace(cfg.TvIp) && !string.IsNullOrWhiteSpace(cfg.ClientKey));
                SelectTab(isConfigured ? cfg.DashboardTab : SmartTvConfig.SettingsTab, remember: false);
                SelectRemotePanel(cfg.RemotePanel, remember: false);
                if (isConfigured && StatusMessage == NotPairedHint)
                {
                    StatusMessage = string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(cfg.LastModelName) && ModelSummary == "Not paired yet")
                {
                    ModelSummary = string.IsNullOrWhiteSpace(cfg.LastWebOsVersion)
                        ? cfg.LastModelName
                        : $"{cfg.LastModelName} • {cfg.LastWebOsVersion}";
                }
            }
            finally
            {
                _suppressAutoSave = false;
            }
        }

        RunOnUi(Apply);
    }

    private void TriggerAutoSave()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _ = SaveAsync(quiet: true);
    }

    public void ApplyLiveState(LgTvLiveState state)
    {
        RunOnUi(() =>
        {
            IsLiveConnected = state.IsConnected;

            if (state.IsDemo)
            {
                ConnectionBadgeText = "DEMO SIMULATOR";
                ConnectionBadgeTone = DemoTone;
            }
            else if (state.IsConnected)
            {
                ConnectionBadgeText = $"LIVE • {state.TransportLabel}";
                ConnectionBadgeTone = LiveTone;
            }
            else if (!string.IsNullOrWhiteSpace(_plugin.Config.ClientKey))
            {
                ConnectionBadgeText = "PAIRED • STANDBY";
                ConnectionBadgeTone = MutedTone;
            }
            else if (!string.IsNullOrWhiteSpace(_plugin.Config.TvIp))
            {
                ConnectionBadgeText = "NEEDS PAIRING";
                ConnectionBadgeTone = WarnTone;
            }
            else
            {
                ConnectionBadgeText = "NOT CONFIGURED";
                ConnectionBadgeTone = MutedTone;
            }

            if (!string.IsNullOrWhiteSpace(state.ModelName) || !string.IsNullOrWhiteSpace(state.WebOsVersion))
            {
                ModelSummary = string.Join(" • ", new[] { state.ModelName, state.WebOsVersion }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            else if (!string.IsNullOrWhiteSpace(_plugin.Config.LastModelName))
            {
                ModelSummary = string.Join(" • ", new[] { _plugin.Config.LastModelName, _plugin.Config.LastWebOsVersion }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            NowShowingText = state.NowShowingSummary;
            NowShowingProgramText = state.IsLiveTv && state.CurrentProgram is { } program
                ? $"{program.Name} {program.Start.LocalDateTime:HH:mm}–{program.End.LocalDateTime:HH:mm}"
                : string.Empty;
            _soundOutputRaw = state.SoundOutput;
            SoundOutputText = state.SoundOutputLabel;
            Raise(nameof(IsAudioArcActive));
            Raise(nameof(IsAudioSpeakersActive));
            Raise(nameof(IsAudioOpticalActive));
            Raise(nameof(IsAudioBluetoothActive));

            IsScreenOff = state.IsScreenOff;
            ScreenStateText = !state.IsConnected && !state.IsDemo
                ? "Standby"
                : (state.IsScreenOff ? "OLED Screen OFF (Audio)" : "Panel ON");

            _suppressVolumeSend = true;
            try
            {
                TvVolume = state.Volume;
                IsTvMuted = state.IsMuted;
            }
            finally
            {
                _suppressVolumeSend = false;
            }

            var targetInputs = state.Inputs.Count > 0 ? state.Inputs : DefaultOfflineInputs;
            Inputs.Clear();
            foreach (var inp in targetInputs)
            {
                Inputs.Add(inp);
            }

            _allApps = state.Apps.Count > 0 ? state.Apps : DefaultOfflineApps;
            AppsCountBadge = state.Apps.Count > 0 ? $"{state.Apps.Count} installed apps" : "7 quick apps (pair to load all)";
            RebuildFilteredApps();
        });
    }

    public void SetLastActivity(string msg) =>
        RunOnUi(() => StatusMessage = $"{DateTime.Now:HH:mm:ss} • {msg}");

    private void RebuildFilteredApps()
    {
        var query = (_appSearchQuery ?? string.Empty).Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allApps
            : _allApps.Where(a =>
                a.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        FilteredApps.Clear();
        foreach (var app in filtered)
        {
            FilteredApps.Add(app);
        }
    }

    private void ScheduleVolumeSend(int targetVolume)
    {
        _volumeDebounceCts?.Cancel();
        _volumeDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _volumeDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(140, cts.Token);
                await _plugin.Client.SetVolumeAsync(targetVolume, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Newer slider drag superseded this one.
            }
            catch (Exception ex)
            {
                SetLastActivity($"⚠ Volume error: {ex.Message}");
            }
        });
    }

    private async Task DiscoverAsync()
    {
        SelectTab(SmartTvConfig.SettingsTab, remember: false);
        IsBusy = true;
        StatusMessage = "Scanning local LAN for LG webOS Smart TVs (SSDP + ARP)...";
        try
        {
            var found = await _plugin.DiscoverTvsAsync();
            if (found.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(TvIp))
                {
                    var mac = await LgTvDiscoveryService.ResolveMacAddressAsync(TvIp);
                    if (!string.IsNullOrWhiteSpace(mac))
                    {
                        MacAddress = mac;
                        StatusMessage = $"No SSDP broadcast reply, but resolved MAC {mac} for {TvIp} via ARP! Click Pair while TV is ON.";
                        return;
                    }
                }

                StatusMessage = "No LG webOS TV answered SSDP multicast (some routers filter multicast). Enter the TV IP manually and click Pair — MAC will auto-fill via ARP.";
                return;
            }

            var first = found[0];
            TvName = first.FriendlyName;
            TvIp = first.IpAddress;
            if (!string.IsNullOrWhiteSpace(first.MacAddress))
            {
                MacAddress = first.MacAddress;
            }

            await SaveAsync();
            StatusMessage = $"Discovered {first.FriendlyName} at {first.IpAddress} (MAC: {(string.IsNullOrWhiteSpace(first.MacAddress) ? "not resolved" : first.MacAddress)}). Click Pair with LG TV!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ResolveMacAsync()
    {
        if (string.IsNullOrWhiteSpace(TvIp))
        {
            StatusMessage = "Enter your TV LAN IP first to auto-resolve its MAC address.";
            return;
        }

        StatusMessage = $"Resolving hardware MAC address for {TvIp} via ARP...";
        var mac = await LgTvDiscoveryService.ResolveMacAddressAsync(TvIp);
        if (string.IsNullOrWhiteSpace(mac))
        {
            StatusMessage = $"Could not resolve MAC for {TvIp} via ARP (make sure TV is awake on the same subnet).";
            return;
        }

        MacAddress = mac;
        await SaveAsync();
        StatusMessage = $"✓ Auto-filled Wake-on-LAN MAC address: {mac}";
    }

    private async Task PairAsync(bool forcePairing)
    {
        if (string.IsNullOrWhiteSpace(TvIp))
        {
            StatusMessage = "Enter your LG webOS TV IP address first (or click Auto-Discover TV).";
            return;
        }

        IsBusy = true;
        StatusMessage = forcePairing
            ? $"Re-pairing with full permissions at {TvIp}... Accept the Denon Desktop prompt on your TV screen."
            : $"Connecting to LG TV at {TvIp} (wss://:3001)... Accept the prompt on your TV screen if shown.";
        try
        {
            StatusMessage = $"✓ {await _plugin.PairTvAsync(TvIp, forcePairing)}";
            SyncFromConfig(_plugin.Config);
            ApplyLiveState(_plugin.Client.CurrentState);
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Pairing failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshStateAsync()
    {
        IsBusy = true;
        StatusMessage = "Refreshing live LG webOS TV state, HDMI inputs & installed apps...";
        try
        {
            await _plugin.Client.RefreshStateAsync();
            ApplyLiveState(_plugin.Client.CurrentState);
            StatusMessage = $"✓ Synced live state ({_plugin.Client.CurrentState.Inputs.Count} inputs, {_plugin.Client.CurrentState.Apps.Count} installed apps).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync(bool quiet = false)
    {
        await _plugin.SaveConfigAsync(new SmartTvConfig
        {
            TvName = TvName.Trim(),
            TvIp = TvIp.Trim(),
            MacAddress = MacAddress.Trim(),
            ClientKey = _plugin.Config.ClientKey,
            DemoMode = DemoMode,
            WakeWithDenonPowerOn = WakeWithDenon,
            TurnOffWithDenonStandby = OffWithStandby,
            ShowSceneToastOnTv = ShowSceneToast,
            ChannelLogos = ChannelLogos,
            ChannelLogoOverrides = _plugin.Config.ChannelLogoOverrides,
            SwitchToArcOnDenonPowerOn = SwitchToArcOnDenon,
            MuteTvSpeakersOnDenonPowerOn = MuteSpeakersOnDenon,
            ScreenOffOnMusicScene = ScreenOffOnMusicScene,
            AutoRefreshPreview = AutoRefreshPreview,
            MovieNightInputAppId = _plugin.Config.MovieNightInputAppId,
            LastModelName = _plugin.Config.LastModelName,
            LastWebOsVersion = _plugin.Config.LastWebOsVersion,
            CecWakeCastDevice = _cecWakeCastDevice,
            DashboardTab = _plugin.Config.DashboardTab,
            RemotePanel = _plugin.Config.RemotePanel,
        });
        StatusMessage = quiet ? "✓ Coordination setting saved automatically." : "✓ Saved Smart TV settings.";
    }

    private async Task ToggleDemoAsync()
    {
        DemoMode = !DemoMode;
        await SaveAsync();
        ApplyLiveState(_plugin.Client.CurrentState);
        StatusMessage = DemoMode
            ? "Smart TV Demo Simulator active — test HDMI inputs, installed apps, Magic Remote D-Pad, OLED Screen Off & Audio Output!"
            : "Exited Demo Mode.";
    }

    private async Task RunActionAsync(string actionId)
    {
        var result = await _plugin.ExecuteActionAsync(actionId);
        StatusMessage = result.Success ? $"✓ {result.Message}" : $"⚠ {result.Message}";
    }

    private async Task SwitchInputAsync(string inputIdOrAppId)
    {
        try
        {
            await _plugin.Client.SwitchInputAsync(inputIdOrAppId);
            var label = Inputs.FirstOrDefault(i => i.AppId.Equals(inputIdOrAppId, StringComparison.OrdinalIgnoreCase) || i.Id.Equals(inputIdOrAppId, StringComparison.OrdinalIgnoreCase))?.Label ?? inputIdOrAppId;
            StatusMessage = $"✓ Switched TV input to {label}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Input switch failed: {ex.Message}";
        }
    }

    private async Task LaunchAppAsync(string appId)
    {
        NotifyUserInteraction();
        try
        {
            var title = await _plugin.Client.LaunchAppAsync(appId);
            StatusMessage = $"✓ Launched {title} on TV.";

            if (!AutoRefreshPreview && HasPreviewImage &&
                (appId.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
                 appId.Equals("com.palm.app.settings", StringComparison.OrdinalIgnoreCase)))
            {
                await Task.Delay(3200);
                await CapturePreviewAsync(showCursor: false, quiet: true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ App launch failed: {ex.Message}";
        }
    }

    private async Task SendButtonAsync(string button)
    {
        try
        {
            await _plugin.Client.SendButtonAsync(button);
            StatusMessage = $"✓ Sent remote key: {button.ToUpperInvariant()}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Remote button failed: {ex.Message}";
        }
    }

    private async Task SendMediaAsync(string mediaAction)
    {
        try
        {
            await _plugin.Client.SendMediaControlAsync(mediaAction);
            StatusMessage = $"✓ Sent TV media command: {mediaAction}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Media command failed: {ex.Message}";
        }
    }

    private async Task SetAudioOutputAsync(string outputId)
    {
        try
        {
            await _plugin.Client.SetSoundOutputAsync(outputId);
            StatusMessage = $"✓ TV audio output → {LgTvLiveState.FormatSoundOutput(outputId)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Audio output change failed: {ex.Message}";
        }
    }

    private async Task ToggleTvMuteAsync()
    {
        try
        {
            var next = !IsTvMuted;
            await _plugin.Client.SetMuteAsync(next);
            StatusMessage = next ? "✓ Muted TV audio." : "✓ Unmuted TV audio.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Mute toggle failed: {ex.Message}";
        }
    }

    private async Task StepTvVolumeAsync(bool up)
    {
        try
        {
            await _plugin.Client.StepVolumeAsync(up);
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Volume step failed: {ex.Message}";
        }
    }

    private async Task SendKeyboardAsync(bool pressEnter)
    {
        if (string.IsNullOrWhiteSpace(KeyboardText))
        {
            StatusMessage = "Type text first (while an on-screen TV search/input field is focused).";
            return;
        }

        try
        {
            var payload = KeyboardText;
            await _plugin.Client.SendKeyboardTextAsync(payload, pressEnter);
            KeyboardText = string.Empty;
            StatusMessage = pressEnter
                ? $"✓ Typed \"{payload}\" + Enter on TV keyboard."
                : $"✓ Typed \"{payload}\" on TV keyboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Keyboard input failed (make sure a text box is open on TV): {ex.Message}";
        }
    }

    private async Task BackspaceKeyboardAsync()
    {
        try
        {
            await _plugin.Client.DeleteKeyboardCharactersAsync(1);
            StatusMessage = "✓ Sent Backspace to TV keyboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Backspace failed: {ex.Message}";
        }
    }

    private async Task CastYouTubeAsync()
    {
        var input = (YouTubeUrlOrId ?? string.Empty).Trim();
        try
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                var title = await _plugin.Client.LaunchAppAsync("youtube");
                StatusMessage = $"✓ Launched {title} on TV.";
            }
            else
            {
                var vid = LgWebOsClient.ExtractYouTubeVideoId(input);
                await _plugin.Client.LaunchAppAsync("youtube", vid);
                StatusMessage = $"✓ Cast YouTube video ({vid}) to LG TV!";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ YouTube cast failed: {ex.Message}";
        }
    }

    private async Task SendCustomToastAsync()
    {
        var msg = string.IsNullOrWhiteSpace(ToastText) ? "Denon Desktop connected to LG webOS TV" : ToastText.Trim();
        try
        {
            await _plugin.Client.SendToastAsync(msg);
            StatusMessage = $"✓ Displayed TV toast: \"{msg}\"";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Toast failed: {ex.Message}";
        }
    }

    public async Task CapturePreviewAsync(bool showCursor = true, bool quiet = false)
    {
        if (Interlocked.CompareExchange(ref _isCapturingPreview, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!quiet)
            {
                PreviewStatusText = "Capturing TV screen...";
            }

            var shot = await _plugin.Client.CaptureScreenshotAsync(width: 960, height: 540, showCursor: showCursor);
            if (!shot.Success || string.IsNullOrWhiteSpace(shot.ImageBase64))
            {
                var message = shot.ErrorMessage ?? "Preview unavailable";
                RunOnUi(() =>
                {
                    PreviewStatusText = message;
                    if (!quiet)
                    {
                        StatusMessage = $"⚠ {message}";
                    }
                });
                return;
            }

            var bytes = Convert.FromBase64String(shot.ImageBase64);
            var bmp = DecodeFrozenBitmap(bytes);
            var kb = Math.Max(1, shot.ByteLength / 1024);
            var isFast = AutoRefreshPreview && (DateTimeOffset.UtcNow - _lastUserInteractionUtc) < TimeSpan.FromSeconds(6);
            var liveTag = isFast ? "Live 1s" : "Live";
            RunOnUi(() =>
            {
                PreviewImage = bmp;
                PreviewStatusText = $"{liveTag} • {DateTime.Now:HH:mm:ss} • {shot.Width}×{shot.Height} ({kb} KB)";
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() => PreviewStatusText = $"Preview failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _isCapturingPreview, 0);
        }
    }

    public async Task ClickPreviewAtNormalizedAsync(int normX, int normY)
    {
        NotifyUserInteraction();
        try
        {
            PreviewStatusText = $"Clicking ({normX}, {normY})...";
            await _plugin.Client.MovePointerToNormalizedAsync(normX, normY, recalibrate: false);
            await Task.Delay(70);
            await _plugin.Client.ClickPointerAsync();
            StatusMessage = $"✓ Clicked TV screen at ({normX}, {normY}).";

            if (!AutoRefreshPreview)
            {
                await Task.Delay(450);
                await CapturePreviewAsync(showCursor: true, quiet: true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Preview click failed: {ex.Message}";
        }
    }

    public async Task MovePointerRelativeAsync(int dx, int dy)
    {
        NotifyUserInteraction();
        try
        {
            await _plugin.Client.MovePointerRelativeAsync(dx, dy, drag: false);
        }
        catch
        {
            // Ignore transient pointer move failures during rapid gestures.
        }
    }

    public async Task ClickPointerAsync()
    {
        NotifyUserInteraction();
        try
        {
            await _plugin.Client.ClickPointerAsync();
            StatusMessage = "✓ Sent Magic Remote click.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Pointer click failed: {ex.Message}";
        }
    }

    public async Task ScrollPointerAsync(int dx, int dy)
    {
        NotifyUserInteraction();
        try
        {
            await _plugin.Client.ScrollPointerAsync(dx, dy);
            StatusMessage = dy > 0 ? "✓ Scrolled TV up." : "✓ Scrolled TV down.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Pointer scroll failed: {ex.Message}";
        }
    }

    private async Task RecalibrateCursorAsync()
    {
        NotifyUserInteraction();
        try
        {
            await _plugin.Client.MovePointerToNormalizedAsync(500, 500, recalibrate: true);
            StatusMessage = "✓ Recalibrated Magic Remote cursor to center (500, 500).";
            await CapturePreviewAsync(showCursor: true, quiet: true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Cursor recalibration failed: {ex.Message}";
        }
    }

    private async Task OpenBrowserUrlAsync()
    {
        NotifyUserInteraction();
        var raw = (BrowserUrlText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = "https://www.google.com";
        }

        try
        {
            var opened = await _plugin.Client.OpenBrowserUrlAsync(raw);
            StatusMessage = $"✓ Opened {opened} in LG webOS Browser.";
            if (!AutoRefreshPreview)
            {
                await Task.Delay(2200);
                await CapturePreviewAsync(showCursor: true, quiet: true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Open URL failed: {ex.Message}";
        }
    }

    private void UpdatePreviewLoop()
    {
        _previewLoopCts?.Cancel();
        _previewLoopCts?.Dispose();
        _previewLoopCts = null;

        if (!_autoRefreshPreview || !_isViewVisible || !IsRemoteTab)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _previewLoopCts = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (_plugin.Client.IsConnected && !DemoMode)
                {
                    await CapturePreviewAsync(showCursor: false, quiet: true);
                }

                var isActiveGesture = (DateTimeOffset.UtcNow - _lastUserInteractionUtc) < TimeSpan.FromSeconds(6);
                var delay = isActiveGesture ? TimeSpan.FromSeconds(1.0) : TimeSpan.FromSeconds(2.5);
                try
                {
                    await Task.Delay(delay, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, cts.Token);
    }

    private static BitmapImage DecodeFrozenBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }

    private bool Set<T>(ref T field, T val, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, val)) return false;
        field = val;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        return true;
    }

    private void Raise(string prop) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    private sealed class AsyncRelayCommand(Func<object?, Task> exec) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public async void Execute(object? p)
        {
            try
            {
                await exec(p);
            }
            catch
            {
                // Individual command handlers report user-visible errors via StatusMessage.
            }
        }
    }
}
