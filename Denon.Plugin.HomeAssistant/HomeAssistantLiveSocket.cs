using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;

namespace Denon.Plugin.HomeAssistant;

internal enum HaLiveState
{
    Off,
    Connecting,
    Live,
    Disconnected,
    AuthRejected,
}

internal sealed class HomeAssistantLiveSocketOptions
{
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// Keeps one Home Assistant WebSocket open: auth, subscribe to state_changed, keep-alive ping, reconnect with backoff
/// (2 s, doubled up to 60 s, back to 2 s after a session that got live). It stops for good when Home Assistant
/// rejects the token. It never throws out of its loop, and it never writes the token anywhere.
/// </summary>
/// <remarks>
/// Not tested against a real Home Assistant server: only against the protocol text in the Home Assistant docs and a
/// loopback test server.
/// </remarks>
internal sealed class HomeAssistantLiveSocket : IAsyncDisposable
{
    private readonly Uri _uri;
    private readonly string _token;
    private readonly HttpMessageInvoker? _invoker;
    private readonly Func<CancellationToken, Task> _onSubscribed;
    private readonly Action<string, HaEntityItem?> _onStateChanged;
    private readonly Action<HaLiveState, string?> _onLiveStateChanged;
    private readonly HomeAssistantLiveSocketOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _nextId;
    private TaskCompletionSource? _pendingPong;
    private int _pendingPingId;
    private volatile bool _pongMissed;

    /// <param name="invoker">
    /// The plugin's HttpClient, so the socket uses the same TLS rule as the REST calls. Null uses a default handler.
    /// </param>
    /// <param name="onSubscribed">Runs each time a session is live: a full REST read, so no change is lost while down.</param>
    /// <param name="onStateChanged">Entity id and its new state. The state is null when the entity was removed.</param>
    /// <param name="onLiveStateChanged">The new state and a short reason (never server text with secrets).</param>
    public HomeAssistantLiveSocket(
        Uri uri,
        string accessToken,
        HttpMessageInvoker? invoker,
        Func<CancellationToken, Task> onSubscribed,
        Action<string, HaEntityItem?> onStateChanged,
        Action<HaLiveState, string?> onLiveStateChanged,
        HomeAssistantLiveSocketOptions? options = null)
    {
        _uri = uri;
        _token = accessToken;
        _invoker = invoker;
        _onSubscribed = onSubscribed;
        _onStateChanged = onStateChanged;
        _onLiveStateChanged = onLiveStateChanged;
        _options = options ?? new HomeAssistantLiveSocketOptions();
    }

    public HaLiveState State { get; private set; } = HaLiveState.Off;

    /// <summary>Completes when the loop ends: after <see cref="DisposeAsync"/>, or when the token was rejected.</summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // The loop catches its own errors. A slow close is left to finish in the background.
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = _options.MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            var wentLive = false;
            string reason;
            try
            {
                _pongMissed = false;
                SetState(HaLiveState.Connecting, null);
                await RunSessionAsync(() => wentLive = true, ct);
                reason = "closed by server";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (TokenRejectedException)
            {
                // Do not try again with this token: each try would fail the same way.
                SetState(HaLiveState.AuthRejected, "token rejected");
                return;
            }
            catch (Exception ex)
            {
                reason = _pongMissed ? "no answer to ping" : ShortReason(ex);
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (wentLive)
            {
                delay = _options.MinBackoff;
            }

            SetState(HaLiveState.Disconnected, reason);
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxBackoff.Ticks));
        }

