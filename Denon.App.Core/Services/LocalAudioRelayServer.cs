using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

public sealed class LocalAudioRelayServer : IDisposable
{
    private readonly YouTubeMediaService _youtubeService;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppLogStore _logStore;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, RegisteredTrack> _tracks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RegisteredLocalTrack> _localTracks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PcCastFormat> _livePcSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeStreamTokens = new(StringComparer.Ordinal);
    private byte[]? _calibrationWavBytes;
    private Action<long>? _calibrationStreamStartedCallback;

    private IRemoteWebApiHandler? _webApiHandler;
    private Func<Stream, PcCastFormat, CancellationToken, Task>? _livePcAudioHandler;
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private Task? _acceptLoopTask;
    private int _boundPort;
    private RelayStatusSnapshot _status = RelayStatusSnapshot.Idle;

    public LocalAudioRelayServer(
        YouTubeMediaService youtubeService,
        AppSettingsStore settingsStore,
        AppLogStore logStore)
    {
        _youtubeService = youtubeService;
        _settingsStore = settingsStore;
        _logStore = logStore;
    }

    public event Action<RelayStatusSnapshot>? StatusChanged;

    public event Action<string, DateTimeOffset>? TrackPlaybackStarted;

    public event Action<string, YouTubeTrackItem>? TrackStreamCompleted;

    public event Action<string, LocalAudioTrackItem>? LocalTrackStreamCompleted;

    public RelayStatusSnapshot CurrentStatus => _status;

    public void AttachWebApiHandler(IRemoteWebApiHandler handler) => _webApiHandler = handler;

    public void AttachLivePcAudioHandler(Func<Stream, PcCastFormat, CancellationToken, Task> handler) =>
        _livePcAudioHandler = handler;

    public async Task<(string Url, string Token)> RegisterLocalTrackAsync(LocalAudioTrackItem track, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        CancelAllActiveStreams();

        var ffmpegPath = _youtubeService.FindFfmpegPath();
        var useDirectNative = track.IsNativeHeosFormat;
        var token = Guid.NewGuid().ToString("N")[..12];
        var ext = useDirectNative ? track.Extension.TrimStart('.').ToLowerInvariant() : "mp3";

        if (_localTracks.Count > 8)
        {
            foreach (var oldKey in _localTracks.Keys.Take(_localTracks.Count - 4).ToList())
            {
                _localTracks.TryRemove(oldKey, out _);
            }
        }

        _localTracks[token] = new RegisteredLocalTrack(token, track, useDirectNative, ffmpegPath);

        var lanIp = ResolveLocalLanIpForReceiver(_settingsStore.Current.Receiver.Host);
        var baseUrl = $"http://{lanIp}:{_boundPort}";
        var streamUrl = $"{baseUrl}/local/{token}.{ext}";

        UpdateStatus(new RelayStatusSnapshot(
            IsRunning: true,
            EndpointBaseUrl: baseUrl,
            TranscodeMode: useDirectNative ? $"Direct Local {track.CodecBadge}" : "FFmpeg MP3 320k",
            HasFfmpeg: !string.IsNullOrWhiteSpace(ffmpegPath),
            FfmpegPath: ffmpegPath,
            StreamState: $"Ready ({track.CodecBadge})",
            ActiveTrackTitle: track.Title,
            BytesStreamed: 0,
            LastError: null));

        _logStore.Info($"Registered local file relay URL: {streamUrl} ({track.FileName})");
        return (streamUrl, token);
    }

    public async Task<(string Url, string Token)> RegisterLivePcCastAsync(PcCastFormat format, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        CancelAllActiveStreams();

        var token = Guid.NewGuid().ToString("N")[..10];
        _livePcSessions.Clear();
        _livePcSessions[token] = format;

        var ext = format == PcCastFormat.Wav_Lossless ? "wav" : "mp3";
        var lanIp = ResolveLocalLanIpForReceiver(_settingsStore.Current.Receiver.Host);
        var baseUrl = $"http://{lanIp}:{_boundPort}";
        var streamUrl = $"{baseUrl}/live/pc/{token}.{ext}";
        var ffmpegPath = _youtubeService.FindFfmpegPath();

        UpdateStatus(new RelayStatusSnapshot(
            IsRunning: true,
            EndpointBaseUrl: baseUrl,
            TranscodeMode: format == PcCastFormat.Wav_Lossless ? "PC Live WAV Lossless" : "PC Live MP3 320k",
            HasFfmpeg: !string.IsNullOrWhiteSpace(ffmpegPath),
            FfmpegPath: ffmpegPath,
            StreamState: "Awaiting Denon connection...",
            ActiveTrackTitle: "Windows PC Audio (Live)",
            BytesStreamed: 0,
            LastError: null));

        _logStore.Info($"Registered PC Audio live cast URL: {streamUrl}");
        return (streamUrl, token);
    }

