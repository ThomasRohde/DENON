using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.Core.Services.Metering;
using Denon.App.PluginSdk;
using Microsoft.Extensions.Hosting;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Denon.App.Desktop.Services;

/// <summary>
/// Allocation-free real-time audio analysis & routing service:
/// <list type="bullet">
///   <item>WASAPI loopback FFT analyzer on the Windows default render endpoint (PC mode + loopback activity detection).</item>
///   <item>Opt-in, privacy-gated WASAPI Room Mic / Line-In capture with studio-grade AGC, adaptive noise-floor estimator, and soft-knee noise gate.</item>
///   <item>Routed ~60 Hz <see cref="PluginAudioFrame"/> publisher to <see cref="PluginHostService"/> (Philips Hue Music Sync) respecting HEOS delay during PC Cast / YouTube / Jukebox and Room Mic when active.</item>
/// </list>
/// </summary>
public sealed class LiveAudioAnalyzerService : BackgroundService
{
    private readonly PluginHostService _pluginHost;
    private readonly AppLogStore _logStore;
    private readonly ReceiverStateStore? _receiverStateStore;
    private readonly AppSettingsStore? _settingsStore;
    private readonly PcAudioCastService? _pcAudioCastService;
    private readonly AppStreamMeterService? _appStreamMeterService;

    private readonly AudioSpectrumAnalyzer _loopbackAnalyzer = new();
    private readonly AudioMeterRouter _router = new();
    private readonly RoomMicDspProcessor _roomMicDsp = new();

    private readonly object _frameLock = new();
    private readonly double[] _spectrum31 = new double[AudioMeterMath.BandCount];
    private readonly double[] _appStreamBandsScratch = new double[AudioMeterMath.BandCount];
    private PluginAudioFrame _latestFrame;
    private readonly AudioFrameHistory _history = new(capacity: 1024); // ~10 s at 60-100 Hz; HEOS delay is at most 8 s
    private long _lastPublishTickMs;
    private long _lastAudioTickMs;

    private readonly object _roomMicLock = new();
    private readonly double[] _roomMicSpectrum31 = new double[AudioMeterMath.BandCount];
    private PluginAudioFrame _latestRoomMicFrame;
    private readonly AudioFrameHistory _roomMicHistory = new(capacity: 256);
    private long _lastRoomMicPacketTickMs;

    // Plugin router beat cursors: each source beat goes to Hue once, whatever the sampling rates.
    private long _pcCastBeatCursorMs = long.MinValue;
    private long _roomMicBeatCursorMs = long.MinValue;
    private long _loopbackBeatCursorMs = long.MinValue;
    private readonly SemaphoreSlim _roomMicWakeSignal = new(0, 1);
    private CancellationTokenSource? _roomMicSessionCts;

    private volatile bool _vuCaptureRequested;
    private volatile bool _roomMicEnabled;
    private volatile string _roomMicDeviceId = string.Empty;
    private volatile bool _isRoomMicActive;
    private string? _loggedMissingRoomMicDevice;
    private bool _loggedNoDefaultRoomMic;

    public LiveAudioAnalyzerService(
        PluginHostService pluginHost,
        AppLogStore logStore,
        ReceiverStateStore? receiverStateStore = null,
        AppSettingsStore? settingsStore = null,
        PcAudioCastService? pcAudioCastService = null,
        AppStreamMeterService? appStreamMeterService = null)
    {
        _pluginHost = pluginHost;
        _logStore = logStore;
        _receiverStateStore = receiverStateStore;
        _settingsStore = settingsStore;
        _pcAudioCastService = pcAudioCastService;
        _appStreamMeterService = appStreamMeterService;

        if (_receiverStateStore is not null)
        {
            _receiverStateStore.StateChanged += OnReceiverStateChanged;
        }

        if (_pcAudioCastService is not null)
        {
            _pcAudioCastService.CastStateChanged += OnPcCastStateChanged;
        }
    }

    public bool IsCapturing { get; private set; }

    public bool IsRoomMicEnabled => _roomMicEnabled;

    public string RoomMicDeviceId => _roomMicDeviceId;

    /// <summary>True only while the WASAPI microphone / Line-In capture device is actively open and listening.</summary>
    public bool IsRoomMicActive => _isRoomMicActive;

    public event Action<bool>? RoomMicActiveChanged;

