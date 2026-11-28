using System.Net;
using Denon.Plugin.Chromecast.Cast;
using Xunit;

namespace Denon.Plugin.Chromecast.Tests;

public sealed class CastChannelTests
{
    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.20")]
    [InlineData("169.254.10.10")] // A Cast device without DHCP picks a link-local address.
    public void Private_lan_addresses_are_allowed(string address)
    {
        Assert.True(CastChannel.IsPrivateLanAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.251")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:192.168.1.20")] // Only plain IPv4: the TLS socket is IPv4.
    public void Other_addresses_are_refused(string address)
    {
        Assert.False(CastChannel.IsPrivateLanAddress(IPAddress.Parse(address)));
    }
}
