using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Denon.App.Desktop.Services;

public sealed record AudioOutputDeviceOption(string DeviceId, string FriendlyName, bool IsDefault);

public sealed class PcAudioCastService : IDisposable
{
    private readonly LocalAudioRelayServer _relayServer;
    private readonly ReceiverController _receiverController;
    private readonly YouTubeMediaService _youtubeService;
    private readonly AppLogStore _logStore;
    private readonly object _sync = new();

    private volatile bool _isCasting;
    private string _selectedDeviceId = string.Empty;
    private string _selectedDeviceName = "Default System Audio";
    private PcCastFormat _selectedFormat = PcCastFormat.Mp3_320k;
    private CancellationTokenSource? _castCts;

    // Lock-free sub-millisecond meter state read directly on VSync by the VU control
    private double _liveLeft;
    private double _liveRight;
    private long _liveSampleTick;
    private readonly AudioFrameHistory _meterHistory = new(capacity: 1024);

    public PcAudioCastService(
        LocalAudioRelayServer relayServer,
        ReceiverController receiverController,
        YouTubeMediaService youtubeService,
        AppLogStore logStore)
    {
        _relayServer = relayServer;
        _receiverController = receiverController;
        _youtubeService = youtubeService;
        _logStore = logStore;

        _relayServer.AttachLivePcAudioHandler(StreamLiveAudioToClientAsync);
        _receiverController.NonRelaySourceActivated += OnExternalSourceActivated;
    }

    public event Action<bool, string>? CastStateChanged;

    public event Action<AudioMeterSample>? AudioMeterUpdated;

    public bool IsCasting => _isCasting;

    /// <summary>True when the cast captures the Windows default output (the same device the FFT analyzer reads).</summary>
    public bool IsCastingDefaultDevice => _isCasting && string.IsNullOrWhiteSpace(_selectedDeviceId);

    /// <summary>The cast meter level from <paramref name="delayMs"/> ago: what the Denon plays now, after the HEOS buffer.</summary>
    public AudioMeterSample GetDelayedMeterSample(int delayMs) =>
        _meterHistory.TryGetAt(Environment.TickCount64 - Math.Max(0, delayMs), out var left, out var right)
            ? new AudioMeterSample(left, right, IsLiveSignal: true)
            : new AudioMeterSample(0, 0, false);

    public AudioMeterSample GetLatestMeterSample()
    {
        var tick = Volatile.Read(ref _liveSampleTick);
        if (tick <= 0 || (Environment.TickCount64 - tick) > 350)
        {
            return new AudioMeterSample(0, 0, false);
        }

        return new AudioMeterSample(
            Volatile.Read(ref _liveLeft),
            Volatile.Read(ref _liveRight),
            IsLiveSignal: true);
    }

