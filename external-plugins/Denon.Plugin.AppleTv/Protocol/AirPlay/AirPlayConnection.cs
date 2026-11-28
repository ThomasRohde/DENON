using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Denon.Plugin.AppleTv.Protocol.Hap;

namespace Denon.Plugin.AppleTv.Protocol.AirPlay;

/// <summary>An HTTP or RTSP message (request or response).</summary>
internal sealed record HttpMessage(string StartLine, Dictionary<string, string> Headers, byte[] Body)
{
    public int StatusCode => int.TryParse(StartLine.Split(' ').ElementAtOrDefault(1), out var code) ? code : 0;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>Formats a message: start line, headers (Content-Length added for a body), blank line, body.</summary>
    public static byte[] Format(string startLine, IEnumerable<KeyValuePair<string, string>> headers, byte[]? body)
    {
        var sb = new StringBuilder(startLine);
        foreach (var (key, value) in headers)
        {
            sb.Append("\r\n").Append(key).Append(": ").Append(value);
        }

        if (body is { Length: > 0 })
        {
            sb.Append("\r\nContent-Length: ").Append(body.Length);
        }

        sb.Append("\r\n\r\n");
        var head = Encoding.UTF8.GetBytes(sb.ToString());
        return body is { Length: > 0 } ? [.. head, .. body] : head;
    }

    /// <summary>Largest header block we wait for. Real AirPlay headers are well under 1 KB.</summary>
    public const int MaxHeaderBytes = 64 * 1024;

    /// <summary>Largest body we accept. AirPlay bodies are small plists.</summary>
    public const int MaxBodyBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Parses one complete message from the start of <paramref name="buffer"/>; null when incomplete.
    /// Throws <see cref="InvalidDataException"/> for a bad or oversized Content-Length or header block,
    /// so a broken peer fails the connection instead of stalling it.
    /// </summary>
    public static HttpMessage? TryParse(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;
        var end = buffer.IndexOf("\r\n\r\n"u8);
        if (end > MaxHeaderBytes || (end < 0 && buffer.Length > MaxHeaderBytes))
        {
            // Also when the end is there: a huge header block would still be decoded and split in full.
            throw new InvalidDataException("The HTTP header block is too large.");
        }

        if (end < 0)
        {
            return null;
        }

        var lines = Encoding.UTF8.GetString(buffer[..end]).Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon]] = line[(colon + 2)..];
            }
        }

        var length = 0;
        if (headers.TryGetValue("Content-Length", out var cl)
            && (!int.TryParse(cl.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > MaxBodyBytes))
        {
            throw new InvalidDataException($"The HTTP Content-Length '{cl}' is invalid.");
        }

        if (buffer.Length - (end + 4) < length)
        {
            return null;
        }

        consumed = end + 4 + length;
        return new HttpMessage(lines[0], headers, buffer.Slice(end + 4, length).ToArray());
    }
}

internal sealed class AirPlayHttpException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The AirPlay control connection (port 7000): sequential HTTP/RTSP request/response, encrypted with a
/// <see cref="HapSession"/> after pair-verify (pyatv: support/http.HttpConnection + airplay/auth.verify_connection).
/// </summary>
internal sealed class AirPlayConnection : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly MemoryStream _buffer = new();
    private HapSession? _session;

    private AirPlayConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        LocalAddress = ((IPEndPoint)tcp.Client.LocalEndPoint!).Address;
        RemoteAddress = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
    }

    public IPAddress LocalAddress { get; }

    public IPAddress RemoteAddress { get; }

    public bool IsConnected => _tcp.Connected;

    public static async Task<AirPlayConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, port, timeout.Token);
            return new AirPlayConnection(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public void EnableEncryption(byte[] outputKey, byte[] inputKey) => _session = new HapSession(outputKey, inputKey);

    /// <summary>Sends a request and returns the response. Non-2xx throws <see cref="AirPlayHttpException"/>.</summary>
    public async Task<HttpMessage> SendAsync(
        string method,
        string uri,
        IEnumerable<KeyValuePair<string, string>> headers,
        byte[]? body,
        CancellationToken cancellationToken,
        string protocol = "HTTP/1.1",
        bool allowError = false)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var raw = HttpMessage.Format($"{method} {uri} {protocol}", headers, body);
            var wire = _session is null ? raw : _session.Encrypt(raw);
            await _stream.WriteAsync(wire, cancellationToken);
            await _stream.FlushAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var response = await ReadMessageAsync(timeout.Token);
            if (!allowError && response.StatusCode is < 200 or >= 300)
            {
                throw new AirPlayHttpException($"{method} {uri} failed: {response.StartLine}", response.StatusCode);
            }

            return response;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<HttpMessage> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var chunk = new byte[8192];
        while (true)
        {
            var parsed = HttpMessage.TryParse(_buffer.GetBuffer().AsSpan(0, (int)_buffer.Length), out var consumed);
            if (parsed is not null)
            {
                var rest = _buffer.GetBuffer().AsSpan(consumed, (int)_buffer.Length - consumed).ToArray();
                _buffer.SetLength(0);
                _buffer.Write(rest);
                return parsed;
            }

            var read = await _stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                throw new IOException("The Apple TV closed the AirPlay connection.");
            }

            _buffer.Write(_session is null ? chunk.AsSpan(0, read) : _session.Decrypt(chunk.AsSpan(0, read)));
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _tcp.Dispose();
        }
        catch
        {
        }

        // The lock is not disposed: a request still in flight releases it when its read fails, and
        // releasing a disposed SemaphoreSlim throws. It holds no unmanaged handle, so nothing leaks.
        return ValueTask.CompletedTask;
    }
}
