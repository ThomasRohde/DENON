namespace Denon.App.Core.Services.Metering;

/// <summary>
/// Turns decoded interleaved stereo float PCM into <see cref="MeterTimeline"/> frames: one frame per
/// <c>hopFrames</c> samples, with the same level and band math as the live PC analyzer.
/// </summary>
public sealed class PcmMeterAnalyzer
{
    private readonly MeterTimeline _timeline;
    private readonly double _sampleRate;
    private readonly int _hopFrames;
    private readonly float[] _monoRing = new float[AudioMeterMath.FftSize];
    private readonly double[] _fftReal = new double[AudioMeterMath.FftSize];
    private readonly double[] _fftImag = new double[AudioMeterMath.FftSize];
    private readonly double[] _bands = new double[AudioMeterMath.BandCount];
    private int _ringWriteIndex;
    private int _framesInHop;
    private double _sumL, _sumR, _peakL, _peakR;

    public PcmMeterAnalyzer(MeterTimeline timeline, int sampleRate, int hopFrames)
    {
        _timeline = timeline;
        _sampleRate = sampleRate;
        _hopFrames = Math.Max(64, hopFrames);
    }

    /// <summary>False once the timeline is full; the caller can stop decoding.</summary>
    public bool CanAcceptMore => !_timeline.IsFull;

    /// <summary>Feeds interleaved stereo samples (L, R, L, R…).</summary>
    public void Process(ReadOnlySpan<float> interleavedStereo)
    {
        var frames = interleavedStereo.Length / 2;
        for (var f = 0; f < frames; f++)
        {
            var l = interleavedStereo[f * 2];
            var r = interleavedStereo[(f * 2) + 1];

            var absL = Math.Abs(l);
            var absR = Math.Abs(r);
            if (absL > _peakL) _peakL = absL;
            if (absR > _peakR) _peakR = absR;
            _sumL += l * l;
            _sumR += r * r;

            _monoRing[_ringWriteIndex] = (l + r) * 0.5f;
            _ringWriteIndex = (_ringWriteIndex + 1) & (AudioMeterMath.FftSize - 1);

            if (++_framesInHop >= _hopFrames)
            {
                EmitFrame();
            }
        }
    }

    /// <summary>Writes the last partial hop (if it holds at least a quarter hop).</summary>
    public void Flush()
    {
        if (_framesInHop >= _hopFrames / 4)
        {
            EmitFrame();
        }

        _framesInHop = 0;
    }

    private void EmitFrame()
    {
        var n = Math.Max(1, _framesInHop);
        var isSilent = _peakL <= AudioMeterMath.SilencePeak && _peakR <= AudioMeterMath.SilencePeak;

        if (isSilent)
        {
            Array.Clear(_bands);
            _timeline.Append(0, 0, _bands);
        }
        else
        {
            var left = AudioMeterMath.ToNormalizedLevel(_peakL, Math.Sqrt(_sumL / n));
            var right = AudioMeterMath.ToNormalizedLevel(_peakR, Math.Sqrt(_sumR / n));

            var hann = AudioMeterMath.Hann;
            var readIdx = _ringWriteIndex;
            for (var i = 0; i < AudioMeterMath.FftSize; i++)
            {
                _fftReal[i] = _monoRing[readIdx] * hann[i];
                _fftImag[i] = 0.0;
                readIdx = (readIdx + 1) & (AudioMeterMath.FftSize - 1);
            }

            AudioMeterMath.ComputeInPlaceFft(_fftReal, _fftImag);
            AudioMeterMath.ComputeBands31(_fftReal, _fftImag, _sampleRate, _bands);
            _timeline.Append(left, right, _bands);
        }

        _framesInHop = 0;
        _sumL = _sumR = _peakL = _peakR = 0;
    }
}
