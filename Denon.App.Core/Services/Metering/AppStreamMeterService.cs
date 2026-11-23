using System.Diagnostics;
using System.Runtime.InteropServices;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services.Metering;

/// <summary>
/// Real, synced meters for audio that this app streams to the Denon from YouTube / YouTube Music and the
/// local jukebox. When a track starts, a separate ffmpeg process decodes a copy of the same source to PCM
/// (the stream to the Denon is not touched), and <see cref="PcmMeterAnalyzer"/> builds a level + 31-band
/// timeline by media time. The UI reads the frame at the position the speakers play now
/// (playback clock minus the measured HEOS delay).
/// </summary>
public sealed class AppStreamMeterService : IDisposable
{
    public const int SampleRate = 44100;
    public const int HopFrames = 1024; // ≈23 ms per frame
    public static readonly TimeSpan MaxAnalyzedDuration = TimeSpan.FromMinutes(90); // ≈19 MB of frames

    private readonly YouTubeQueueService _youtubeQueue;
    private readonly LocalMediaJukeboxService _jukebox;
    private readonly YouTubeMediaService _youtubeMedia;
    private readonly AppLogStore _logStore;
    private readonly object _sync = new();
    private ActiveAnalysis? _active;
    private CancellationTokenSource? _cts;
    private bool _warnedNoFfmpeg;

    public AppStreamMeterService(
        YouTubeQueueService youtubeQueue,
        LocalMediaJukeboxService jukebox,
        YouTubeMediaService youtubeMedia,
        AppLogStore logStore)
    {
        _youtubeQueue = youtubeQueue;
        _jukebox = jukebox;
        _youtubeMedia = youtubeMedia;
        _logStore = logStore;

        _youtubeQueue.TrackStarted += OnYouTubeTrackStarted;
        _jukebox.JukeboxChanged += OnJukeboxChanged;
    }

    public static double HopSeconds => (double)HopFrames / SampleRate;

