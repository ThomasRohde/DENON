using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Denon.Plugin.PhilipsHue.Models;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Denon.Plugin.PhilipsHue.Services;

/// <summary>
/// True Philips Hue Entertainment API v2 streaming client over DTLS 1.2 PSK (UDP port 2100)
/// supporting 50–60 Hz zero-lag RGB frame streaming directly to Hue Entertainment Areas.
/// </summary>
public sealed class HueEntertainmentStreamClient : IAsyncDisposable
{
    private const int HueEntertainmentUdpPort = 2100;
    private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("HueStream");

    private readonly HttpClient _http;
    private readonly object _sendLock = new();

    /// <summary>
    /// Start and stop run one at a time. A stop that comes during a start waits for it, then stops what it started.
    /// </summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    /// <summary>Counts the streams started; the cleanup after a failed send stops only its own stream.</summary>
    private int _sessionId;

    private Socket? _udpSocket;
    private DtlsTransport? _dtlsTransport;
    private string? _activeBridgeIp;
    private string? _activeAppKey;
    private string? _activeAreaId;
    private byte[]? _areaUuidAscii;
    private int _channelCount;
    private byte _sequence;
    private volatile bool _isStreaming;

    public HueEntertainmentStreamClient(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public bool IsStreaming => _isStreaming;

    public int ActiveChannelCount => _channelCount;

    /// <summary>The id of the area that streams now, or null when no stream runs.</summary>
    public string? StreamingAreaId => _isStreaming ? Volatile.Read(ref _activeAreaId) : null;

    public async Task<bool> StartAsync(
        HuePluginConfig config,
        HueEntertainmentArea area,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            return await StartCoreAsync(config, area, cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<bool> StartCoreAsync(
        HuePluginConfig config,
        HueEntertainmentArea area,
        CancellationToken cancellationToken)
    {
        await StopCoreAsync();
        Interlocked.Increment(ref _sessionId);

        if (config.DemoMode ||
            string.IsNullOrWhiteSpace(config.BridgeIp) ||
            string.IsNullOrWhiteSpace(config.ApplicationKey) ||
            string.IsNullOrWhiteSpace(config.ClientKey) ||
            string.IsNullOrWhiteSpace(area.Id))
        {
            return false;
        }

        var cleanHost = CleanHost(config.BridgeIp);
        var pskSecret = Convert.FromHexString(config.ClientKey.Trim());
        var pskIdentity = await ResolveHueApplicationIdAsync(cleanHost, config.ApplicationKey, cancellationToken);

        // 1. Activate streaming mode on the Bridge via CLIP v2
        var activated = await SetEntertainmentModeAsync(cleanHost, config.ApplicationKey, area.Id, start: true, cancellationToken);
        if (!activated)
        {
            return false;
        }

        // Retain stop identifiers IMMEDIATELY after Bridge activation so any DTLS handshake failure
        // always sends the matching Stop request and never leaves the entertainment area locked active!
        _activeBridgeIp = cleanHost;
        _activeAppKey = config.ApplicationKey;
        _activeAreaId = area.Id;

        try
        {
            // 2. Connect UDP socket & perform DTLS 1.2 PSK handshake on port 2100 (bounded 10s timeout)
            // The bridge address may be a host name typed by hand, not only an IP from discovery.
            var ip = await ResolveBridgeAddressAsync(cleanHost, cancellationToken);
            var socket = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(ip, HueEntertainmentUdpPort));
            _udpSocket = socket;

            var udpTransport = new SocketDatagramTransport(socket);
            var dtlsClient = new HuePskDtlsClient(
                Encoding.ASCII.GetBytes(pskIdentity),
                pskSecret);
            var protocol = new DtlsClientProtocol();

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(10));
            _dtlsTransport = await Task.Run(() => protocol.Connect(dtlsClient, udpTransport), handshakeCts.Token)
                .WaitAsync(handshakeCts.Token);
            _areaUuidAscii = Encoding.ASCII.GetBytes(area.Id.PadRight(36)[..36]);
            _channelCount = Math.Clamp(area.ChannelCount, 1, 20);
            _sequence = 0;
            _isStreaming = true;
            return true;
        }
        catch
        {
            await StopCoreAsync();
            return false;
        }
    }

    /// <summary>
    /// Sends a single HueStream v2.0 RGB frame over DTLS/UDP (non-blocking, safe up to 60 Hz).
    /// Each channel entry is (R, G, B) in 0..255 scaled by brightness.
    /// </summary>
    public bool TrySendRgbFrame(ReadOnlySpan<(byte R, byte G, byte B)> channelColors)
    {
        if (!_isStreaming || _dtlsTransport is null || _areaUuidAscii is null || channelColors.Length == 0)
        {
            return false;
        }

        var count = Math.Min(channelColors.Length, _channelCount);
        var packetLen = 16 + 36 + (count * 7);
        var packet = new byte[packetLen];

        // Header: "HueStream" (9) + version 2.0 (2) + seq (1) + reserved (2) + colorspace RGB 0x00 (1) + reserved (1)
        MagicHeader.CopyTo(packet, 0);
        packet[9] = 0x02;
        packet[10] = 0x00;
        packet[11] = _sequence++;
        packet[12] = 0x00;
        packet[13] = 0x00;
        packet[14] = 0x00; // RGB
        packet[15] = 0x00;

        // Entertainment Configuration UUID (36 ASCII bytes)
        _areaUuidAscii.CopyTo(packet, 16);

        var offset = 52;
        for (var ch = 0; ch < count; ch++)
        {
            var (r, g, b) = channelColors[ch];
            var r16 = (ushort)(r * 257);
            var g16 = (ushort)(g * 257);
            var b16 = (ushort)(b * 257);

            packet[offset++] = (byte)ch;
            packet[offset++] = (byte)(r16 >> 8);
            packet[offset++] = (byte)(r16 & 0xFF);
            packet[offset++] = (byte)(g16 >> 8);
            packet[offset++] = (byte)(g16 & 0xFF);
            packet[offset++] = (byte)(b16 >> 8);
            packet[offset++] = (byte)(b16 & 0xFF);
        }

        lock (_sendLock)
        {
            if (!_isStreaming || _dtlsTransport is null)
            {
                return false;
            }

            try
            {
                _dtlsTransport.Send(packet, 0, packet.Length);
                return true;
            }
            catch
            {
                _isStreaming = false;
            }
        }

        // Close the socket and release the area now; the next call returns false at once (not streaming).
        // Only this stream: a new one may have started by the time the cleanup runs.
        var failedSession = Volatile.Read(ref _sessionId);
        _ = Task.Run(() => StopSessionAsync(failedSession));
        return false;
    }

    private async Task StopSessionAsync(int sessionId)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _sessionId) == sessionId)
            {
                await StopCoreAsync();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static async Task<IPAddress> ResolveBridgeAddressAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        return addresses.FirstOrDefault(static a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new InvalidOperationException($"Could not resolve the Hue bridge '{host}'.");
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        _isStreaming = false;

        lock (_sendLock)
        {
            try { _dtlsTransport?.Close(); } catch { }
            _dtlsTransport = null;

            try { _udpSocket?.Close(); } catch { }
            try { _udpSocket?.Dispose(); } catch { }
            _udpSocket = null;
        }

        if (!string.IsNullOrWhiteSpace(_activeBridgeIp) &&
            !string.IsNullOrWhiteSpace(_activeAppKey) &&
            !string.IsNullOrWhiteSpace(_activeAreaId))
        {
            try
            {
                await SetEntertainmentModeAsync(_activeBridgeIp, _activeAppKey, _activeAreaId, start: false, CancellationToken.None);
            }
            catch
            {
            }
        }

        _activeBridgeIp = null;
        _activeAppKey = null;
        _activeAreaId = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task<string> ResolveHueApplicationIdAsync(string host, string appKey, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/auth/v1");
            req.Headers.Add("hue-application-key", appKey);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.Headers.TryGetValues("hue-application-id", out var values))
            {
                var id = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id.Trim();
                }
            }
        }
        catch
        {
        }

        return appKey;
    }

    private async Task<bool> SetEntertainmentModeAsync(string host, string appKey, string areaId, bool start, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"https://{host}/clip/v2/resource/entertainment_configuration/{Uri.EscapeDataString(areaId)}");
            req.Headers.Add("hue-application-key", appKey);
            req.Content = new StringContent(start ? "{\"action\":\"start\"}" : "{\"action\":\"stop\"}", Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string CleanHost(string ip) =>
        ip.Trim()
          .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
          .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
          .TrimEnd('/');

    private sealed class HuePskDtlsClient : PskTlsClient
    {
        public HuePskDtlsClient(byte[] identity, byte[] psk)
            : base(new BcTlsCrypto(new SecureRandom()), new BasicTlsPskIdentity(identity, psk))
        {
        }

        protected override ProtocolVersion[] GetSupportedVersions() =>
            [ProtocolVersion.DTLSv12];

        protected override int[] GetSupportedCipherSuites() =>
            [CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];

        public override TlsAuthentication GetAuthentication() => new NoOpTlsAuth();

        private sealed class NoOpTlsAuth : TlsAuthentication
        {
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
            {
            }

            public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
        }
    }

    private sealed class SocketDatagramTransport(Socket socket) : DatagramTransport
    {
        public int GetReceiveLimit() => 1400;

        public int GetSendLimit() => 1400;

        public int Receive(byte[] buf, int off, int len, int waitMillis)
        {
            if (!socket.Poll(Math.Max(1, waitMillis) * 1000, SelectMode.SelectRead))
            {
                return -1;
            }

            return socket.Receive(buf, off, len, SocketFlags.None);
        }

        public int Receive(Span<byte> buffer, int waitMillis)
        {
            if (!socket.Poll(Math.Max(1, waitMillis) * 1000, SelectMode.SelectRead))
            {
                return -1;
            }

            return socket.Receive(buffer, SocketFlags.None);
        }

        public void Send(byte[] buf, int off, int len) =>
            socket.Send(buf, off, len, SocketFlags.None);

        public void Send(ReadOnlySpan<byte> buffer) =>
            socket.Send(buffer, SocketFlags.None);

        public void Close()
        {
            try { socket.Close(); } catch { }
        }
    }
}
