using System.Diagnostics;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.PluginSdk;
using Microsoft.Extensions.Hosting;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Denon.App.Desktop.Services;

/// <summary>
/// Allocation-free real-time WASAPI loopback FFT spectrum analyzer & beat onset detector.
/// Feeds 3-band Bass/Mid/Treble energy and beat pulses into <see cref="PluginHostService"/>
/// whenever any <see cref="IAudioReactivePlugin"/> (such as Philips Hue Music Sync) is active,
/// and provides live stereo levels for the Analog VU Meters.
/// </summary>
public sealed class LiveAudioAnalyzerService : BackgroundService
{
    private const int FftSize = 512;
    private const int FftBits = 9; // 2^9 = 512

    private readonly PluginHostService _pluginHost;
    private readonly AppLogStore _logStore;

    private readonly float[] _hannWindow = new float[FftSize];
    private readonly double[] _fftReal = new double[FftSize];
    private readonly double[] _fftImag = new double[FftSize];
    private readonly float[] _monoRing = new float[FftSize];
    private int _ringWriteIndex;

    // Adaptive beat onset history (rolling ~0.8s bass energy buffer)
    private readonly double[] _bassHistory = new double[42];
    private int _historyIndex;
    private int _historyCount;
    private long _lastBeatTickMs;
    private long _lastPublishTickMs;
    private long _lastAudioTickMs;

    private readonly object _frameLock = new();
    private readonly double[] _spectrum31 = new double[31];
    private PluginAudioFrame _latestFrame;
    private readonly AudioFrameHistory _history = new(capacity: 1024); // ~10 s at 60-100 Hz; HEOS delay is at most 8 s
    private volatile bool _vuCaptureRequested;

    public LiveAudioAnalyzerService(PluginHostService pluginHost, AppLogStore logStore)
    {
        _pluginHost = pluginHost;
        _logStore = logStore;

        for (var i = 0; i < FftSize; i++)
        {
            _hannWindow[i] = (float)(0.5 * (1.0 - Math.Cos((2.0 * Math.PI * i) / (FftSize - 1))));
        }
    }

    public bool IsCapturing { get; private set; }

    public void SetVuCaptureRequested(bool requested) => _vuCaptureRequested = requested;

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

    /// <summary>
    /// The frame from <paramref name="delayMs"/> ago (levels + 31 bands). Used to show PC-cast audio in sync
    /// with the Denon speakers, which play it after the HEOS buffer.
    /// </summary>
    public bool TryGetDelayedFrame(int delayMs, out double left, out double right, double[]? bandsDestination = null) =>
        _history.TryGetAt(Environment.TickCount64 - Math.Max(0, delayMs), out left, out right, bandsDestination);

