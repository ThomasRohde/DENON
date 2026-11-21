using System.Windows.Threading;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Services;

namespace Denon.App.Desktop.ViewModels;

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

    public RelayCommand CycleMeterDisplayModeCommand { get; private set; } = null!;
    public RelayCommand ToggleLiveLyricsCommand { get; private set; } = null!;
    public AsyncRelayCommand RunAcousticCalibrationCommand { get; private set; } = null!;

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
                _liveAudioAnalyzer?.SetVuCaptureRequested(IsAnyAudioMeterVisible);
            }
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
                    _ = RefreshLyricsForCurrentTrackAsync(force: true);
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

        _lyricsSyncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _lyricsSyncTimer.Tick += (_, _) => UpdateSyncedLyricLine();
        _lyricsSyncTimer.Start();
    }

    public (bool IsLive, bool IsSimulated) StepSpectrumFrame(double[] bandsBuffer, double deltaSeconds)
    {
        if (bandsBuffer.Length < 31)
        {
            return (false, false);
        }

        if (_liveAudioAnalyzer is not null && _liveAudioAnalyzer.CopySpectrumBands(bandsBuffer))
        {
            return (true, false);
        }

        if (IsActiveAndPlaying)
        {
            _rtaSimPhase += Math.Clamp(deltaSeconds, 0.001, 0.08) * 6.0;
            var volScale = Math.Clamp(0.28 + (LiveVolume / 98.0) * 0.65, 0.25, 0.92);
            for (var i = 0; i < 31; i++)
            {
                var rolloff = 1.0 - (i / 48.0);
                var wave = 0.44 + (0.28 * Math.Sin((_rtaSimPhase * (1.1 + i * 0.08)) + (i * 0.45)))
                                + (0.20 * Math.Cos((_rtaSimPhase * 2.3) - (i * 0.27)));
                bandsBuffer[i] = Math.Clamp(wave * rolloff * volScale, 0.04, 0.95);
            }

            return (false, true);
        }

        Array.Clear(bandsBuffer, 0, 31);
        return (false, false);
    }

    private void NotifyTrackChangedForLyrics(string title, string detail)
    {
        var key = $"{title}||{detail}";
        if (string.Equals(_lastLyricsTrackKey, key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastLyricsTrackKey = key;
        _nonYouTubeTrackStartedAt = DateTimeOffset.Now;
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
            CurrentLyricLine = string.Empty;
            NextLyricLine = string.Empty;
            return;
        }

        if (!force && _currentLyricsDoc is not null &&
            string.Equals(_currentLyricsDoc.TrackName, title, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LyricsStatusBadge = "SEARCHING...";
        CurrentLyricLine = $"Looking up synced lyrics for \"{title}\"...";
        NextLyricLine = string.Empty;

        var doc = await _lyricsService.FetchLyricsAsync(title, detail);
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
            posSeconds = Math.Max(0, (DateTimeOffset.Now - _nonYouTubeTrackStartedAt).TotalSeconds);
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
                SettingsCinemaSyncOffsetMs = result.MeasuredOffsetMs.ToString();
                var updated = _settingsStore.Current.Clone();
                updated.Media.CinemaSyncOffsetMs = result.MeasuredOffsetMs;
                await _settingsStore.SaveAsync(updated);
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