    /// <summary>
    /// Level and bands that the Denon speakers play now, when the app streams the current track and that
    /// time is analyzed. False means "no synced frame" (the caller uses the simulation).
    /// </summary>
    public bool TryGetFrame(int heosDelayMs, out double left, out double right, double[]? bandsDestination = null)
    {
        left = 0;
        right = 0;
        var active = Volatile.Read(ref _active);
        if (active is null)
        {
            return false;
        }

        double position;
        if (active.IsYouTube)
        {
            if (!string.Equals(_youtubeQueue.CurrentVideoId, active.SourceId, StringComparison.Ordinal))
            {
                return false;
            }

            position = _youtubeQueue.GetEstimatedPlaybackPositionSeconds(heosDelayMs);
        }
        else
        {
            if (!string.Equals(_jukebox.CurrentTrackFilePath, active.SourceId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            position = _jukebox.GetEstimatedPlaybackPositionSeconds(heosDelayMs);
        }

        return active.Timeline.TryGetAt(position, out left, out right, bandsDestination);
    }

    private void OnYouTubeTrackStarted(YouTubeTrackItem track)
    {
        if (string.IsNullOrWhiteSpace(track.VideoId))
        {
            return;
        }

        var videoId = track.VideoId;
        StartAnalysis(isYouTube: true, videoId, track.Title, (ffmpeg, timeline, ct) => DecodeToTimelineAsync(
            ffmpeg,
            inputFile: null,
            openInput: async token =>
            {
                var info = await _youtubeMedia.ResolveAudioStreamInfoAsync(videoId, preferMp4Direct: true, token);
                return await _youtubeMedia.OpenAudioStreamAsync(info, token);
            },
            timeline,
            ct));
    }

    private void OnJukeboxChanged(LocalJukeboxSnapshot snapshot)
    {
        if (snapshot.CurrentTrack is not { } track || string.IsNullOrWhiteSpace(track.FilePath))
        {
            return;
        }

        var path = track.FilePath;
        StartAnalysis(isYouTube: false, path, track.Title, (ffmpeg, timeline, ct) => DecodeToTimelineAsync(
            ffmpeg,
            inputFile: path,
            openInput: null,
            timeline,
            ct));
    }

    private void StartAnalysis(
        bool isYouTube,
        string sourceId,
        string title,
        Func<string, MeterTimeline, CancellationToken, Task<bool>> decode)
    {
        CancellationTokenSource cts;
        MeterTimeline timeline;
        lock (_sync)
        {
            // Same track again (repeat, or a jukebox refresh): keep the timeline.
            if (_active is { } current &&
                current.IsYouTube == isYouTube &&
                string.Equals(current.SourceId, sourceId, isYouTube ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) &&
                !current.Timeline.IsFailed)
            {
                return;
            }

            var ffmpegPath = _youtubeMedia.FindFfmpegPath();
            CancelLocked();
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                if (!_warnedNoFfmpeg)
                {
                    _warnedNoFfmpeg = true;
                    _logStore.Info("Synced Denon meters need FFmpeg; meters use the simulation for YouTube and the jukebox.");
                }

                return;
            }

            var maxFrames = (int)Math.Ceiling(MaxAnalyzedDuration.TotalSeconds / HopSeconds);
            timeline = new MeterTimeline((isYouTube ? "yt:" : "local:") + sourceId, HopSeconds, maxFrames);
            cts = new CancellationTokenSource();
            _cts = cts;
            Volatile.Write(ref _active, new ActiveAnalysis(timeline, isYouTube, sourceId));

            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var ok = await decode(ffmpegPath, timeline, token);
                    if (!token.IsCancellationRequested)
                    {
                        timeline.MarkComplete(failed: !ok);
                        _logStore.Info(ok
                            ? $"Synced Denon meters ready for '{title}' ({timeline.AnalyzedSeconds:0} s of audio analyzed in {sw.Elapsed.TotalSeconds:0.0} s)."
                            : $"Synced Denon meters stopped for '{title}' after {timeline.AnalyzedSeconds:0} s of audio; the rest uses the simulation.");
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    timeline.MarkComplete(failed: true);
                }
                catch (Exception ex)
                {
                    timeline.MarkComplete(failed: true);
                    _logStore.Warning($"Synced Denon meter analysis failed for '{title}': {ex.Message}");
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Decodes <paramref name="inputFile"/> (or the stream from <paramref name="openInput"/>, piped to stdin)
    /// with ffmpeg to 44.1 kHz stereo float PCM and fills <paramref name="timeline"/>. Returns true when the
    /// whole input was decoded (or the timeline is full).
    /// </summary>
    public static async Task<bool> DecodeToTimelineAsync(
        string ffmpegPath,
        string? inputFile,
        Func<CancellationToken, Task<Stream>>? openInput,
        MeterTimeline timeline,
        CancellationToken cancellationToken)
    {
        if (inputFile is null && openInput is null)
        {
            throw new ArgumentException("Give an input file or an input stream.");
        }

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = openInput is not null,
        };

        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error" })
        {
            psi.ArgumentList.Add(arg);
        }

        if (openInput is null)
        {
            psi.ArgumentList.Add("-nostdin");
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputFile ?? "pipe:0");
        foreach (var arg in new[] { "-vn", "-ac", "2", "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1" })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
        try
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var pumpTask = openInput is null
                ? Task.CompletedTask
                : Task.Run(async () =>
                {
                    try
                    {
                        await using var source = await openInput(cancellationToken);
                        await source.CopyToAsync(process.StandardInput.BaseStream, 65536, cancellationToken);
                    }
                    catch (Exception)
                    {
                        // Cancelled, ffmpeg closed stdin (timeline full / decode error), or the source failed.
                        // ffmpeg then ends early and the caller reports a partial analysis.
                    }
                    finally
                    {
                        try { process.StandardInput.Close(); } catch { }
                    }
                }, CancellationToken.None);

            var analyzer = new PcmMeterAnalyzer(timeline, SampleRate, HopFrames);
            var stdout = process.StandardOutput.BaseStream;
            var buffer = new byte[64 * 1024];
            var carry = 0;
            var full = false;

            while (true)
            {
                var read = await stdout.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                var total = carry + read;
                var usable = total - (total % 8); // 8 bytes per stereo float frame
                analyzer.Process(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, usable)));
                carry = total - usable;
                if (carry > 0)
                {
                    Buffer.BlockCopy(buffer, usable, buffer, 0, carry);
                }

                if (!analyzer.CanAcceptMore)
                {
                    full = true;
                    break;
                }
            }

            analyzer.Flush();

            if (full)
            {
                return true;
            }

            await process.WaitForExitAsync(cancellationToken);
            await pumpTask;
            try { await stderrTask; } catch { }
            return process.ExitCode == 0 && timeline.FrameCount > 0;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    private void CancelLocked()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        Volatile.Write(ref _active, null);
    }

    public void Dispose()
    {
        _youtubeQueue.TrackStarted -= OnYouTubeTrackStarted;
        _jukebox.JukeboxChanged -= OnJukeboxChanged;
        lock (_sync)
        {
            CancelLocked();
        }
    }

    private sealed record ActiveAnalysis(MeterTimeline Timeline, bool IsYouTube, string SourceId);
}
