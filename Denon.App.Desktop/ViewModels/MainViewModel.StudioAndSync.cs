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

    public AudioMeterSource StepSpectrumFrame(double[] bandsBuffer, double deltaSeconds)
    {
        if (bandsBuffer.Length < 31)
        {
            return AudioMeterSource.Off;
        }

        var hasPcSignal = _liveAudioAnalyzer?.GetLatestFrame().IsLiveSignal ?? false;
        var hasStreamFrame = TryGetAppStreamMeterFrame(out _, out _, bandsBuffer);
        var source = ResolveMeterSource(hasPcSignal, hasStreamFrame);

        switch (source)
        {
            case AudioMeterSource.Denon when hasStreamFrame:
                // YouTube / jukebox: bandsBuffer already holds the frame the speakers play now.
                return AudioMeterSource.Denon;

            case AudioMeterSource.Denon:
                // The FFT reads the Windows default output. It matches the cast only when the cast captures that device.
                if (_pcAudioCastService.IsCastingDefaultDevice &&
                    _liveAudioAnalyzer is not null &&
                    _liveAudioAnalyzer.TryGetDelayedFrame(HeosSyncDelayMs, out _, out _, bandsBuffer))
                {
                    return AudioMeterSource.Denon;
                }

                if (_pcAudioCastService.IsCastingDefaultDevice)
                {
                    Array.Clear(bandsBuffer, 0, 31); // Synced silence (pause in the cast).
                    return AudioMeterSource.Denon;
                }

                FillSimulatedSpectrum(bandsBuffer, deltaSeconds);
                return AudioMeterSource.Simulated;

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
                SettingsCinemaSyncOffsetMs = result.MeasuredOffsetMs.ToString();
                _suppressNextSettingsReload = true;
                try
                {
                    await _settingsStore.UpdateAsync(s => s.Media.CinemaSyncOffsetMs = result.MeasuredOffsetMs);
                }
                finally
                {
                    _suppressNextSettingsReload = false;
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