    public void SetVuCaptureRequested(bool requested)
    {
        if (_vuCaptureRequested == requested)
        {
            return;
        }

        _vuCaptureRequested = requested;
        ReevaluateRoomMicGate();
    }

    /// <summary>Opt-in toggle for Room Mic mode (off by default). Disabling immediately closes and releases the microphone.</summary>
    public void SetRoomMicEnabled(bool enabled)
    {
        if (_roomMicEnabled == enabled)
        {
            return;
        }

        _roomMicEnabled = enabled;
        if (!enabled)
        {
            CancelActiveRoomMicSession();
        }

        SignalRoomMicLoop();
    }

    /// <summary>Selects a specific Windows capture endpoint ID (or empty/null for the default capture device).</summary>
    public void SetRoomMicDeviceId(string? deviceId)
    {
        var normalized = deviceId?.Trim() ?? string.Empty;
        if (string.Equals(_roomMicDeviceId, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _roomMicDeviceId = normalized;
        // Rebind immediately if a Room Mic capture session is currently open
        CancelActiveRoomMicSession();
        SignalRoomMicLoop();
    }

    /// <summary>Lists active Windows audio capture devices (microphones & Line-In inputs) for the Room Mic picker.</summary>
    public IReadOnlyList<AudioCaptureDeviceOption> GetCaptureDevices()
    {
        var devices = new List<AudioCaptureDeviceOption>
        {
            new(string.Empty, "Default Microphone / Line-In", true),
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                defaultId = def.ID;
            }
            catch
            {
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    var isDef = string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase);
                    devices.Add(new AudioCaptureDeviceOption(
                        device.ID,
                        isDef ? $"{device.FriendlyName} (Default)" : device.FriendlyName,
                        isDef));
                }
            }
        }
        catch (Exception ex)
        {
            _logStore.Warning($"Could not enumerate Windows capture devices: {ex.Message}");
        }

        return devices;
    }

    public PluginAudioFrame GetLatestFrame()
    {
        lock (_frameLock)
        {
            if (!_latestFrame.IsLiveSignal || (Environment.TickCount64 - _latestFrame.TimestampMs) > 400)
            {
                return default;
            }

            return _latestFrame;
        }
    }

    /// <summary>Latest studio-conditioned Room Mic frame (levels, 3-band energy, beat). Returns default when inactive or noise-gated.</summary>
    public PluginAudioFrame GetLatestRoomMicFrame()
    {
        if (!_isRoomMicActive)
        {
            return default;
        }

        lock (_roomMicLock)
        {
            if (!_latestRoomMicFrame.IsLiveSignal || (Environment.TickCount64 - _latestRoomMicFrame.TimestampMs) > 400)
            {
                return default;
            }

            return _latestRoomMicFrame;
        }
    }

    /// <summary>
    /// The frame from <paramref name="delayMs"/> ago (levels + 31 bands). During PC Cast this reads directly from
    /// <see cref="PcAudioCastService"/> so both default and non-default output devices have full synced RTA data.
    /// </summary>
    public bool TryGetDelayedFrame(int delayMs, out double left, out double right, double[]? bandsDestination = null)
    {
        if (_pcAudioCastService is { IsCasting: true })
        {
            return _pcAudioCastService.TryGetDelayedFrame(delayMs, out left, out right, bandsDestination);
        }

        return _history.TryGetAt(Environment.TickCount64 - Math.Max(0, delayMs), out left, out right, bandsDestination);
    }

    public bool CopySpectrumBands(double[] destination)
    {
        if (destination.Length < AudioMeterMath.BandCount)
        {
            return false;
        }

        lock (_frameLock)
        {
            if (!_latestFrame.IsLiveSignal || (Environment.TickCount64 - _latestFrame.TimestampMs) > 400)
            {
                Array.Clear(destination, 0, AudioMeterMath.BandCount);
                return false;
            }

            Array.Copy(_spectrum31, destination, AudioMeterMath.BandCount);
            return true;
        }
    }

    /// <summary>
    /// Copies the latest 31-band Room Mic FFT spectrum into <paramref name="destination"/>.
    /// Returns true while Room Mic is actively listening (copying zeros when noise-gated so the RTA rests cleanly at 0).
    /// </summary>
    public bool CopyRoomMicSpectrumBands(double[] destination)
    {
        if (destination.Length < AudioMeterMath.BandCount)
        {
            return false;
        }

        if (!_isRoomMicActive)
        {
            Array.Clear(destination, 0, AudioMeterMath.BandCount);
            return false;
        }

        lock (_roomMicLock)
        {
            if ((Environment.TickCount64 - _lastRoomMicPacketTickMs) > 400)
            {
                Array.Clear(destination, 0, AudioMeterMath.BandCount);
                return true;
            }

            Array.Copy(_roomMicSpectrum31, destination, AudioMeterMath.BandCount);
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        var loopbackTask = RunLoopbackSupervisorAsync(stoppingToken);
        var roomMicTask = RunRoomMicSupervisorAsync(stoppingToken);
        var pluginRouterTask = RunPluginAudioRouterAsync(stoppingToken);

        await Task.WhenAll(loopbackTask, roomMicTask, pluginRouterTask);
    }

    private async Task RunLoopbackSupervisorAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var shouldCapture = _pluginHost.HasActiveAudioReactivePlugins() || _vuCaptureRequested;
            if (!shouldCapture)
            {
                await Task.Delay(250, stoppingToken);
                continue;
            }

            try
            {
                await RunLoopbackCaptureLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                IsCapturing = false;
                _logStore.Warning($"Live audio FFT analyzer restarted after error: {ex.Message}");
                await Task.Delay(1500, stoppingToken);
            }
        }
    }

    private async Task RunLoopbackCaptureLoopAsync(CancellationToken ct)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var capturedDeviceId = device.ID;
        var capturedDeviceName = device.FriendlyName;

