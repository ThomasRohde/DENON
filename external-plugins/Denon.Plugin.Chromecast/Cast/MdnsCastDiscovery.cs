using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Denon.Plugin.Chromecast.Cast;

internal sealed record DiscoveredCastDevice(string Id, string Name, string Model, string Host, int Port);

/// <summary>
/// Minimal mDNS browser for "_googlecast._tcp.local". It sends a legacy unicast query
/// on every active IPv4 interface and reads PTR, SRV, TXT and A records from the answers.
/// </summary>
internal static class MdnsCastDiscovery
{
    private const string ServiceName = "_googlecast._tcp.local";
    private static readonly IPEndPoint MdnsEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<IReadOnlyList<DiscoveredCastDevice>> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var records = new RecordSet();
        var query = BuildPtrQuery(ServiceName);
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

            // Send the query three times: mDNS answers on Wi-Fi are often lost.
            for (var i = 0; i < 3 && !timeout.IsCancellationRequested; i++)
            {
                foreach (var client in clients)
                {
                    try
                    {
                        await client.SendAsync(query, MdnsEndpoint, timeout.Token);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
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
        return records.Build();
    }

    internal static byte[] BuildPtrQuery(string name)
    {
        using var ms = new MemoryStream();
        ms.Write([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]); // id 0, flags 0, 1 question
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }

        ms.Write([0, 0, 12, 0, 1]); // root, type PTR, class IN
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
                case 12: // PTR
                    var rdPos = pos;
                    var instance = ReadName(packet, ref rdPos);
                    if (name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase))
                    {
                        records.Instances.Add(instance);
                        records.Senders[instance] = sender;
                    }

                    break;
                case 33: // SRV
                    if (length >= 7)
                    {
                        var port = ReadUInt16(packet, pos + 4);
                        var targetPos = pos + 6;
                        records.Services[name] = (ReadName(packet, ref targetPos), port);
                        records.Senders.TryAdd(name, sender);
                    }

                    break;
                case 16: // TXT
                    var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var p = pos;
                    while (p < end)
                    {
                        var len = packet[p++];
                        if (p + len > end)
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
                    }

                    records.Texts[name] = txt;
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
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && CastChannel.IsPrivateLanAddress(unicast.Address))
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

    internal sealed class RecordSet
    {
        public HashSet<string> Instances { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, (string Target, int Port)> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Dictionary<string, string>> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, IPAddress> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, IPAddress> Senders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<DiscoveredCastDevice> Build()
        {
            var result = new List<DiscoveredCastDevice>();
            foreach (var instance in Instances.Union(Services.Keys, StringComparer.OrdinalIgnoreCase))
            {
                Texts.TryGetValue(instance, out var txt);
                txt ??= new Dictionary<string, string>();
                var model = txt.GetValueOrDefault("md", "");
                if (model.Equals("Google Cast Group", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Speaker groups are not supported in this version.
                }

                var port = 8009;
                IPAddress? address = null;
                if (Services.TryGetValue(instance, out var srv))
                {
                    port = srv.Port;
                    Addresses.TryGetValue(srv.Target, out address);
                }

                address ??= Senders.GetValueOrDefault(instance);
                if (address is null || !CastChannel.IsPrivateLanAddress(address))
                {
                    continue;
                }

                var id = txt.GetValueOrDefault("id") is { Length: > 0 } txtId ? txtId : instance;
                var name = txt.GetValueOrDefault("fn") is { Length: > 0 } fn ? fn : instance.Split('.')[0];
                result.Add(new DiscoveredCastDevice(id, name, model, address.ToString(), port));
            }

            return result;
        }
    }
}
