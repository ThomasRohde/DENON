using System.IO;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Denon.Plugin.Chromecast.Cast;

internal static class CastNamespaces
{
    public const string Connection = "urn:x-cast:com.google.cast.tp.connection";
    public const string Heartbeat = "urn:x-cast:com.google.cast.tp.heartbeat";
    public const string Receiver = "urn:x-cast:com.google.cast.receiver";
    public const string Media = "urn:x-cast:com.google.cast.media";
}

/// <summary>
/// One TLS connection to a Cast device (port 8009): framing, heartbeat, and request/response matching.
/// </summary>
internal sealed class CastChannel : IAsyncDisposable
{
    public const string SenderId = "sender-denon";
    public const string ReceiverId = "receiver-0";

    private const int MaxMessageBytes = 1 << 20;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);

    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _requestId;
    private long _lastReceiveTicks = Environment.TickCount64;
    private int _closed;
    private Task? _readLoop;
    private Task? _heartbeatLoop;

    private CastChannel(TcpClient tcp, SslStream ssl)
    {
        _tcp = tcp;
        _ssl = ssl;
    }

    public event Action<CastMessage, JsonElement>? MessageReceived;

    public event Action<Exception?>? Closed;

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    public static async Task<CastChannel> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var address = await ResolvePrivateLanAddressAsync(host, cancellationToken);
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        SslStream? ssl = null;
        CastChannel? channel = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            await tcp.ConnectAsync(address, port, timeout.Token);

            // Cast devices present a self-signed device certificate. We accept it because the
            // address is checked above to be a private LAN address.
            ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = address.ToString() },
                timeout.Token);

            channel = new CastChannel(tcp, ssl);
            channel.Start();
            await channel.SendAsync(CastNamespaces.Connection, ReceiverId, new JsonObject { ["type"] = "CONNECT" }, cancellationToken);
            return channel;
        }
        catch
        {
            if (channel is not null)
            {
                // Stops its loops and disposes its token source, the TLS stream and the socket.
                await channel.DisposeAsync();
            }
            else
            {
                ssl?.Dispose();
                tcp.Dispose();
            }

            throw;
        }
    }

    public async Task SendAsync(string ns, string destinationId, JsonObject payload, CancellationToken cancellationToken = default)
    {
        if (IsClosed)
        {
            throw new IOException("The Cast connection is closed.");
        }

        var body = new CastMessage(SenderId, destinationId, ns, payload.ToJsonString()).Encode();
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _ssl.WriteAsync(frame, cancellationToken);
            await _ssl.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Close(ex);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<JsonElement> RequestAsync(
        string ns,
        string destinationId,
        JsonObject payload,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _requestId);
        payload["requestId"] = id;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(ns, destinationId, payload, cancellationToken);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(8), cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsClosed)
        {
            try
            {
                // A half-open socket can block the write: wait at most CloseTimeout.
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await SendAsync(CastNamespaces.Connection, ReceiverId, new JsonObject { ["type"] = "CLOSE" }, timeout.Token);
            }
            catch
            {
                // Best effort.
            }
        }

        Close(null);
        foreach (var loop in new[] { _readLoop, _heartbeatLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The loops end with cancellation or IO errors after Close.
            }
        }

        _cts.Dispose();
    }

    internal static bool IsPrivateLanAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254);
    }

    private static async Task<IPAddress> ResolvePrivateLanAddressAsync(string host, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            var candidates = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);
            address = candidates.FirstOrDefault(IsPrivateLanAddress)
                      ?? throw new InvalidOperationException($"'{host}' does not resolve to a private LAN address.");
        }

        if (!IsPrivateLanAddress(address))
        {
            throw new InvalidOperationException($"'{host}' is not a private LAN address. Only LAN Cast devices are allowed.");
        }

        return address;
    }

    private void Start()
    {
        _readLoop = Task.Run(ReadLoopAsync);
        _heartbeatLoop = Task.Run(HeartbeatLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[4];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _ssl.ReadExactlyAsync(header, _cts.Token);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length == 0 || length > MaxMessageBytes)
                {
                    throw new InvalidDataException($"Invalid Cast frame length {length}.");
                }

                var body = new byte[length];
                await _ssl.ReadExactlyAsync(body, _cts.Token);
                Volatile.Write(ref _lastReceiveTicks, Environment.TickCount64);

                var message = CastMessage.Decode(body);
                JsonElement json;
                try
                {
                    using var document = JsonDocument.Parse(message.PayloadUtf8);
                    json = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                var type = json.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString()
                    : null;

                if (message.Namespace == CastNamespaces.Heartbeat && type == "PING")
                {
                    _ = SendQuietlyAsync(CastNamespaces.Heartbeat, message.SourceId, new JsonObject { ["type"] = "PONG" });
                    continue;
                }

                if (message.Namespace == CastNamespaces.Connection && type == "CLOSE" && message.SourceId == ReceiverId)
                {
                    Close(new IOException("The Cast device closed the connection."));
                    return;
                }

                if (json.TryGetProperty("requestId", out var idEl) &&
                    idEl.ValueKind == JsonValueKind.Number &&
                    idEl.TryGetInt32(out var requestId) &&
                    requestId > 0 &&
                    _pending.TryRemove(requestId, out var tcs))
                {
                    tcs.TrySetResult(json);
                }

                try
                {
                    MessageReceived?.Invoke(message, json);
                }
                catch
                {
                    // A subscriber error must not stop the read loop.
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Close(ex);
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                var silence = TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastReceiveTicks));
                if (silence > SilenceLimit)
                {
                    Close(new TimeoutException($"No data from the Cast device for {silence.TotalSeconds:0} s."));
                    return;
                }

                await SendQuietlyAsync(CastNamespaces.Heartbeat, ReceiverId, new JsonObject { ["type"] = "PING" });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendQuietlyAsync(string ns, string destinationId, JsonObject payload)
    {
        try
        {
            await SendAsync(ns, destinationId, payload, _cts.Token);
        }
        catch
        {
            // Close() already ran for IO errors.
        }
    }

    private void Close(Exception? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        var error = reason ?? new IOException("The Cast connection is closed.");
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(error);
        }

        _pending.Clear();
        try { _ssl.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }

        try
        {
            Closed?.Invoke(reason);
        }
        catch
        {
        }
    }
}