    public bool CopySpectrumBands(double[] destination)
    {
        if (destination.Length < 31)
        {
            return false;
        }

        lock (_frameLock)
        {
            if (!_latestFrame.IsLiveSignal || (Environment.TickCount64 - _latestFrame.TimestampMs) > 400)
            {
                Array.Clear(destination, 0, 31);
                return false;
            }

            Array.Copy(_spectrum31, destination, 31);
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

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
                await RunCaptureLoopAsync(stoppingToken);
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

    private async Task RunCaptureLoopAsync(CancellationToken ct)
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
        var isFloat = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat;

        var stoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        capture.DataAvailable += (_, args) =>
        {
            if (args.BytesRecorded <= 0 || !isFloat)
            {
                return;
            }

            ProcessFloatBuffer(args.Buffer, args.BytesRecorded, channels, sampleRate);
        };

        capture.RecordingStopped += (_, _) => stoppedTcs.TrySetResult();

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
                    PublishFrame(new PluginAudioFrame(0, 0, 0, 0, 0, 0, false, false, now));
                }

                await Task.Delay(40, ct);
            }
        }
        finally
        {
            IsCapturing = false;
            try { capture.StopRecording(); } catch { }
            PublishFrame(new PluginAudioFrame(0, 0, 0, 0, 0, 0, false, false, Environment.TickCount64));
        }
    }

    private void ProcessFloatBuffer(byte[] buffer, int bytesRecorded, int channels, int sampleRate)
    {
        var floatCount = bytesRecorded / 4;
        var frames = floatCount / channels;
        if (frames <= 0)
        {
            return;
        }

        double sumL = 0, sumR = 0, peakL = 0, peakR = 0;

        for (var f = 0; f < frames; f++)
        {
            var l = BitConverter.ToSingle(buffer, (f * channels) * 4);
            var r = channels > 1 ? BitConverter.ToSingle(buffer, ((f * channels) + 1) * 4) : l;

            var absL = Math.Abs(l);
            var absR = Math.Abs(r);
            if (absL > peakL) peakL = absL;
            if (absR > peakR) peakR = absR;
            sumL += l * l;
            sumR += r * r;

            _monoRing[_ringWriteIndex] = (l + r) * 0.5f;
            _ringWriteIndex = (_ringWriteIndex + 1) & (FftSize - 1);
        }

        var now = Environment.TickCount64;
        if (peakL > 0.0005 || peakR > 0.0005)
        {
            _lastAudioTickMs = now;
        }

        // Throttle FFT + plugin dispatch to ~50-60 Hz (~16 ms)
        if (now - _lastPublishTickMs < 16)
        {
            return;
        }

        var rmsL = Math.Sqrt(sumL / frames);
        var rmsR = Math.Sqrt(sumR / frames);
        var leftLevel = ToNormalizedLevel(peakL, rmsL);
        var rightLevel = ToNormalizedLevel(peakR, rmsR);

        // Copy ring buffer in chronological order with Hann window
        var readIdx = _ringWriteIndex;
        for (var i = 0; i < FftSize; i++)
        {
            _fftReal[i] = _monoRing[readIdx] * _hannWindow[i];
            _fftImag[i] = 0.0;
            readIdx = (readIdx + 1) & (FftSize - 1);
        }

        ComputeInPlaceFft(_fftReal, _fftImag);

        // Aggregate FFT magnitudes into Bass (20-180 Hz), Mid (180-2000 Hz), Treble (2000-16000 Hz)
        var hzPerBin = (double)sampleRate / FftSize;
        var halfBins = FftSize / 2;

        double bassSum = 0, midSum = 0, trebleSum = 0;
        int bassCount = 0, midCount = 0, trebleCount = 0;

        for (var bin = 1; bin < halfBins; bin++)
        {
            var freq = bin * hzPerBin;
            var mag = Math.Sqrt((_fftReal[bin] * _fftReal[bin]) + (_fftImag[bin] * _fftImag[bin])) / (FftSize * 0.25);

            if (freq >= 20 && freq < 180)
            {
                bassSum += mag;
                bassCount++;
            }
            else if (freq >= 180 && freq < 2000)
            {
                midSum += mag;
                midCount++;
            }
            else if (freq >= 2000 && freq <= 16000)
            {
                trebleSum += mag;
                trebleCount++;
            }
        }

        var bassRaw = bassCount > 0 ? bassSum / bassCount : 0;
        var midRaw = midCount > 0 ? midSum / midCount : 0;
        var trebleRaw = trebleCount > 0 ? trebleSum / trebleCount : 0;

        // Perceptual compression for crisp, musical visual response
        var bass = Math.Clamp(Math.Pow(bassRaw * 3.4, 0.72), 0.0, 1.0);
        var mid = Math.Clamp(Math.Pow(midRaw * 5.2, 0.75), 0.0, 1.0);
        var treble = Math.Clamp(Math.Pow(trebleRaw * 9.0, 0.78), 0.0, 1.0);

        // Compute 31 logarithmically spaced ISO frequency bands (25 Hz -> 16 kHz)
        Span<double> bands31 = stackalloc double[31];
        const double minFreq = 25.0;
        const double maxFreq = 16000.0;
        var ratio = Math.Pow(maxFreq / minFreq, 1.0 / 31.0);

        for (var b = 0; b < 31; b++)
        {
            var fLow = minFreq * Math.Pow(ratio, b);
            var fHigh = fLow * ratio;
            var binLow = Math.Clamp((int)Math.Floor(fLow / hzPerBin), 1, halfBins - 1);
            var binHigh = Math.Clamp((int)Math.Ceiling(fHigh / hzPerBin), binLow, halfBins - 1);

            double peak = 0;
            for (var bin = binLow; bin <= binHigh; bin++)
            {
                var mag = Math.Sqrt((_fftReal[bin] * _fftReal[bin]) + (_fftImag[bin] * _fftImag[bin])) / (FftSize * 0.25);
                if (mag > peak) peak = mag;
            }

            // Gentle pink-noise tilt (+boost in mids/highs) so flat music looks balanced across all 31 bars
            var tilt = 2.8 + (b * 0.34);
            bands31[b] = Math.Clamp(Math.Pow(peak * tilt, 0.70), 0.0, 1.0);
        }

        lock (_frameLock)
        {
            for (var b = 0; b < 31; b++)
            {
                _spectrum31[b] = bands31[b];
            }
        }

        // Adaptive spectral onset beat detection on bass + low-mid energy
        var onsetSignal = (bass * 0.78) + (mid * 0.22);
        double avgEnergy = 0;
        if (_historyCount > 0)
        {
            for (var i = 0; i < _historyCount; i++)
            {
                avgEnergy += _bassHistory[i];
            }

            avgEnergy /= _historyCount;
        }

        _bassHistory[_historyIndex] = onsetSignal;
        _historyIndex = (_historyIndex + 1) % _bassHistory.Length;
        if (_historyCount < _bassHistory.Length)
        {
            _historyCount++;
        }

        var delta = onsetSignal - avgEnergy;
        var beatIntensity = Math.Clamp(delta * 3.2, 0.0, 1.0);
        var isBeat = onsetSignal > 0.20 &&
                     onsetSignal > (avgEnergy * 1.32 + 0.04) &&
                     (now - _lastBeatTickMs) >= 165;

        if (isBeat)
        {
            _lastBeatTickMs = now;
        }

        var isLive = (peakL > 0.0005 || peakR > 0.0005);
        PublishFrame(new PluginAudioFrame(
            leftLevel,
            rightLevel,
            bass,
            mid,
            treble,
            beatIntensity,
            isBeat,
            isLive,
            now));
    }

    private void PublishFrame(in PluginAudioFrame frame)
    {
        _lastPublishTickMs = frame.TimestampMs;
        lock (_frameLock)
        {
            _latestFrame = frame;
            _history.Add(frame.TimestampMs, frame.LeftLevel, frame.RightLevel, frame.IsLiveSignal, _spectrum31);
        }

        _pluginHost.PublishAudioFrame(in frame);
    }

    private static double ToNormalizedLevel(double peak, double rms)
    {
        var blended = (peak * 0.58) + (rms * 0.42);
        if (blended <= 0.0006)
        {
            return 0;
        }

        var db = 20.0 * Math.Log10(blended);
        return Math.Clamp((db + 40.0) / 40.0, 0.0, 1.0);
    }

    /// <summary>
    /// Standard iterative Radix-2 Cooley-Tukey FFT (allocation-free).
    /// </summary>
    private static void ComputeInPlaceFft(double[] real, double[] imag)
    {
        var n = real.Length;

        // Bit-reversal permutation
        var j = 0;
        for (var i = 0; i < n; i++)
        {
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }

            var bit = n >> 1;
            while (bit > 0 && (j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }

            j |= bit;
        }

        // Butterfly stages
        for (var s = 1; s <= FftBits; s++)
        {
            var m = 1 << s;
            var halfM = m >> 1;
            var angleStep = -2.0 * Math.PI / m;

            for (var k = 0; k < n; k += m)
            {
                for (var idx = 0; idx < halfM; idx++)
                {
                    var angle = angleStep * idx;
                    var wr = Math.Cos(angle);
                    var wi = Math.Sin(angle);

                    var even = k + idx;
                    var odd = even + halfM;

                    var tr = (wr * real[odd]) - (wi * imag[odd]);
                    var ti = (wr * imag[odd]) + (wi * real[odd]);

                    real[odd] = real[even] - tr;
                    imag[odd] = imag[even] - ti;
                    real[even] += tr;
                    imag[even] += ti;
                }
            }
        }
    }
}
