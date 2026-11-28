using System.Collections.ObjectModel;
using System.Windows.Threading;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Services;

namespace Denon.App.Desktop.ViewModels;

public sealed record RoomMicDeviceChoice(string DeviceId, string FriendlyName);

public sealed partial class MainViewModel
{
    private LyricsService _lyricsService = null!;
    private AcousticSyncCalibrationService _calibrationService = null!;
    private DispatcherTimer? _lyricsSyncTimer;
    private LyricsDocument? _currentLyricsDoc;
    private string _lastLyricsTrackKey = string.Empty;
    private DateTimeOffset _nonYouTubeTrackStartedAt = DateTimeOffset.Now;

    // 31-Band RTA Spectrum Analyzer & Meter Mode ("VU", "RTA", "Both", "Off")
    private string _meterDisplayMode = "VU";
    private double _rtaSimPhase;

    // Synced Live Lyrics
    private bool _showLiveLyrics;
    private string _currentLyricLine = string.Empty;
    private string _nextLyricLine = string.Empty;
    private string _lyricsStatusBadge = "LRCLIB";

    // Acoustic Lip-Sync Auto-Calibration
    private bool _isCalibratingSync;
    private string _acousticCalibrationStatus = "Play a 1,750 Hz chirp through your Denon AVR and measure exact speaker delay via PC mic.";

    // Room Mic Meter (Phase 3 opt-in ROOM mode for TV/ARC, HDMI & Chromecast)
    // False until the window reports that it is shown: a start in the tray must not open the microphone.
    private bool _isWindowVisibleForMeters;
    private bool _isLoadingRoomMicSettings;
    private bool _settingsAllowRoomMicMeter;
    private string _settingsMeterMicDeviceId = string.Empty;
    private bool _isRoomMicListening;

    public ObservableCollection<RoomMicDeviceChoice> RoomMicCaptureDevices { get; } = [];

    public RelayCommand CycleMeterDisplayModeCommand { get; private set; } = null!;
    public RelayCommand ToggleLiveLyricsCommand { get; private set; } = null!;
    public AsyncRelayCommand RunAcousticCalibrationCommand { get; private set; } = null!;
    public RelayCommand RefreshRoomMicDevicesCommand { get; private set; } = null!;

    public string MeterDisplayMode
    {
        get => _meterDisplayMode;
        set
        {
            if (SetProperty(ref _meterDisplayMode, value))
            {
                OnPropertyChanged(nameof(ShowAnalogVu));
                OnPropertyChanged(nameof(ShowRtaSpectrum));
                OnPropertyChanged(nameof(IsAnyAudioMeterVisible));
                OnPropertyChanged(nameof(MeterModeButtonLabel));
                UpdateMeterCaptureRequest();
            }
        }
    }

    public bool SettingsAllowRoomMicMeter
    {
        get => _settingsAllowRoomMicMeter;
        set
        {
            if (SetProperty(ref _settingsAllowRoomMicMeter, value))
            {
                OnPropertyChanged(nameof(RoomMicStatusHint));
                if (!_isLoadingRoomMicSettings)
                {
                    _ = SaveRoomMicSettingAsync(ui => ui.AllowRoomMicMeter = value);
                }
            }
        }
    }

