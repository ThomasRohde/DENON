using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Denon.Plugin.Chromecast.Cast;

internal enum CastConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

internal sealed record CastDeviceState(
    string DeviceId,
    CastConnectionState Connection = CastConnectionState.Disconnected,
    string? Error = null,
    string? AppId = null,
    string? AppName = null,
    string? StatusText = null,
    bool IsIdleScreen = false,
    string? SessionId = null,
    double VolumeLevel = 0,
    bool IsMuted = false,
    string PlayerState = "",
    string? Title = null,
    string? Subtitle = null,
    string? ImageUrl = null,
    double? CurrentTime = null,
    double? Duration = null,
    int? MediaSessionId = null,
    DateTimeOffset UpdatedAt = default)
{
    /// <summary>A real app runs (not the ambient/Backdrop idle screen).</summary>
    public bool HasActiveApp => Connection == CastConnectionState.Connected && !string.IsNullOrEmpty(AppId) && !IsIdleScreen;

    public bool IsPlaying => PlayerState is "PLAYING" or "BUFFERING";

    public CastDeviceState WithoutApp() => this with
    {
        AppId = null, AppName = null, StatusText = null, IsIdleScreen = false, SessionId = null,
    };

    public CastDeviceState WithoutMedia() => this with
    {
        PlayerState = "", Title = null, Subtitle = null, ImageUrl = null,
        CurrentTime = null, Duration = null, MediaSessionId = null,
    };
}

/// <summary>
/// Keeps one Cast device connected (with reconnect), tracks receiver and media status,
/// and sends media and volume commands.
/// </summary>
internal sealed class CastDeviceClient : IAsyncDisposable
{
    private readonly object _gate = new();
    private CastChannel? _channel;
    private string? _mediaTransportId;
    private CastDeviceState _state;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public CastDeviceClient(string deviceId, string host, int port)
    {
        DeviceId = deviceId;
        Host = host;
        Port = port;
        _state = new CastDeviceState(deviceId);
    }

    /// <summary>Raised with (previous, current) state. Raised on a thread-pool thread.</summary>
    public event Action<CastDeviceState, CastDeviceState>? StateChanged;

    public event Action<string>? Diagnostic;

    public string DeviceId { get; }

    public string Host { get; }

    public int Port { get; }

    public CastDeviceState State
    {
        get { lock (_gate) { return _state; } }
    }

    public void Start()
    {
        if (_runTask is not null)
        {
            return;
        }

        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        _runTask = Task.Run(() => RunAsync(token));
    }

    public async ValueTask DisposeAsync()
    {
        _runCts?.Cancel();
        CastChannel? channel;
        lock (_gate)
        {
            channel = _channel;
            _channel = null;
        }

        if (channel is not null)
        {
            await channel.DisposeAsync();
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }
        }

