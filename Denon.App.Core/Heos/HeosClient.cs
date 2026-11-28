using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Models;

namespace Denon.App.Core.Heos;

public sealed class HeosClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AsyncCommandTimeout = TimeSpan.FromSeconds(8);

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string? _connectedHost;
    private int _connectedPort;
    private ReceiverPlayerInfo? _cachedPlayer;
    private string? _cachedPlayerKey;

    /// <summary>
    /// Set by <see cref="Disconnect"/> while a command holds the connection. The next command drops the
    /// connection and opens a new one. Disconnect never waits for the gate: the UI thread calls it.
    /// </summary>
    private volatile bool _resetRequested;

    public async Task<IReadOnlyList<ReceiverPlayerInfo>> GetPlayersAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(settings, "heos://player/get_players", DefaultCommandTimeout, cancellationToken);
        EnsureSuccess(response);

        var payload = response.Payload ?? throw new InvalidOperationException("Player payload was missing.");
        var players = payload.Deserialize<List<PlayerPayload>>(JsonOptions) ?? [];
        return players.Select(MapPlayer).ToList();
    }

    public async Task<ReceiverStatus> GetStatusAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var player = await ResolvePlayerAsync(settings, cancellationToken);
        return await GetStatusAsync(settings, player, cancellationToken);
    }

    public async Task<ReceiverStatus> SetVolumeAsync(ReceiverSettings settings, int level, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        if (level > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "Volume must be between 0 and 100.");
        }

        var player = await ResolvePlayerAsync(settings, cancellationToken);
        var command = $"heos://player/set_volume?pid={player.PlayerId}&level={level}";
        EnsureSuccess(await SendCommandAsync(settings, command, DefaultCommandTimeout, cancellationToken));
        return await GetStatusAsync(settings, player, cancellationToken);
    }

    public async Task<ReceiverStatus> SetMuteAsync(ReceiverSettings settings, bool isMuted, CancellationToken cancellationToken)
    {
        var player = await ResolvePlayerAsync(settings, cancellationToken);
        var state = isMuted ? "on" : "off";
        var command = $"heos://player/set_mute?pid={player.PlayerId}&state={state}";
        EnsureSuccess(await SendCommandAsync(settings, command, DefaultCommandTimeout, cancellationToken));
        return await GetStatusAsync(settings, player, cancellationToken);
    }

    public async Task<ReceiverStatus> SetPlayStateAsync(ReceiverSettings settings, string state, CancellationToken cancellationToken)
    {
        if (state is not ("play" or "pause" or "stop"))
        {
            throw new ArgumentException("Play state must be play, pause, or stop.", nameof(state));
        }

        var player = await ResolvePlayerAsync(settings, cancellationToken);
        var command = $"heos://player/set_play_state?pid={player.PlayerId}&state={state}";
        EnsureSuccess(await SendCommandAsync(settings, command, DefaultCommandTimeout, cancellationToken));
        return await GetStatusAsync(settings, player, cancellationToken);
    }

    public async Task<ReceiverStatus> PlayNextAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var player = await ResolvePlayerAsync(settings, cancellationToken);
        EnsureSuccess(await SendCommandAsync(settings, $"heos://player/play_next?pid={player.PlayerId}", DefaultCommandTimeout, cancellationToken));
        return await GetStatusAsync(settings, player, cancellationToken);
    }

    public async Task<ReceiverStatus> PlayPreviousAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var player = await ResolvePlayerAsync(settings, cancellationToken);
        EnsureSuccess(await SendCommandAsync(settings, $"heos://player/play_previous?pid={player.PlayerId}", DefaultCommandTimeout, cancellationToken));
        return await GetStatusAsync(settings, player, cancellationToken);
    }

    public async Task<ReceiverStatus> PlayUrlAsync(
        ReceiverSettings settings,
        string url,
        int? volume,
        bool unmute,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("A stream URL is required.", nameof(url));
        }

        if (volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 100.");
        }

        var player = await ResolvePlayerAsync(settings, cancellationToken);
        if (volume is int level)
        {
            EnsureSuccess(await SendCommandAsync(settings, $"heos://player/set_volume?pid={player.PlayerId}&level={level}", DefaultCommandTimeout, cancellationToken));
        }

        if (unmute)
        {
            EnsureSuccess(await SendCommandAsync(settings, $"heos://player/set_mute?pid={player.PlayerId}&state=off", DefaultCommandTimeout, cancellationToken));
        }

        // HEOS CLI requires literal scheme/path characters (http://host/path) rather than full Uri.EscapeDataString.
        // Percent-encoding ':' and '/' causes HEOS firmware to load "Url Stream" and immediately transition to state=stop.
        var heosUrl = FormatStreamUrlForHeos(url);
        var streamResponse = await SendCommandAsync(
            settings,
            $"heos://browse/play_stream?pid={player.PlayerId}&url={heosUrl}",
            AsyncCommandTimeout,
            cancellationToken);
        EnsureSuccess(streamResponse);

        // Allow HEOS to begin buffering the stream; play_stream transitions to state=play automatically.
        await Task.Delay(700, cancellationToken);
        return await GetStatusAsync(settings, player, cancellationToken);
    }

    public static string FormatStreamUrlForHeos(string url) =>
        url.Trim()
            .Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("&", "%26", StringComparison.Ordinal);

    public async Task<IReadOnlyList<HeosFavoriteItem>> GetFavoritesAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        // HEOS Favorites always live under sid=1028
        var response = await SendCommandAsync(settings, "heos://browse/browse?sid=1028", AsyncCommandTimeout, cancellationToken);
        EnsureSuccess(response);

        if (!response.Payload.HasValue)
        {
            return [];
        }

        var items = response.Payload.Value.Deserialize<List<FavoritePayload>>(JsonOptions) ?? [];
        var favorites = new List<HeosFavoriteItem>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            favorites.Add(new HeosFavoriteItem(
                Name: item.Name ?? $"Favorite {index + 1}",
                MediaId: item.MediaId ?? string.Empty,
                ImageUrl: item.ImageUrl ?? string.Empty,
                PresetIndex: index + 1));
        }

        return favorites;
    }

    public async Task<ReceiverStatus> PlayFavoriteAsync(ReceiverSettings settings, int presetIndex, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(presetIndex, 1);

        var player = await ResolvePlayerAsync(settings, cancellationToken);
        var response = await SendCommandAsync(
            settings,
            $"heos://browse/play_preset?pid={player.PlayerId}&preset={presetIndex}",
            AsyncCommandTimeout,
            cancellationToken);
        EnsureSuccess(response);
        return await GetStatusAsync(settings, player, cancellationToken);
    }

    public async Task<string> SendRawCommandAsync(ReceiverSettings settings, string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A HEOS command is required.", nameof(command));
        }

        var trimmed = command.Trim();
        if (!trimmed.StartsWith("heos://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "heos://" + trimmed.TrimStart('/');
        }

        return await SendRawInternalAsync(settings.Host, settings.Port, trimmed, AsyncCommandTimeout, cancellationToken);
    }

    public void InvalidateCache()
    {
        _cachedPlayer = null;
        _cachedPlayerKey = null;
    }

    /// <summary>
    /// Drops the connection without waiting. When a command holds it, the next command drops it instead.
    /// Never blocks: the UI thread calls this (Reset, Save &amp; Sync, Import) while a command it started may
    /// still wait for the receiver, and that command needs the UI thread to finish.
    /// </summary>
    public void Disconnect()
    {
        _cachedPlayer = null;
        _cachedPlayerKey = null;
        if (!_connectionGate.Wait(0))
        {
            _resetRequested = true;
            return;
        }

        try
        {
            _resetRequested = false;
            ResetConnectionLocked();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// Refuses a command with a control character. A CR or LF inside a stream URL would make the speaker
    /// run a second command (for example a set_volume line), past the safe-volume check.
    /// </summary>
    internal static void EnsureSingleCommand(string command)
    {
        if (command.Any(char.IsControl))
        {
            throw new ArgumentException("A HEOS command must be one line without control characters.", nameof(command));
        }
    }

    private async Task<ReceiverStatus> GetStatusAsync(
        ReceiverSettings settings,
        ReceiverPlayerInfo player,
        CancellationToken cancellationToken)
    {
        var playStateResponse = await SendCommandAsync(settings, $"heos://player/get_play_state?pid={player.PlayerId}", DefaultCommandTimeout, cancellationToken);
        var volumeResponse = await SendCommandAsync(settings, $"heos://player/get_volume?pid={player.PlayerId}", DefaultCommandTimeout, cancellationToken);
        var muteResponse = await SendCommandAsync(settings, $"heos://player/get_mute?pid={player.PlayerId}", DefaultCommandTimeout, cancellationToken);
        var nowPlayingResponse = await SendCommandAsync(settings, $"heos://player/get_now_playing_media?pid={player.PlayerId}", DefaultCommandTimeout, cancellationToken);

        EnsureSuccess(playStateResponse);
        EnsureSuccess(volumeResponse);
        EnsureSuccess(muteResponse);
        EnsureSuccess(nowPlayingResponse);

        var playState = playStateResponse.MessageFields.TryGetValue("state", out var playStateValue) ? playStateValue : "unknown";
        var volume = volumeResponse.MessageFields.TryGetValue("level", out var volumeValue) && int.TryParse(volumeValue, out var parsedVolume)
            ? parsedVolume
            : 0;
        var isMuted = muteResponse.MessageFields.TryGetValue("state", out var muteState) &&
                      string.Equals(muteState, "on", StringComparison.OrdinalIgnoreCase);

        var nowPlayingPayload = nowPlayingResponse.Payload;
        var nowPlaying = nowPlayingPayload.HasValue
            ? nowPlayingPayload.Value.Deserialize<NowPlayingPayload>(JsonOptions) ?? new NowPlayingPayload()
            : new NowPlayingPayload();

        return new ReceiverStatus(
            player,
            playState,
            volume,
            isMuted,
            new NowPlayingMedia(
                nowPlaying.Type ?? string.Empty,
                nowPlaying.Song ?? string.Empty,
                nowPlaying.Album ?? string.Empty,
                nowPlaying.Artist ?? string.Empty,
                nowPlaying.ImageUrl ?? string.Empty,
                nowPlaying.AlbumId ?? string.Empty,
                nowPlaying.MediaId ?? string.Empty,
                nowPlaying.QueueId,
                nowPlaying.SourceId,
                nowPlaying.Station ?? string.Empty),
            DateTimeOffset.Now,
            VolumeExact: volume);
    }

    private async Task<ReceiverPlayerInfo> ResolvePlayerAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var cacheKey = $"{settings.Host}:{settings.Port}:{settings.PlayerName}";
        if (_cachedPlayer is not null && string.Equals(_cachedPlayerKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return _cachedPlayer;
        }

        var players = await GetPlayersAsync(settings, cancellationToken);
        if (players.Count == 0)
        {
            throw new InvalidOperationException("No HEOS players were found.");
        }

        var requested = settings.PlayerName?.Trim() ?? string.Empty;

        // Auto-detect mode: when PlayerName is left blank, match the receiver's own IP or pick the first player
        if (string.IsNullOrWhiteSpace(requested))
        {
            var byIp = players.FirstOrDefault(p =>
                string.Equals(p.IpAddress, settings.Host, StringComparison.OrdinalIgnoreCase));
            var detected = byIp ?? players[0];
            _cachedPlayer = detected;
            _cachedPlayerKey = cacheKey;
            return detected;
        }

        var normalizedTarget = SlugNormalizer.Normalize(requested);

        foreach (var player in players)
        {
            var candidates = new[]
            {
                SlugNormalizer.Normalize(player.Name),
                SlugNormalizer.Normalize(player.Model),
                SlugNormalizer.Normalize(player.IpAddress),
            };

            if (candidates.Contains(normalizedTarget, StringComparer.Ordinal))
            {
                _cachedPlayer = player;
                _cachedPlayerKey = cacheKey;
                return player;
            }
        }

        // Fallback: if there is only one player on this host, use it directly
        if (players.Count == 1)
        {
            _cachedPlayer = players[0];
            _cachedPlayerKey = cacheKey;
            return players[0];
        }

        throw new InvalidOperationException($"Could not resolve HEOS player '{settings.PlayerName}'.");
    }

    private static void EnsureSuccess(HeosResponse response)
    {
        if (!response.IsSuccess)
        {
            if (response.MessageFields.TryGetValue("text", out var errorText) && !string.IsNullOrWhiteSpace(errorText))
            {
                var eid = response.MessageFields.TryGetValue("eid", out var errorId) ? $" (eid {errorId})" : string.Empty;
                throw new InvalidOperationException($"HEOS error{eid}: {errorText}");
            }

            var message = response.MessageFields.Count == 0
                ? response.Command
                : string.Join(", ", response.MessageFields.Select(static pair => $"{pair.Key}={pair.Value}"));
            throw new InvalidOperationException($"HEOS command failed: {message}");
        }
    }

    private static ReceiverPlayerInfo MapPlayer(PlayerPayload payload) =>
        new(
            payload.Name ?? string.Empty,
            payload.PlayerId,
            payload.Model ?? string.Empty,
            payload.Version ?? string.Empty,
            payload.IpAddress ?? string.Empty,
            payload.Network ?? string.Empty,
            payload.LineOut,
            payload.Serial ?? string.Empty);

    private async Task<HeosResponse> SendCommandAsync(
        ReceiverSettings settings,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EnsureSingleCommand(command);
        Exception? lastException = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await _connectionGate.WaitAsync(cancellationToken);
            try
            {
                await EnsureConnectedLockedAsync(settings.Host, settings.Port, cancellationToken);
                return await SendAndReadMatchedResponseLockedAsync(command, timeout, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException && attempt == 0)
            {
                lastException = exception;
                ResetConnectionLocked();
                _cachedPlayer = null;
            }
            catch (Exception)
            {
                ResetConnectionLocked();
                throw;
            }
            finally
            {
                _connectionGate.Release();
            }
        }

        throw lastException ?? new InvalidOperationException($"HEOS command '{command}' failed.");
    }

    private async Task<string> SendRawInternalAsync(
        string host,
        int port,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EnsureSingleCommand(command);
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedLockedAsync(host, port, cancellationToken);

            await _writer!.WriteLineAsync(command.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);

            var lines = new List<string>();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            while (true)
            {
                string? line;
                try
                {
                    line = await _reader!.ReadLineAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (line is null)
                {
                    ResetConnectionLocked();
                    break;
                }

                lines.Add(line);
                if (HeosMessageParser.TryParseLine(line, command, out var parsed) &&
                    parsed is not null &&
                    !HeosMessageParser.IsEvent(parsed.Command) &&
                    !parsed.IsCommandUnderProcess)
                {
                    break;
                }
            }

            return lines.Count == 0
                ? "(no response)"
                : string.Join(Environment.NewLine, lines);
        }
        catch
        {
            ResetConnectionLocked();
            throw;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task EnsureConnectedLockedAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (_resetRequested)
        {
            _resetRequested = false;
            ResetConnectionLocked();
        }

        if (_client is { Connected: true } &&
            _reader is not null &&
            _writer is not null &&
            string.Equals(_connectedHost, host, StringComparison.OrdinalIgnoreCase) &&
            _connectedPort == port)
        {
            return;
        }

        ResetConnectionLocked();

        var client = new TcpClient { NoDelay = true };
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(ConnectTimeout);

        try
        {
            await client.ConnectAsync(host, port, connectCts.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var stream = client.GetStream();
        _client = client;
        _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { NewLine = "\r\n" };
        _connectedHost = host;
        _connectedPort = port;
    }

    private async Task<HeosResponse> SendAndReadMatchedResponseLockedAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await _writer!.WriteLineAsync(command.AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);

        var expectedPath = HeosMessageParser.NormalizeCommandPath(command);
        HeosResponse? underProcessResponse = null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            string? line;
            try
            {
                line = await _reader!.ReadLineAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (underProcessResponse is not null)
                {
                    // The final answer may still come. Drop the connection, so a later command of the same
                    // kind cannot take that late answer as its own.
                    ResetConnectionLocked();
                    return underProcessResponse;
                }

                throw new TimeoutException($"Timed out waiting for HEOS response to '{command}'.");
            }

            if (line is null)
            {
                throw new IOException($"HEOS connection closed while waiting for '{command}'.");
            }

            if (!HeosMessageParser.TryParseLine(line, command, out var parsed) || parsed is null)
            {
                continue;
            }

            if (HeosMessageParser.IsEvent(parsed.Command))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(expectedPath) &&
                !string.Equals(HeosMessageParser.NormalizeCommandPath(parsed.Command), expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parsed.IsCommandUnderProcess)
            {
                underProcessResponse = parsed;
                continue;
            }

            return parsed;
        }
    }

    private void ResetConnectionLocked()
    {
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _client = null;
        _connectedHost = null;
        _connectedPort = 0;
    }

    /// <summary>The gate holds no OS handle, so it is not disposed: a command in flight may still release it.</summary>
    public void Dispose() => Disconnect();

    private sealed class PlayerPayload
    {
        public string? Name { get; set; }

        [JsonPropertyName("pid")]
        public int PlayerId { get; set; }

        public string? Model { get; set; }

        public string? Version { get; set; }

        [JsonPropertyName("ip")]
        public string? IpAddress { get; set; }

        public string? Network { get; set; }

        [JsonPropertyName("lineout")]
        public int LineOut { get; set; }

        public string? Serial { get; set; }
    }

    private sealed class NowPlayingPayload
    {
        public string? Type { get; set; }

        public string? Song { get; set; }

        public string? Station { get; set; }

        public string? Album { get; set; }

        public string? Artist { get; set; }

        [JsonPropertyName("image_url")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("album_id")]
        public string? AlbumId { get; set; }

        [JsonPropertyName("mid")]
        public string? MediaId { get; set; }

        [JsonPropertyName("qid")]
        public int? QueueId { get; set; }

        [JsonPropertyName("sid")]
        public int? SourceId { get; set; }
    }

    private sealed class FavoritePayload
    {
        public string? Name { get; set; }

        [JsonPropertyName("mid")]
        public string? MediaId { get; set; }

        [JsonPropertyName("image_url")]
        public string? ImageUrl { get; set; }
    }
}
