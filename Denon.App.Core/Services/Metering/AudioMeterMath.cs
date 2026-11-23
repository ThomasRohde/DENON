namespace Denon.App.Core.Services.Metering;

/// <summary>
/// Shared meter math for the VU meters and the 31-band RTA. The live WASAPI analyzer and the app-stream
/// analyzer both use it, so DENON and PC meters look the same.
/// </summary>
public static class AudioMeterMath
{
    public const int FftSize = 512;
    public const int FftBits = 9; // 2^9 = 512
    public const int BandCount = 31;

    /// <summary>Below this peak a block counts as silence.</summary>
    public const double SilencePeak = 0.0005;

    private static readonly float[] HannWindow = BuildHann();

    /// <summary>Hann window for <see cref="FftSize"/> samples.</summary>
    public static ReadOnlySpan<float> Hann => HannWindow;

    /// <summary>Peak/RMS blend mapped to 0..1 over a 40 dB range (-40 dBFS = 0, 0 dBFS = 1).</summary>
    public static double ToNormalizedLevel(double peak, double rms)
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
    /// 31 log-spaced bands (25 Hz → 16 kHz) from an FFT result: the peak magnitude in each band,
    /// with a gentle pink-noise tilt and perceptual compression.
    /// </summary>
    public static void ComputeBands31(double[] real, double[] imag, double sampleRate, Span<double> destination)
    {
        var hzPerBin = sampleRate / FftSize;
        var halfBins = FftSize / 2;
        const double minFreq = 25.0;
        const double maxFreq = 16000.0;
        var ratio = Math.Pow(maxFreq / minFreq, 1.0 / BandCount);

        for (var b = 0; b < BandCount; b++)
        {
            var fLow = minFreq * Math.Pow(ratio, b);
            var fHigh = fLow * ratio;
            var binLow = Math.Clamp((int)Math.Floor(fLow / hzPerBin), 1, halfBins - 1);
            var binHigh = Math.Clamp((int)Math.Ceiling(fHigh / hzPerBin), binLow, halfBins - 1);

            double peak = 0;
            for (var bin = binLow; bin <= binHigh; bin++)
            {
                var mag = Magnitude(real, imag, bin);
                if (mag > peak) peak = mag;
            }

            // Gentle pink-noise tilt (+boost in mids/highs) so flat music looks balanced across all 31 bars
            var tilt = 2.8 + (b * 0.34);
            destination[b] = Math.Clamp(Math.Pow(peak * tilt, 0.70), 0.0, 1.0);
        }
    }

    /// <summary>Normalized magnitude of one FFT bin.</summary>
    public static double Magnitude(double[] real, double[] imag, int bin) =>
        Math.Sqrt((real[bin] * real[bin]) + (imag[bin] * imag[bin])) / (FftSize * 0.25);

    /// <summary>Standard iterative radix-2 Cooley-Tukey FFT for <see cref="FftSize"/> points (allocation-free).</summary>
    public static void ComputeInPlaceFft(double[] real, double[] imag)
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

    private static float[] BuildHann()
    {
        var w = new float[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            w[i] = (float)(0.5 * (1.0 - Math.Cos((2.0 * Math.PI * i) / (FftSize - 1))));
        }

        return w;
    }
}
