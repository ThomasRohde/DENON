using System.Diagnostics;
using System.Runtime.InteropServices;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services.Metering;

/// <summary>
/// Real, synced meters for audio that this app sends to the Denon: YouTube / YouTube Music, the local jukebox, and
/// internet radio started from the app (a station button, a Smart Scene's stream or an MCP stream). When it starts,
/// a separate ffmpeg process decodes a copy of the same source to PCM (the stream to the Denon is not touched), and
/// <see cref="PcmMeterAnalyzer"/> builds a level + 31-band timeline by media time. The UI and Hue Music Sync read the
/// frame at the position the speakers play now: the track's playback clock minus the HEOS delay, or for radio the
/// <see cref="RadioPlaybackClock"/> (a live stream keeps a rolling timeline of the last minutes).
/// </summary>
public sealed class AppStreamMeterService : IDisposable
{
    public const int SampleRate = 44100;
    public const int HopFrames = 1024; // ≈23 ms per frame
    public static readonly TimeSpan MaxAnalyzedDuration = TimeSpan.FromMinutes(90); // ≈19 MB of frames

    /// <summary>A live radio stream keeps this much (≈2 MB of frames).</summary>
    public static readonly TimeSpan RadioWindow = TimeSpan.FromMinutes(10);

    /// <summary>A radio decoder that stopped (network, server) starts again at most this often while HEOS plays on.</summary>
    public static readonly TimeSpan RadioRestartInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// After the app starts a stream, the receiver's state may still show the source before it (a HEOS push event is
    /// patched onto the old input and metadata until the next full read), so another source cannot end it this soon.
    /// </summary>
    public static readonly TimeSpan RadioStartGrace = TimeSpan.FromSeconds(15);

    private readonly YouTubeQueueService _youtubeQueue;
    private readonly LocalMediaJukeboxService _jukebox;
    private readonly YouTubeMediaService _youtubeMedia;
    private readonly ReceiverController _receiver;
    private readonly ReceiverStateStore _state;
    private readonly AppLogStore _logStore;
    private readonly object _sync = new();
    private ActiveAnalysis? _active;
    private CancellationTokenSource? _cts;
    private bool _warnedNoFfmpeg;

    // The radio stream the app started last; kept over a pause so the analysis starts again on play.
    private RadioSource? _radio;
    private long _radioRestartAfterMs;
    private long _radioGraceUntilMs;
    private bool _radioPaused; // the decoder was stopped for a pause (not by a failure)
    private bool _sourceSeen; // a YouTube track, a jukebox track or a stream started in this run of the app

    public AppStreamMeterService(
        YouTubeQueueService youtubeQueue,
        LocalMediaJukeboxService jukebox,
        YouTubeMediaService youtubeMedia,
        ReceiverController receiver,
        ReceiverStateStore state,
        AppLogStore logStore)
    {
        _youtubeQueue = youtubeQueue;
        _jukebox = jukebox;
        _youtubeMedia = youtubeMedia;
        _receiver = receiver;
        _state = state;
        _logStore = logStore;

        _youtubeQueue.TrackStarted += OnYouTubeTrackStarted;
        _jukebox.JukeboxChanged += OnJukeboxChanged;
        _receiver.StreamUrlStarted += OnStreamUrlStarted;
        _state.StateChanged += OnReceiverStateChanged;
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
            TryRestartRadio();
            return false;
        }

