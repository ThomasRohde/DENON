using Denon.Plugin.Chromecast.Cast;
using Xunit;

namespace Denon.Plugin.Chromecast.Tests;

public sealed class DiscoveryMergeTests
{
    [Fact]
    public void A_manual_device_found_by_mdns_gets_the_mdns_id_and_restarts_under_its_old_id()
    {
        var manual = new CastDeviceConfig
        {
            Id = "ip-192.168.1.20", Name = "Cast 192.168.1.20", Host = "192.168.1.20", Source = "manual",
        };
        var devices = new List<CastDeviceConfig> { manual };
        var restart = new List<ChromecastPlugin.DiscoveryRestart>();

        var added = ChromecastPlugin.MergeDiscovered(
            devices, [new DiscoveredCastDevice("a1b2c3", "Stuen 2", "Chromecast", "192.168.1.20", 8009)], restart);

        Assert.Equal(0, added);
        var device = Assert.Single(devices);
        Assert.Same(manual, device);
        Assert.Equal("a1b2c3", device.Id);
        Assert.Equal("Stuen 2", device.Name);
        Assert.Equal("mdns", device.Source);

        // Same host and port, but a new id: the client must stop under "ip-…" and start under "a1b2c3".
        var change = Assert.Single(restart);
        Assert.Same(manual, change.Config);
        Assert.Equal("ip-192.168.1.20", change.OldId);
    }

    [Fact]
    public void A_known_device_on_the_same_address_only_gets_the_new_details()
    {
        var devices = new List<CastDeviceConfig>
        {
            new() { Id = "a1b2c3", Name = "Old name", Host = "192.168.1.20", Port = 8009 },
        };
        var restart = new List<ChromecastPlugin.DiscoveryRestart>();

        var added = ChromecastPlugin.MergeDiscovered(
            devices, [new DiscoveredCastDevice("a1b2c3", "Stuen 2", "Chromecast", "192.168.1.20", 8009)], restart);

        Assert.Equal(0, added);
        Assert.Equal("Stuen 2", devices[0].Name);
        Assert.Empty(restart);
    }

    [Fact]
    public void An_id_that_only_changes_case_restarts_so_every_map_uses_the_new_spelling()
    {
        var devices = new List<CastDeviceConfig> { new() { Id = "a1b2c3", Host = "192.168.1.20", Port = 8009 } };
        var restart = new List<ChromecastPlugin.DiscoveryRestart>();

        ChromecastPlugin.MergeDiscovered(
            devices, [new DiscoveredCastDevice("A1B2C3", "Stuen 2", "Chromecast", "192.168.1.20", 8009)], restart);

        Assert.Single(devices);
        Assert.Equal("A1B2C3", devices[0].Id);
        Assert.Equal("a1b2c3", Assert.Single(restart).OldId);
    }

    [Fact]
    public void A_device_that_moved_restarts_under_the_same_id()
    {
        var devices = new List<CastDeviceConfig> { new() { Id = "a1b2c3", Host = "192.168.1.20", Port = 8009 } };
        var restart = new List<ChromecastPlugin.DiscoveryRestart>();

        ChromecastPlugin.MergeDiscovered(
            devices, [new DiscoveredCastDevice("a1b2c3", "Stuen 2", "Chromecast", "192.168.1.44", 8009)], restart);

        var change = Assert.Single(restart);
        Assert.Equal("a1b2c3", change.OldId);
        Assert.Equal("192.168.1.44", change.Config.Host);
    }

    [Fact]
    public void A_new_device_is_added_and_started()
    {
        var devices = new List<CastDeviceConfig>();
        var restart = new List<ChromecastPlugin.DiscoveryRestart>();

        var added = ChromecastPlugin.MergeDiscovered(
            devices, [new DiscoveredCastDevice("a1b2c3", "Stuen 2", "Chromecast", "192.168.1.20", 8009)], restart);

        Assert.Equal(1, added);
        var device = Assert.Single(devices);
        Assert.Equal("mdns", device.Source);
        Assert.Equal("a1b2c3", Assert.Single(restart).OldId);
    }

    [Fact]
    public void Only_a_manual_device_is_matched_by_host()
    {
        var devices = new List<CastDeviceConfig> { new() { Id = "old-id", Host = "192.168.1.20", Source = "mdns" } };
        var restart = new List<ChromecastPlugin.DiscoveryRestart>();

        var added = ChromecastPlugin.MergeDiscovered(
            devices, [new DiscoveredCastDevice("new-id", "Other", "Chromecast", "192.168.1.20", 8009)], restart);

        Assert.Equal(1, added);
        Assert.Equal(["old-id", "new-id"], devices.Select(d => d.Id));
    }
}
