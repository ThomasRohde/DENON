using System.Net;
using System.Net.Sockets;

namespace Denon.App.Core.Services;

/// <summary>
/// The LAN-only rule (Settings → Security): which remote addresses the Phone Remote server (:18925) and the MCP server
/// (:18926) still answer when it is on.
/// </summary>
public static class LanAddressGuard
{
    /// <summary>
    /// This PC or the local network: loopback (127/8, ::1), private IPv4 (10/8, 172.16/12, 192.168/16), link-local
    /// (169.254/16, fe80::/10) and IPv6 unique-local (fc00::/7). An IPv4-mapped IPv6 address counts as its IPv4
    /// address. Anything else, and no address at all, is outside.
    /// </summary>
    public static bool IsLocalNetwork(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 127 ||
                   b[0] == 10 ||
                   (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IPAddress.IPv6Loopback.Equals(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        }

        return false;
    }

    /// <summary>True when the rule is on and <paramref name="address"/> is outside the local network.</summary>
    public static bool Refuses(bool lanOnly, IPAddress? address) => lanOnly && !IsLocalNetwork(address);

    /// <summary>The reply text for a refused request.</summary>
    public const string RefusalMessage = "Only devices on the local network may connect (Settings: LAN only).";
}
