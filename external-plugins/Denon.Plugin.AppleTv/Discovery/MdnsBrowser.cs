using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Denon.Plugin.AppleTv.Discovery;

/// <summary>One DNS-SD service instance found on the LAN.</summary>
/// <param name="Type">Service type, for example <c>_companion-link._tcp.local</c>.</param>
/// <param name="Instance">Full instance name, for example <c>Living Room._companion-link._tcp.local</c>.</param>
/// <param name="Target">SRV target host name, or null when no SRV record was seen.</param>
/// <param name="Port">SRV port, or 0 when unknown.</param>
/// <param name="Txt">TXT key/value pairs (flag keys without '=' have an empty value).</param>
/// <param name="Address">IPv4 address (A record of the target, else the sender of the answer).</param>
internal sealed record MdnsService(
    string Type,
    string Instance,
    string? Target,
    int Port,
    IReadOnlyDictionary<string, string> Txt,
    IPAddress? Address)
{
    /// <summary>The user-visible part of the instance name (before the service type).</summary>
    public string InstanceLabel => Instance.EndsWith("." + Type, StringComparison.OrdinalIgnoreCase)
        ? Instance[..^(Type.Length + 1)]
        : Instance.Split('.')[0];
}

/// <summary>
/// Minimal DNS-SD browser. One query asks for several service types (PTR). It is sent as a legacy unicast
/// query (from an ephemeral port) to the mDNS multicast group on every active private IPv4 interface, and
/// optionally straight to known hosts on port 5353 (answers then come back from that host, which also works
/// when the firewall drops unsolicited multicast replies). PTR, SRV, TXT and A records are read from all
/// sections of every answer.
/// </summary>
internal static class MdnsBrowser
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<IReadOnlyList<MdnsService>> BrowseAsync(
        IReadOnlyList<string> serviceTypes,
        TimeSpan duration,
        IReadOnlyList<IPAddress>? unicastHosts,
        CancellationToken cancellationToken)
    {
        var records = new RecordSet(serviceTypes);
        var query = BuildPtrQuery(serviceTypes);
        var clients = new List<UdpClient>();
        try
        {
            foreach (var address in GetLocalIPv4Addresses())
            {
                try
                {
                    var client = new UdpClient(new IPEndPoint(address, 0));
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    clients.Add(client);
                }
                catch (SocketException)
                {
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(duration);
            var receivers = clients.Select(c => ReceiveLoopAsync(c, records, timeout.Token)).ToList();

            // Send three times: mDNS answers on Wi-Fi are often lost.
            for (var i = 0; i < 3 && !timeout.IsCancellationRequested; i++)
            {
                foreach (var client in clients)
                {
                    await TrySendAsync(client, query, MulticastEndpoint, timeout.Token);
                    foreach (var host in unicastHosts ?? [])
                    {
                        await TrySendAsync(client, query, new IPEndPoint(host, 5353), timeout.Token);
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(700), timeout.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await Task.WhenAll(receivers);
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (records)
        {
            return records.Build();
        }
    }

    internal static byte[] BuildPtrQuery(IReadOnlyList<string> names)
    {
        using var ms = new MemoryStream();
        ms.Write([0, 0, 0, 0, 0, (byte)names.Count, 0, 0, 0, 0, 0, 0]); // id 0, flags 0, N questions
        foreach (var name in names)
        {
            foreach (var label in name.Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes);
            }

            ms.Write([0, 0, 12, 0, 1]); // root, type PTR, class IN
        }

        return ms.ToArray();
    }

    internal static void ParsePacket(byte[] packet, IPAddress sender, RecordSet records)
    {
        if (packet.Length < 12)
        {
            return;
        }

        var questions = ReadUInt16(packet, 4);
        var total = ReadUInt16(packet, 6) + ReadUInt16(packet, 8) + ReadUInt16(packet, 10);
        var pos = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(packet, ref pos);
            pos += 4;
        }

        for (var i = 0; i < total && pos + 10 <= packet.Length; i++)
        {
            var name = ReadName(packet, ref pos);
            if (pos + 10 > packet.Length)
            {
                return;
            }

            var type = ReadUInt16(packet, pos);
            var length = ReadUInt16(packet, pos + 8);
            pos += 10;
            var end = pos + length;
            if (end > packet.Length)
            {
                return;
            }

            switch (type)
            {
                case 12: // PTR: service type → instance
                    var rdPos = pos;
                    var instance = ReadName(packet, ref rdPos);
                    if (records.IsWanted(name))
                    {
                        records.Instances[instance] = name;
                        records.Senders[instance] = sender;
                    }

                    break;
                case 33: // SRV: instance → target host + port
                    if (length >= 7)
                    {
                        var port = ReadUInt16(packet, pos + 4);
                        var targetPos = pos + 6;
                        records.Services[name] = (ReadName(packet, ref targetPos), port);
                        records.Senders.TryAdd(name, sender);
                    }

                    break;
                case 16: // TXT
                    records.Texts[name] = ParseTxt(packet, pos, end);
                    break;
                case 1: // A
                    if (length == 4)
                    {
                        records.Addresses[name] = new IPAddress(packet.AsSpan(pos, 4));
                    }

                    break;
            }

            pos = end;
        }
    }

    internal static Dictionary<string, string> ParseTxt(byte[] packet, int start, int end)
    {
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var p = start;
        while (p < end)
        {
            var len = packet[p++];
            if (len == 0 || p + len > end)
            {
                break;
            }

            var entry = Encoding.UTF8.GetString(packet, p, len);
            p += len;
            var eq = entry.IndexOf('=');
            if (eq > 0)
            {
                txt[entry[..eq]] = entry[(eq + 1)..];
            }
            else if (eq < 0)
            {
                txt[entry] = string.Empty;
            }
        }

        return txt;
    }

    /// <summary>True for RFC 1918 private IPv4 addresses (the plugin talks to the local LAN only).</summary>
    internal static bool IsPrivateLanAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static async Task TrySendAsync(UdpClient client, byte[] query, IPEndPoint endpoint, CancellationToken ct)
    {
        try
        {
            await client.SendAsync(query, endpoint, ct);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
        }
    }

    private static async Task ReceiveLoopAsync(UdpClient client, RecordSet records, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(ct);
                lock (records)
                {
                    ParsePacket(result.Buffer, result.RemoteEndPoint.Address, records);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }
            catch
            {
                // Ignore malformed packets.
            }
        }
    }

    private static IEnumerable<IPAddress> GetLocalIPv4Addresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                !nic.SupportsMulticast)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (IsPrivateLanAddress(unicast.Address))
                {
                    yield return unicast.Address;
                }
            }
        }
    }

    private static int ReadUInt16(byte[] data, int offset) =>
        offset + 1 < data.Length ? (data[offset] << 8) | data[offset + 1] : 0;

    private static string ReadName(byte[] data, ref int pos)
    {
        var labels = new List<string>();
        var cursor = pos;
        var jumped = false;
        for (var guard = 0; guard < 128 && cursor < data.Length; guard++)
        {
            var len = data[cursor];
            if (len == 0)
            {
                cursor++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= data.Length)
                {
                    break;
                }

                var target = ((len & 0x3F) << 8) | data[cursor + 1];
                if (!jumped)
                {
                    pos = cursor + 2;
                }

                jumped = true;
                cursor = target;
                continue;
            }

            cursor++;
            if (cursor + len > data.Length)
            {
                break;
            }

            labels.Add(Encoding.UTF8.GetString(data, cursor, len));
            cursor += len;
        }

        if (!jumped)
        {
            pos = cursor;
        }

        return string.Join('.', labels);
    }

    internal sealed class RecordSet(IReadOnlyList<string> wantedTypes)
    {
        private readonly HashSet<string> _wanted = new(wantedTypes, StringComparer.OrdinalIgnoreCase);

        /// <summary>Instance name → service type.</summary>
        public Dictionary<string, string> Instances { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, (string Target, int Port)> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Dictionary<string, string>> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, IPAddress> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, IPAddress> Senders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsWanted(string serviceType) => _wanted.Contains(serviceType);

        public IReadOnlyList<MdnsService> Build()
        {
            var result = new List<MdnsService>();
            foreach (var (instance, type) in Instances)
            {
                string? target = null;
                var port = 0;
                IPAddress? address = null;
                if (Services.TryGetValue(instance, out var srv))
                {
                    target = srv.Target;
                    port = srv.Port;
                    Addresses.TryGetValue(srv.Target, out address);
                }

                address ??= Senders.GetValueOrDefault(instance);
                if (address is not null && !IsPrivateLanAddress(address))
                {
                    continue;
                }

                Texts.TryGetValue(instance, out var txt);
                result.Add(new MdnsService(type, instance, target, port, txt ?? new Dictionary<string, string>(), address));
            }

            return result;
        }
    }
}