    public string SettingsMeterMicDeviceId
    {
        get => _settingsMeterMicDeviceId;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _settingsMeterMicDeviceId, normalized) && !_isLoadingRoomMicSettings)
            {
                _ = SaveRoomMicSettingAsync(ui => ui.MeterMicDeviceId = normalized);
            }
        }
    }

    public bool IsRoomMicListening
    {
        get => _isRoomMicListening;
        private set
        {
            if (SetProperty(ref _isRoomMicListening, value))
            {
                OnPropertyChanged(nameof(RoomMicStatusHint));
            }
        }
    }

    public string RoomMicStatusHint => !SettingsAllowRoomMicMeter
        ? "Off by default. Turn on to drive VU & RTA meters (and Hue Music Sync) from a PC microphone or Line-In for TV, HDMI & Chromecast audio."
        : IsRoomMicListening
            ? "ROOM mode active: the microphone is listening now. It closes on standby, mute or PC audio, and when the meters are hidden unless Hue Music Sync is on."
            : "ROOM mode armed: listens only on TV/HDMI/Cast sources while the meters are visible or Hue Music Sync is on. Closes at once on standby or mute.";

    public void SetWindowVisibleForMeters(bool isVisible)
    {
        if (_isWindowVisibleForMeters == isVisible)
        {
            return;
        }

        _isWindowVisibleForMeters = isVisible;
        UpdateMeterCaptureRequest();
    }

    internal void UpdateMeterCaptureRequest()
    {
        var shouldCapture = _isWindowVisibleForMeters &&
                            IsAnyAudioMeterVisible &&
                            string.Equals(ActiveSection, "Control", StringComparison.OrdinalIgnoreCase);
        _liveAudioAnalyzer?.SetVuCaptureRequested(shouldCapture);
    }

    private void LoadRoomMicSettings(UiSettings ui)
    {
        _isLoadingRoomMicSettings = true;
        try
        {
            SettingsAllowRoomMicMeter = ui.AllowRoomMicMeter;
            SettingsMeterMicDeviceId = ui.MeterMicDeviceId ?? string.Empty;
            _liveAudioAnalyzer?.SetRoomMicDeviceId(SettingsMeterMicDeviceId);
            _liveAudioAnalyzer?.SetRoomMicEnabled(SettingsAllowRoomMicMeter);
            EnsureSelectedRoomMicDeviceListed(SettingsMeterMicDeviceId);
        }
        finally
        {
            _isLoadingRoomMicSettings = false;
        }
    }

    private void RefreshRoomMicCaptureDevices()
    {
        var selectedId = SettingsMeterMicDeviceId;

        // Clearing the list makes the ComboBox write null back through its SelectedValue binding. Hold the
        // setter's save while the list is rebuilt, then select the saved device again.
        var wasLoading = _isLoadingRoomMicSettings;
        _isLoadingRoomMicSettings = true;
        try
        {
            RoomMicCaptureDevices.Clear();

            if (_liveAudioAnalyzer is not null)
            {
                foreach (var device in _liveAudioAnalyzer.GetCaptureDevices())
                {
                    RoomMicCaptureDevices.Add(new RoomMicDeviceChoice(device.DeviceId, device.FriendlyName));
                }
            }

            if (RoomMicCaptureDevices.Count == 0)
            {
                RoomMicCaptureDevices.Add(new RoomMicDeviceChoice(string.Empty, "Default Microphone / Line-In"));
            }

            EnsureSelectedRoomMicDeviceListed(selectedId);
            _settingsMeterMicDeviceId = selectedId;
            OnPropertyChanged(nameof(SettingsMeterMicDeviceId));
        }
        finally
        {
            _isLoadingRoomMicSettings = wasLoading;
        }
    }

    private void EnsureSelectedRoomMicDeviceListed(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        if (!RoomMicCaptureDevices.Any(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
        {
            RoomMicCaptureDevices.Add(new RoomMicDeviceChoice(deviceId, $"Saved microphone ({deviceId[..Math.Min(12, deviceId.Length)]}…)"));
        }
    }

    private async Task SaveRoomMicSettingAsync(Action<UiSettings> mutator)
    {
        try
        {
            _liveAudioAnalyzer?.SetRoomMicDeviceId(SettingsMeterMicDeviceId);
            _liveAudioAnalyzer?.SetRoomMicEnabled(SettingsAllowRoomMicMeter);
            if (!await _settingsStore.SaveUiStateAsync(mutator))
            {
                StatusMessage = "Room Mic setting updated in memory. It is saved with your next Save & Sync (the settings file is being recovered).";
            }
        }
        catch (Exception exception)
        {
            LastError = $"Could not save Room Mic setting: {exception.Message}";
        }
    }

    public bool ShowAnalogVu => ShowVuMeters && MeterDisplayMode is "VU" or "Both";

    public bool ShowRtaSpectrum => ShowVuMeters && MeterDisplayMode is "RTA" or "Both";

    public bool IsAnyAudioMeterVisible => ShowAnalogVu || ShowRtaSpectrum;

    public string MeterModeButtonLabel => MeterDisplayMode switch
    {
        "RTA" => "RTA 31",
        "Both" => "VU + RTA",
        _ => "VU Meter",
    };

    public bool ShowLiveLyrics
    {
        get => _showLiveLyrics;
        set
        {
            if (SetProperty(ref _showLiveLyrics, value))
            {
                OnPropertyChanged(nameof(LyricsToggleLabel));
                if (value)
                {
                    _lyricsSyncTimer?.Start();
                    _ = RefreshLyricsForCurrentTrackAsync(force: true);
                }
                else
                {
                    _lyricsSyncTimer?.Stop();
                }
            }
        }
    }

    public string LyricsToggleLabel => ShowLiveLyrics ? "Lyrics ON" : "Lyrics";

    public string CurrentLyricLine
    {
        get => _currentLyricLine;
        private set => SetProperty(ref _currentLyricLine, value);
    }

    public string NextLyricLine
    {
        get => _nextLyricLine;
        private set => SetProperty(ref _nextLyricLine, value);
    }

    public string LyricsStatusBadge
    {
        get => _lyricsStatusBadge;
        private set => SetProperty(ref _lyricsStatusBadge, value);
    }

    public bool IsCalibratingSync
    {
        get => _isCalibratingSync;
        private set
        {
            if (SetProperty(ref _isCalibratingSync, value))
            {
                RunAcousticCalibrationCommand?.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CalibrateSyncButtonLabel));
            }
        }
    }

    public string CalibrateSyncButtonLabel => IsCalibratingSync ? "Calibrating (Listen)..." : "Auto-Calibrate Mic";

    public string AcousticCalibrationStatus
    {
        get => _acousticCalibrationStatus;
        private set => SetProperty(ref _acousticCalibrationStatus, value);
    }

    private void InitializeStudioAndSync(
        LyricsService lyricsService,
        AcousticSyncCalibrationService calibrationService)
    {
        _lyricsService = lyricsService;
        _calibrationService = calibrationService;

        CycleMeterDisplayModeCommand = new RelayCommand(_ =>
        {
            if (!ShowVuMeters)
            {
                ShowVuMeters = true;
                MeterDisplayMode = "VU";
                return;
            }

            MeterDisplayMode = MeterDisplayMode switch
            {
                "VU" => "RTA",
                "RTA" => "Both",
                _ => "VU",
            };
        });

        ToggleLiveLyricsCommand = new RelayCommand(_ => ShowLiveLyrics = !ShowLiveLyrics);
        RunAcousticCalibrationCommand = new AsyncRelayCommand(_ => RunAcousticCalibrationAsync(), _ => !IsCalibratingSync && CanControlReceiver(null));
        RefreshRoomMicDevicesCommand = new RelayCommand(_ => RefreshRoomMicCaptureDevices());
        RefreshRoomMicCaptureDevices();

        if (_liveAudioAnalyzer is not null)
        {
            _liveAudioAnalyzer.RoomMicActiveChanged += active => _ = _dispatcher.InvokeAsync(() =>
            {
                IsRoomMicListening = active;
            });
            IsRoomMicListening = _liveAudioAnalyzer.IsRoomMicActive;
        }

        _lyricsSyncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _lyricsSyncTimer.Tick += (_, _) => UpdateSyncedLyricLine();
        if (ShowLiveLyrics)
        {
            _lyricsSyncTimer.Start();
        }
    }

    public AudioMeterSource StepSpectrumFrame(double[] bandsBuffer, double deltaSeconds)
    {
        if (bandsBuffer.Length < 31)
        {
            return AudioMeterSource.Off;
        }

        if (_freezeVuForScreenshots)
        {
            // Screenshot showcase: a static, music-like spectrum (warm low end, gentle high-frequency roll-off).
            for (var i = 0; i < 31; i++)
            {
                bandsBuffer[i] = Math.Clamp(0.86 - (Math.Pow((i - 7) / 25.0, 2) * 0.5) + (0.09 * Math.Sin(i * 1.9)), 0.05, 0.97);
            }

            return AudioMeterSource.Denon;
        }

        UpdateMeterCaptureRequest();

        var hasPcSignal = _liveAudioAnalyzer?.GetLatestFrame().IsLiveSignal ?? false;
        var hasStreamFrame = TryGetAppStreamMeterFrame(out _, out _, bandsBuffer);
        var source = ResolveMeterSource(hasPcSignal, hasStreamFrame);

        switch (source)
        {
            case AudioMeterSource.Denon when hasStreamFrame:
                // YouTube / jukebox: bandsBuffer already holds the frame the speakers play now.
                return AudioMeterSource.Denon;

            case AudioMeterSource.Denon:
                // PC Cast: 31-band FFT computed directly on the cast capture (works for both default and non-default output devices).
                if (!_pcAudioCastService.TryGetDelayedFrame(HeosSyncDelayMs, out _, out _, bandsBuffer))
                {
                    Array.Clear(bandsBuffer, 0, 31); // Synced silence (pause in the cast or initial buffer fill).
                }

                return AudioMeterSource.Denon;

            case AudioMeterSource.Room:
                if (_liveAudioAnalyzer is null || !_liveAudioAnalyzer.CopyRoomMicSpectrumBands(bandsBuffer))
                {
                    Array.Clear(bandsBuffer, 0, 31);
                }

                return AudioMeterSource.Room;

            case AudioMeterSource.Pc when _liveAudioAnalyzer is not null && _liveAudioAnalyzer.CopySpectrumBands(bandsBuffer):
                return AudioMeterSource.Pc;

            case AudioMeterSource.Simulated:
                FillSimulatedSpectrum(bandsBuffer, deltaSeconds);
                return AudioMeterSource.Simulated;

            case AudioMeterSource.Muted:
                Array.Clear(bandsBuffer, 0, 31);
                return AudioMeterSource.Muted;

            default:
                Array.Clear(bandsBuffer, 0, 31);
                return AudioMeterSource.Off;
        }
    }

    private void FillSimulatedSpectrum(double[] bandsBuffer, double deltaSeconds)
    {
        _rtaSimPhase += Math.Clamp(deltaSeconds, 0.001, 0.08) * 6.0;
        var volScale = AudioMeterSourceRouter.SimulationScale(LiveVolume, floor: 0.28, span: 0.65);
        for (var i = 0; i < 31; i++)
        {
            var rolloff = 1.0 - (i / 48.0);
            var wave = 0.44 + (0.28 * Math.Sin((_rtaSimPhase * (1.1 + i * 0.08)) + (i * 0.45)))
                            + (0.20 * Math.Cos((_rtaSimPhase * 2.3) - (i * 0.27)));
            bandsBuffer[i] = Math.Clamp(wave * rolloff * volScale, 0.0, 0.95);
        }
    }

    private int _lyricsRequestSeq;
    private TimeSpan _nonYouTubePausedElapsed = TimeSpan.Zero;
    private bool _wasLyricsClockPaused;

    private void NotifyTrackChangedForLyrics(string title, string detail)
    {
        var key = $"{title}||{detail}";
        if (string.Equals(_lastLyricsTrackKey, key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastLyricsTrackKey = key;
        _nonYouTubeTrackStartedAt = DateTimeOffset.Now;
        _nonYouTubePausedElapsed = TimeSpan.Zero;
        _wasLyricsClockPaused = false;
        if (ShowLiveLyrics)
        {
            _ = RefreshLyricsForCurrentTrackAsync(force: false);
        }
    }

    private async Task RefreshLyricsForCurrentTrackAsync(bool force)
    {
        var title = NowPlayingTitle;
        var detail = NowPlayingDetail;
        if (string.IsNullOrWhiteSpace(title))
        {
            Interlocked.Increment(ref _lyricsRequestSeq);
            CurrentLyricLine = string.Empty;
            NextLyricLine = string.Empty;
            return;
        }

        if (!force && _currentLyricsDoc is not null &&
            string.Equals(_currentLyricsDoc.TrackName, title, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var requestSeq = Interlocked.Increment(ref _lyricsRequestSeq);
        LyricsStatusBadge = "SEARCHING...";
        CurrentLyricLine = $"Looking up synced lyrics for \"{title}\"...";
        NextLyricLine = string.Empty;

        var doc = await _lyricsService.FetchLyricsAsync(title, detail);

        // Discard stale responses if the user skipped to another track while this request was in flight!
        if (requestSeq != Volatile.Read(ref _lyricsRequestSeq))
        {
            return;
        }

        _currentLyricsDoc = doc;

        if (doc is null)
        {
            LyricsStatusBadge = "NO LYRICS";
            CurrentLyricLine = "No synced lyrics found on LRCLIB for this stream.";
            NextLyricLine = string.Empty;
            return;
        }

        LyricsStatusBadge = doc.IsSynced ? "SYNCED LRC" : "LYRICS";
        UpdateSyncedLyricLine();
    }

    private void UpdateSyncedLyricLine()
    {
        if (!ShowLiveLyrics || _currentLyricsDoc is null)
        {
            return;
        }

        double posSeconds;
        if (_youtubeQueueService is not null && _youtubeQueueService.IsYouTubePlaybackActive)
        {
            posSeconds = _youtubeQueueService.GetEstimatedPlaybackPositionSeconds(_settingsStore.Current.Media.CinemaSyncOffsetMs);
        }
        else
        {
            if (IsPaused)
            {
                if (!_wasLyricsClockPaused)
                {
                    _nonYouTubePausedElapsed += DateTimeOffset.Now - _nonYouTubeTrackStartedAt;
                    _wasLyricsClockPaused = true;
                }

                posSeconds = Math.Max(0, _nonYouTubePausedElapsed.TotalSeconds);
            }
            else
            {
                if (_wasLyricsClockPaused)
                {
                    _nonYouTubeTrackStartedAt = DateTimeOffset.Now;
                    _wasLyricsClockPaused = false;
                }

                posSeconds = Math.Max(0, (_nonYouTubePausedElapsed + (DateTimeOffset.Now - _nonYouTubeTrackStartedAt)).TotalSeconds);
            }
        }

        var (cur, next) = LyricsService.GetDisplayLines(_currentLyricsDoc, posSeconds);
        CurrentLyricLine = cur;
        NextLyricLine = next;
    }

    private async Task RunAcousticCalibrationAsync()
    {
        IsCalibratingSync = true;
        AcousticCalibrationStatus = "Starting 1-click acoustic lip-sync calibration...";
        StatusMessage = AcousticCalibrationStatus;

        try
        {
            var result = await _calibrationService.RunCalibrationAsync(progress =>
                _ = _dispatcher.InvokeAsync(() =>
                {
                    AcousticCalibrationStatus = progress;
                    StatusMessage = progress;
                }));

            AcousticCalibrationStatus = result.Summary;
            StatusMessage = result.Summary;

            if (result.Success)
            {
                var (updated, savedToDisk) = await _settingsStore.UpdateQuietAsync(
                    s => s.Media.CinemaSyncOffsetMs = result.MeasuredOffsetMs);
                SettingsCinemaSyncOffsetMs = updated.Media.CinemaSyncOffsetMs.ToString();
                if (!savedToDisk)
                {
                    StatusMessage += " It is saved with your next Save & Sync (the settings file is being recovered).";
                }
            }
        }
        catch (Exception ex)
        {
            AcousticCalibrationStatus = $"Calibration error: {ex.Message}";
            StatusMessage = AcousticCalibrationStatus;
        }
        finally
        {
            IsCalibratingSync = false;
        }
    }
}
