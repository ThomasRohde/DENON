using Denon.Plugin.SmartTv;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>The Smart TV page opens on the tab used last; an unknown or empty value opens Watch.</summary>
public sealed class TvPageTabTests
{
    [Theory]
    [InlineData("remote", "remote")]
    [InlineData(" Apps ", "apps")]
    [InlineData("SETTINGS", "settings")]
    [InlineData("watch", "watch")]
    [InlineData("", "watch")]
    [InlineData(null, "watch")]
    [InlineData("old-tab", "watch")]
    public void A_saved_tab_is_read_back_or_falls_back_to_watch(string? saved, string expected) =>
        Assert.Equal(expected, SmartTvConfig.NormalizeTab(saved));

    [Fact]
    public void A_new_config_opens_on_watch_with_the_remote_buttons()
    {
        Assert.Equal(SmartTvConfig.WatchTab, new SmartTvConfig().DashboardTab);
        Assert.Equal(SmartTvConfig.ButtonsPanel, new SmartTvConfig().RemotePanel);
    }

    [Theory]
    [InlineData("trackpad", "trackpad")]
    [InlineData(" Trackpad ", "trackpad")]
    [InlineData("buttons", "buttons")]
    [InlineData("", "buttons")]
    [InlineData(null, "buttons")]
    public void The_remote_panel_is_read_back_or_falls_back_to_the_buttons(string? saved, string expected) =>
        Assert.Equal(expected, SmartTvConfig.NormalizeRemotePanel(saved));
}
