using Denon.App.Core.Services.Metering;
using Denon.App.PluginSdk;

namespace Denon.App.Desktop.Services;

/// <summary>
/// Allocation-free real-time FFT analyzer shared by WASAPI loopback, PC Cast capture, and Room Mic capture.
/// Computes 31 ISO RTA bands (via <see cref="AudioMeterMath"/>), 3-band Bass/Mid/Treble energy, and adaptive
/// beat onset detection for <see cref="PluginAudioFrame"/>.
/// </summary>
public sealed class AudioSpectrumAnalyzer
{
    private const int FftSize = AudioMeterMath.FftSize;

    private readonly double[] _fftReal = new double[FftSize];
    private readonly double[] _fftImag = new double[FftSize];
    private readonly float[] _monoRing = new float[FftSize];
    private int _ringWriteIndex;

    // Adaptive beat onset history (rolling ~0.8s bass+low-mid energy buffer)
    private readonly double[] _onsetHistory = new double[42];
    private int _historyIndex;
    private int _historyCount;
    private long _lastBeatTickMs;

    /// <summary>Writes one mono sample into the circular FFT window.</summary>
    public void PushMonoSample(float sample)
    {
        _monoRing[_ringWriteIndex] = sample;
        _ringWriteIndex = (_ringWriteIndex + 1) & (FftSize - 1);
    }

    /// <summary>Clears the ring buffer and beat history (e.g. when a capture stream stops or gates closed).</summary>
    public void Reset()
    {
        Array.Clear(_monoRing);
        _ringWriteIndex = 0;
        Array.Clear(_onsetHistory);
        _historyIndex = 0;
        _historyCount = 0;
        _lastBeatTickMs = 0;
    }

    /// <summary>
    /// Runs the 512-point Hann-windowed FFT on the current ring buffer, fills <paramref name="bands31Destination"/>
    /// with 31 log-spaced ISO bands, and builds a beat-detected <see cref="PluginAudioFrame"/>.
    /// </summary>
    public PluginAudioFrame Analyze(
        int sampleRate,
        double leftLevel,
        double rightLevel,
        bool isLive,
        long timestampMs,
        Span<double> bands31Destination)
    {
        var effectiveRate = Math.Max(8000, sampleRate);
        var hann = AudioMeterMath.Hann;

        var readIdx = _ringWriteIndex;
        for (var i = 0; i < FftSize; i++)
        {
            _fftReal[i] = _monoRing[readIdx] * hann[i];
            _fftImag[i] = 0.0;
            readIdx = (readIdx + 1) & (FftSize - 1);
        }

        AudioMeterMath.ComputeInPlaceFft(_fftReal, _fftImag);

        // Aggregate FFT magnitudes into Bass (20-180 Hz), Mid (180-2000 Hz), Treble (2000-16000 Hz)
        var hzPerBin = (double)effectiveRate / FftSize;
        var halfBins = FftSize / 2;

        double bassSum = 0, midSum = 0, trebleSum = 0;
        int bassCount = 0, midCount = 0, trebleCount = 0;

        for (var bin = 1; bin < halfBins; bin++)
        {
            var freq = bin * hzPerBin;
            var mag = AudioMeterMath.Magnitude(_fftReal, _fftImag, bin);

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

        var bass = Math.Clamp(Math.Pow(bassRaw * 3.4, 0.72), 0.0, 1.0);
        var mid = Math.Clamp(Math.Pow(midRaw * 5.2, 0.75), 0.0, 1.0);
        var treble = Math.Clamp(Math.Pow(trebleRaw * 9.0, 0.78), 0.0, 1.0);

        if (bands31Destination.Length >= AudioMeterMath.BandCount)
        {
            AudioMeterMath.ComputeBands31(_fftReal, _fftImag, effectiveRate, bands31Destination);
        }

        var (beatIntensity, isBeat) = DetectBeat(bass, mid, timestampMs);

        return new PluginAudioFrame(
            leftLevel,
            rightLevel,
            bass,
            mid,
            treble,
            beatIntensity,
            isBeat,
            isLive,
            timestampMs);
    }

    /// <summary>
    /// Derives a <see cref="PluginAudioFrame"/> (3-band energy + adaptive beat onset) from an already-computed
    /// 31-band ISO spectrum (25 Hz → 16 kHz), such as synced frames from <see cref="AppStreamMeterService"/>.
    /// </summary>
    public PluginAudioFrame AnalyzeFromBands31(
        double leftLevel,
        double rightLevel,
        ReadOnlySpan<double> bands31,
        bool isLive,
        long timestampMs)
    {
        if (!isLive || bands31.Length < AudioMeterMath.BandCount)
        {
            return new PluginAudioFrame(0, 0, 0, 0, 0, 0, false, false, timestampMs);
        }

        // 31 log-spaced bands from 25 Hz to 16 kHz:
        // 0..8  ≈ 25..180 Hz (Bass)
        // 9..19 ≈ 180..2000 Hz (Mid)
        // 20..30 ≈ 2000..16000 Hz (Treble)
        double bassSum = 0, midSum = 0, trebleSum = 0;
        for (var i = 0; i <= 8; i++) bassSum += bands31[i];
        for (var i = 9; i <= 19; i++) midSum += bands31[i];
        for (var i = 20; i < AudioMeterMath.BandCount; i++) trebleSum += bands31[i];

        var bass = Math.Clamp(bassSum / 9.0, 0.0, 1.0);
        var mid = Math.Clamp(midSum / 11.0, 0.0, 1.0);
        var treble = Math.Clamp(trebleSum / 11.0, 0.0, 1.0);
        var (beatIntensity, isBeat) = DetectBeat(bass, mid, timestampMs);

        return new PluginAudioFrame(
            Math.Clamp(leftLevel, 0.0, 1.0),
            Math.Clamp(rightLevel, 0.0, 1.0),
            bass,
            mid,
            treble,
            beatIntensity,
            isBeat,
            IsLiveSignal: true,
            timestampMs);
    }

    private (double BeatIntensity, bool IsBeat) DetectBeat(double bass, double mid, long timestampMs)
    {
        var onsetSignal = (bass * 0.78) + (mid * 0.22);
        double avgEnergy = 0;
        if (_historyCount > 0)
        {
            for (var i = 0; i < _historyCount; i++)
            {
                avgEnergy += _onsetHistory[i];
            }

            avgEnergy /= _historyCount;
        }

        _onsetHistory[_historyIndex] = onsetSignal;
        _historyIndex = (_historyIndex + 1) % _onsetHistory.Length;
        if (_historyCount < _onsetHistory.Length)
        {
            _historyCount++;
        }

        var delta = onsetSignal - avgEnergy;
        var beatIntensity = Math.Clamp(delta * 3.2, 0.0, 1.0);
        var isBeat = onsetSignal > 0.20 &&
                     onsetSignal > (avgEnergy * 1.32 + 0.04) &&
                     (timestampMs - _lastBeatTickMs) >= 165;

        if (isBeat)
        {
            _lastBeatTickMs = timestampMs;
        }

        return (beatIntensity, isBeat);
    }
}
