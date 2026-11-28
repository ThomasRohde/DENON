using System.Net;
using Denon.Plugin.AppleTv.Discovery;
using Denon.Plugin.AppleTv.Protocol.Mrp;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

/// <summary>Merging discovered Apple TVs into the saved ones. Made-up ids and addresses.</summary>
public sealed class DiscoveryMergeTests
{
    private const string TvId = "AA:BB:CC:DD:EE:01";

    private static AppleDevice AppleTv(string ip, string? deviceId, bool withAirPlay, int companionPort = 49153)
    {
        var address = IPAddress.Parse(ip);
        var companion = new MdnsService(AppleServiceTypes.Companion, $"Stue.{AppleServiceTypes.Companion}", null, companionPort,
            new Dictionary<string, string> { ["rpMd"] = "AppleTV11,1" }, address);
        List<MdnsService> services = [companion];
        if (withAirPlay)
        {
            services.Add(new MdnsService(AppleServiceTypes.AirPlay, $"Stue.{AppleServiceTypes.AirPlay}", null, 7000,
                new Dictionary<string, string> { ["model"] = "AppleTV11,1", ["deviceid"] = deviceId ?? string.Empty }, address));
        }

        return new AppleDevice("Stue", address, "AppleTV11,1", "Apple TV 4K (2nd generation)", AppleDeviceKind.AppleTv, "26.1", deviceId, services);
    }

    [Fact]
    public void A_reply_with_only_companion_does_not_add_a_second_apple_tv()
    {
        var saved = new AppleTvDeviceConfig { Id = TvId, Name = "Stue", Host = "192.168.50.2", AirPlayPort = 7001 };
        List<AppleTvDeviceConfig> devices = [saved];
        var restart = new List<AppleTvDeviceConfig>();
        var skipped = new List<AppleDevice>();

        var added = AppleTvPlugin.MergeDiscovered(devices, [AppleTv("192.168.50.2", null, withAirPlay: false)], restart, skipped);

        Assert.Equal(0, added);
        Assert.Same(saved, Assert.Single(devices));
        Assert.Equal(7001, saved.AirPlayPort); // no AirPlay record: the saved port stays
        Assert.Empty(restart);
        Assert.Empty(skipped);
    }

    [Fact]
    public void A_new_apple_tv_is_added_only_with_its_device_id()
    {
        var devices = new List<AppleTvDeviceConfig>();
        var skipped = new List<AppleDevice>();

        var added = AppleTvPlugin.MergeDiscovered(devices, [AppleTv("192.168.50.3", null, withAirPlay: false)], [], skipped);
        Assert.Equal(0, added);
        Assert.Empty(devices);
        Assert.Single(skipped);

        added = AppleTvPlugin.MergeDiscovered(devices, [AppleTv("192.168.50.3", TvId, withAirPlay: true)], [], []);
        Assert.Equal(1, added);
        Assert.Equal(TvId, Assert.Single(devices).Id);
    }

    [Fact]
    public void A_partial_reply_and_a_full_reply_in_one_pass_are_one_apple_tv()
    {
        var devices = new List<AppleTvDeviceConfig>();

        var added = AppleTvPlugin.MergeDiscovered(
            devices, [AppleTv("192.168.50.3", null, withAirPlay: false), AppleTv("192.168.50.3", TvId, withAirPlay: true)], [], []);

        Assert.Equal(1, added);
        Assert.Single(devices);
    }

    [Fact]
    public void A_moved_paired_apple_tv_restarts()
    {
        var saved = new AppleTvDeviceConfig
        {
            Id = TvId, Host = "192.168.50.2", Credentials = "x", Identity = Protocol.Companion.CompanionIdentity.Create("test"),
        };
        var restart = new List<AppleTvDeviceConfig>();

        AppleTvPlugin.MergeDiscovered([saved], [AppleTv("192.168.50.9", TvId.ToLowerInvariant(), withAirPlay: true)], restart, []);

        Assert.Same(saved, Assert.Single(restart));
        Assert.Equal("192.168.50.9", saved.Host);
    }

    [Fact]
    public async Task Artwork_is_saved_whole_and_found_again()
    {
        var directory = Path.Combine(Path.GetTempPath(), "denon-atv-artwork-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(AppleTvDeviceClient.FindCachedArtwork(directory, "abc"));

            var artwork = new MrpArtwork([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], ".jpg");
            var path = await AppleTvDeviceClient.SaveArtworkAsync(directory, "abc", artwork, CancellationToken.None);

            Assert.Equal(artwork.Data, File.ReadAllBytes(path));
            Assert.Equal(path, AppleTvDeviceClient.FindCachedArtwork(directory, "abc"));
            Assert.Equal([path], Directory.GetFiles(directory)); // no temporary file is left behind
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
