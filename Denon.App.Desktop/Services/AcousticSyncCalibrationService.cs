using System.Diagnostics;
using System.IO;
using System.Text;
using Denon.App.Core.Services;
using NAudio.Wave;

namespace Denon.App.Desktop.Services;

public sealed record AcousticCalibrationResult(
    bool Success,
    int MeasuredOffsetMs,
    double PeakSnrRatio,
    string Summary);

/// <summary>
/// 1-Click Acoustic Lip-Sync Auto-Calibration:
/// Generates a 1,750 Hz precision chirp WAV, casts it over LAN to the Denon AVR,
/// listens on the PC microphone with a narrowband Goertzel filter, and measures
/// the exact HEOS buffer + acoustic round-trip delay in milliseconds.
/// </summary>
public sealed class AcousticSyncCalibrationService
{
    private const int SampleRate = 44100;
    private const double ChirpFreqHz = 1750.0;
    private const int LeadingSilenceMs = 250;
    private const int ChirpDurationMs = 55;

    private readonly LocalAudioRelayServer _relayServer;
    private readonly ReceiverController _receiverController;
    private readonly AppLogStore _logStore;

    public AcousticSyncCalibrationService(
        LocalAudioRelayServer relayServer,
        ReceiverController receiverController,
        AppLogStore logStore)
    {
        _relayServer = relayServer;
        _receiverController = receiverController;
        _logStore = logStore;
    }

    public async Task<AcousticCalibrationResult> RunCalibrationAsync(
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable CS0618
        if (WaveInEvent.DeviceCount <= 0)
        {
            return new AcousticCalibrationResult(
                false,
                0,
                0,
                "No Windows microphone detected. Connect a mic/webcam mic to run acoustic auto-calibration.");
        }

        onProgress?.Invoke("Generating 1,750 Hz acoustic calibration chirp...");
        var wavBytes = GenerateCalibrationChirpWav();

        long streamStartTicks = 0;
        long micStartTicks = 0;
        var recordedSamples = new List<short>(SampleRate * 7);
        var sampleLock = new object();

        var url = await _relayServer.RegisterCalibrationWavAsync(
            wavBytes,
            ts => Interlocked.CompareExchange(ref streamStartTicks, ts, 0),
            cancellationToken);

        using var waveIn = new WaveInEvent
#pragma warning restore CS0618
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 20,
        };

        waveIn.DataAvailable += (_, args) =>
        {
            if (args.BytesRecorded <= 0)
            {
                return;
            }

            var count = args.BytesRecorded / 2;
            lock (sampleLock)
            {
                for (var i = 0; i < count; i++)
                {
                    recordedSamples.Add(BitConverter.ToInt16(args.Buffer, i * 2));
                }
            }
        };

        try
        {
            onProgress?.Invoke("Listening on PC microphone & casting chirp to Denon AVR...");
            micStartTicks = Stopwatch.GetTimestamp();
            waveIn.StartRecording();

            await _receiverController.PlayRelayStreamAsync(
                url,
                "Acoustic Sync Chirp",
                useLowVolume: false,
                unmute: true,
                cancellationToken);

            // Listen up to 5.2 seconds for Denon HEOS buffer + chirp playback
            for (var step = 0; step < 52; step++)
            {
                await Task.Delay(100, cancellationToken);
                if (step == 18)
                {
                    onProgress?.Invoke("Measuring acoustic chirp arrival on microphone...");
                }
            }

            waveIn.StopRecording();
        }
        catch (Exception ex)
        {
            try { waveIn.StopRecording(); } catch { }
            return new AcousticCalibrationResult(false, 0, 0, $"Calibration failed: {ex.Message}");
        }

        short[] samples;
        lock (sampleLock)
        {
            samples = [.. recordedSamples];
        }

        if (samples.Length < SampleRate)
        {
            return new AcousticCalibrationResult(false, 0, 0, "Microphone captured too little audio. Check Windows microphone permissions.");
        }

        var streamPullTicks = Interlocked.Read(ref streamStartTicks);
        var streamPullOffsetMs = streamPullTicks > micStartTicks
            ? (streamPullTicks - micStartTicks) * 1000.0 / Stopwatch.Frequency
            : 0.0;

        // Analyze recording in 10 ms windows using Goertzel narrowband detector tuned to 1,750 Hz
        const int windowSamples = SampleRate / 100; // 10 ms = 441 samples
        var windowCount = samples.Length / windowSamples;
        var energies = new double[windowCount];

