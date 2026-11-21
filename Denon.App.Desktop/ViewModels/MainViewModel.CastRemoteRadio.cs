using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Services;
using QRCoder;

namespace Denon.App.Desktop.ViewModels;

public sealed class LocalTrackItemViewModel : ObservableObject
{
    private bool _isCurrent;
    private int _position;

    public LocalTrackItemViewModel(LocalAudioTrackItem track, int position, bool isCurrent)
    {
        Track = track;
        _position = position;
        _isCurrent = isCurrent;
    }

    public LocalAudioTrackItem Track { get; }

    public string TrackId => Track.TrackId;

    public string Title => Track.Title;

    public string Artist => Track.Artist;

    public string CodecBadge => Track.CodecBadge;

    public string SizeDisplay => Track.SizeDisplay;

    public string PositionLabel => IsCurrent ? "▶" : $"{_position:00}";

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(PositionLabel));
            }
        }
    }
}

public sealed partial class MainViewModel
{
    private RadioBrowserService _radioBrowserService = null!;
    private LocalMediaJukeboxService _localJukeboxService = null!;
    private PcAudioCastService _pcAudioCastService = null!;
    private HeosEventListenerService _heosEventListener = null!;

    // Couch Phone Remote & QR
    private string _remoteWebUrl = "Starting LAN server...";
    private BitmapImage? _remoteQrImage;

    // PC Audio Cast
    private AudioOutputDeviceOption? _selectedAudioDevice;
    private PcCastFormat _selectedPcCastFormat = PcCastFormat.Mp3_320k;
    private bool _isPcCastActive;
    private string _pcCastStatusText = "Select your Windows audio output and click 'Start PC Audio Cast' to stream any Windows app wirelessly to your Denon.";

    // Local Hi-Res Jukebox
    private bool _isLocalJukeboxActive;
    private bool _isLocalShuffleEnabled;
    private YouTubeRepeatMode _localRepeatMode = YouTubeRepeatMode.Off;
    private string _localJukeboxStatusText = "Drag & drop .FLAC, .WAV, .MP3, or .M4A files/folders here — streams bit-perfect over LAN to HEOS.";

    // Radio Browser (40k+ Stations)
    private string _stationsSubTab = "Presets";
    private string _radioBrowserQuery = string.Empty;
    private string _radioBrowserCountry = string.Empty;
    private string _radioBrowserTag = string.Empty;
    private bool _radioBrowserHiResOnly;
    private bool _isSearchingRadioBrowser;
    private string _radioBrowserStatusText = "Search 40,000+ global internet radio stations by name, genre, country, or Hi-Res/FLAC.";

    // Live Push Events & Analog VU Meters (VSync-driven zero-lag state)
    private bool _isLivePushConnected;
    private bool _showVuMeters = true;
    private double _vuLeftLevel;
    private double _vuRightLevel;
    private bool _isVuLiveSignal;
    private double _simPhase;
    private bool _freezeVuForScreenshots;

    public ObservableCollection<AudioOutputDeviceOption> AudioOutputDevices { get; } = [];
    public ObservableCollection<LocalTrackItemViewModel> LocalTracks { get; } = [];
    public ObservableCollection<RadioBrowserStation> RadioBrowserResults { get; } = [];

    // Commands
    public RelayCommand CopyRemoteUrlCommand { get; private set; } = null!;
    public RelayCommand OpenRemoteInBrowserCommand { get; private set; } = null!;
    public AsyncRelayCommand TogglePcCastCommand { get; private set; } = null!;
    public RelayCommand SetPcCastFormatCommand { get; private set; } = null!;
    public RelayCommand RefreshAudioDevicesCommand { get; private set; } = null!;

    public RelayCommand AddLocalFilesCommand { get; private set; } = null!;
    public RelayCommand AddLocalFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand PlayLocalTrackCommand { get; private set; } = null!;
    public AsyncRelayCommand PlayAllLocalTracksCommand { get; private set; } = null!;
    public RelayCommand RemoveLocalTrackCommand { get; private set; } = null!;
    public RelayCommand ClearLocalTracksCommand { get; private set; } = null!;
    public RelayCommand ToggleLocalShuffleCommand { get; private set; } = null!;
    public RelayCommand CycleLocalRepeatCommand { get; private set; } = null!;