        SetState(HaLiveState.Off, "stopped");
    }

    private async Task RunSessionAsync(Action markLive, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        int subscribeId;
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            handshake.CancelAfter(_options.HandshakeTimeout);
            var hct = handshake.Token;
            if (_invoker is null)
            {
                await ws.ConnectAsync(_uri, hct);
            }
            else
            {
                await ws.ConnectAsync(_uri, _invoker, hct);
            }

            if (HomeAssistantWebSocketProtocol.Parse(await ReceiveTextAsync(ws, _options.MaxMessageBytes, hct)) is not HaWsMessage.AuthRequired)
            {
                throw new ProtocolException("no auth request");
            }

            await SendTextAsync(ws, HomeAssistantWebSocketProtocol.BuildAuth(_token), hct);
            switch (HomeAssistantWebSocketProtocol.Parse(await ReceiveTextAsync(ws, _options.MaxMessageBytes, hct)))
            {
                case HaWsMessage.AuthOk:
                    break;
                case HaWsMessage.AuthInvalid:
                    throw new TokenRejectedException();
                default:
                    throw new ProtocolException("no auth answer");
            }

            subscribeId = Interlocked.Increment(ref _nextId);
            await SendTextAsync(ws, HomeAssistantWebSocketProtocol.BuildSubscribeStateChanged(subscribeId), hct);
            while (true)
            {
                var text = await ReceiveTextAsync(ws, _options.MaxMessageBytes, hct) ?? throw new ProtocolException("closed before subscribe");
                if (HomeAssistantWebSocketProtocol.Parse(text) is HaWsMessage.Result result && result.Id == subscribeId)
                {
                    if (!result.Success)
                    {
                        throw new ProtocolException("subscribe failed");
                    }

                    break;
                }
            }
        }

        markLive();
        SetState(HaLiveState.Live, null);

        // One full REST read per session. Events that come in during this read wait in the socket and are applied
        // after it, in order, so the last state wins.
        try
        {
            await _onSubscribed(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The plugin reports a failed REST read itself. The live session goes on.
        }

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingTask = PingLoopAsync(ws, session.Token);
        try
        {
            while (true)
            {
                // Only the ping loop sends after the handshake, so sends never overlap.
                var text = await ReceiveTextAsync(ws, _options.MaxMessageBytes, session.Token);
                if (text is null)
                {
                    await CloseQuietlyAsync(ws);
                    return;
                }

                switch (HomeAssistantWebSocketProtocol.Parse(text))
                {
                    case HaWsMessage.StateChanged change when change.Id == subscribeId:
                        try
                        {
                            _onStateChanged(change.EntityId, change.NewState);
                        }
                        catch
                        {
                            // A bad tile update must not end the session.
                        }

                        break;
                    case HaWsMessage.Pong pong when pong.Id == Volatile.Read(ref _pendingPingId):
                        Volatile.Read(ref _pendingPong)?.TrySetResult();
                        break;
                }
            }
        }
        finally
        {
            await session.CancelAsync();
            try
            {
                await pingTask;
            }
            catch
            {
                // Ping errors end the session; the receive side reports why.
            }
        }
    }

    private async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.PingInterval, ct);
            var id = Interlocked.Increment(ref _nextId);
            var pong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pendingPong, pong);
            Volatile.Write(ref _pendingPingId, id);
            await SendTextAsync(ws, HomeAssistantWebSocketProtocol.BuildPing(id), ct);

            var done = await Task.WhenAny(pong.Task, Task.Delay(_options.PongTimeout, ct));
            ct.ThrowIfCancellationRequested();
            if (done != pong.Task)
            {
                // No pong: the link is dead. Abort makes the receive side fail and the loop reconnect.
                _pongMissed = true;
                ws.Abort();
                return;
            }
        }
    }

    /// <summary>
    /// Reads one whole text message, also when it comes in more than one frame. Null when the server closes.
    /// A message larger than <paramref name="maxBytes"/> throws <see cref="InvalidDataException"/>.
    /// </summary>
    internal static async Task<string?> ReceiveTextAsync(WebSocket ws, int maxBytes, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (message.Length + result.Count > maxBytes)
                {
                    throw new InvalidDataException("message too large");
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Task SendTextAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, endOfMessage: true, ct).AsTask();

    private static async Task CloseQuietlyAsync(WebSocket ws)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            }
        }
        catch
        {
            // The socket is going away anyway.
        }
    }

    /// <summary>Short words for the status and the log. Exception text can hold a URL, so it is not used.</summary>
    private static string ShortReason(Exception ex) => ex switch
    {
        ProtocolException p => p.Message,
        InvalidDataException => "message too large",
        OperationCanceledException or TimeoutException => "timed out",
        WebSocketException { WebSocketErrorCode: WebSocketError.NotAWebSocket } => "no WebSocket at this URL",
        WebSocketException { InnerException: HttpRequestException } or HttpRequestException => "cannot reach server",
        WebSocketException => "connection lost",
        _ => "error",
    };

    private void SetState(HaLiveState state, string? reason)
    {
        if (State == state && state is not HaLiveState.Disconnected)
        {
            return;
        }

        State = state;
        try
        {
            _onLiveStateChanged(state, reason);
        }
        catch
        {
            // A status callback must not stop the loop.
        }
    }

    private sealed class TokenRejectedException : Exception;

    private sealed class ProtocolException(string message) : Exception(message);
}
