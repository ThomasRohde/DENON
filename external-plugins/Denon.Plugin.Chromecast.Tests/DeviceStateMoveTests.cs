using System.Collections.Concurrent;
using Xunit;

namespace Denon.Plugin.Chromecast.Tests;

/// <summary>Per-device state follows a device to its new id when discovery re-keys it. Made-up ids.</summary>
public sealed class DeviceStateMoveTests
{
    [Fact]
    public void A_value_moves_to_the_new_id()
    {
        var map = new ConcurrentDictionary<string, string>(StringComparer.Ordinal) { ["ip-192.168.1.20"] = "session-1", ["other"] = "session-2" };

        ChromecastPlugin.MoveKey(map, "ip-192.168.1.20", "cast-abc");

        Assert.False(map.ContainsKey("ip-192.168.1.20"));
        Assert.Equal("session-1", map["cast-abc"]);
        Assert.Equal("session-2", map["other"]);
    }

    [Fact]
    public void Nothing_moves_when_the_old_id_has_no_value()
    {
        var map = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        ChromecastPlugin.MoveKey(map, "ip-192.168.1.20", "cast-abc");

        Assert.Empty(map);
    }

    [Fact]
    public void Session_keys_of_the_old_id_get_the_new_id()
    {
        var map = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            ["ip-192.168.1.20:s1"] = 0,
            ["ip-192.168.1.20:s2"] = 0,
            ["ip-192.168.1.200:s3"] = 0, // another device whose id starts the same
            ["cast-xyz:s4"] = 0,
        };

        ChromecastPlugin.MoveSessionKeys(map, "ip-192.168.1.20", "cast-abc");

        Assert.Equal(["cast-abc:s1", "cast-abc:s2", "cast-xyz:s4", "ip-192.168.1.200:s3"], map.Keys.Order(StringComparer.Ordinal));
    }
}