    public IReadOnlyList<AudioOutputDeviceOption> EnumerateOutputDevices()
    {
        var devices = new List<AudioOutputDeviceOption>
        {
            new(string.Empty, "Default System Output (All PC Audio)", true),
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            }
            catch
            {
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var isDef = string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase);
                    devices.Add(new AudioOutputDeviceOption(
                        device.ID,
                        isDef ? $"{device.FriendlyName} (Default)" : device.FriendlyName,
                        isDef));
                }
            }
        }
        catch (Exception ex)
        {
            _logStore.Warning($"Could not enumerate Windows audio devices: {ex.Message}");
        }

        return devices;
    }

    public async Task StartCastingAsync(
        string? deviceId,
        string deviceName,
        PcCastFormat format,
        bool useLowVolume,
        bool unmute,
        CancellationToken cancellationToken = default)
    {
        await StopCastingAsync();

        var ffmpegPath = _youtubeService.FindFfmpegPath();
        var effectiveFormat = format == PcCastFormat.Mp3_320k && string.IsNullOrWhiteSpace(ffmpegPath)
            ? PcCastFormat.Wav_Lossless
            : format;

        lock (_sync)
        {
            _selectedDeviceId = deviceId ?? string.Empty;
            _selectedDeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Default System Audio" : deviceName;
            _selectedFormat = effectiveFormat;
            _castCts = new CancellationTokenSource();
            _isCasting = true;
        }

        CastStateChanged?.Invoke(true, $"Starting PC Audio Cast ({_selectedDeviceName})...");

        try
        {
            var (relayUrl, _) = await _relayServer.RegisterLivePcCastAsync(effectiveFormat, cancellationToken);
            var formatLabel = effectiveFormat == PcCastFormat.Wav_Lossless ? "Lossless WAV" : "MP3 320k";
            await _receiverController.PlayRelayStreamAsync(
                relayUrl,
                $"PC Audio ({formatLabel})",
                useLowVolume,
                unmute,
                cancellationToken);

            CastStateChanged?.Invoke(true, $"Live casting '{_selectedDeviceName}' ({formatLabel}) to Denon AVR.");
            _logStore.Info($"Started PC Audio Cast ({_selectedDeviceName}, {formatLabel}).");
        }
        catch
        {
            await StopCastingAsync();
            throw;
        }
    }

    public Task StopCastingAsync()
    {
        var wasCasting = false;
        lock (_sync)
        {
            if (_isCasting)
            {
                wasCasting = true;
                _isCasting = false;
            }

            try { _castCts?.Cancel(); } catch { }
            _castCts?.Dispose();
            _castCts = null;
        }

        if (wasCasting)
        {
            _relayServer.CancelAllActiveStreams();
            PublishMeter(0, 0, isLive: false);
            CastStateChanged?.Invoke(false, "PC Audio Cast stopped.");
            _logStore.Info("Stopped PC Audio Cast.");
        }

        return Task.CompletedTask;
    }

    private async Task StreamLiveAudioToClientAsync(Stream clientStream, PcCastFormat format, CancellationToken requestToken)
    {
        CancellationTokenSource? linkedCts;
        string deviceId;
        lock (_sync)
        {
            if (!_isCasting || _castCts is null)
            {
                return;
            }

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(requestToken, _castCts.Token);
            deviceId = _selectedDeviceId;
        }

        using (linkedCts)
        {
            var ct = linkedCts.Token;
            using var enumerator = new MMDeviceEnumerator();
            using var device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);

#pragma warning disable CS0618
            using var capture = new WasapiLoopbackCapture(device);
#pragma warning restore CS0618
            var waveFormat = capture.WaveFormat;
            var sampleRate = waveFormat.SampleRate;
            var channels = waveFormat.Channels;

            var pcmChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(24)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            var lastAudioTick = Environment.TickCount64;

            capture.DataAvailable += (_, args) =>
            {
                if (args.BytesRecorded <= 0)
                {
                    return;
                }

                lastAudioTick = Environment.TickCount64;

                // Zero-throttle, zero-allocation peak+RMS meter computation on every ~10ms WASAPI packet
                var (left, right) = ComputeStereoLevels(args.Buffer, args.BytesRecorded, waveFormat);
                PublishMeter(left, right, isLive: true);

                var copy = new byte[args.BytesRecorded];
                Buffer.BlockCopy(args.Buffer, 0, copy, 0, args.BytesRecorded);
                pcmChannel.Writer.TryWrite(copy);
            };

            capture.RecordingStopped += (_, _) => pcmChannel.Writer.TryComplete();
            capture.StartRecording();

            // Silence keepalive loop paced 1:1 to real audio time (100ms silence every 100ms idle)
            // with its own linked CTS cancelled BEFORE awaiting on disconnect so WASAPI releases cleanly.
            var silenceFrameBytes = (sampleRate / 10) * waveFormat.BlockAlign; // 100ms of silence
            var silenceBuffer = new byte[Math.Max(waveFormat.BlockAlign * 4, silenceFrameBytes)];
            using var silenceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var silenceCt = silenceCts.Token;
            var silenceTask = Task.Run(async () =>
            {
                try
                {
                    while (!silenceCt.IsCancellationRequested)
                    {
                        await Task.Delay(100, silenceCt);
                        var idleMs = Environment.TickCount64 - Volatile.Read(ref lastAudioTick);
                        if (idleMs > 90)
                        {
                            PublishMeter(0, 0, isLive: true);
                        }

                        if (idleMs >= 150)
                        {
                            pcmChannel.Writer.TryWrite(silenceBuffer);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, silenceCt);

            try
            {
                var ffmpegPath = _youtubeService.FindFfmpegPath();
                if (format == PcCastFormat.Mp3_320k && !string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    await PumpViaFfmpegMp3Async(pcmChannel.Reader, clientStream, ffmpegPath, waveFormat, ct);
                }
                else
                {
                    await PumpViaRawWavAsync(pcmChannel.Reader, clientStream, waveFormat, ct);
                }
            }
            finally
            {
                try { silenceCts.Cancel(); } catch { }
                try { capture.StopRecording(); } catch { }
                pcmChannel.Writer.TryComplete();
                try { await silenceTask; } catch { }
                PublishMeter(0, 0, isLive: false);
            }
        }
    }

    private static async Task PumpViaFfmpegMp3Async(
        ChannelReader<byte[]> reader,
        Stream clientStream,
        string ffmpegPath,
        WaveFormat sourceFormat,
        CancellationToken ct)
    {
        var inputFmt = ResolveFfmpegInputFormat(sourceFormat);
        var sampleRate = sourceFormat.SampleRate;
        var channels = Math.Max(1, sourceFormat.Channels);
        using var ffmpeg = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -loglevel error -f {inputFmt} -ar {sampleRate} -ac {channels} -i pipe:0 -c:a libmp3lame -b:a 320k -ar 44100 -ac 2 -flush_packets 1 -f mp3 pipe:1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        if (!ffmpeg.Start())
        {
            throw new InvalidOperationException("Failed to start FFmpeg for PC Audio Cast.");
        }

        try
        {
            var writeTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var chunk in reader.ReadAllAsync(ct))
                    {
                        await ffmpeg.StandardInput.BaseStream.WriteAsync(chunk, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                finally
                {
                    try { ffmpeg.StandardInput.Close(); } catch { }
                }
            }, ct);

            var outBuf = new byte[16384];
            while (!ct.IsCancellationRequested)
            {
                var read = await ffmpeg.StandardOutput.BaseStream.ReadAsync(outBuf, ct);
                if (read <= 0)
                {
                    break;
                }

                await clientStream.WriteAsync(outBuf.AsMemory(0, read), ct);
                await clientStream.FlushAsync(ct);
            }

            await writeTask;
        }
        finally
        {
            try { if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static async Task PumpViaRawWavAsync(
        ChannelReader<byte[]> reader,
        Stream clientStream,
        WaveFormat sourceFormat,
        CancellationToken ct)
    {
        // Write streaming 16-bit PCM Stereo WAV header (with large streaming length)
        const int outChannels = 2;
        const int bitsPerSample = 16;
        var sampleRate = sourceFormat.SampleRate;
        var byteRate = sampleRate * outChannels * (bitsPerSample / 8);
        var blockAlign = (short)(outChannels * (bitsPerSample / 8));
        const uint streamDataLen = 0x7FFFF000;

        using (var headerStream = new MemoryStream(44))
        using (var bw = new BinaryWriter(headerStream, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(streamDataLen + 36);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write((short)outChannels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write((short)bitsPerSample);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(streamDataLen);
            bw.Flush();
            await clientStream.WriteAsync(headerStream.ToArray(), ct);
            await clientStream.FlushAsync(ct);
        }

        await foreach (var chunk in reader.ReadAllAsync(ct))
        {
            var outBytes = ConvertToStereo16Pcm(chunk, sourceFormat);
            if (outBytes.Length > 0)
            {
                await clientStream.WriteAsync(outBytes, ct);
            }
        }
    }

    internal static string ResolveFfmpegInputFormat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")))
        {
            return format.BitsPerSample == 64 ? "f64le" : "f32le";
        }

        return format.BitsPerSample switch
        {
            24 => "s24le",
            32 => "s32le",
            _ => "s16le",
        };
    }

    internal static byte[] ConvertToStereo16Pcm(byte[] chunk, WaveFormat sourceFormat)
    {
        var srcChannels = Math.Max(1, sourceFormat.Channels);
        var bytesPerSample = Math.Max(1, sourceFormat.BitsPerSample / 8);
        var frameSize = srcChannels * bytesPerSample;
        if (frameSize <= 0 || chunk.Length < frameSize)
        {
            return [];
        }

        var isFloat = sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (sourceFormat is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));

        // Fast pass-through only when input is already 16-bit Stereo PCM!
        if (!isFloat && sourceFormat.BitsPerSample == 16 && srcChannels == 2)
        {
            return chunk;
        }

        var frames = chunk.Length / frameSize;
        var outBytes = new byte[frames * 4];
        var outIdx = 0;

        for (var f = 0; f < frames; f++)
        {
            var frameOffset = f * frameSize;
            float left = ReadNormalizedSample(chunk, frameOffset, bytesPerSample, isFloat);
            float right = srcChannels > 1
                ? ReadNormalizedSample(chunk, frameOffset + bytesPerSample, bytesPerSample, isFloat)
                : left;

            // Downmix extra surround/center channels cleanly into L/R when >2 channels
            if (srcChannels > 2)
            {
                var center = ReadNormalizedSample(chunk, frameOffset + (2 * bytesPerSample), bytesPerSample, isFloat);
                left = Math.Clamp(left + (center * 0.707f), -1f, 1f);
                right = Math.Clamp(right + (center * 0.707f), -1f, 1f);
                for (var ch = 3; ch < srcChannels; ch++)
                {
                    var extra = ReadNormalizedSample(chunk, frameOffset + (ch * bytesPerSample), bytesPerSample, isFloat) * 0.5f;
                    if (ch % 2 == 0) left = Math.Clamp(left + extra, -1f, 1f);
                    else right = Math.Clamp(right + extra, -1f, 1f);
                }
            }

            var l16 = (short)Math.Clamp(left * 32767f, -32768f, 32767f);
            var r16 = (short)Math.Clamp(right * 32767f, -32768f, 32767f);

            outBytes[outIdx++] = (byte)(l16 & 0xFF);
            outBytes[outIdx++] = (byte)((l16 >> 8) & 0xFF);
            outBytes[outIdx++] = (byte)(r16 & 0xFF);
            outBytes[outIdx++] = (byte)((r16 >> 8) & 0xFF);
        }

        return outBytes;
    }

    private static float ReadNormalizedSample(byte[] buffer, int offset, int bytesPerSample, bool isFloat)
    {
        if (isFloat && bytesPerSample == 4)
        {
            return Math.Clamp(BitConverter.ToSingle(buffer, offset), -1f, 1f);
        }

        if (isFloat && bytesPerSample == 8)
        {
            return (float)Math.Clamp(BitConverter.ToDouble(buffer, offset), -1.0, 1.0);
        }

        if (bytesPerSample == 2)
        {
            return BitConverter.ToInt16(buffer, offset) / 32768f;
        }

        if (bytesPerSample == 3)
        {
            int sample24 = buffer[offset] | (buffer[offset + 1] << 8) | ((sbyte)buffer[offset + 2] << 16);
            return sample24 / 8388608f;
        }

        if (bytesPerSample == 4)
        {
            return BitConverter.ToInt32(buffer, offset) / 2147483648f;
        }

        return 0f;
    }

    private void PublishMeter(double left, double right, bool isLive)
    {
        _meterHistory.Add(Environment.TickCount64, left, right, isLive);
        Volatile.Write(ref _liveLeft, left);
        Volatile.Write(ref _liveRight, right);
        Volatile.Write(ref _liveSampleTick, isLive ? Environment.TickCount64 : 0);
        AudioMeterUpdated?.Invoke(new AudioMeterSample(left, right, isLive));
    }

    private static (double Left, double Right) ComputeStereoLevels(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded < 8 || format.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            return (0, 0);
        }

        var channels = Math.Max(1, format.Channels);
        var floatCount = bytesRecorded / 4;
        var frames = floatCount / channels;
        if (frames <= 0)
        {
            return (0, 0);
        }

        double sumL = 0;
        double sumR = 0;
        double peakL = 0;
        double peakR = 0;
        var step = Math.Max(1, frames / 256);
        var samples = 0;

        for (var f = 0; f < frames; f += step)
        {
            var l = Math.Abs(BitConverter.ToSingle(buffer, (f * channels) * 4));
            var r = channels > 1 ? Math.Abs(BitConverter.ToSingle(buffer, ((f * channels) + 1) * 4)) : l;
            if (l > peakL) peakL = l;
            if (r > peakR) peakR = r;
            sumL += l * l;
            sumR += r * r;
            samples++;
        }

        if (samples == 0)
        {
            return (0, 0);
        }

        var rmsL = Math.Sqrt(sumL / samples);
        var rmsR = Math.Sqrt(sumR / samples);

        // Blend fast Peak transient with RMS loudness for instantaneous yet musical VU response
        static double ToVu(double peak, double rms)
        {
            var blended = (peak * 0.58) + (rms * 0.42);
            if (blended <= 0.0006)
            {
                return 0;
            }

            var db = 20.0 * Math.Log10(blended);
            return Math.Clamp((db + 40.0) / 40.0, 0.0, 1.0);
        }

        return (ToVu(peakL, rmsL), ToVu(peakR, rmsR));
    }

    private void OnExternalSourceActivated()
    {
        if (_isCasting)
        {
            _ = StopCastingAsync();
        }
    }

    public void Dispose()
    {
        _receiverController.NonRelaySourceActivated -= OnExternalSourceActivated;
        _ = StopCastingAsync();
    }
}
