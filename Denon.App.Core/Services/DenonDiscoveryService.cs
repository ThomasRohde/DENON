using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Denon.App.Core.Heos;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

public sealed class DenonDiscoveryService
{
    private const string MulticastAddress = "239.255.255.250";
    private const int SsdpPort = 1900;
    private const int DefaultHeosPort = 1255;

    private static readonly string[] SearchTargets =
    [
        "urn:schemas-denon-com:device:ACT-Denon:1",
        "urn:schemas-upnp-org:device:MediaRenderer:1",
    ];

    public async Task<IReadOnlyList<DiscoveredReceiver>> DiscoverAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var scanDuration = timeout ?? TimeSpan.FromSeconds(3.5);
        var candidateIps = await DiscoverCandidateIpsViaSsdpAsync(scanDuration, cancellationToken);
        if (candidateIps.Count == 0)
        {
            return [];
        }

        var probeTasks = candidateIps.Select(ip => ProbeHeosHostAsync(ip, cancellationToken));
        var results = await Task.WhenAll(probeTasks);

        return results
            .SelectMany(static list => list)
            .DistinctBy(static r => $"{r.IpAddress}:{r.Name}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(static r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<HashSet<string>> DiscoverCandidateIpsViaSsdpAsync(
        TimeSpan scanDuration,
        CancellationToken cancellationToken)
    {
        // Windows sends a multicast out of one interface only (the lowest metric). With a VPN or a
        // vEthernet adapter first, the LAN never sees the search. So search on every LAN interface.
        var localAddresses = GetLocalIPv4Addresses();
        var scans = localAddresses.Count > 0
            ? localAddresses.Select(address => SearchOnInterfaceAsync(address, scanDuration, cancellationToken))
            : [SearchOnInterfaceAsync(IPAddress.Any, scanDuration, cancellationToken)];

        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var found in await Task.WhenAll(scans))
        {
            ips.UnionWith(found);
        }

        return ips;
    }

    /// <summary>The IPv4 address of each network interface that is up and can send multicast (no loopback).</summary>
    internal static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(static nic => nic.OperationalStatus == OperationalStatus.Up &&
                                     nic.SupportsMulticast &&
                                     nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(static nic => nic.GetIPProperties().UnicastAddresses)
                .Select(static unicast => unicast.Address)
                .Where(static address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static async Task<HashSet<string>> SearchOnInterfaceAsync(
        IPAddress localAddress,
        TimeSpan scanDuration,
        CancellationToken cancellationToken)
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var udpClient = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(localAddress, 0));
            if (!localAddress.Equals(IPAddress.Any))
            {
                udpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
            }
        }
        catch (SocketException)
        {
            return ips; // This interface cannot send multicast.
        }

        var multicastEndPoint = new IPEndPoint(IPAddress.Parse(MulticastAddress), SsdpPort);
        foreach (var target in SearchTargets)
        {
            var packet =
                $"M-SEARCH * HTTP/1.1\r\n" +
                $"HOST: {MulticastAddress}:{SsdpPort}\r\n" +
                $"MAN: \"ssdp:discover\"\r\n" +
                $"MX: 2\r\n" +
                $"ST: {target}\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(packet);
            try
            {
                await udpClient.SendAsync(bytes, multicastEndPoint, cancellationToken);
            }
            catch (SocketException)
            {
                // Ignore interface multicast send errors
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(scanDuration);

        var receiveErrors = 0;
        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                var received = await udpClient.ReceiveAsync(timeoutCts.Token);
                var text = Encoding.ASCII.GetString(received.Buffer);
                if (LooksLikeDenonOrHeos(text))
                {
                    ips.Add(received.RemoteEndPoint.Address.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (++receiveErrors <= 20)
            {
                // One bad datagram (for example an ICMP reset) must not end the scan.
            }
            catch (SocketException)
            {
                break;
            }
        }

        return ips;
    }

    private static bool LooksLikeDenonOrHeos(string ssdpResponse) =>
        ssdpResponse.Contains("denon", StringComparison.OrdinalIgnoreCase) ||
        ssdpResponse.Contains("marantz", StringComparison.OrdinalIgnoreCase) ||
        ssdpResponse.Contains("heos", StringComparison.OrdinalIgnoreCase) ||
        ssdpResponse.Contains("ACT-Denon", StringComparison.OrdinalIgnoreCase) ||
        ssdpResponse.Contains("AiosServices", StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<DiscoveredReceiver>> ProbeHeosHostAsync(
        string ipAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var heosClient = new HeosClient();
            var settings = new ReceiverSettings
            {
                Host = ipAddress,
                Port = DefaultHeosPort,
            };

            var players = await heosClient.GetPlayersAsync(settings, cancellationToken);
            return players
                .Select(player => new DiscoveredReceiver(
                    IpAddress: string.IsNullOrWhiteSpace(player.IpAddress) ? ipAddress : player.IpAddress,
                    Name: string.IsNullOrWhiteSpace(player.Name) ? player.Model : player.Name,
                    Model: player.Model,
                    Version: player.Version,
                    Port: DefaultHeosPort))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