        double position;
        switch (active.Kind)
        {
            case SourceKind.YouTube:
                if (!string.Equals(_youtubeQueue.CurrentVideoId, active.SourceId, StringComparison.Ordinal))
                {
                    return false;
                }

                position = _youtubeQueue.GetEstimatedPlaybackPositionSeconds(heosDelayMs);
                break;

            case SourceKind.Local:
                if (!string.Equals(_jukebox.CurrentTrackFilePath, active.SourceId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                position = _jukebox.GetEstimatedPlaybackPositionSeconds(heosDelayMs);
                break;

            default:
                // Radio: HEOS's own buffering is in the clock, so the relay's HEOS delay does not apply.
                if (!IsRadioPlaying(_state.Snapshot) || active.Clock?.PositionSeconds(Environment.TickCount64) is not { } radioPosition)
                {
                    return false;
                }

                position = radioPosition;
                break;
        }

        return active.Timeline.TryGetAt(position, out left, out right, bandsDestination);
    }

    /// <summary>The receiver plays a URL stream on HEOS (not a named service such as Spotify, and not another input).</summary>
    internal static bool IsRadioPlaying(ReceiverStateSnapshot snapshot) =>
        snapshot.IsConnected &&
        snapshot.Status is { IsPoweredOn: true, IsMainZoneOn: true } status &&
        DenonCatalog.InputMatches(status.InputSource, "NET") &&
        string.Equals(status.PlayState, "play", StringComparison.OrdinalIgnoreCase) &&
        !status.NowPlaying.IsNamedServiceSource;

    private void OnYouTubeTrackStarted(YouTubeTrackItem track)
    {
        if (string.IsNullOrWhiteSpace(track.VideoId))
        {
            return;
        }

        lock (_sync)
        {
            _radio = null; // another source plays now
            _sourceSeen = true;
        }

        var videoId = track.VideoId;
        StartAnalysis(SourceKind.YouTube, videoId, track.Title, clock: null, (ffmpeg, timeline, ct) => DecodeToTimelineAsync(
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

        lock (_sync)
        {
            _radio = null;
            _sourceSeen = true;
        }

        var path = track.FilePath;
        StartAnalysis(SourceKind.Local, path, track.Title, clock: null, (ffmpeg, timeline, ct) => DecodeToTimelineAsync(
            ffmpeg,
            inputFile: path,
            openInput: null,
            timeline,
            ct));
    }

    private void OnStreamUrlStarted(string url, string title)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        lock (_sync)
        {
            _radio = new RadioSource(url, title);
            _sourceSeen = true;
            _radioPaused = false;
            _radioGraceUntilMs = Environment.TickCount64 + (long)RadioStartGrace.TotalMilliseconds;
        }

        StartRadio(url, title, RadioPlaybackClock.Commanded(Environment.TickCount64), restart: true);
    }

    private void OnReceiverStateChanged(ReceiverStateSnapshot snapshot)
    {
        var active = Volatile.Read(ref _active);
        RadioSource? radio;
        bool inGrace;
        lock (_sync)
        {
            radio = _radio;
            inGrace = Environment.TickCount64 < _radioGraceUntilMs;
        }

        if (radio is null)
        {
            TryResumeStationAfterRestart(snapshot);
            return;
        }

        var status = snapshot.Status;
        var onHeos = snapshot.IsConnected &&
                     status is { IsPoweredOn: true, IsMainZoneOn: true } &&
                     DenonCatalog.InputMatches(status.InputSource, "NET") &&
                     !status.NowPlaying.IsNamedServiceSource;
        if (!onHeos && snapshot.IsConnected && !inGrace)
        {
            // Standby, another input, or Spotify / AirPlay / TuneIn on HEOS: this stream is over.
            lock (_sync)
            {
                _radio = null;
                if (Volatile.Read(ref _active) is { Kind: SourceKind.Radio })
                {
                    CancelLocked();
                }
            }

            return;
        }

        var playing = IsRadioPlaying(snapshot);
        if (active is { Kind: SourceKind.Radio } radioAnalysis)
        {
            if (playing)
            {
                radioAnalysis.Clock?.OnPlayState(status!.PlayState, Environment.TickCount64);
            }
            else if (radioAnalysis.Clock is { HasStarted: true } &&
                     status is { } paused &&
                     !string.Equals(paused.PlayState, "play", StringComparison.OrdinalIgnoreCase))
            {
                // Paused or stopped: a live stream resumes at its live edge, so this timeline no longer lines up.
                lock (_sync)
                {
                    if (ReferenceEquals(_active, radioAnalysis))
                    {
                        CancelLocked();
                        _radioPaused = true;
                    }
                }
            }
        }
        else if (active is null && playing)
        {
            bool resumed;
            lock (_sync)
            {
                resumed = _radioPaused;
                _radioPaused = false;
            }

            if (resumed)
            {
                // Playing again after a pause: HEOS reopened the live stream, so decode it again from now.
                StartRadio(radio.Url, radio.Title, RadioPlaybackClock.PlayingSince(Environment.TickCount64), restart: true);
            }
            else
            {
                // The decoder stopped (network, server): try again, but not more often than the retry interval.
                TryRestartRadio();
            }
        }
    }

    /// <summary>
    /// The station HEOS still plays after the app restarted, when it is the one the app started last and nothing
    /// else started in this run. Null otherwise. The app learns a stream's address only when it starts it, so
    /// without this the meters and Hue Music Sync stay silent until the station is started again.
    /// </summary>
    internal static StationPreset? StationToResume(bool sourceSeen, ReceiverStateSnapshot snapshot, StationPreset? lastStarted)
    {
        if (sourceSeen || lastStarted is null || !IsRadioPlaying(snapshot))
        {
            return null;
        }

        return Uri.TryCreate(lastStarted.Url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? lastStarted
            : null;
    }

    private void TryResumeStationAfterRestart(ReceiverStateSnapshot snapshot)
    {
        StationPreset? station;
        lock (_sync)
        {
            station = StationToResume(_sourceSeen, snapshot, _receiver.LastStartedStation);
            if (station is null)
            {
                return;
            }

            // Once per run: from now on the usual pause, resume and retry rules follow this stream.
            _sourceSeen = true;
            _radio = new RadioSource(station.Url, station.DisplayName);
            _radioPaused = false;
        }

        _logStore.Info($"'{station.DisplayName}' plays on from before the app started: the synced meters and Hue Music Sync follow it again.");
        StartRadio(station.Url, station.DisplayName, RadioPlaybackClock.PlayingSince(Environment.TickCount64), restart: true);
    }

    /// <summary>A radio decoder that stopped while HEOS plays on starts again (at most every <see cref="RadioRestartInterval"/>).</summary>
    private void TryRestartRadio()
    {
        RadioSource? radio;
        lock (_sync)
        {
            radio = _radio;
            if (radio is null || Environment.TickCount64 < _radioRestartAfterMs)
            {
                return;
            }
        }

        if (IsRadioPlaying(_state.Snapshot))
        {
            StartRadio(radio.Url, radio.Title, RadioPlaybackClock.PlayingSince(Environment.TickCount64), restart: false);
        }
    }

    private void StartRadio(string url, string title, RadioPlaybackClock clock, bool restart)
    {
        lock (_sync)
        {
            _radioRestartAfterMs = Environment.TickCount64 + (long)RadioRestartInterval.TotalMilliseconds;
        }

        StartAnalysis(SourceKind.Radio, url, title, clock, (ffmpeg, timeline, ct) => DecodeToTimelineAsync(
            ffmpeg,
            inputFile: url,
            openInput: null,
            timeline,
            ct), force: restart);
    }

    private void StartAnalysis(
        SourceKind kind,
        string sourceId,
        string title,
        RadioPlaybackClock? clock,
        Func<string, MeterTimeline, CancellationToken, Task<bool>> decode,
        bool force = false)
    {
        CancellationTokenSource cts;
        MeterTimeline timeline;
        ActiveAnalysis analysis;
        lock (_sync)
        {
            // Same track again (repeat, or a jukebox refresh): keep the timeline. A radio stream always starts over.
            if (!force &&
                _active is { } current &&
                current.Kind == kind &&
                string.Equals(current.SourceId, sourceId, kind == SourceKind.YouTube ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) &&
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
                    _logStore.Info("Synced Denon meters need FFmpeg; meters and Hue Music Sync use the simulation for YouTube, the jukebox and radio.");
                }

                return;
            }

            var rolling = kind == SourceKind.Radio;
            var window = rolling ? RadioWindow : MaxAnalyzedDuration;
            var maxFrames = (int)Math.Ceiling(window.TotalSeconds / HopSeconds);
            var prefix = kind switch { SourceKind.YouTube => "yt:", SourceKind.Local => "local:", _ => "radio:" };
            timeline = new MeterTimeline(prefix + sourceId, HopSeconds, maxFrames, rolling);
            cts = new CancellationTokenSource();
            _cts = cts;
            analysis = new ActiveAnalysis(timeline, kind, sourceId, clock);
            Volatile.Write(ref _active, analysis);

            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var ok = await decode(ffmpegPath, timeline, token);
                    if (!token.IsCancellationRequested)
                    {
                        timeline.MarkComplete(failed: !ok || rolling);
                        if (rolling)
                        {
                            // A live stream does not end by itself: the connection dropped. Try again while HEOS plays.
                            ClearIfActive(analysis);
                            _logStore.Info($"Synced meters for '{title}' stopped after {timeline.AnalyzedSeconds:0} s of the stream; trying again while it plays.");
                        }
                        else
                        {
                            _logStore.Info(ok
                                ? $"Synced Denon meters ready for '{title}' ({timeline.AnalyzedSeconds:0} s of audio analyzed in {sw.Elapsed.TotalSeconds:0.0} s)."
                                : $"Synced Denon meters stopped for '{title}' after {timeline.AnalyzedSeconds:0} s of audio; the rest uses the simulation.");
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    timeline.MarkComplete(failed: true);
                }
                catch (Exception ex)
                {
                    timeline.MarkComplete(failed: true);
                    if (rolling)
                    {
                        ClearIfActive(analysis);
                    }

                    _logStore.Warning($"Synced Denon meter analysis failed for '{title}': {ex.Message}");
                }
            }, CancellationToken.None);
        }

        if (kind == SourceKind.Radio)
        {
            _logStore.Info($"Synced meters and Hue Music Sync follow the radio stream '{title}'.");
        }
    }

    private void ClearIfActive(ActiveAnalysis analysis)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_active, analysis))
            {
                CancelLocked();
            }
        }
    }

    /// <summary>
    /// Decodes <paramref name="inputFile"/> (a file or an http(s) stream URL, or the stream from
    /// <paramref name="openInput"/>, piped to stdin) with ffmpeg to 44.1 kHz stereo float PCM and fills
    /// <paramref name="timeline"/>. Returns true when the whole input was decoded (or the timeline is full).
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

        if (inputFile is not null &&
            (inputFile.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || inputFile.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            // A radio URL from a third-party catalog: network protocols only, so a playlist cannot point FFmpeg at
            // local files.
            psi.ArgumentList.Add("-protocol_whitelist");
            psi.ArgumentList.Add("http,https,tcp,tls,crypto");
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
        _receiver.StreamUrlStarted -= OnStreamUrlStarted;
        _state.StateChanged -= OnReceiverStateChanged;
        lock (_sync)
        {
            _radio = null;
            CancelLocked();
        }
    }

    private enum SourceKind
    {
        YouTube,
        Local,
        Radio,
    }

    private sealed record ActiveAnalysis(MeterTimeline Timeline, SourceKind Kind, string SourceId, RadioPlaybackClock? Clock);

    private sealed record RadioSource(string Url, string Title);
}
