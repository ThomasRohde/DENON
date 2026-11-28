using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using Denon.Plugin.AppleTv.Protocol.Hap;

namespace Denon.Plugin.AppleTv.Protocol.Companion;

/// <summary>Companion frame types (pyatv: companion/connection.FrameType).</summary>
internal enum CompanionFrameType : byte
{
    Unknown = 0,
    NoOp = 1,
    PsStart = 3,
    PsNext = 4,
    PvStart = 5,
    PvNext = 6,
    UOpack = 7,
    EOpack = 8,
    POpack = 9,
    PaReq = 10,
    PaRsp = 11,
    SessionStartRequest = 16,
    SessionStartResponse = 17,
    SessionData = 18,
    FamilyIdentityRequest = 32,
    FamilyIdentityResponse = 33,
    FamilyIdentityUpdate = 34,
}

/// <summary>Companion message types ("_t").</summary>
internal enum CompanionMessageType
{
    Event = 1,
    Request = 2,
    Response = 3,
}

internal sealed class CompanionProtocolException(string message) : Exception(message);

/// <summary>
/// TCP connection to the Companion service (pyatv: companion/connection.py + protocol.py).
/// Frame = 1 byte type + 3 bytes big-endian payload length + payload. After pair-verify the payload is
/// ChaCha20-Poly1305 encrypted: nonce = 12-byte little-endian counter per direction, AAD = the 4-byte header,
/// length includes the 16-byte tag. Auth replies (PS_*/PV_*) are matched by frame type, OPACK replies by "_x".
/// </summary>
internal sealed class CompanionConnection : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private const int HeaderLength = 4;

    /// <summary>The header holds a 24-bit length, so no frame payload can be larger.</summary>
    public const int MaxFrameLength = 0xFFFFFF;

    private static readonly TimeSpan AuthGateTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<object, object?>>> _pending = new();
    private readonly Task _readLoop;
    private volatile byte[]? _outKey;
    private volatile byte[]? _inKey;
    private TaskCompletionSource? _authGate;
    private ulong _outCounter;
    private ulong _inCounter;
    private long _xid = Random.Shared.Next(0, 65536);
    private int _closed;

    private CompanionConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>An event ("_t" = 1) arrived: identifier ("_i") and content ("_c").</summary>
    public event Action<string, Dictionary<object, object?>>? EventReceived;

    /// <summary>The connection closed (null) or failed (the exception).</summary>
    public event Action<Exception?>? Closed;

    public bool IsConnected => Volatile.Read(ref _closed) == 0 && _tcp.Connected;

    public bool IsEncrypted => _outKey is not null;

    public static async Task<CompanionConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, port, timeout.Token);
            return new CompanionConnection(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Encrypts all following frames (keys from pair-verify). The read loop waits after each auth reply until
    /// this runs (or the next frame is sent), so the first encrypted frame is never read as plaintext.
    /// </summary>
    public void EnableEncryption(byte[] outputKey, byte[] inputKey)
    {
        _outCounter = 0;
        _inCounter = 0;
        _outKey = outputKey;
        _inKey = inputKey;
        ReleaseAuthGate();
    }

    /// <summary>Lets the read loop go on after an auth reply.</summary>
    private void ReleaseAuthGate() => Volatile.Read(ref _authGate)?.TrySetResult();

    /// <summary>Sends a PS_*/PV_* frame and waits for the matching auth reply (Start is answered with Next).</summary>
    public Task<Dictionary<object, object?>> ExchangeAuthAsync(
        CompanionFrameType frameType, Dictionary<string, object?> data, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var replyType = frameType switch
        {
            CompanionFrameType.PsStart => CompanionFrameType.PsNext,
            CompanionFrameType.PvStart => CompanionFrameType.PvNext,
            _ => frameType,
        };

        return ExchangeAsync(frameType, data, AuthKey(replyType), cancellationToken, timeout);
    }

    /// <summary>Sends an OPACK request with a new "_x" and waits for the response with the same "_x".</summary>
    public Task<Dictionary<object, object?>> ExchangeOpackAsync(
        CompanionFrameType frameType, Dictionary<string, object?> data, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var xid = Interlocked.Increment(ref _xid);
        data["_x"] = xid;
        return ExchangeAsync(frameType, data, XidKey(xid), cancellationToken, timeout);
    }

    /// <summary>Sends an OPACK message without waiting (events).</summary>
    public Task SendOpackAsync(CompanionFrameType frameType, Dictionary<string, object?> data, CancellationToken cancellationToken)
    {
        if (!data.ContainsKey("_x"))
        {
            data["_x"] = Interlocked.Increment(ref _xid);
        }

        return SendFrameAsync(frameType, Opack.Pack(data), cancellationToken);
    }

    public async Task SendFrameAsync(CompanionFrameType frameType, byte[] payload, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new CompanionProtocolException("Not connected to the Apple TV.");
        }

        // The caller has handled the last auth reply and moves on, so the read loop may read the answer.
        ReleaseAuthGate();
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            var outKey = _outKey;
            var encrypt = outKey is not null && payload.Length > 0;
            var length = payload.Length + (encrypt ? HapCrypto.TagLength : 0);
            if (length > MaxFrameLength)
            {
                // A larger length would be cut to 24 bits and break the stream.
                throw new CompanionProtocolException($"The Companion frame is too large ({length} bytes).");
            }

            byte[] header = [(byte)frameType, (byte)(length >> 16), (byte)(length >> 8), (byte)length];
            var body = encrypt ? HapCrypto.Encrypt(outKey!, CounterNonce(_outCounter++), payload, header) : payload;

            await _stream.WriteAsync(header, cancellationToken);
            await _stream.WriteAsync(body, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>12-byte nonce: the counter as little-endian (pyatv Chacha20Cipher with nonce_length=12).</summary>
    internal static byte[] CounterNonce(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, counter);
        return nonce;
    }

    private async Task<Dictionary<object, object?>> ExchangeAsync(
        CompanionFrameType frameType, Dictionary<string, object?> data, string key, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        var tcs = new TaskCompletionSource<Dictionary<object, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        try
        {
            if (!data.ContainsKey("_x"))
            {
                data["_x"] = Interlocked.Increment(ref _xid);
            }

            await SendFrameAsync(frameType, Opack.Pack(data), cancellationToken);
            var reply = await tcs.Task.WaitAsync(timeout ?? DefaultTimeout, cancellationToken);
            if (reply.TryGetValue("_em", out var error))
            {
                throw new CompanionProtocolException($"The Apple TV rejected the command: {error}");
            }

            return reply;
        }
        catch (TimeoutException)
        {
            throw new CompanionProtocolException($"The Apple TV did not answer ({data.GetValueOrDefault("_i") ?? frameType}).");
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        var header = new byte[HeaderLength];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _cts.Token);
                var length = (header[1] << 16) | (header[2] << 8) | header[3];
                var payload = new byte[length];
                if (length > 0)
                {
                    await _stream.ReadExactlyAsync(payload, _cts.Token);
                }

                if (_inKey is { } inKey && length > 0)
                {
                    payload = HapCrypto.Decrypt(inKey, CounterNonce(_inCounter++), payload, header);
                }

                Dispatch((CompanionFrameType)header[0], payload);
                await WaitForAuthHandledAsync();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
            failure = new CompanionProtocolException("The Apple TV closed the connection.");
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Shutdown(failure);
    }

    /// <summary>
    /// After an auth reply, waits until the caller has handled it: the reply to the last pair-verify step is
    /// followed by encrypted frames, and those must not be read before the keys are set.
    /// </summary>
    private async Task WaitForAuthHandledAsync()
    {
        if (Volatile.Read(ref _authGate) is not { } gate)
        {
            return;
        }

        try
        {
            await gate.Task.WaitAsync(AuthGateTimeout, _cts.Token);
        }
        catch (TimeoutException)
        {
            // The caller gave up on the reply; go on reading.
        }

        Interlocked.CompareExchange(ref _authGate, null, gate);
    }

    private void Dispatch(CompanionFrameType frameType, byte[] payload)
    {
        if (frameType is not (CompanionFrameType.PsStart or CompanionFrameType.PsNext or CompanionFrameType.PvStart or CompanionFrameType.PvNext
            or CompanionFrameType.UOpack or CompanionFrameType.EOpack or CompanionFrameType.POpack))
        {
            return; // NoOp and unsupported frames
        }

        Dictionary<object, object?> message;
        try
        {
            message = Opack.UnpackDictionary(payload);
        }
        catch (FormatException)
        {
            return;
        }

        if (frameType is CompanionFrameType.PsStart or CompanionFrameType.PsNext or CompanionFrameType.PvStart or CompanionFrameType.PvNext)
        {
            if (_pending.TryRemove(AuthKey(frameType), out var auth))
            {
                // The gate is set before the caller can see the reply, so its EnableEncryption always opens it.
                Volatile.Write(ref _authGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                auth.TrySetResult(message);
            }

            return;
        }

        switch (message.GetValueOrDefault("_t") is long t ? (CompanionMessageType)t : 0)
        {
            case CompanionMessageType.Event:
                if (message.GetValueOrDefault("_i") is string name)
                {
                    var content = message.GetValueOrDefault("_c") as Dictionary<object, object?> ?? [];
                    try
                    {
                        EventReceived?.Invoke(name, content);
                    }
                    catch
                    {
                        // A listener error must not kill the connection.
                    }
                }

                break;
            case CompanionMessageType.Response:
                if (message.GetValueOrDefault("_x") is long xid && _pending.TryRemove(XidKey(xid), out var response))
                {
                    response.TrySetResult(message);
                }

                break;
        }
    }

    private void Shutdown(Exception? failure)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        foreach (var (_, pending) in _pending)
        {
            pending.TrySetException(failure ?? new CompanionProtocolException("Connection closed."));
        }

        _pending.Clear();
        ReleaseAuthGate();
        try
        {
            _tcp.Dispose();
        }
        catch
        {
        }

        try
        {
            Closed?.Invoke(failure);
        }
        catch
        {
        }
    }

    private static string AuthKey(CompanionFrameType frameType) => $"auth:{(int)frameType}";

    private static string XidKey(long xid) => $"x:{xid}";

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Shutdown(null);
        try
        {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _cts.Dispose();

        // The send lock is not disposed: a send still in flight releases it when its write fails, and releasing
        // a disposed SemaphoreSlim throws. It holds no unmanaged handle, so nothing leaks.
    }
}
