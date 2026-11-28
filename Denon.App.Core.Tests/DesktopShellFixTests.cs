using Denon.App.Core.Services;
using Denon.App.Desktop.Services;
using Denon.App.Desktop.ViewModels;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The shortcut editor refuses keys the main window takes first and lets custom rows be removed (#54); the phone
/// shows volume as the receiver's 0-98 number.
/// </summary>
public sealed class DesktopShellFixTests
{
    [Theory]
    [InlineData("Ctrl+8")]
    [InlineData("Ctrl+9")]
    [InlineData("F1")]
    [InlineData("Ctrl+K")] // The command palette.
    [InlineData("Ctrl+1")]
    public void Keys_the_main_window_takes_first_are_refused_for_in_app_shortcuts(string gesture) =>
        Assert.Contains("reserved", GlobalHotkeyService.GetReservedWarning(gesture, isGlobal: false));

    [Fact]
    public void A_free_key_is_accepted()
    {
        Assert.Null(GlobalHotkeyService.GetReservedWarning("Ctrl+Shift+8", isGlobal: false));
    }

    [Theory]
    [InlineData("scene:movie", true)]
    [InlineData("plugin:philips-hue/hue.all_off", true)]
    [InlineData("volume_up", false)]
    public void Only_scene_and_plugin_rows_can_be_removed(string actionId, bool custom) =>
        Assert.Equal(custom, new ShortcutItemEditor(actionId, "Label", isGlobal: true, "Ctrl+Alt+M").IsCustom);

    [Fact]
    public void The_phone_shows_volume_as_the_receivers_number_not_db()
    {
        var html = RemoteWebUiHtml.PageHtml;
        Assert.DoesNotContain("fmtDb", html);
        Assert.DoesNotContain(" dB", html);
        Assert.Contains("Volume ${v} of", html);
    }
}