        for (var w = 0; w < windowCount; w++)
        {
            energies[w] = ComputeGoertzelMagnitude(samples, w * windowSamples, windowSamples, ChirpFreqHz, SampleRate);
        }

        // Ignore the very first 200ms (mic click/startup), compute noise floor from median energy
        var startWindow = Math.Min(windowCount - 1, Math.Max(20, (int)(streamPullOffsetMs / 10.0) + 15));
        var sorted = energies.Skip(20).OrderBy(static v => v).ToArray();
        var noiseFloor = sorted.Length > 0 ? Math.Max(1e-5, sorted[sorted.Length / 2]) : 1e-5;

        double maxEnergy = 0;
        var peakWindow = -1;
        for (var w = startWindow; w < windowCount; w++)
        {
            if (energies[w] > maxEnergy)
            {
                maxEnergy = energies[w];
                peakWindow = w;
            }
        }

        var snr = maxEnergy / noiseFloor;
        if (peakWindow < 0 || snr < 3.5)
        {
            return new AcousticCalibrationResult(
                false,
                0,
                snr,
                $"Could not hear the 1,750 Hz chirp clearly above room noise (SNR {snr:0.0}×). Turn up your Denon slightly and try again.");
        }

        // Walk slightly back from peak to find the leading edge of the first chirp burst
        var threshold = Math.Max(noiseFloor * 3.0, maxEnergy * 0.38);
        var leadingWindow = peakWindow;
        for (var w = startWindow; w <= peakWindow; w++)
        {
            if (energies[w] >= threshold)
            {
                leadingWindow = w;
                break;
            }
        }

        var acousticArrivalMs = leadingWindow * 10.0;
        var measuredDelayMs = (int)Math.Round(acousticArrivalMs - streamPullOffsetMs - LeadingSilenceMs);
        measuredDelayMs = Math.Clamp(measuredDelayMs, 250, 6000);

        var summary = $"Measured HEOS + acoustic delay: {measuredDelayMs} ms (Chirp SNR {snr:0.0}×). Applied to Cinema A/V Sync!";
        _logStore.Info($"[Calibration] {summary}");
        return new AcousticCalibrationResult(true, measuredDelayMs, snr, summary);
    }

    private static double ComputeGoertzelMagnitude(short[] samples, int offset, int count, double targetFreq, int sampleRate)
    {
        var k = (int)Math.Round((count * targetFreq) / sampleRate);
        var omega = (2.0 * Math.PI * k) / count;
        var coeff = 2.0 * Math.Cos(omega);

        double sPrev = 0;
        double sPrev2 = 0;
        for (var i = 0; i < count; i++)
        {
            var x = samples[offset + i] / 32768.0;
            var s = x + (coeff * sPrev) - sPrev2;
            sPrev2 = sPrev;
            sPrev = s;
        }

        var power = (sPrev2 * sPrev2) + (sPrev * sPrev) - (coeff * sPrev * sPrev2);
        return Math.Sqrt(Math.Max(0, power)) / count;
    }

    private static byte[] GenerateCalibrationChirpWav()
    {
        const double totalSeconds = 2.0;
        var totalSamples = (int)(SampleRate * totalSeconds);
        var pcm = new short[totalSamples];

        // 3 sharp 55ms 1,750 Hz bursts at 250ms, 750ms, and 1250ms with Hann envelope
        int[] burstStartsMs = [LeadingSilenceMs, LeadingSilenceMs + 500, LeadingSilenceMs + 1000];
        var burstLenSamples = (int)(SampleRate * (ChirpDurationMs / 1000.0));

        foreach (var startMs in burstStartsMs)
        {
            var startIdx = (int)(SampleRate * (startMs / 1000.0));
            for (var i = 0; i < burstLenSamples && (startIdx + i) < totalSamples; i++)
            {
                var env = 0.5 * (1.0 - Math.Cos((2.0 * Math.PI * i) / (burstLenSamples - 1)));
                var sample = Math.Sin(2.0 * Math.PI * ChirpFreqHz * i / SampleRate) * env * 0.78;
                pcm[startIdx + i] = (short)Math.Clamp(sample * 32767.0, -32768, 32767);
            }
        }

        using var ms = new MemoryStream(44 + (totalSamples * 2));
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        var dataLen = totalSamples * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataLen);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // Mono
        bw.Write(SampleRate);
        bw.Write(SampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);

        for (var i = 0; i < totalSamples; i++)
        {
            bw.Write(pcm[i]);
        }

        bw.Flush();
        return ms.ToArray();
    }
}
