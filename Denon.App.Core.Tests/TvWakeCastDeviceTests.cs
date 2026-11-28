using Denon.Plugin.SmartTv;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The TV's HDMI-CEC wake uses the Chromecast on this TV: the one chosen on the Smart TV page, else the only one
/// known, else the only one online. With several online it picks none, since another one may sit on another TV.
/// </summary>
public sealed class TvWakeCastDeviceTests
{
    private const string TwoDevices =
        """[{"id":"a1","name":"Living room","host":"10.0.0.20","enabled":true,"connection":"Connected"},""" +
        """{"id":"b2","name":"Bedroom","host":"10.0.0.19","enabled":true,"connection":"Disconnected"},""" +
        """{"id":"c3","name":"Old","host":"10.0.0.18","enabled":false,"connection":"Connected"}]""";

    [Fact]
    public void The_device_list_leaves_out_disabled_devices_and_reads_the_connection()
    {
        var devices = SmartTvPlugin.ReadCastDevices(TwoDevices);

        Assert.Equal(["a1", "b2"], devices.Select(d => d.Id));
        Assert.True(devices[0].IsConnected);
        Assert.False(devices[1].IsConnected);
        Assert.Empty(SmartTvPlugin.ReadCastDevices("not json"));
        Assert.Empty(SmartTvPlugin.ReadCastDevices(null));
    }

    [Fact]
    public void Automatic_uses_the_only_device_or_the_only_one_online()
    {
        var devices = SmartTvPlugin.ReadCastDevices(TwoDevices);

        Assert.Equal("a1", SmartTvPlugin.PickCecCastDevice(null, devices).Device);
        Assert.Equal("b2", SmartTvPlugin.PickCecCastDevice("", [devices[1]]).Device);
    }

    [Fact]
    public void A_chosen_device_wins_even_when_another_one_is_online()
    {
        var devices = SmartTvPlugin.ReadCastDevices(TwoDevices);

        Assert.Equal("b2", SmartTvPlugin.PickCecCastDevice(" b2 ", devices).Device);
    }

    [Fact]
    public void Several_online_devices_give_no_pick_and_say_where_to_choose()
    {
        CastDeviceChoice[] devices = [new("a1", "Living room", true), new("b2", "Bedroom", true)];

        var (device, reason) = SmartTvPlugin.PickCecCastDevice(null, devices);

        Assert.Null(device);
        Assert.Contains("Living room, Bedroom", reason);
        Assert.Contains("Smart TV page", reason);
    }

    [Fact]
    public void No_plugin_or_no_device_gives_a_reason()
    {
        Assert.Null(SmartTvPlugin.PickCecCastDevice(null, null).Device);
        Assert.Null(SmartTvPlugin.PickCecCastDevice(null, []).Device);
        var (device, reason) = SmartTvPlugin.PickCecCastDevice(
            null, [new("a1", "Living room", false), new("b2", "Bedroom", false)]);
        Assert.Null(device);
        Assert.Contains("no Chromecast is online", reason);
    }
}