    public async Task<string> RegisterCalibrationWavAsync(
        byte[] wavBytes,
        Action<long> onStreamStartTimestamp,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        CancelAllActiveStreams();

        _calibrationWavBytes = wavBytes;
        _calibrationStreamStartedCallback = onStreamStartTimestamp;

        var token = Guid.NewGuid().ToString("N")[..8];
        var lanIp = ResolveLocalLanIpForReceiver(_settingsStore.Current.Receiver.Host);
        return $"http://{lanIp}:{_boundPort}/calibrate/{token}.wav";
    }

    public async Task<string> RegisterTrackAsync(YouTubeTrackItem track, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);

        var ffmpegPath = _youtubeService.FindFfmpegPath();
        var useFfmpeg = _settingsStore.Current.Media.PreferFfmpegMp3Transcode && !string.IsNullOrWhiteSpace(ffmpegPath);
        var token = Guid.NewGuid().ToString("N")[..12];
        var ext = useFfmpeg ? "mp3" : "m4a";

        // Cancel any previous streaming connection so resources release immediately
        CancelAllActiveStreams();

        // Keep only the last few registered tracks to avoid unbounded growth
        if (_tracks.Count > 8)
        {
            foreach (var oldKey in _tracks.Keys.Take(_tracks.Count - 4).ToList())
            {
                _tracks.TryRemove(oldKey, out _);
            }
        }

        _tracks[token] = new RegisteredTrack(token, track, useFfmpeg, ffmpegPath);

        var lanIp = ResolveLocalLanIpForReceiver(_settingsStore.Current.Receiver.Host);
        var baseUrl = $"http://{lanIp}:{_boundPort}";
        var streamUrl = $"{baseUrl}/yt/{token}.{ext}";

        UpdateStatus(new RelayStatusSnapshot(
            IsRunning: true,
            EndpointBaseUrl: baseUrl,
            TranscodeMode: useFfmpeg ? "FFmpeg MP3 320k" : "Direct M4A Proxy",
            HasFfmpeg: !string.IsNullOrWhiteSpace(ffmpegPath),
            FfmpegPath: ffmpegPath,
            StreamState: "Ready for Denon",
            ActiveTrackTitle: track.Title,
            BytesStreamed: 0,
            LastError: null));

