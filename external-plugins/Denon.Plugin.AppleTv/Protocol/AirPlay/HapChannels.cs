using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Denon.Plugin.AppleTv.Protocol.Hap;

namespace Denon.Plugin.AppleTv.Protocol.AirPlay;

/// <summary>
/// A separate TCP channel encrypted with a <see cref="HapSession"/> (pyatv: auth/hap_channel.AbstractHAPChannel).
/// Subclasses parse the decrypted byte stream.
/// </summary>
internal abstract class HapChannel : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly HapSession _session;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private int _closed;

    protected HapChannel(TcpClient tcp, byte[] outputKey, byte[] inputKey)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _session = new HapSession(outputKey, inputKey);
    }

    public event Action<Exception?>? Closed;

    protected MemoryStream Buffer { get; } = new();

    protected static async Task<TcpClient> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, port, timeout.Token);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    protected void Start() => _readLoop = Task.Run(ReadLoopAsync);

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(_session.Encrypt(data), cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Called with new plaintext in <see cref="Buffer"/>; consume what is complete.</summary>
    protected abstract Task HandleReceivedAsync(CancellationToken cancellationToken);

    /// <summary>Removes the first <paramref name="count"/> bytes from <see cref="Buffer"/>.</summary>
    protected void Consume(int count)
    {
        var rest = Buffer.GetBuffer().AsSpan(count, (int)Buffer.Length - count).ToArray();
        Buffer.SetLength(0);
        Buffer.Write(rest);
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        var chunk = new byte[16384];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(chunk, _cts.Token);
                if (read == 0)
                {
                    break;
                }

                var plain = _session.Decrypt(chunk.AsSpan(0, read));
                if (plain.Length > 0)
                {
                    Buffer.Write(plain);
                    await HandleReceivedAsync(_cts.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            try
            {
                Closed?.Invoke(failure);
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        _cts.Cancel();
        try
        {
            _tcp.Dispose();
        }
        catch
        {
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        _cts.Dispose();
    }
}

/// <summary>
/// Event channel: the Apple TV sends HTTP/RTSP requests; we answer 200 OK (pyatv: channels.EventChannel).
/// It is not used for data, but it must be open for the session to work.
/// </summary>
internal sealed class EventChannel : HapChannel
{
    private EventChannel(TcpClient tcp, byte[] outputKey, byte[] inputKey)
        : base(tcp, outputKey, inputKey)
    {
    }

    public static async Task<EventChannel> ConnectAsync(string host, int port, byte[] outputKey, byte[] inputKey, CancellationToken cancellationToken)
    {
        var channel = new EventChannel(await ConnectTcpAsync(host, port, cancellationToken), outputKey, inputKey);
        channel.Start();
        return channel;
    }

    protected override async Task HandleReceivedAsync(CancellationToken cancellationToken)
    {
        while (HttpMessage.TryParse(Buffer.GetBuffer().AsSpan(0, (int)Buffer.Length), out var consumed) is { } request)
        {
            Consume(consumed);
            var protocol = request.StartLine.Split(' ').ElementAtOrDefault(2) ?? "RTSP/1.0";
            var headers = new List<KeyValuePair<string, string>>
            {
                new("Content-Length", "0"),
                new("Audio-Latency", "0"),
            };
            if (request.Header("Server") is { } server)
            {
                headers.Add(new("Server", server));
            }

            if (request.Header("CSeq") is { } cseq)
            {
                headers.Add(new("CSeq", cseq));
            }

            await SendAsync(HttpMessage.Format($"{protocol} 200 OK", headers, null), cancellationToken);
        }
    }
}

/// <summary>
/// Data stream channel (type 130) carrying MRP protobuf messages (pyatv: channels.DataStreamChannel).
/// Frame = 32-byte big-endian header (size incl. header, 12-byte message type, 4-byte command, 8-byte seqno,
/// 4-byte padding) + binary plist {"params": {"data": varint-length-prefixed protobufs}}. "sync" frames from
/// the Apple TV are answered with an empty "rply" frame with the same seqno.
/// </summary>
internal sealed class DataStreamChannel : HapChannel
{
    public const int HeaderLength = 32;

    private readonly ulong _sendSeqno = (ulong)Random.Shared.NextInt64(0x100000000, 0x1FFFFFFFF);

    private DataStreamChannel(TcpClient tcp, byte[] outputKey, byte[] inputKey)
        : base(tcp, outputKey, inputKey)
    {
    }

    /// <summary>Raw serialized ProtocolMessage protobufs received from the Apple TV.</summary>
    public event Action<byte[]>? ProtobufReceived;

    public static async Task<DataStreamChannel> ConnectAsync(string host, int port, byte[] outputKey, byte[] inputKey, CancellationToken cancellationToken)
    {
        var channel = new DataStreamChannel(await ConnectTcpAsync(host, port, cancellationToken), outputKey, inputKey);
        channel.Start();
        return channel;
    }

    public Task SendProtobufAsync(byte[] protocolMessage, CancellationToken cancellationToken)
    {
        byte[] data = [.. ProtoWriter.Varint((ulong)protocolMessage.Length), .. protocolMessage];
        var payload = BinaryPlist.Write(new Dictionary<string, object?> { ["params"] = new Dictionary<string, object?> { ["data"] = data } });
        return SendAsync(EncodeFrame("sync", "comm", _sendSeqno, payload), cancellationToken);
    }

    public static byte[] EncodeFrame(string messageType, string command, ulong seqno, byte[] payload)
    {
        var frame = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)frame.Length);
        Encoding.ASCII.GetBytes(messageType).AsSpan(0, Math.Min(12, messageType.Length)).CopyTo(frame.AsSpan(4, 12));
        Encoding.ASCII.GetBytes(command).AsSpan(0, Math.Min(4, command.Length)).CopyTo(frame.AsSpan(16, 4));
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(20), seqno);
        // padding (4 bytes) stays zero
        payload.CopyTo(frame, HeaderLength);
        return frame;
    }

    /// <summary>Splits concatenated protobufs: varint length prefixes, or one raw message starting with 0x08.</summary>
    public static List<byte[]> SplitProtobufs(byte[] data)
    {
        var messages = new List<byte[]>();
        var pos = 0;
        while (pos < data.Length)
        {
            if (data[pos] == 0x08)
            {
                messages.Add(data[pos..]); // not length-prefixed (seen for ConfigureConnectionMessage)
                break;
            }

            var length = (int)ProtoReader.ReadVarint(data, ref pos);
            if (length <= 0 || pos + length > data.Length)
            {
                break;
            }

            messages.Add(data[pos..(pos + length)]);
            pos += length;
        }

        return messages;
    }

    protected override async Task HandleReceivedAsync(CancellationToken cancellationToken)
    {
        while (Buffer.Length >= HeaderLength)
        {
            var span = Buffer.GetBuffer().AsSpan(0, (int)Buffer.Length);
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(span);
            if (size < HeaderLength || span.Length < size)
            {
                break;
            }

            var messageType = Encoding.ASCII.GetString(span.Slice(4, 12)).TrimEnd('\0');
            var seqno = BinaryPrimitives.ReadUInt64BigEndian(span[20..]);
            var payload = span[HeaderLength..size].ToArray();
            Consume(size);

            if (payload.Length > 0)
            {
                try
                {
                    if (BinaryPlist.Read(payload) is Dictionary<string, object?> plist &&
                        plist.GetValueOrDefault("params") is Dictionary<string, object?> parameters &&
                        parameters.GetValueOrDefault("data") is byte[] data)
                    {
                        foreach (var message in SplitProtobufs(data))
                        {
                            ProtobufReceived?.Invoke(message);
                        }
                    }
                }
                catch (FormatException)
                {
                    // Unknown payload: ignore, but still answer sync frames below.
                }
            }

            if (messageType.StartsWith("sync", StringComparison.Ordinal))
            {
                await SendAsync(EncodeFrame("rply", "\0\0\0\0", seqno, []), cancellationToken);
            }
        }
    }
}