    public AsyncRelayCommand SearchRadioBrowserCommand { get; private set; } = null!;
    public AsyncRelayCommand QuickRadioTagCommand { get; private set; } = null!;
    public AsyncRelayCommand PlayRadioBrowserStationCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveRadioBrowserStationCommand { get; private set; } = null!;

    public RelayCommand ToggleVuMetersCommand { get; private set; } = null!;
    public RelayCommand SetStationsSubTabCommand { get; private set; } = null!;

    private void InitializeCastRemoteRadio(
        RadioBrowserService radioBrowserService,
        LocalMediaJukeboxService localJukeboxService,
        PcAudioCastService pcAudioCastService,
        HeosEventListenerService heosEventListener)
    {
        _radioBrowserService = radioBrowserService;
        _localJukeboxService = localJukeboxService;
        _pcAudioCastService = pcAudioCastService;
        _heosEventListener = heosEventListener;

        CopyRemoteUrlCommand = new RelayCommand(_ => CopyRemoteUrl());
        OpenRemoteInBrowserCommand = new RelayCommand(_ => OpenRemoteInBrowser());
        TogglePcCastCommand = new AsyncRelayCommand(_ => TogglePcCastAsync(), CanControlReceiver);
        SetPcCastFormatCommand = new RelayCommand(param =>
        {
            if (param is string fmt && Enum.TryParse<PcCastFormat>(fmt, ignoreCase: true, out var parsed))
            {
                SelectedPcCastFormat = parsed;
            }
        });
        RefreshAudioDevicesCommand = new RelayCommand(_ => RefreshAudioDevices());

        AddLocalFilesCommand = new RelayCommand(_ => BrowseAddLocalFiles());
        AddLocalFolderCommand = new RelayCommand(_ => BrowseAddLocalFolder());
        PlayLocalTrackCommand = new AsyncRelayCommand(PlayLocalTrackAsync, CanControlReceiver);
        PlayAllLocalTracksCommand = new AsyncRelayCommand(_ => PlayAllLocalTracksAsync(), _ => !IsBusy && LocalTracks.Count > 0);
        RemoveLocalTrackCommand = new RelayCommand(param =>
        {
            if (param is LocalTrackItemViewModel item)
            {
                _localJukeboxService.RemoveTrack(item.TrackId);
            }
        });
        ClearLocalTracksCommand = new RelayCommand(_ => _localJukeboxService.Clear());
        ToggleLocalShuffleCommand = new RelayCommand(_ => _localJukeboxService.ToggleShuffle());
        CycleLocalRepeatCommand = new RelayCommand(_ => _localJukeboxService.CycleRepeatMode());

        SearchRadioBrowserCommand = new AsyncRelayCommand(_ => SearchRadioBrowserAsync(), _ => !IsSearchingRadioBrowser);
        QuickRadioTagCommand = new AsyncRelayCommand(QuickRadioTagAsync, _ => !IsSearchingRadioBrowser);
        PlayRadioBrowserStationCommand = new AsyncRelayCommand(PlayRadioBrowserStationAsync, CanControlReceiver);
        SaveRadioBrowserStationCommand = new AsyncRelayCommand(SaveRadioBrowserStationAsync);

        ToggleVuMetersCommand = new RelayCommand(_ => ShowVuMeters = !ShowVuMeters);
        SetStationsSubTabCommand = new RelayCommand(param =>
        {
            if (param is string tab && !string.IsNullOrWhiteSpace(tab))
            {
                StationsSubTab = tab;
                if (tab == "Explorer" && RadioBrowserResults.Count == 0 && !IsSearchingRadioBrowser)
                {
                    _ = SearchRadioBrowserAsync();
                }
            }
        });

        _localJukeboxService.JukeboxChanged += snap => _ = _dispatcher.InvokeAsync(() => ApplyLocalJukeboxSnapshot(snap));
        _pcAudioCastService.CastStateChanged += (active, msg) => _ = _dispatcher.InvokeAsync(() =>
        {
            IsPcCastActive = active;
            PcCastStatusText = msg;
            ApplyStateSnapshot(_stateStore.Snapshot);
        });
        _heosEventListener.PushConnectionChanged += connected => _ = _dispatcher.InvokeAsync(() => IsLivePushConnected = connected);
        IsLivePushConnected = _heosEventListener.IsPushConnected;

        _relayServer.StatusChanged += snap => _ = _dispatcher.InvokeAsync(() => UpdateRemoteQrFromRelay(snap));
        UpdateRemoteQrFromRelay(_relayServer.CurrentStatus);

        RefreshAudioDevices();
        ApplyLocalJukeboxSnapshot(_localJukeboxService.Snapshot);
    }

