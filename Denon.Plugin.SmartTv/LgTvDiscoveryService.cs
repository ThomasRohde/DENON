using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Denon.Plugin.SmartTv;

/// <summary>
/// Discovers LG webOS Smart TVs on the local LAN via multi-interface SSDP M-SEARCH
/// and resolves hardware MAC addresses via Win32 ARP for zero-config Wake-on-LAN.
/// </summary>
public static partial class LgTvDiscoveryService
{
    private static readonly string[] SearchTargets =
    [
        "urn:lge-com:service:webos-second-screen:1",
        "urn:dial-multiscreen-org:service:dial:1",
    ];

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, [Out] byte[] macAddr, ref uint phyAddrLen);

    public static async Task<IReadOnlyList<LgTvDiscoveredDevice>> DiscoverAsync(
        HttpClient httpClient,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var listenDuration = timeout ?? TimeSpan.FromSeconds(2.8);
        var localIps = GetActiveLocalIpv4Addresses();
        if (localIps.Count == 0)
        {
            localIps.Add(IPAddress.Any);
        }

        var locationByIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sync = new object();

        var probeTasks = localIps.Select(localIp => ProbeInterfaceAsync(localIp, listenDuration, locationByIp, sync, ct)).ToArray();
        await Task.WhenAll(probeTasks);

        var results = new List<LgTvDiscoveredDevice>();
        foreach (var (ip, location) in locationByIp)
        {
            var device = await InspectLocationAsync(httpClient, ip, location, ct);
            if (device is not null)
            {
                results.Add(device);
            }
        }

        return results;
    }

    public static Task<string> ResolveMacAddressAsync(string ipAddress, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!IPAddress.TryParse(ipAddress.Trim(), out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                return string.Empty;
            }

            try
            {
                var bytes = parsed.GetAddressBytes();
                var destIp = BitConverter.ToUInt32(bytes, 0);
                var mac = new byte[6];
                uint len = (uint)mac.Length;
                var result = SendARP(destIp, 0, mac, ref len);
                if (result == 0 && len == 6)
                {
                    return string.Join(':', mac.Select(b => b.ToString("X2")));
                }
            }
            catch
            {
                // Best-effort ARP lookup.
            }

            return string.Empty;
        }, ct);
    }

    public static List<IPAddress> GetActiveLocalIpv4Addresses()
    {
        var ips = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var name = nic.Name + " " + nic.Description;
                if (name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("VMware", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    {
                        ips.Add(ua.Address);
                    }
                }
            }
        }
        catch
        {
            // Fallback to IPAddress.Any
        }

        return ips;
    }

    public static List<(IPAddress LocalIp, IPAddress BroadcastIp)> GetBroadcastEndpoints()
    {
        var list = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null)
                    {
                        continue;
                    }

                    var ipBytes = ua.Address.GetAddressBytes();
                    var maskBytes = ua.IPv4Mask.GetAddressBytes();
                    var broadcastBytes = new byte[4];
                    for (var i = 0; i < 4; i++)
                    {
                        broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                    }

                    list.Add((ua.Address, new IPAddress(broadcastBytes)));
                }
            }
        }
        catch
        {
            // Best effort.
        }

        return list;
    }

    private static async Task ProbeInterfaceAsync(
        IPAddress localIp,
        TimeSpan duration,
        Dictionary<string, string> locationByIp,
        object sync,
        CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(localIp, 0));
            udp.EnableBroadcast = true;
            var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

            foreach (var st in SearchTargets)
            {
                var payload =
                    $"M-SEARCH * HTTP/1.1\r\n" +
                    $"HOST: 239.255.255.250:1900\r\n" +
                    $"MAN: \"ssdp:discover\"\r\n" +
                    $"MX: 2\r\n" +
                    $"ST: {st}\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(payload);
                await udp.SendAsync(bytes, bytes.Length, multicast);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(duration);

            while (!cts.IsCancellationRequested)
            {
                var resp = await udp.ReceiveAsync(cts.Token);
                var text = Encoding.UTF8.GetString(resp.Buffer);
                var isLgHint =
                    text.Contains("webOS", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("lge", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("LG", StringComparison.OrdinalIgnoreCase);

                var match = LocationRegex().Match(text);
                if (!match.Success)
                {
                    continue;
                }

                var location = match.Groups[1].Value.Trim();
                var ip = resp.RemoteEndPoint.Address.ToString();
                if (!IsLocationOnResponder(location, ip))
                {
                    continue;
                }

                lock (sync)
                {
                    // Prefer the webos-second-screen XML if we receive multiple responses from the same IP.
                    if (!locationByIp.ContainsKey(ip) || isLgHint)
                    {
                        locationByIp[ip] = location;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Listen window finished.
        }
        catch
        {
            // Ignore non-routable adapters.
        }
    }

    private static async Task<LgTvDiscoveredDevice?> InspectLocationAsync(
        HttpClient httpClient,
        string ip,
        string locationUrl,
        CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2.5));
            var xml = await httpClient.GetStringAsync(locationUrl, cts.Token);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var device = doc.Descendants(ns + "device").FirstOrDefault();
            if (device is null)
            {
                return null;
            }

            var manufacturer = device.Element(ns + "manufacturer")?.Value?.Trim() ?? string.Empty;
            var friendlyName = device.Element(ns + "friendlyName")?.Value?.Trim() ?? string.Empty;
            var modelName = device.Element(ns + "modelName")?.Value?.Trim() ?? string.Empty;
            var modelDesc = device.Element(ns + "modelDescription")?.Value?.Trim() ?? string.Empty;
            var udn = device.Element(ns + "UDN")?.Value?.Trim() ?? string.Empty;

            var combined = $"{manufacturer} {friendlyName} {modelName} {modelDesc}";
            if (!combined.Contains("LG", StringComparison.OrdinalIgnoreCase) &&
                !combined.Contains("webOS", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var mac = await ResolveMacAddressAsync(ip, ct);
            var cleanName = friendlyName.Replace("[LG] webOS TV ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(cleanName))
            {
                cleanName = string.IsNullOrWhiteSpace(modelName) ? $"LG webOS TV ({ip})" : modelName;
            }

            return new LgTvDiscoveredDevice(cleanName, ip, mac, modelName, udn);
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"^LOCATION:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex LocationRegex();

    /// <summary>True when the SSDP LOCATION is an http(s) address on the device that sent the answer.</summary>
    internal static bool IsLocationOnResponder(string location, string responderIp) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.Equals(uri.Host, responderIp, StringComparison.OrdinalIgnoreCase);
}