        _runCts?.Dispose();
    }

    public Task PlayAsync(CancellationToken ct = default) => MediaCommandAsync("PLAY", null, ct);

    public Task PauseAsync(CancellationToken ct = default) => MediaCommandAsync("PAUSE", null, ct);

    public Task StopMediaAsync(CancellationToken ct = default) => MediaCommandAsync("STOP", null, ct);

    public Task SeekAsync(double seconds, CancellationToken ct = default) =>
        MediaCommandAsync("SEEK", p => p["currentTime"] = Math.Max(0, seconds), ct);

    public Task TogglePlayAsync(CancellationToken ct = default) =>
        State.IsPlaying ? PauseAsync(ct) : PlayAsync(ct);

    public async Task SetVolumeAsync(double level, CancellationToken ct = default)
    {
        var channel = RequireChannel();
        await ReceiverCommandAsync(channel, new JsonObject
        {
            ["type"] = "SET_VOLUME",
            ["volume"] = new JsonObject { ["level"] = Math.Round(Math.Clamp(level, 0, 1), 3) },
        }, ct);
    }

    public async Task SetMutedAsync(bool muted, CancellationToken ct = default)
    {
        var channel = RequireChannel();
        await ReceiverCommandAsync(channel, new JsonObject
        {
            ["type"] = "SET_VOLUME",
            ["volume"] = new JsonObject { ["muted"] = muted },
        }, ct);
    }

    /// <summary>The Cast Default Media Receiver: an empty app that only makes the device the active HDMI source.</summary>
    public const string DefaultMediaReceiverAppId = "CC1AD845";

    /// <summary>
    /// Starts the Default Media Receiver. Like any cast, it makes the device the active source over HDMI-CEC, which
    /// turns on a TV in standby (when the TV has CEC on: SimpLink on LG, Anynet+ on Samsung).
    /// </summary>
    /// <returns>The new session's id from the device's answer, or null when the answer has none.</returns>
    public async Task<string?> LaunchDefaultReceiverAsync(CancellationToken ct = default)
    {
        var channel = RequireChannel();
        var response = await ReceiverCommandAsync(channel, new JsonObject { ["type"] = "LAUNCH", ["appId"] = DefaultMediaReceiverAppId }, ct);
        if (response.TryGetProperty("status", out var status) &&
            status.TryGetProperty("applications", out var apps) && apps.ValueKind == JsonValueKind.Array)
        {
            foreach (var app in apps.EnumerateArray())
            {
                if (GetString(app, "appId") == DefaultMediaReceiverAppId && GetString(app, "sessionId") is { Length: > 0 } id)
                {
                    return id;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Closes the running app (the device goes back to its idle screen). With <paramref name="onlySessionId"/>, only
    /// when that session still runs: an app the user started since then stays. Returns false when nothing closed.
    /// </summary>
    public async Task<bool> QuitAppAsync(string? onlySessionId = null, CancellationToken ct = default)
    {
        var channel = RequireChannel();
        var sessionId = State.SessionId;
        if (sessionId is null)
        {
            if (onlySessionId is not null)
            {
                return false;
            }

            throw new InvalidOperationException("No app is running.");
        }

        if (onlySessionId is not null && !string.Equals(sessionId, onlySessionId, StringComparison.Ordinal))
        {
            return false;
        }

        await ReceiverCommandAsync(channel, new JsonObject { ["type"] = "STOP", ["sessionId"] = sessionId }, ct);
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            CastChannel? channel = null;
            try
            {
                Update(s => s with { Connection = CastConnectionState.Connecting });
                channel = await CastChannel.ConnectAsync(Host, Port, ct);

                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var connected = channel;
                channel.Closed += ex => closed.TrySetResult(ex);
                channel.MessageReceived += (message, json) => OnMessage(connected, message, json);
                if (channel.IsClosed)
                {
                    closed.TrySetResult(null);
                }

                lock (_gate)
                {
                    _channel = channel;
                    _mediaTransportId = null;
                }

                await channel.RequestAsync(CastNamespaces.Receiver, CastChannel.ReceiverId, new JsonObject { ["type"] = "GET_STATUS" }, ct);
                Update(s => s with { Connection = CastConnectionState.Connected, Error = null });
                Diagnostic?.Invoke($"Connected to {Host}:{Port}.");
                delay = TimeSpan.FromSeconds(3);

                var reason = await closed.Task.WaitAsync(ct);
                Diagnostic?.Invoke($"Connection to {Host} closed: {reason?.Message ?? "normal close"}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke($"Cannot connect to {Host}:{Port}: {ex.Message}");
                Update(s => s with { Error = ex.Message });
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_channel, channel))
                    {
                        _channel = null;
                        _mediaTransportId = null;
                    }
                }

                if (channel is not null)
                {
                    await channel.DisposeAsync();
                }
            }

            Update(s => s.WithoutApp().WithoutMedia() with { Connection = CastConnectionState.Disconnected });
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }

        Update(s => s.WithoutApp().WithoutMedia() with { Connection = CastConnectionState.Disconnected });
    }

    private void OnMessage(CastChannel channel, CastMessage message, JsonElement json)
    {
        var type = GetString(json, "type");
        if (message.Namespace == CastNamespaces.Receiver && type == "RECEIVER_STATUS" &&
            json.TryGetProperty("status", out var status))
        {
            ApplyReceiverStatus(channel, status);
        }
        else if (message.Namespace == CastNamespaces.Media && type == "MEDIA_STATUS" &&
                 json.TryGetProperty("status", out var mediaStatus))
        {
            ApplyMediaStatus(mediaStatus);
        }
        else if (message.Namespace == CastNamespaces.Connection && type == "CLOSE")
        {
            lock (_gate)
            {
                if (_mediaTransportId == message.SourceId)
                {
                    _mediaTransportId = null;
                }
            }
        }
    }

    private void ApplyReceiverStatus(CastChannel channel, JsonElement status)
    {
        var volumeLevel = 0.0;
        var muted = false;
        if (status.TryGetProperty("volume", out var volume))
        {
            if (volume.TryGetProperty("level", out var levelEl) && levelEl.ValueKind == JsonValueKind.Number)
            {
                volumeLevel = levelEl.GetDouble();
            }

            muted = volume.TryGetProperty("muted", out var mutedEl) && mutedEl.ValueKind == JsonValueKind.True;
        }

        JsonElement? app = null;
        if (status.TryGetProperty("applications", out var apps) && apps.ValueKind == JsonValueKind.Array && apps.GetArrayLength() > 0)
        {
            app = apps[0];
        }

        string? transportToConnect = null;
        Update(s =>
        {
            var next = s with { Connection = CastConnectionState.Connected, VolumeLevel = volumeLevel, IsMuted = muted };
            if (app is not { } a)
            {
                return next.WithoutApp().WithoutMedia();
            }

            var sessionId = GetString(a, "sessionId");
            if (sessionId != s.SessionId)
            {
                next = next.WithoutMedia();
            }

            next = next with
            {
                AppId = GetString(a, "appId"),
                AppName = CastAppNames.Friendly(GetString(a, "appId"), GetString(a, "displayName")),
                StatusText = GetString(a, "statusText"),
                IsIdleScreen = a.TryGetProperty("isIdleScreen", out var idle) && idle.ValueKind == JsonValueKind.True,
                SessionId = sessionId,
            };

            var transportId = GetString(a, "transportId");
            if (!next.IsIdleScreen && transportId is not null && SupportsMedia(a))
            {
                lock (_gate)
                {
                    if (_mediaTransportId != transportId)
                    {
                        _mediaTransportId = transportId;
                        transportToConnect = transportId;
                    }
                }
            }

            return next;
        });

        if (app is null)
        {
            lock (_gate)
            {
                _mediaTransportId = null;
            }
        }

        if (transportToConnect is not null)
        {
            _ = ConnectMediaAsync(channel, transportToConnect);
        }
    }

    private async Task ConnectMediaAsync(CastChannel channel, string transportId)
    {
        try
        {
            await channel.SendAsync(CastNamespaces.Connection, transportId, new JsonObject { ["type"] = "CONNECT" });
            await channel.RequestAsync(CastNamespaces.Media, transportId, new JsonObject { ["type"] = "GET_STATUS" });
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"Media status request failed: {ex.Message}");
        }
    }

    private void ApplyMediaStatus(JsonElement statusArray)
    {
        if (statusArray.ValueKind != JsonValueKind.Array || statusArray.GetArrayLength() == 0)
        {
            Update(s => s.WithoutMedia() with { PlayerState = "IDLE" });
            return;
        }

        var entry = statusArray[0];
        int? mediaSessionId = entry.TryGetProperty("mediaSessionId", out var msid) && msid.TryGetInt32(out var id) ? id : null;
        var playerState = GetString(entry, "playerState") ?? "";
        double? currentTime = entry.TryGetProperty("currentTime", out var ctEl) && ctEl.ValueKind == JsonValueKind.Number
            ? ctEl.GetDouble()
            : null;

        Update(s =>
        {
            var next = mediaSessionId != s.MediaSessionId ? s.WithoutMedia() : s;
            next = next with { MediaSessionId = mediaSessionId, PlayerState = playerState, CurrentTime = currentTime };

            if (entry.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
            {
                double? duration = media.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetDouble()
                    : null;
                string? title = null, subtitle = null, image = null;
                if (media.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    title = GetString(meta, "title");
                    subtitle = GetString(meta, "artist") ?? GetString(meta, "subtitle") ??
                               GetString(meta, "seriesTitle") ?? GetString(meta, "albumName");
                    if (meta.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array &&
                        images.GetArrayLength() > 0)
                    {
                        image = GetString(images[0], "url");
                    }
                }

                next = next with { Duration = duration, Title = title, Subtitle = subtitle, ImageUrl = image };
            }

            return next;
        });
    }

    private async Task MediaCommandAsync(string type, Action<JsonObject>? extend, CancellationToken ct)
    {
        CastChannel channel;
        string transportId;
        lock (_gate)
        {
            channel = _channel ?? throw new InvalidOperationException("The device is not connected.");
            transportId = _mediaTransportId ?? throw new InvalidOperationException("The running app does not accept media commands.");
        }

        var mediaSessionId = State.MediaSessionId ?? throw new InvalidOperationException("Nothing is loaded in the media player.");
        var payload = new JsonObject { ["type"] = type, ["mediaSessionId"] = mediaSessionId };
        extend?.Invoke(payload);
        var response = await channel.RequestAsync(CastNamespaces.Media, transportId, payload, ct);
        var responseType = GetString(response, "type");
        if (responseType is "INVALID_REQUEST" or "INVALID_PLAYER_STATE" or "LOAD_FAILED" or "LOAD_CANCELLED")
        {
            throw new InvalidOperationException($"The device refused {type}: {responseType} {GetString(response, "reason")}".Trim());
        }
    }

    private static async Task<JsonElement> ReceiverCommandAsync(CastChannel channel, JsonObject payload, CancellationToken ct)
    {
        var response = await channel.RequestAsync(CastNamespaces.Receiver, CastChannel.ReceiverId, payload, ct);
        var responseType = GetString(response, "type");
        if (responseType is "INVALID_REQUEST" or "LAUNCH_ERROR")
        {
            throw new InvalidOperationException($"The device refused the command: {GetString(response, "reason") ?? responseType}");
        }

        return response;
    }

    private CastChannel RequireChannel()
    {
        lock (_gate)
        {
            return _channel ?? throw new InvalidOperationException("The device is not connected.");
        }
    }

    private void Update(Func<CastDeviceState, CastDeviceState> change)
    {
        CastDeviceState previous, current;
        lock (_gate)
        {
            previous = _state;
            current = change(previous);
            if (current == previous)
            {
                return;
            }

            current = current with { UpdatedAt = DateTimeOffset.Now };
            _state = current;
        }

        try
        {
            StateChanged?.Invoke(previous, current);
        }
        catch
        {
        }
    }

    private static bool SupportsMedia(JsonElement app)
    {
        if (!app.TryGetProperty("namespaces", out var namespaces) || namespaces.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var ns in namespaces.EnumerateArray())
        {
            if (GetString(ns, "name") == CastNamespaces.Media)
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