        _logStore.Info($"Registered local audio relay URL: {streamUrl} ({(useFfmpeg ? "FFmpeg MP3 320k" : "Direct M4A")})");
        return streamUrl;
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_listener is not null && _serverCts is { IsCancellationRequested: false })
            {
                return;
            }

            var preferredPort = _settingsStore.Current.Media.RelayPort;
            var candidatePorts = new List<int> { preferredPort };
            for (var p = 18925; p <= 18940; p++)
            {
                if (p != preferredPort)
                {
                    candidatePorts.Add(p);
                }
            }
            candidatePorts.Add(0); // OS ephemeral fallback

            TcpListener? started = null;
            Exception? lastError = null;
            foreach (var port in candidatePorts)
            {
                try
                {
                    var candidate = new TcpListener(IPAddress.Any, port);
                    candidate.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    candidate.Start(16);
                    started = candidate;
                    _boundPort = ((IPEndPoint)candidate.LocalEndpoint).Port;
                    break;
                }
                catch (SocketException ex)
                {
                    lastError = ex;
                }
            }

            if (started is null)
            {
                throw new InvalidOperationException($"Unable to start local audio relay server on any port. {lastError?.Message}", lastError);
            }

            _listener = started;
            _serverCts = new CancellationTokenSource();
            var serverToken = _serverCts.Token;
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(started, serverToken), serverToken);

            var ffmpegPath = _youtubeService.FindFfmpegPath();
            var useFfmpeg = _settingsStore.Current.Media.PreferFfmpegMp3Transcode && !string.IsNullOrWhiteSpace(ffmpegPath);
            var lanIp = ResolveLocalLanIpForReceiver(_settingsStore.Current.Receiver.Host);
            var baseUrl = $"http://{lanIp}:{_boundPort}";

            UpdateStatus(new RelayStatusSnapshot(
                IsRunning: true,
                EndpointBaseUrl: baseUrl,
                TranscodeMode: useFfmpeg ? "FFmpeg MP3 320k" : "Direct M4A Proxy",
                HasFfmpeg: !string.IsNullOrWhiteSpace(ffmpegPath),
                FfmpegPath: ffmpegPath,
                StreamState: "Listening",
                ActiveTrackTitle: null,
                BytesStreamed: 0,
                LastError: null));

            _logStore.Info($"Local audio relay server listening on {baseUrl} (FFmpeg: {(string.IsNullOrWhiteSpace(ffmpegPath) ? "Not found - M4A proxy mode" : ffmpegPath)}).");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void CancelAllActiveStreams()
    {
        foreach (var pair in _activeStreamTokens.ToArray())
        {
            if (_activeStreamTokens.TryRemove(pair.Key, out var cts))
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
        }
    }

    public static string ResolveLocalLanIpForReceiver(string? receiverHost)
    {
        if (!string.IsNullOrWhiteSpace(receiverHost))
        {
            try
            {
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Connect(receiverHost.Trim(), 1255);
                if (udp.LocalEndPoint is IPEndPoint localEndPoint &&
                    !IPAddress.IsLoopback(localEndPoint.Address) &&
                    !localEndPoint.Address.Equals(IPAddress.Any))
                {
                    return localEndPoint.Address.ToString();
                }
            }
            catch
            {
                // Fall back to active NIC scan
            }
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    var text = addr.Address.ToString();
                    if (!text.StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        return text;
                    }
                }
            }
        }

        return "127.0.0.1";
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken serverToken)
    {
        while (!serverToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(serverToken);
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(client, serverToken), serverToken);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                _logStore.Warning($"Relay accept error: {ex.Message}");
                await Task.Delay(250, serverToken);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        await using (var networkStream = client.GetStream())
        {
            try
            {
                var request = await ReadHttpRequestAsync(networkStream, serverToken);
                if (request is null)
                {
                    return;
                }

                // Health endpoint for diagnostics
                if (string.Equals(request.Path, "/health", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteSimpleResponseAsync(networkStream, "200 OK", "text/plain", "Denon Desktop Audio Relay OK\r\n", serverToken);
                    return;
                }

                // Couch Phone Remote Web UI & Party Jukebox JSON API
                if (request.Path == "/" ||
                    request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) ||
                    request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    if (_webApiHandler is null)
                    {
                        await WriteSimpleResponseAsync(networkStream, "503 Service Unavailable", "text/plain", "Remote API not initialized\r\n", serverToken);
                        return;
                    }

                    var resp = await _webApiHandler.HandleAsync(request.Method, request.Path, request.Query, request.Body, serverToken);
                    var statusText = resp.StatusCode switch
                    {
                        200 => "200 OK",
                        400 => "400 Bad Request",
                        404 => "404 Not Found",
                        _ => "500 Internal Server Error",
                    };
                    await WriteSimpleResponseAsync(networkStream, statusText, resp.ContentType, resp.Body, serverToken);
                    return;
                }

                // Local Hi-Res File Jukebox (/local/{token}.{ext})
                if (request.Path.StartsWith("/local/", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleLocalFileRequestAsync(networkStream, request, serverToken);
                    return;
                }

                // Live PC Audio Cast (/live/pc/{token}.{ext})
                if (request.Path.StartsWith("/live/pc/", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleLivePcCastRequestAsync(networkStream, request, serverToken);
                    return;
                }

                // Acoustic Lip-Sync Calibration chirp (/calibrate/{token}.wav)
                if (request.Path.StartsWith("/calibrate/", StringComparison.OrdinalIgnoreCase))
                {
                    var wav = _calibrationWavBytes;
                    if (wav is null || wav.Length == 0)
                    {
                        await WriteSimpleResponseAsync(networkStream, "404 Not Found", "text/plain", "No calibration chirp registered.\r\n", serverToken);
                        return;
                    }

                    var hdr = $"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {wav.Length}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n";
                    var hdrBytes = Encoding.ASCII.GetBytes(hdr);
                    await networkStream.WriteAsync(hdrBytes, serverToken);
                    if (!request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                    {
                        _calibrationStreamStartedCallback?.Invoke(Stopwatch.GetTimestamp());
                        await networkStream.WriteAsync(wav, serverToken);
                    }

                    await networkStream.FlushAsync(serverToken);
                    return;
                }

                // Match /yt/{token}.mp3 or /yt/{token}.m4a
                var token = ExtractTrackToken(request.Path);
                if (string.IsNullOrWhiteSpace(token) || !_tracks.TryGetValue(token, out var registered))
                {
                    await WriteSimpleResponseAsync(networkStream, "404 Not Found", "text/plain", "Track not found\r\n", serverToken);
                    return;
                }

                if (string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    var headContentType = registered.UseFfmpeg ? "audio/mpeg" : "audio/mp4";
                    var headHeaders = BuildStreamHeaders(headContentType, registered.Track, contentLength: null, isRange: false);
                    await networkStream.WriteAsync(Encoding.ASCII.GetBytes(headHeaders), serverToken);
                    await networkStream.FlushAsync(serverToken);
                    return;
                }

                if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteSimpleResponseAsync(networkStream, "405 Method Not Allowed", "text/plain", "Method Not Allowed\r\n", serverToken);
                    return;
                }

                // Cancel any previous active stream for the same token (e.g. HEOS probe reconnect)
                if (_activeStreamTokens.TryRemove(token, out var previousCts))
                {
                    try { previousCts.Cancel(); } catch { }
                    previousCts.Dispose();
                }

                using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                _activeStreamTokens[token] = streamCts;

                try
                {
                    var completedNaturally = registered.UseFfmpeg && !string.IsNullOrWhiteSpace(registered.FfmpegPath)
                        ? await StreamTranscodedMp3Async(networkStream, registered, streamCts.Token)
                        : await StreamDirectMp4Async(networkStream, registered, request.RangeHeader, streamCts.Token);

                    if (completedNaturally && !streamCts.IsCancellationRequested)
                    {
                        // Remove finished token immediately so HEOS firmware cannot auto-reconnect and loop the finished stream
                        _tracks.TryRemove(registered.Token, out _);
                        UpdateStatus(_status with { StreamState = "Track Completed" });
                        TrackStreamCompleted?.Invoke(registered.Token, registered.Track);
                    }
                }
                finally
                {
                    if (_activeStreamTokens.TryGetValue(token, out var current) && ReferenceEquals(current, streamCts))
                    {
                        _activeStreamTokens.TryRemove(token, out _);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on track switch or shutdown
            }
            catch (IOException)
            {
                // Expected when HEOS closes probe socket or user stops playback
            }
            catch (SocketException)
            {
                // Expected on abrupt client disconnect
            }
            catch (Exception ex)
            {
                _logStore.Error($"Local audio relay error: {ex.Message}", ex);
                UpdateStatus(_status with { StreamState = "Error", LastError = ex.Message });
            }
        }
    }

    private async Task<bool> StreamTranscodedMp3Async(
        NetworkStream networkStream,
        RegisteredTrack registered,
        CancellationToken cancellationToken)
    {
        UpdateStatus(_status with
        {
            StreamState = "Resolving YouTube stream...",
            ActiveTrackTitle = registered.Track.Title,
            BytesStreamed = 0,
            LastError = null,
        });

        var streamInfo = await _youtubeService.ResolveAudioStreamInfoAsync(
            registered.Track.VideoId,
            preferMp4Direct: false,
            cancellationToken);

        Process ffmpeg;
        try
        {
            ffmpeg = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = registered.FfmpegPath!,
                    Arguments = "-hide_banner -loglevel error -i pipe:0 -vn -c:a libmp3lame -b:a 320k -ar 44100 -ac 2 -f mp3 pipe:1",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            if (!ffmpeg.Start())
            {
                ffmpeg.Dispose();
                throw new InvalidOperationException("Failed to start FFmpeg transcoding process.");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Never close with 0 bytes after 200 OK (which causes HEOS to fall back to the previous radio station):
            // fall back cleanly to Direct M4A streaming before headers are written!
            _logStore.Warning($"FFmpeg launch failed ({ex.Message}); falling back automatically to Direct M4A stream.");
            return await StreamDirectMp4Async(networkStream, registered, rangeHeader: null, cancellationToken);
        }

        using (ffmpeg)
        {
        var headers = BuildStreamHeaders("audio/mpeg", registered.Track, contentLength: null, isRange: false);
        await networkStream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await networkStream.FlushAsync(cancellationToken);

        UpdateStatus(_status with { StreamState = "Streaming to Denon (MP3 320k)" });

        var totalBytes = 0L;
        var sourceReachedEof = false;
        var outputReachedEof = false;
        var playbackClockSignaled = false;

        try
        {
            await using var youtubeStream = await _youtubeService.OpenAudioStreamAsync(streamInfo, cancellationToken);

            var pumpInputTask = Task.Run(async () =>
            {
                try
                {
                    await youtubeStream.CopyToAsync(ffmpeg.StandardInput.BaseStream, 65536, cancellationToken);
                    sourceReachedEof = true;
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
            }, cancellationToken);

            var readStderrTask = ffmpeg.StandardError.ReadToEndAsync(cancellationToken);

            var buffer = new byte[32768];
            var lastStatusTick = Environment.TickCount64;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await ffmpeg.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                {
                    outputReachedEof = true;
                    break;
                }

                await networkStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalBytes += read;

                if (!playbackClockSignaled && totalBytes >= 16384)
                {
                    playbackClockSignaled = true;
                    TrackPlaybackStarted?.Invoke(registered.Token, DateTimeOffset.UtcNow);
                }

                var nowTick = Environment.TickCount64;
                if (nowTick - lastStatusTick >= 1000)
                {
                    lastStatusTick = nowTick;
                    UpdateStatus(_status with { StreamState = "Streaming to Denon (MP3 320k)", BytesStreamed = totalBytes });
                }
            }

            await networkStream.FlushAsync(cancellationToken);
            await pumpInputTask;

            var stderr = string.Empty;
            try { stderr = await readStderrTask; } catch { }
            if (!string.IsNullOrWhiteSpace(stderr) && !cancellationToken.IsCancellationRequested)
            {
                _logStore.Warning($"FFmpeg notice: {stderr.Trim()}");
            }

            UpdateStatus(_status with { BytesStreamed = totalBytes });
            return sourceReachedEof && outputReachedEof && totalBytes > 24_000;
        }
        finally
        {
            try
            {
                if (!ffmpeg.HasExited)
                {
                    ffmpeg.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore cleanup failure
            }
        }
        }
    }

    private async Task<bool> StreamDirectMp4Async(
        NetworkStream networkStream,
        RegisteredTrack registered,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        UpdateStatus(_status with
        {
            StreamState = "Resolving YouTube M4A...",
            ActiveTrackTitle = registered.Track.Title,
            BytesStreamed = 0,
            LastError = null,
        });

        var streamInfo = await _youtubeService.ResolveAudioStreamInfoAsync(
            registered.Track.VideoId,
            preferMp4Direct: true,
            cancellationToken);

        var totalSize = streamInfo.Size.Bytes;
        long rangeStart = 0;
        long rangeEnd = totalSize > 0 ? totalSize - 1 : -1;
        var isPartial = false;

        if (!string.IsNullOrWhiteSpace(rangeHeader) && totalSize > 0 && TryParseRangeHeader(rangeHeader, totalSize, out var s, out var e))
        {
            rangeStart = s;
            rangeEnd = e;
            isPartial = rangeStart > 0 || rangeEnd < totalSize - 1;
        }

        var contentLength = totalSize > 0 ? (rangeEnd - rangeStart + 1) : (long?)null;
        var headers = BuildStreamHeaders(
            streamInfo.Container == YoutubeExplode.Videos.Streams.Container.Mp4 ? "audio/mp4" : "application/octet-stream",
            registered.Track,
            contentLength,
            isPartial,
            rangeStart,
            rangeEnd,
            totalSize);

        await networkStream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await networkStream.FlushAsync(cancellationToken);

        UpdateStatus(_status with { StreamState = "Streaming to Denon (Direct M4A)" });

        await using var youtubeStream = await _youtubeService.OpenAudioStreamAsync(streamInfo, cancellationToken);
        if (rangeStart > 0 && youtubeStream.CanSeek)
        {
            youtubeStream.Seek(rangeStart, SeekOrigin.Begin);
        }

        var buffer = new byte[32768];
        var remaining = contentLength ?? long.MaxValue;
        long written = 0;
        var lastStatusTick = Environment.TickCount64;
        var playbackClockSignaled = false;

        while (remaining > 0 && !cancellationToken.IsCancellationRequested)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await youtubeStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await networkStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            if (contentLength.HasValue)
            {
                remaining -= read;
            }

            if (!playbackClockSignaled && !isPartial && written >= 16384)
            {
                playbackClockSignaled = true;
                TrackPlaybackStarted?.Invoke(registered.Token, DateTimeOffset.UtcNow);
            }

            var nowTick = Environment.TickCount64;
            if (nowTick - lastStatusTick >= 1000)
            {
                lastStatusTick = nowTick;
                UpdateStatus(_status with { StreamState = "Streaming to Denon (Direct M4A)", BytesStreamed = written });
            }
        }

        await networkStream.FlushAsync(cancellationToken);
        UpdateStatus(_status with { BytesStreamed = written });

        var reachedEnd = !isPartial || (totalSize > 0 && rangeEnd >= totalSize - 1);
        return reachedEnd && written > 24_000 && (!contentLength.HasValue || remaining == 0);
    }

    private static string BuildStreamHeaders(
        string contentType,
        YouTubeTrackItem track,
        long? contentLength,
        bool isRange,
        long rangeStart = 0,
        long rangeEnd = 0,
        long totalSize = 0)
    {
        var icyName = SanitizeAsciiHeader($"{track.Title} - {track.Author}");
        var sb = new StringBuilder();
        sb.Append(isRange ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        if (contentLength.HasValue)
        {
            sb.Append($"Content-Length: {contentLength.Value}\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            if (isRange && totalSize > 0)
            {
                sb.Append($"Content-Range: bytes {rangeStart}-{rangeEnd}/{totalSize}\r\n");
            }
        }
        else
        {
            sb.Append("Accept-Ranges: none\r\n");
            sb.Append("icy-br: 320\r\n");
        }

        sb.Append($"icy-name: {icyName}\r\n");
        sb.Append("icy-genre: YouTube Music\r\n");
        sb.Append("Cache-Control: no-cache, no-store\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private static async Task WriteSimpleResponseAsync(
        NetworkStream stream,
        string status,
        string contentType,
        string body,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<ParsedHttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        while (builder.Length < 16384)
        {
            var read = await stream.ReadAsync(buffer, timeoutCts.Token);
            if (read <= 0)
            {
                break;
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var raw = builder.ToString();
        var headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerPart = headerEnd >= 0 ? raw[..headerEnd] : raw;
        var initialBody = headerEnd >= 0 ? raw[(headerEnd + 4)..] : string.Empty;

        var lines = headerPart.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        string? rangeHeader = null;
        var contentLength = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                rangeHeader = lines[i]["Range:".Length..].Trim();
            }
            else if (lines[i].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(lines[i]["Content-Length:".Length..].Trim(), out contentLength);
            }
        }

        var body = initialBody;
        if (contentLength > 0 && contentLength <= 65536)
        {
            var bodyBytes = new List<byte>(Encoding.UTF8.GetBytes(initialBody));
            while (bodyBytes.Count < contentLength)
            {
                var remaining = Math.Min(buffer.Length, contentLength - bodyBytes.Count);
                var read = await stream.ReadAsync(buffer.AsMemory(0, remaining), timeoutCts.Token);
                if (read <= 0)
                {
                    break;
                }

                for (var idx = 0; idx < read; idx++)
                {
                    bodyBytes.Add(buffer[idx]);
                }
            }

            body = Encoding.UTF8.GetString(bodyBytes.ToArray());
        }

        var rawPath = parts[1];
        string? query = null;
        var queryIndex = rawPath.IndexOf('?');
        if (queryIndex >= 0)
        {
            query = rawPath[(queryIndex + 1)..];
            rawPath = rawPath[..queryIndex];
        }

        return new ParsedHttpRequest(parts[0], rawPath, query, rangeHeader, body);
    }

    private async Task HandleLocalFileRequestAsync(
        NetworkStream networkStream,
        ParsedHttpRequest request,
        CancellationToken serverToken)
    {
        var segment = request.Path[7..].Trim('/');
        var dot = segment.IndexOf('.');
        var token = dot > 0 ? segment[..dot] : segment;

        if (string.IsNullOrWhiteSpace(token) || !_localTracks.TryGetValue(token, out var registered) || !File.Exists(registered.Track.FilePath))
        {
            await WriteSimpleResponseAsync(networkStream, "404 Not Found", "text/plain", "Local track not found\r\n", serverToken);
            return;
        }

        var contentType = registered.UseDirectNative
            ? GetMimeTypeForExtension(registered.Track.Extension)
            : "audio/mpeg";

        var fakeYtForHeaders = new YouTubeTrackItem(registered.Token, registered.Track.Title, registered.Track.Artist, null, string.Empty, string.Empty);

        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            var fileLen = registered.UseDirectNative ? new FileInfo(registered.Track.FilePath).Length : (long?)null;
            var headHeaders = BuildStreamHeaders(contentType, fakeYtForHeaders, fileLen, isRange: false);
            await networkStream.WriteAsync(Encoding.ASCII.GetBytes(headHeaders), serverToken);
            await networkStream.FlushAsync(serverToken);
            return;
        }

        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await WriteSimpleResponseAsync(networkStream, "405 Method Not Allowed", "text/plain", "Method Not Allowed\r\n", serverToken);
            return;
        }

        if (_activeStreamTokens.TryRemove(token, out var prevCts))
        {
            try { prevCts.Cancel(); } catch { }
            prevCts.Dispose();
        }

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        _activeStreamTokens[token] = streamCts;

        try
        {
            bool completedNaturally;
            if (registered.UseDirectNative)
            {
                completedNaturally = await StreamDirectLocalFileAsync(networkStream, registered.Track, contentType, fakeYtForHeaders, request.RangeHeader, streamCts.Token);
            }
            else if (!string.IsNullOrWhiteSpace(registered.FfmpegPath))
            {
                completedNaturally = await StreamTranscodedLocalFileAsync(networkStream, registered, fakeYtForHeaders, streamCts.Token);
            }
            else
            {
                completedNaturally = await StreamDirectLocalFileAsync(networkStream, registered.Track, "application/octet-stream", fakeYtForHeaders, request.RangeHeader, streamCts.Token);
            }

            if (completedNaturally && !streamCts.IsCancellationRequested)
            {
                // Remove finished token immediately so HEOS firmware cannot auto-reconnect and replay it
                _localTracks.TryRemove(registered.Token, out _);
                UpdateStatus(_status with { StreamState = "Local Track Completed" });
                LocalTrackStreamCompleted?.Invoke(registered.Token, registered.Track);
            }
        }
        finally
        {
            if (_activeStreamTokens.TryGetValue(token, out var current) && ReferenceEquals(current, streamCts))
            {
                _activeStreamTokens.TryRemove(token, out _);
            }
        }
    }

    private async Task<bool> StreamDirectLocalFileAsync(
        NetworkStream networkStream,
        LocalAudioTrackItem track,
        string contentType,
        YouTubeTrackItem headerMeta,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        await using var fileStream = new FileStream(track.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var totalSize = fileStream.Length;
        long rangeStart = 0;
        long rangeEnd = totalSize > 0 ? totalSize - 1 : -1;
        var isPartial = false;

        if (!string.IsNullOrWhiteSpace(rangeHeader) && totalSize > 0 && TryParseRangeHeader(rangeHeader, totalSize, out var s, out var e))
        {
            rangeStart = s;
            rangeEnd = e;
            isPartial = rangeStart > 0 || rangeEnd < totalSize - 1;
        }

        var contentLength = totalSize > 0 ? (rangeEnd - rangeStart + 1) : 0;
        var headers = BuildStreamHeaders(contentType, headerMeta, contentLength, isPartial, rangeStart, rangeEnd, totalSize);
        await networkStream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await networkStream.FlushAsync(cancellationToken);

        UpdateStatus(_status with
        {
            StreamState = $"Streaming Local {track.CodecBadge}",
            ActiveTrackTitle = track.Title,
            BytesStreamed = 0,
            LastError = null,
        });

        if (rangeStart > 0)
        {
            fileStream.Seek(rangeStart, SeekOrigin.Begin);
        }

        var buffer = new byte[65536];
        var remaining = contentLength;
        long written = 0;
        var lastTick = Environment.TickCount64;

        while (remaining > 0 && !cancellationToken.IsCancellationRequested)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await fileStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await networkStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            remaining -= read;

            var nowTick = Environment.TickCount64;
            if (nowTick - lastTick >= 900)
            {
                lastTick = nowTick;
                UpdateStatus(_status with { StreamState = $"Streaming Local {track.CodecBadge}", BytesStreamed = written });
            }
        }

        await networkStream.FlushAsync(cancellationToken);
        UpdateStatus(_status with { BytesStreamed = written });

        var reachedEnd = !isPartial || (totalSize > 0 && rangeEnd >= totalSize - 1);
        return reachedEnd && remaining == 0 && written > 16_000;
    }

    private async Task<bool> StreamTranscodedLocalFileAsync(
        NetworkStream networkStream,
        RegisteredLocalTrack registered,
        YouTubeTrackItem headerMeta,
        CancellationToken cancellationToken)
    {
        var headers = BuildStreamHeaders("audio/mpeg", headerMeta, contentLength: null, isRange: false);
        await networkStream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await networkStream.FlushAsync(cancellationToken);

        using var ffmpeg = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = registered.FfmpegPath!,
                Arguments = $"-hide_banner -loglevel error -i \"{registered.Track.FilePath}\" -vn -c:a libmp3lame -b:a 320k -ar 44100 -ac 2 -f mp3 pipe:1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        if (!ffmpeg.Start())
        {
            throw new InvalidOperationException("Failed to start FFmpeg for local audio transcode.");
        }

        UpdateStatus(_status with { StreamState = "Streaming Local (MP3 320k)", ActiveTrackTitle = registered.Track.Title, BytesStreamed = 0 });

        long totalBytes = 0;
        var reachedEof = false;
        try
        {
            var buffer = new byte[32768];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await ffmpeg.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                {
                    reachedEof = true;
                    break;
                }

                await networkStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalBytes += read;
            }

            await networkStream.FlushAsync(cancellationToken);
            UpdateStatus(_status with { BytesStreamed = totalBytes });
            return reachedEof && totalBytes > 16_000;
        }
        finally
        {
            try { if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true); } catch { }
        }
    }

    private async Task HandleLivePcCastRequestAsync(
        NetworkStream networkStream,
        ParsedHttpRequest request,
        CancellationToken serverToken)
    {
        var segment = request.Path[9..].Trim('/');
        var dot = segment.IndexOf('.');
        var token = dot > 0 ? segment[..dot] : segment;

        if (string.IsNullOrWhiteSpace(token) || !_livePcSessions.TryGetValue(token, out var format) || _livePcAudioHandler is null)
        {
            await WriteSimpleResponseAsync(networkStream, "404 Not Found", "text/plain", "PC Audio Cast session not active\r\n", serverToken);
            return;
        }

        var contentType = format == PcCastFormat.Wav_Lossless ? "audio/wav" : "audio/mpeg";
        var fakeMeta = new YouTubeTrackItem(token, "Windows PC Audio", "Live WASAPI Stream", null, string.Empty, string.Empty);

        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            var headHeaders = BuildStreamHeaders(contentType, fakeMeta, contentLength: null, isRange: false);
            await networkStream.WriteAsync(Encoding.ASCII.GetBytes(headHeaders), serverToken);
            await networkStream.FlushAsync(serverToken);
            return;
        }

        if (_activeStreamTokens.TryRemove(token, out var prevCts))
        {
            try { prevCts.Cancel(); } catch { }
            prevCts.Dispose();
        }

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        _activeStreamTokens[token] = streamCts;

        try
        {
            var headers = BuildStreamHeaders(contentType, fakeMeta, contentLength: null, isRange: false);
            await networkStream.WriteAsync(Encoding.ASCII.GetBytes(headers), streamCts.Token);
            await networkStream.FlushAsync(streamCts.Token);

            UpdateStatus(_status with
            {
                StreamState = format == PcCastFormat.Wav_Lossless ? "Live PC Cast (WAV Lossless)" : "Live PC Cast (MP3 320k)",
                ActiveTrackTitle = "Windows PC Audio (Live)",
                LastError = null,
            });

            await _livePcAudioHandler(networkStream, format, streamCts.Token);
        }
        finally
        {
            if (_activeStreamTokens.TryGetValue(token, out var current) && ReferenceEquals(current, streamCts))
            {
                _activeStreamTokens.TryRemove(token, out _);
            }
        }
    }

    private static string GetMimeTypeForExtension(string ext) =>
        ext.ToLowerInvariant() switch
        {
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            _ => "application/octet-stream",
        };

    private static string? ExtractTrackToken(string path)
    {
        if (!path.StartsWith("/yt/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segment = path[4..].Trim('/');
        var dot = segment.IndexOf('.');
        return dot > 0 ? segment[..dot] : segment;
    }

    private static bool TryParseRangeHeader(string header, long totalSize, out long start, out long end)
    {
        start = 0;
        end = totalSize - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rangeSpec = header["bytes=".Length..].Split(',')[0].Trim();
        var dash = rangeSpec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var startPart = rangeSpec[..dash].Trim();
        var endPart = rangeSpec[(dash + 1)..].Trim();

        if (!long.TryParse(startPart, out start) || start < 0 || start >= totalSize)
        {
            return false;
        }

        if (string.IsNullOrEmpty(endPart))
        {
            end = totalSize - 1;
            return true;
        }

        if (!long.TryParse(endPart, out end) || end < start)
        {
            return false;
        }

        end = Math.Min(end, totalSize - 1);
        return true;
    }

    private static string SanitizeAsciiHeader(string input)
    {
        var sb = new StringBuilder(Math.Min(input.Length, 100));
        foreach (var ch in input)
        {
            if (ch is >= ' ' and <= '~' && ch is not '\r' and not '\n')
            {
                sb.Append(ch);
            }

            if (sb.Length >= 96)
            {
                break;
            }
        }

        return sb.Length > 0 ? sb.ToString().Trim() : "YouTube Stream";
    }

    private void UpdateStatus(RelayStatusSnapshot status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        CancelAllActiveStreams();
        try { _serverCts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _serverCts?.Dispose();
        _lifecycleGate.Dispose();
    }

    private sealed record RegisteredTrack(string Token, YouTubeTrackItem Track, bool UseFfmpeg, string? FfmpegPath);

    private sealed record RegisteredLocalTrack(string Token, LocalAudioTrackItem Track, bool UseDirectNative, string? FfmpegPath);

    private sealed record ParsedHttpRequest(string Method, string Path, string? Query, string? RangeHeader, string Body);
}