#pragma warning disable CS0618
        using var capture = new WasapiLoopbackCapture(device);
#pragma warning restore CS0618

        var waveFormat = capture.WaveFormat;
        var sampleRate = Math.Max(8000, waveFormat.SampleRate);
        var channels = Math.Max(1, waveFormat.Channels);
        var isFloat = RoomMicDspProcessor.IsFloatFormat(waveFormat);
        var bytesPerSample = Math.Max(1, waveFormat.BitsPerSample / 8);

        var stoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        capture.DataAvailable += (_, args) =>
        {
            if (args.BytesRecorded <= 0)
            {
                return;
            }

            ProcessLoopbackBuffer(args.Buffer, args.BytesRecorded, channels, sampleRate, bytesPerSample, isFloat);
        };

        capture.RecordingStopped += (_, _) => stoppedTcs.TrySetResult();

        _loopbackAnalyzer.Reset();
        capture.StartRecording();
        IsCapturing = true;

        try
        {
            var ticksSinceDeviceCheck = 0;
            while (!ct.IsCancellationRequested && !stoppedTcs.Task.IsCompleted)
            {
                if (!_pluginHost.HasActiveAudioReactivePlugins() && !_vuCaptureRequested)
                {
                    break;
                }

                // Periodically detect Windows default audio output switches (e.g. turning HDMI TV on/off,
                // switching speakers/headphones) so VU meters & 31-band RTA seamlessly rebind to the active device.
                if (++ticksSinceDeviceCheck >= 20)
                {
                    ticksSinceDeviceCheck = 0;
                    try
                    {
                        if (device.State != DeviceState.Active)
                        {
                            _logStore.Info($"Audio endpoint '{capturedDeviceName}' became inactive — rebinding VU/FFT capture...");
                            break;
                        }

                        using var currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        if (!string.Equals(currentDefault.ID, capturedDeviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            _logStore.Info($"Windows default audio changed ('{capturedDeviceName}' → '{currentDefault.FriendlyName}') — rebinding VU/FFT capture...");
                            break;
                        }
                    }
                    catch
                    {
                        break;
                    }
                }

                // Publish smooth zero-decay frames when Windows audio goes silent
                var now = Environment.TickCount64;
                if (now - _lastAudioTickMs > 120 && now - _lastPublishTickMs > 40)
                {
                    StoreLoopbackFrame(new PluginAudioFrame(0, 0, 0, 0, 0, 0, false, false, now), default);
                }

                await Task.Delay(40, ct);
            }
        }
        finally
        {
            IsCapturing = false;
            try { capture.StopRecording(); } catch { }
            StoreLoopbackFrame(new PluginAudioFrame(0, 0, 0, 0, 0, 0, false, false, Environment.TickCount64), default);
        }
    }

    private void ProcessLoopbackBuffer(byte[] buffer, int bytesRecorded, int channels, int sampleRate, int bytesPerSample, bool isFloat)
    {
        var frameSize = channels * bytesPerSample;
        var frames = frameSize > 0 ? bytesRecorded / frameSize : 0;
        if (frames <= 0)
        {
            return;
        }

        double sumL = 0, sumR = 0, peakL = 0, peakR = 0;

        if (isFloat && bytesPerSample == 4)
        {
            for (var f = 0; f < frames; f++)
            {
                var offset = f * frameSize;
                var l = BitConverter.ToSingle(buffer, offset);
                var r = channels > 1 ? BitConverter.ToSingle(buffer, offset + 4) : l;
                if (!float.IsFinite(l)) l = 0f;
                if (!float.IsFinite(r)) r = 0f;

                var absL = Math.Abs(l);
                var absR = Math.Abs(r);
                if (absL > peakL) peakL = absL;
                if (absR > peakR) peakR = absR;
                sumL += l * l;
                sumR += r * r;

                _loopbackAnalyzer.PushMonoSample((l + r) * 0.5f);
            }
        }
        else
        {
            for (var f = 0; f < frames; f++)
            {
                var offset = f * frameSize;
                var l = RoomMicDspProcessor.ReadNormalizedSample(buffer, offset, bytesPerSample, isFloat);
                var r = channels > 1
                    ? RoomMicDspProcessor.ReadNormalizedSample(buffer, offset + bytesPerSample, bytesPerSample, isFloat)
                    : l;

                var absL = Math.Abs(l);
                var absR = Math.Abs(r);
                if (absL > peakL) peakL = absL;
                if (absR > peakR) peakR = absR;
                sumL += l * l;
                sumR += r * r;

                _loopbackAnalyzer.PushMonoSample((l + r) * 0.5f);
            }
        }

        var now = Environment.TickCount64;
        var isLive = peakL > AudioMeterMath.SilencePeak || peakR > AudioMeterMath.SilencePeak;
        if (isLive)
        {
            _lastAudioTickMs = now;
        }

        // Throttle FFT analysis to ~50-60 Hz (~16 ms)
        if (now - _lastPublishTickMs < 16)
        {
            return;
        }

        var rmsL = Math.Sqrt(sumL / frames);
        var rmsR = Math.Sqrt(sumR / frames);
        var leftLevel = AudioMeterMath.ToNormalizedLevel(peakL, rmsL);
        var rightLevel = AudioMeterMath.ToNormalizedLevel(peakR, rmsR);

        Span<double> bands31 = stackalloc double[AudioMeterMath.BandCount];
        var frame = _loopbackAnalyzer.Analyze(sampleRate, leftLevel, rightLevel, isLive, now, bands31);
        StoreLoopbackFrame(in frame, bands31);

        // If PC loopback just became live while Room Mic was listening, release the mic immediately.
        if (isLive && _isRoomMicActive)
        {
            CancelActiveRoomMicSession();
        }
    }

    private void StoreLoopbackFrame(in PluginAudioFrame frame, ReadOnlySpan<double> bands31)
    {
        _lastPublishTickMs = frame.TimestampMs;
        lock (_frameLock)
        {
            _latestFrame = frame;
            if (bands31.Length >= AudioMeterMath.BandCount)
            {
                bands31[..AudioMeterMath.BandCount].CopyTo(_spectrum31);
            }
            else
            {
                Array.Clear(_spectrum31, 0, _spectrum31.Length);
            }

            _history.Add(in frame, _spectrum31);
        }
    }

    private async Task RunRoomMicSupervisorAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!EvaluateRoomMicShouldCapture())
            {
                try
                {
                    await _roomMicWakeSignal.WaitAsync(150, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_roomMicLock)
            {
                _roomMicSessionCts = sessionCts;
            }

            try
            {
                await RunRoomMicCaptureSessionAsync(sessionCts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Expected when gate closes, RoomMicEnabled toggles off, or capture device changes.
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logStore.Warning($"Room Mic capture stopped after error: {ex.Message}");
                try
                {
                    await Task.Delay(1500, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                lock (_roomMicLock)
                {
                    if (ReferenceEquals(_roomMicSessionCts, sessionCts))
                    {
                        _roomMicSessionCts = null;
                    }
                }
            }
        }
    }

    private async Task RunRoomMicCaptureSessionAsync(CancellationToken ct)
    {
        if (!EvaluateRoomMicShouldCapture())
        {
            return;
        }

        var requestedDeviceId = _roomMicDeviceId;
        using var enumerator = new MMDeviceEnumerator();
        using var device = ResolveCaptureDevice(enumerator, requestedDeviceId);
        if (device is null)
        {
            if (string.IsNullOrWhiteSpace(requestedDeviceId) && !_loggedNoDefaultRoomMic)
            {
                _loggedNoDefaultRoomMic = true;
                _logStore.Warning("Room Mic enabled, but no active Windows microphone / Line-In capture device was found.");
            }

            await Task.Delay(2500, ct);
            return;
        }

        _loggedNoDefaultRoomMic = false;

        var capturedDeviceId = device.ID;
        var capturedDeviceName = device.FriendlyName;
        var usingDefault = string.IsNullOrWhiteSpace(requestedDeviceId);

#pragma warning disable CS0618
        using var capture = new WasapiCapture(device);
#pragma warning restore CS0618

        var waveFormat = capture.WaveFormat;
        var stoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _roomMicDsp.Reset();
        _roomMicHistory.Clear();
        var bandsScratch = new double[AudioMeterMath.BandCount];

        capture.DataAvailable += (_, args) =>
        {
            if (args.BytesRecorded <= 0 || ct.IsCancellationRequested)
            {
                return;
            }

            var now = Environment.TickCount64;
            var frame = _roomMicDsp.ProcessBuffer(args.Buffer, args.BytesRecorded, waveFormat, now, bandsScratch);
            lock (_roomMicLock)
            {
                _latestRoomMicFrame = frame;
                _lastRoomMicPacketTickMs = now;
                Array.Copy(bandsScratch, _roomMicSpectrum31, AudioMeterMath.BandCount);
            }

            _roomMicHistory.Add(in frame);
        };

        capture.RecordingStopped += (_, _) => stoppedTcs.TrySetResult();

        capture.StartRecording();
        SetRoomMicActive(true);
        _logStore.Info($"Room Mic active on '{capturedDeviceName}' ({waveFormat.SampleRate} Hz, {waveFormat.BitsPerSample}-bit, {waveFormat.Channels} ch).");

        try
        {
            var ticksSinceDeviceCheck = 0;
            while (!ct.IsCancellationRequested && !stoppedTcs.Task.IsCompleted)
            {
                if (!EvaluateRoomMicShouldCapture())
                {
                    break;
                }

                if (++ticksSinceDeviceCheck >= 20)
                {
                    ticksSinceDeviceCheck = 0;
                    try
                    {
                        if (device.State != DeviceState.Active)
                        {
                            _logStore.Info($"Room Mic endpoint '{capturedDeviceName}' became inactive — rebinding...");
                            break;
                        }

                        if (usingDefault)
                        {
                            using var currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                            if (!string.Equals(currentDefault.ID, capturedDeviceId, StringComparison.OrdinalIgnoreCase))
                            {
                                _logStore.Info($"Windows default capture device changed ('{capturedDeviceName}' → '{currentDefault.FriendlyName}') — rebinding Room Mic...");
                                break;
                            }
                        }
                    }
                    catch
                    {
                        break;
                    }
                }

                var now = Environment.TickCount64;
                lock (_roomMicLock)
                {
                    if (now - _lastRoomMicPacketTickMs > 200)
                    {
                        _latestRoomMicFrame = new PluginAudioFrame(0, 0, 0, 0, 0, 0, false, false, now);
                        Array.Clear(_roomMicSpectrum31, 0, _roomMicSpectrum31.Length);
                    }
                }

                await Task.Delay(40, ct);
            }
        }
        finally
        {
            SetRoomMicActive(false);
            try { capture.StopRecording(); } catch { }
            lock (_roomMicLock)
            {
                _latestRoomMicFrame = default;
                Array.Clear(_roomMicSpectrum31, 0, _roomMicSpectrum31.Length);
            }

            _logStore.Info($"Room Mic released ('{capturedDeviceName}').");
        }
    }

    private MMDevice? ResolveCaptureDevice(MMDeviceEnumerator enumerator, string deviceId)
    {
        // A device the user selected is the only one used: when it is unplugged the mic stays closed and the
        // supervisor retries it, instead of listening through another microphone the user did not choose.
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                var explicitDevice = enumerator.GetDevice(deviceId);
                if (explicitDevice.State == DeviceState.Active && explicitDevice.DataFlow == DataFlow.Capture)
                {
                    _loggedMissingRoomMicDevice = null;
                    return explicitDevice;
                }

                explicitDevice.Dispose();
            }
            catch
            {
            }

            if (!string.Equals(_loggedMissingRoomMicDevice, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                _loggedMissingRoomMicDevice = deviceId;
                _logStore.Warning($"Selected Room Mic device '{deviceId}' is unavailable — the microphone stays closed until it is back.");
            }

            return null;
        }

        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }
        catch
        {
            return null;
        }
    }

    private async Task RunPluginAudioRouterAsync(CancellationToken stoppingToken)
    {
        var publishedLiveRecently = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_pluginHost.HasActiveAudioReactivePlugins())
            {
                if (publishedLiveRecently)
                {
                    publishedLiveRecently = false;
                    var silent = new PluginAudioFrame(0, 0, 0, 0, 0, 0, false, false, Environment.TickCount64);
                    _pluginHost.PublishAudioFrame(in silent);
                }

                _router.ResetAppStreamBeatState();
                await Task.Delay(150, stoppingToken);
                continue;
            }

            var now = Environment.TickCount64;
            var routedFrame = BuildRoutedPluginFrame(now);
            publishedLiveRecently = routedFrame.IsLiveSignal;
            _pluginHost.PublishAudioFrame(in routedFrame);

            await Task.Delay(16, stoppingToken);
        }
    }

    private PluginAudioFrame BuildRoutedPluginFrame(long nowMs)
    {
        var delayMs = GetHeosSyncDelayMs();
        var receiverSnapshot = _receiverStateStore?.Snapshot ?? ReceiverStateSnapshot.Empty;
        var isPcCasting = _pcAudioCastService is { IsCasting: true };

        var pcLoopbackFrame = GetLatestFrame();
        var roomMicFrame = GetLatestRoomMicFrame();

        PluginAudioFrame pcCastDelayedFrame = default;
        var hasPcCastDelayed = false;
        if (isPcCasting && _pcAudioCastService is not null)
        {
            hasPcCastDelayed = _pcAudioCastService.TryGetDelayedAudioFrame(delayMs, out pcCastDelayedFrame);
        }

        PluginAudioFrame appStreamFrame = default;
        var hasAppStream = false;
        if (!isPcCasting &&
            _appStreamMeterService is not null &&
            _appStreamMeterService.TryGetFrame(delayMs, out var streamL, out var streamR, _appStreamBandsScratch))
        {
            hasAppStream = true;
            appStreamFrame = _router.BuildAppStreamPluginFrame(streamL, streamR, _appStreamBandsScratch, nowMs);
        }

        var meterInputs = AudioMeterRouter.BuildInputs(
            receiverSnapshot,
            isPcCasting: isPcCasting,
            hasPcSignal: pcLoopbackFrame.IsLiveSignal,
            hasAppStreamFrame: hasAppStream,
            isRoomMicActive: _isRoomMicActive);

        var context = new AudioMeterRouteContext(
            MeterInputs: meterInputs,
            PcCastDelayedFrame: pcCastDelayedFrame,
            HasPcCastDelayedFrame: hasPcCastDelayed,
            AppStreamFrame: appStreamFrame,
            HasAppStreamFrame: hasAppStream,
            RoomMicFrame: roomMicFrame,
            PcLoopbackFrame: pcLoopbackFrame);

        var (source, frame) = AudioMeterRouter.ResolvePluginAudioFrame(in context, nowMs);

        // The router samples every 16 ms; the sources make a frame every ~10 ms. Take the beats from the time since
        // the last sample (each cursor moves every tick), so a beat is neither lost between samples nor sent twice.
        var pcCastBeat = NextBeat(ref _pcCastBeatCursorMs, nowMs - delayMs,
            _pcAudioCastService is { } cast ? cast.TryGetBeatBetween : NoBeat);
        var roomBeat = NextBeat(ref _roomMicBeatCursorMs, nowMs, _roomMicHistory.TryGetBeatBetween);
        var loopBeat = NextBeat(ref _loopbackBeatCursorMs, nowMs, _history.TryGetBeatBetween);

        var beat = source switch
        {
            AudioMeterSource.Denon when !hasAppStream && isPcCasting => pcCastBeat,
            AudioMeterSource.Room => roomBeat,
            AudioMeterSource.Pc => loopBeat,
            _ => ((bool Found, double Intensity)?)null,
        };

        return beat is { } b && frame.IsLiveSignal
            ? frame with { IsBeat = b.Found, BeatIntensity = b.Found ? Math.Max(frame.BeatIntensity, b.Intensity) : frame.BeatIntensity }
            : frame;
    }

    private delegate bool BeatQuery(long afterTickMs, long upToTickMs, out double intensity);

    private static bool NoBeat(long afterTickMs, long upToTickMs, out double intensity)
    {
        intensity = 0;
        return false;
    }

    /// <summary>
    /// The beat in (cursor, target] and moves the cursor to the target. Only the last 100 ms count, so after a
    /// pause or a source switch an old beat does not flash late; a target that moves back sends nothing twice.
    /// </summary>
    private static (bool Found, double Intensity) NextBeat(ref long cursorMs, long targetMs, BeatQuery query)
    {
        var after = Math.Max(cursorMs, targetMs - 100);
        cursorMs = Math.Max(cursorMs, targetMs);
        return targetMs > after && query(after, targetMs, out var intensity) ? (true, intensity) : (false, 0);
    }

    internal bool EvaluateRoomMicShouldCapture()
    {
        if (!_roomMicEnabled)
        {
            return false;
        }

        var receiverSnapshot = _receiverStateStore?.Snapshot ?? ReceiverStateSnapshot.Empty;
        var isPcCasting = _pcAudioCastService is { IsCasting: true };
        var hasAppStream = !isPcCasting &&
                           _appStreamMeterService is not null &&
                           _appStreamMeterService.TryGetFrame(GetHeosSyncDelayMs(), out _, out _);
        var hasPcLoopback = GetLatestFrame().IsLiveSignal;
        var hueActive = _pluginHost.HasActiveAudioReactivePlugins();

        var gateInputs = AudioMeterRouter.BuildRoomMicGateInputs(
            roomMicEnabled: _roomMicEnabled,
            metersVisible: _vuCaptureRequested,
            hueMusicSyncActive: hueActive,
            receiverSnapshot: receiverSnapshot,
            isPcCasting: isPcCasting,
            hasAppStreamFrame: hasAppStream,
            hasPcLoopbackSignal: hasPcLoopback);

        return RoomMicGate.ShouldCapture(in gateInputs);
    }

    private int GetHeosSyncDelayMs() =>
        Math.Clamp(_settingsStore?.CinemaSyncOffsetMs ?? 1800, 0, 8000);

    private void OnReceiverStateChanged(ReceiverStateSnapshot _) => ReevaluateRoomMicGate();

    private void OnPcCastStateChanged(bool _, string __) => ReevaluateRoomMicGate();

    private void ReevaluateRoomMicGate()
    {
        if (!EvaluateRoomMicShouldCapture())
        {
            CancelActiveRoomMicSession();
        }

        SignalRoomMicLoop();
    }

    private void CancelActiveRoomMicSession()
    {
        lock (_roomMicLock)
        {
            try { _roomMicSessionCts?.Cancel(); } catch { }
        }
    }

    private void SignalRoomMicLoop()
    {
        try
        {
            if (_roomMicWakeSignal.CurrentCount == 0)
            {
                _roomMicWakeSignal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetRoomMicActive(bool active)
    {
        if (_isRoomMicActive == active)
        {
            return;
        }

        _isRoomMicActive = active;
        RoomMicActiveChanged?.Invoke(active);
    }

    public override void Dispose()
    {
        if (_receiverStateStore is not null)
        {
            _receiverStateStore.StateChanged -= OnReceiverStateChanged;
        }

        if (_pcAudioCastService is not null)
        {
            _pcAudioCastService.CastStateChanged -= OnPcCastStateChanged;
        }

        CancelActiveRoomMicSession();
        _roomMicWakeSignal.Dispose();
        base.Dispose();
    }
}
