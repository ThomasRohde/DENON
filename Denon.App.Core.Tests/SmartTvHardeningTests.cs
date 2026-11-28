using Denon.Plugin.SmartTv;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The LG TV client trusts only addresses on the LAN: a screenshot or pointer-socket address the TV sends, a
/// self-signed certificate, and an SSDP LOCATION from discovery (#34).
/// </summary>
public sealed class SmartTvHardeningTests
{
    [Theory]
    [InlineData("http://192.168.1.28:3000/resources/abc/capture.jpg", "192.168.1.28", true)]
    [InlineData("https://192.168.1.28:3001/resources/abc/capture.jpg", "192.168.1.28", true)]
    [InlineData("http://10.0.0.7:3000/x.jpg", "192.168.1.28", true)] // Another LAN address of the TV.
    [InlineData("http://lgwebostv:3000/x.jpg", "lgwebostv", true)] // The configured host name.
    [InlineData("http://203.0.113.5/x.jpg", "192.168.1.28", false)] // The internet.
    [InlineData("http://127.0.0.1:18926/health", "192.168.1.28", false)] // This PC (SSRF).
    [InlineData("file:///c:/windows/win.ini", "192.168.1.28", false)]
    [InlineData("not a url", "192.168.1.28", false)]
    public void A_screenshot_address_must_be_the_tv_on_the_lan(string uri, string tvHost, bool allowed) =>
        Assert.Equal(allowed, LgWebOsClient.IsTvLanUri(uri, tvHost, "http", "https"));

    [Theory]
    [InlineData("ws://192.168.1.28:3000/resources/x/netinput.pointer.sock", true)]
    [InlineData("wss://192.168.1.28:3001/resources/x/netinput.pointer.sock", true)]
    [InlineData("http://192.168.1.28:3000/resources/x", false)] // Wrong scheme for a socket.
    [InlineData("wss://evil.example/sock", false)]
    public void The_pointer_socket_must_be_a_websocket_on_the_lan(string uri, bool allowed) =>
        Assert.Equal(allowed, LgWebOsClient.IsTvLanUri(uri, "192.168.1.28", "ws", "wss"));

    [Theory]
    [InlineData("192.168.1.28", true)]
    [InlineData("172.16.4.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("example.com", false)]
    public void Only_lan_addresses_may_use_a_self_signed_certificate(string host, bool lan) =>
        Assert.Equal(lan, LgWebOsClient.IsLanAddress(host));

    [Theory]
    [InlineData("192.168.1.28", 1)]
    [InlineData("127.0.0.1", 0)] // This PC, not the TV.
    [InlineData("8.8.8.8", 0)]
    [InlineData("localhost", 0)] // Resolves to loopback only.
    public async Task Only_lan_addresses_are_connected_to_after_dns(string host, int lanCount) =>
        Assert.Equal(lanCount, (await LgWebOsClient.ResolveLanAddressesAsync(host, CancellationToken.None)).Length);

    [Theory]
    [InlineData("http://192.168.1.28:1914/", "192.168.1.28", true)]
    [InlineData("http://192.168.1.99:1914/", "192.168.1.28", false)] // Points at another device.
    [InlineData("http://127.0.0.1:18925/api/power", "192.168.1.28", false)] // At this PC.
    [InlineData("ftp://192.168.1.28/", "192.168.1.28", false)]
    public void An_ssdp_location_must_be_on_the_device_that_answered(string location, string responder, bool kept) =>
        Assert.Equal(kept, LgTvDiscoveryService.IsLocationOnResponder(location, responder));
}
