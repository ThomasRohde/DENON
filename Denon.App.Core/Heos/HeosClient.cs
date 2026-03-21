using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Models;

namespace Denon.App.Core.Heos;

public sealed class HeosClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyList<ReceiverPlayerInfo>> GetPlayersAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var response = await SendAsync(settings, "heos://player/get_players", cancellationToken);
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
        EnsureSuccess(await SendAsync(settings, command, cancellationToken));
        return await GetStatusAsync(settings, player, cancellationToken);
    }

    public async Task<ReceiverStatus> SetMuteAsync(ReceiverSettings settings, bool isMuted, CancellationToken cancellationToken)
    {
        var player = await ResolvePlayerAsync(settings, cancellationToken);
        var state = isMuted ? "on" : "off";
        var command = $"heos://player/set_mute?pid={player.PlayerId}&state={state}";
        EnsureSuccess(await SendAsync(settings, command, cancellationToken));
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
        EnsureSuccess(await SendAsync(settings, command, cancellationToken));
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
            EnsureSuccess(await SendAsync(settings, $"heos://player/set_volume?pid={player.PlayerId}&level={level}", cancellationToken));
        }

        if (unmute)
        {
            EnsureSuccess(await SendAsync(settings, $"heos://player/set_mute?pid={player.PlayerId}&state=off", cancellationToken));
        }

        var encodedUrl = Uri.EscapeDataString(url.Trim());
        EnsureSuccess(await SendAsync(settings, $"heos://browse/play_stream?pid={player.PlayerId}&url={encodedUrl}", cancellationToken));
        EnsureSuccess(await SendAsync(settings, $"heos://player/set_play_state?pid={player.PlayerId}&state=play", cancellationToken));
        return await GetStatusAsync(settings, player, cancellationToken);
    }

    private async Task<ReceiverStatus> GetStatusAsync(
        ReceiverSettings settings,
        ReceiverPlayerInfo player,
        CancellationToken cancellationToken)
    {
        var playStateResponse = await SendAsync(settings, $"heos://player/get_play_state?pid={player.PlayerId}", cancellationToken);
        var volumeResponse = await SendAsync(settings, $"heos://player/get_volume?pid={player.PlayerId}", cancellationToken);
        var muteResponse = await SendAsync(settings, $"heos://player/get_mute?pid={player.PlayerId}", cancellationToken);
        var nowPlayingResponse = await SendAsync(settings, $"heos://player/get_now_playing_media?pid={player.PlayerId}", cancellationToken);

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
                nowPlaying.SourceId),
            DateTimeOffset.Now);
    }

    private async Task<ReceiverPlayerInfo> ResolvePlayerAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var players = await GetPlayersAsync(settings, cancellationToken);
        if (players.Count == 0)
        {
            throw new InvalidOperationException("No HEOS players were found.");
        }

        var requested = settings.PlayerName.Trim();
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
                return player;
            }
        }

        throw new InvalidOperationException($"Could not resolve HEOS player '{settings.PlayerName}'.");
    }

    private static void EnsureSuccess(HeosResponse response)
    {
        if (!string.Equals(response.Result, "success", StringComparison.OrdinalIgnoreCase))
        {
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

    private static async Task<HeosResponse> SendAsync(
        ReceiverSettings settings,
        string command,
        CancellationToken cancellationToken)
    {
        var rawResponse = await Task.Run(() => Send(settings.Host, settings.Port, command, cancellationToken), cancellationToken);
        return HeosMessageParser.Parse(rawResponse, command);
    }

    private static string Send(string host, int port, string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var client = new TcpClient
        {
            ReceiveTimeout = (int)ReceiveTimeout.TotalMilliseconds,
            SendTimeout = (int)SendTimeout.TotalMilliseconds,
        };

        client.Connect(host, port);
        using var stream = client.GetStream();

        var commandBytes = Encoding.UTF8.GetBytes(command + "\r\n");
        stream.Write(commandBytes, 0, commandBytes.Length);
        stream.Flush();

        using var bufferStream = new MemoryStream();
        var buffer = new byte[4096];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                bufferStream.Write(buffer, 0, read);
                if (read < buffer.Length)
                {
                    break;
                }
            }
            catch (IOException exception) when (exception.InnerException is SocketException socketException &&
                                               socketException.SocketErrorCode == SocketError.TimedOut)
            {
                break;
            }
        }

        var response = Encoding.UTF8.GetString(bufferStream.ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new InvalidOperationException($"No HEOS response was returned for command '{command}'.");
        }

        return response;
    }

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
}