    public string RemoteWebUrl
    {
        get => _remoteWebUrl;
        private set => SetProperty(ref _remoteWebUrl, value);
    }

    public BitmapImage? RemoteQrImage
    {
        get => _remoteQrImage;
        private set => SetProperty(ref _remoteQrImage, value);
    }

    public AudioOutputDeviceOption? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set => SetProperty(ref _selectedAudioDevice, value);
    }

    public PcCastFormat SelectedPcCastFormat
    {
        get => _selectedPcCastFormat;
        set
        {
            if (SetProperty(ref _selectedPcCastFormat, value))
            {
                OnPropertyChanged(nameof(SelectedPcCastFormatName));
            }
        }
    }

    public string SelectedPcCastFormatName => SelectedPcCastFormat.ToString();

    public bool IsPcCastActive
    {
        get => _isPcCastActive;
        private set
        {
            if (SetProperty(ref _isPcCastActive, value))
            {
                OnPropertyChanged(nameof(PcCastButtonText));
            }
        }
    }

    public string PcCastButtonText => IsPcCastActive ? "⏹ Stop PC Audio Cast" : "▶ Start PC Audio Cast";

    public string PcCastStatusText
    {
        get => _pcCastStatusText;
        private set => SetProperty(ref _pcCastStatusText, value);
    }

    public bool IsLocalJukeboxActive
    {
        get => _isLocalJukeboxActive;
        private set => SetProperty(ref _isLocalJukeboxActive, value);
    }

    public bool IsLocalShuffleEnabled
    {
        get => _isLocalShuffleEnabled;
        private set => SetProperty(ref _isLocalShuffleEnabled, value);
    }

    public YouTubeRepeatMode LocalRepeatMode
    {
        get => _localRepeatMode;
        private set
        {
            if (SetProperty(ref _localRepeatMode, value))
            {
                OnPropertyChanged(nameof(LocalRepeatLabel));
            }
        }
    }

    public string LocalRepeatLabel => LocalRepeatMode switch
    {
        YouTubeRepeatMode.All => "Repeat: All",
        YouTubeRepeatMode.One => "Repeat: One",
        _ => "Repeat: Off",
    };

    public string LocalTrackCountLabel => LocalTracks.Count == 1 ? "1 file" : $"{LocalTracks.Count} files";

    public string LocalJukeboxStatusText
    {
        get => _localJukeboxStatusText;
        private set => SetProperty(ref _localJukeboxStatusText, value);
    }

    public string StationsSubTab
    {
        get => _stationsSubTab;
        set => SetProperty(ref _stationsSubTab, value);
    }

    public string RadioBrowserQuery
    {
        get => _radioBrowserQuery;
        set => SetProperty(ref _radioBrowserQuery, value);
    }

    public string RadioBrowserCountry
    {
        get => _radioBrowserCountry;
        set => SetProperty(ref _radioBrowserCountry, value);
    }

    public string RadioBrowserTag
    {
        get => _radioBrowserTag;
        set => SetProperty(ref _radioBrowserTag, value);
    }

    public bool RadioBrowserHiResOnly
    {
        get => _radioBrowserHiResOnly;
        set => SetProperty(ref _radioBrowserHiResOnly, value);
    }

    public bool IsSearchingRadioBrowser
    {
        get => _isSearchingRadioBrowser;
        private set
        {
            if (SetProperty(ref _isSearchingRadioBrowser, value))
            {
                SearchRadioBrowserCommand?.NotifyCanExecuteChanged();
                QuickRadioTagCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    public string RadioBrowserStatusText
    {
        get => _radioBrowserStatusText;
        private set => SetProperty(ref _radioBrowserStatusText, value);
    }

    public bool IsLivePushConnected
    {
        get => _isLivePushConnected;
        private set => SetProperty(ref _isLivePushConnected, value);
    }

    public bool ShowVuMeters
    {
        get => _showVuMeters;
        set
        {
            if (SetProperty(ref _showVuMeters, value))
            {
                OnPropertyChanged(nameof(VuToggleLabel));
            }
        }
    }

    public string VuToggleLabel => ShowVuMeters ? "VU Meters: ON" : "VU Meters: OFF";

    public double VuLeftLevel
    {
        get => _vuLeftLevel;
        private set => SetProperty(ref _vuLeftLevel, value);
    }

    public double VuRightLevel
    {
        get => _vuRightLevel;
        private set => SetProperty(ref _vuRightLevel, value);
    }

    public bool IsVuLiveSignal
    {
        get => _isVuLiveSignal;
        private set => SetProperty(ref _isVuLiveSignal, value);
    }

    public void HandleDroppedLocalPaths(string[] paths)
    {
        var added = _localJukeboxService.AddPaths(paths);
        LocalJukeboxStatusText = added > 0
            ? $"Added {added} local audio file(s). Click any track or 'Play All' to stream to your Denon."
            : "No supported audio files (.flac, .wav, .mp3, .m4a, .aac, .ogg, .opus) found in drop.";
    }

    private void UpdateRemoteQrFromRelay(RelayStatusSnapshot snap)
    {
        if (!snap.IsRunning || string.IsNullOrWhiteSpace(snap.EndpointBaseUrl))
        {
            return;
        }

        var url = snap.EndpointBaseUrl.TrimEnd('/') + "/";
        if (string.Equals(RemoteWebUrl, url, StringComparison.OrdinalIgnoreCase) && RemoteQrImage is not null)
        {
            return;
        }

        RemoteWebUrl = url;
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            var pngQr = new PngByteQRCode(data);
            var bytes = pngQr.GetGraphic(8, [0, 212, 255], [10, 14, 22]);

            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }

            bmp.Freeze();
            RemoteQrImage = bmp;
        }
        catch
        {
        }
    }

    private void CopyRemoteUrl()
    {
        if (string.IsNullOrWhiteSpace(RemoteWebUrl) || !RemoteWebUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(RemoteWebUrl);
            StatusMessage = $"Copied Phone Remote URL ({RemoteWebUrl}) to clipboard.";
        }
        catch
        {
        }
    }

    private void OpenRemoteInBrowser()
    {
        if (string.IsNullOrWhiteSpace(RemoteWebUrl) || !RemoteWebUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(RemoteWebUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void RefreshAudioDevices()
    {
        var previousId = SelectedAudioDevice?.DeviceId;
        AudioOutputDevices.Clear();
        foreach (var dev in _pcAudioCastService.EnumerateOutputDevices())
        {
            AudioOutputDevices.Add(dev);
        }

        SelectedAudioDevice = AudioOutputDevices.FirstOrDefault(d => string.Equals(d.DeviceId, previousId, StringComparison.OrdinalIgnoreCase))
                              ?? AudioOutputDevices.FirstOrDefault();
    }

    private async Task TogglePcCastAsync()
    {
        if (IsPcCastActive)
        {
            await _pcAudioCastService.StopCastingAsync();
            try
            {
                await _receiverController.SetPlayStateAsync("stop");
            }
            catch
            {
            }

            StatusMessage = "PC Audio Cast stopped.";
            return;
        }

        try
        {
            _youtubeQueueService.StopSession();
            _localJukeboxService.StopSession();
            var dev = SelectedAudioDevice;
            await _pcAudioCastService.StartCastingAsync(
                dev?.DeviceId,
                dev?.FriendlyName ?? "Default System Output",
                SelectedPcCastFormat,
                useLowVolume: false,
                UnmuteBeforePlay);
            StatusMessage = "Casting live PC Audio to Denon AVR.";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
            PcCastStatusText = $"PC Audio Cast failed: {ex.Message}";
        }
    }

    private void BrowseAddLocalFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add Local Audio Files to Hi-Res Jukebox",
            Multiselect = true,
            Filter = "Audio Files (*.flac;*.wav;*.mp3;*.m4a;*.aac;*.ogg;*.opus)|*.flac;*.wav;*.mp3;*.m4a;*.aac;*.ogg;*.opus|All Files (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
        {
            HandleDroppedLocalPaths(dialog.FileNames);
        }
    }

    private void BrowseAddLocalFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a music folder to load into the Hi-Res Jukebox",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            HandleDroppedLocalPaths([dialog.SelectedPath]);
        }
    }

    private async Task PlayLocalTrackAsync(object? parameter)
    {
        if (parameter is not LocalTrackItemViewModel item)
        {
            return;
        }

        try
        {
            _youtubeQueueService.StopSession();
            await _pcAudioCastService.StopCastingAsync();
            LocalJukeboxStatusText = $"Streaming \"{item.Title}\" ({item.CodecBadge}) to Denon AVR...";
            await _localJukeboxService.PlayTrackAsync(item.TrackId, useLowVolume: false, UnmuteBeforePlay);
            LocalJukeboxStatusText = $"Playing local {item.CodecBadge}: {item.Title}";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LocalJukeboxStatusText = $"Local playback failed: {ex.Message}";
        }
    }

    private async Task PlayAllLocalTracksAsync()
    {
        if (LocalTracks.Count == 0)
        {
            return;
        }

        try
        {
            _youtubeQueueService.StopSession();
            await _pcAudioCastService.StopCastingAsync();
            await _localJukeboxService.PlayAllAsync(useLowVolume: false, UnmuteBeforePlay);
            LocalJukeboxStatusText = $"Playing {LocalTracks.Count} local audio tracks.";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LocalJukeboxStatusText = $"Local playback failed: {ex.Message}";
        }
    }

    private void ApplyLocalJukeboxSnapshot(LocalJukeboxSnapshot snap)
    {
        IsLocalJukeboxActive = snap.IsActive && snap.CurrentTrack is not null;
        IsLocalShuffleEnabled = snap.IsShuffleEnabled;
        LocalRepeatMode = snap.RepeatMode;

        LocalTracks.Clear();
        for (var i = 0; i < snap.Tracks.Count; i++)
        {
            LocalTracks.Add(new LocalTrackItemViewModel(snap.Tracks[i], i + 1, snap.IsActive && i == snap.CurrentIndex));
        }

        OnPropertyChanged(nameof(LocalTrackCountLabel));
        PlayAllLocalTracksCommand?.NotifyCanExecuteChanged();
        ApplyStateSnapshot(_stateStore.Snapshot);
    }

    private async Task SearchRadioBrowserAsync()
    {
        IsSearchingRadioBrowser = true;
        RadioBrowserStatusText = "Querying Radio Browser global directory (40,000+ stations)...";

        try
        {
            var results = await _radioBrowserService.SearchStationsAsync(
                RadioBrowserQuery,
                RadioBrowserTag,
                RadioBrowserCountry,
                RadioBrowserHiResOnly,
                limit: 30);

            RadioBrowserResults.Clear();
            foreach (var st in results)
            {
                RadioBrowserResults.Add(st);
            }

            RadioBrowserStatusText = results.Count > 0
                ? $"Found {results.Count} live global station(s). Click ▶ Play or + Save to add to your presets."
                : "No matching stations found. Try broader keywords or clear the country/genre filter.";
        }
        catch (Exception ex)
        {
            RadioBrowserStatusText = $"Radio Explorer error: {ex.Message}";
        }
        finally
        {
            IsSearchingRadioBrowser = false;
        }
    }

    private async Task QuickRadioTagAsync(object? parameter)
    {
        if (parameter is not string preset)
        {
            return;
        }

        RadioBrowserQuery = string.Empty;
        if (preset.Equals("FLAC", StringComparison.OrdinalIgnoreCase))
        {
            RadioBrowserTag = string.Empty;
            RadioBrowserCountry = string.Empty;
            RadioBrowserHiResOnly = true;
        }
        else if (preset.Equals("Denmark", StringComparison.OrdinalIgnoreCase))
        {
            RadioBrowserTag = string.Empty;
            RadioBrowserCountry = "Denmark";
            RadioBrowserHiResOnly = false;
        }
        else
        {
            RadioBrowserTag = preset;
            RadioBrowserCountry = string.Empty;
            RadioBrowserHiResOnly = false;
        }

        await SearchRadioBrowserAsync();
    }

    private async Task PlayRadioBrowserStationAsync(object? parameter)
    {
        if (parameter is not RadioBrowserStation station)
        {
            return;
        }

        await ExecuteReceiverActionAsync(
            () => _receiverController.PlayCustomUrlAsync(station.UrlResolved, useLowVolume: false, UnmuteBeforePlay),
            $"Streaming {station.Name} ({station.CodecBadge})");
    }

    private async Task SaveRadioBrowserStationAsync(object? parameter)
    {
        if (parameter is not RadioBrowserStation station)
        {
            return;
        }

        var alias = SlugNormalizer.Normalize(station.Name);
        if (StationPresets.Any(s => string.Equals(s.Url, station.UrlResolved, StringComparison.OrdinalIgnoreCase)))
        {
            RadioBrowserStatusText = $"'{station.Name}' is already in your saved stations.";
            return;
        }

        StationPresets.Add(new StationPresetEditor
        {
            Alias = string.IsNullOrWhiteSpace(alias) ? $"radio-{StationPresets.Count + 1}" : alias,
            DisplayName = station.Name,
            Url = station.UrlResolved,
        });

        FilteredStationPresets.Refresh();
        await SaveSettingsAsync();
        RadioBrowserStatusText = $"Saved '{station.Name}' to your preset stations!";
    }

    /// <summary>
    /// Computes the next VU needle position on DWM VSync (60-144 Hz) with sub-10ms attack ballistics
    /// and zero WPF PropertyChanged/binding allocations per frame.
    /// </summary>
    public (double Left, double Right, bool IsLive) StepVuMeterFrame(double deltaSeconds)
    {
        if (_freezeVuForScreenshots)
        {
            return (_vuLeftLevel, _vuRightLevel, _isVuLiveSignal);
        }

        var dt = Math.Clamp(deltaSeconds, 0.001, 0.08);
        var live = _pcAudioCastService.GetLatestMeterSample();

        double targetL;
        double targetR;

        if (live.IsLiveSignal)
        {
            targetL = live.Left;
            targetR = live.Right;
        }
        else if (IsActiveAndPlaying)
        {
            _simPhase += dt * 7.2;
            var volScale = Math.Clamp(0.30 + (LiveVolume / 98.0) * 0.66, 0.28, 0.94);
            var grooveL = 0.50 + (0.28 * Math.Sin(_simPhase * 1.4)) + (0.17 * Math.Sin(_simPhase * 3.9)) + (0.09 * Math.Cos(_simPhase * 6.7));
            var grooveR = 0.48 + (0.27 * Math.Sin((_simPhase * 1.4) + 0.52)) + (0.18 * Math.Cos(_simPhase * 3.3)) + (0.09 * Math.Sin(_simPhase * 5.9));
            targetL = Math.Clamp(grooveL * volScale, 0.05, 0.95);
            targetR = Math.Clamp(grooveR * volScale, 0.05, 0.95);
        }
        else
        {
            targetL = 0;
            targetR = 0;
        }

        // Frame-rate independent exponential ballistics: ultra-fast 8ms attack, crisp 85ms decay
        var alphaAttack = 1.0 - Math.Exp(-dt / 0.008);
        var alphaRelease = 1.0 - Math.Exp(-dt / 0.085);

        _vuLeftLevel += (targetL - _vuLeftLevel) * (targetL > _vuLeftLevel ? alphaAttack : alphaRelease);
        _vuRightLevel += (targetR - _vuRightLevel) * (targetR > _vuRightLevel ? alphaAttack : alphaRelease);

        if (_vuLeftLevel < 0.003) _vuLeftLevel = 0;
        if (_vuRightLevel < 0.003) _vuRightLevel = 0;

        _isVuLiveSignal = live.IsLiveSignal;
        return (_vuLeftLevel, _vuRightLevel, _isVuLiveSignal);
    }

    public void LoadShowcaseStateForScreenshots()
    {
        var demoPlayer = new ReceiverPlayerInfo(
            Name: "Denon AVR-X2400H",
            PlayerId: 1001,
            Model: "Denon AVR-X2400H",
            Version: "3.139.175",
            IpAddress: "192.168.1.3",
            Network: "wired",
            LineOut: 0,
            Serial: "DEMO");

        var demoStatus = new ReceiverStatus(
            Player: demoPlayer,
            PlayState: "play",
            Volume: 42,
            IsMuted: false,
            NowPlaying: new NowPlayingMedia("station", "Radio Paradise — Main Mix", "Eclectic Hi-Fi Radio", "FLAC / 320 kbps AAC", string.Empty, string.Empty, string.Empty, null, null),
            RetrievedAt: DateTimeOffset.Now,
            IsPoweredOn: true,
            IsMainZoneOn: true,
            InputSource: "NET",
            SoundMode: "STEREO",
            EcoMode: "AUTO",
            SleepTimerMinutes: 60,
            IsZone2On: false,
            MaxVolume: 98,
            VolumeExact: 42.0,
            ActiveStationAlias: StationPresets.FirstOrDefault()?.Alias);

        ApplyStateSnapshot(new ReceiverStateSnapshot
        {
            IsConnected = true,
            Status = demoStatus,
            LastSuccessfulRefresh = DateTimeOffset.Now,
            LastAttemptedRefresh = DateTimeOffset.Now,
        });

        _hasLoadedAcousticsOnce = true;
        _freezeVuForScreenshots = true;

        IsLivePushConnected = true;
        ShowVuMeters = true;
        VuLeftLevel = 0.68;
        VuRightLevel = 0.74;
        IsVuLiveSignal = true;

        // Populate Acoustics showcase
        ApplyAcousticStatus(new AcousticStatus(
            MultEq: "AUDYSSEY",
            DynamicEq: true,
            DynamicVolume: "LIT",
            Restorer: "MED",
            Dimmer: "DIM",
            SubwooferAdjustEnabled: true,
            SubwooferTrimDb: 2,
            DialogAdjustEnabled: true,
            DialogTrimDb: 1,
            ToneControlEnabled: false,
            BassDb: 0,
            TrebleDb: 0,
            RetrievedAt: DateTimeOffset.Now));
        AcousticsStatusText = "Synced from Denon AVR-X2400H (Audyssey MultEQ Active)";

        // Populate YouTube Music & Queue showcase
        YouTubeSearchInput = "Daft Punk Random Access Memories";
        YouTubeStatusText = "Found 5 YouTube Music results • Direct LAN 320 kbps MP3 Relay ready.";
        YouTubeSearchResults.Clear();
        YouTubeTrackItem[] demoTracks =
        [
            new("5NV6Rdv1a3I", "Daft Punk - Get Lucky (Official Audio) ft. Pharrell Williams", "Daft Punk", TimeSpan.FromSeconds(248), string.Empty, "https://www.youtube.com/watch?v=5NV6Rdv1a3I"),
            new("a5uQMwRMHcs", "Daft Punk - Instant Crush (Video) ft. Julian Casablancas", "Daft Punk", TimeSpan.FromSeconds(339), string.Empty, "https://www.youtube.com/watch?v=a5uQMwRMHcs"),
            new("zhlGrammar1", "Daft Punk - Giorgio by Moroder (Official Audio)", "Daft Punk", TimeSpan.FromSeconds(544), string.Empty, "https://www.youtube.com/watch?v=zhlGrammar1"),
            new("NF-kLy44Hls", "Daft Punk - Lose Yourself to Dance (Official Audio)", "Daft Punk", TimeSpan.FromSeconds(353), string.Empty, "https://www.youtube.com/watch?v=NF-kLy44Hls"),
            new("6QVtHogFrI0", "Daft Punk - Touch (Official Audio) ft. Paul Williams", "Daft Punk", TimeSpan.FromSeconds(498), string.Empty, "https://www.youtube.com/watch?v=6QVtHogFrI0"),
        ];
        foreach (var t in demoTracks)
        {
            YouTubeSearchResults.Add(t);
        }

        YouTubeQueue.Clear();
        for (var i = 0; i < 4; i++)
        {
            YouTubeQueue.Add(new YouTubeQueueItemViewModel(demoTracks[i], i + 1, isCurrent: i == 0));
        }

        OnPropertyChanged(nameof(YouTubeQueueCountLabel));

        // Populate Phone Remote QR + Local FLAC showcase
        UpdateRemoteQrFromRelay(new RelayStatusSnapshot(
            IsRunning: true,
            EndpointBaseUrl: "http://192.168.1.31:18925",
            TranscodeMode: "FFmpeg MP3 320k",
            HasFfmpeg: true,
            FfmpegPath: "ffmpeg.exe",
            StreamState: "Listening",
            ActiveTrackTitle: null,
            BytesStreamed: 0,
            LastError: null));
        ApplyRelayStatus(new RelayStatusSnapshot(
            IsRunning: true,
            EndpointBaseUrl: "http://192.168.1.31:18925",
            TranscodeMode: "FFmpeg MP3 320k",
            HasFfmpeg: true,
            FfmpegPath: "ffmpeg.exe",
            StreamState: "Streaming to Denon (MP3 320k)",
            ActiveTrackTitle: "Daft Punk - Get Lucky",
            BytesStreamed: 4_820_000,
            LastError: null));

        LocalTracks.Clear();
        LocalTracks.Add(new LocalTrackItemViewModel(new LocalAudioTrackItem("1", @"C:\Music\01 - Give Life Back to Music.flac", "01 - Give Life Back to Music.flac", "Give Life Back to Music", "Daft Punk", ".flac", 32_400_000), 1, true));
        LocalTracks.Add(new LocalTrackItemViewModel(new LocalAudioTrackItem("2", @"C:\Music\02 - So What.flac", "02 - So What.flac", "So What (24-bit Master)", "Miles Davis", ".flac", 48_900_000), 2, false));
        LocalTracks.Add(new LocalTrackItemViewModel(new LocalAudioTrackItem("3", @"C:\Music\03 - Time.wav", "03 - Time.wav", "Time (Inception OST)", "Hans Zimmer", ".wav", 45_100_000), 3, false));
        OnPropertyChanged(nameof(LocalTrackCountLabel));

        AudioOutputDevices.Clear();
        AudioOutputDevices.Add(new AudioOutputDeviceOption(string.Empty, "Default System Output (All PC Audio)", true));
        AudioOutputDevices.Add(new AudioOutputDeviceOption("dac", "Speakers (USB Hi-Fi DAC)", false));
        AudioOutputDevices.Add(new AudioOutputDeviceOption("hp", "Headphones (Studio Reference)", false));
        SelectedAudioDevice = AudioOutputDevices[0];

        // Populate Global Radio Explorer showcase
        RadioBrowserResults.Clear();
        RadioBrowserResults.Add(new RadioBrowserStation("1", "Radio Paradise — Main Mix (FLAC)", "http://stream.radioparadise.com/flac", string.Empty, "United States", "eclectic, rock, indie", "FLAC", 1411, 18420));
        RadioBrowserResults.Add(new RadioBrowserStation("2", "Classic Vinyl HD", "https://icecast.walmradio.com:8443/classic", string.Empty, "United States", "jazz, classic, hi-fi", "MP3", 320, 9640));
        RadioBrowserResults.Add(new RadioBrowserStation("3", "SomaFM Groove Salad", "http://ice2.somafm.com/groovesalad-256-mp3", string.Empty, "United States", "ambient, chillout, downtempo", "MP3", 256, 14210));
        RadioBrowserResults.Add(new RadioBrowserStation("4", "DR P8 Jazz", "http://live-icy.dr.dk/A/A22L.mp3", string.Empty, "Denmark", "jazz, danish public radio", "MP3", 192, 5320));
        RadioBrowserResults.Add(new RadioBrowserStation("5", "Nightride FM", "https://stream.nightride.fm/nightride.mp3", string.Empty, "Germany", "synthwave, retrowave, electronic", "MP3", 320, 7890));
        RadioBrowserStatusText = "Showing 5 of 40,000+ live global stations — click ▶ Play or + Save to add to presets.";
    }
}
