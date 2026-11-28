using Denon.App.Desktop;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>The Cinema window's full-screen HUD: receiver buttons and the volume rule (#79).</summary>
public sealed class CinemaHudTests
{
    [Theory]
    [InlineData(40, -2, 98, 65, 38, false)]
    [InlineData(1, -2, 98, 65, 0, false)] // Vol − stops at 0.
    [InlineData(40, 2, 98, 65, 42, false)]
    [InlineData(64, 2, 98, 65, 65, true)] // Vol + stops at the safe volume.
    [InlineData(65, 2, 98, 65, 65, true)]
    [InlineData(70, 2, 98, 65, 70, true)] // Above the safe volume (set by hand): Vol + does not go up.
    [InlineData(70, -2, 98, 65, 68, false)] // ... but Vol − does go down.
    [InlineData(59, 2, 60, 80, 60, true)] // The receiver's own maximum is lower than the safe volume.
    [InlineData(10, 2, 98, 5, 12, false)] // The safe volume is never below 15, as in Volume Memory.
    [InlineData(14, 2, 98, 5, 15, true)]
    public void Vol_buttons_move_in_receiver_steps_and_keep_the_safe_volume(
        int current, int delta, int max, int safe, int expected, bool capped) =>
        Assert.Equal((expected, capped), CinemaHudVolume.Next(current, delta, max, safe));

    [Fact]
    public void One_press_is_two_receiver_steps() => Assert.Equal(2, CinemaHudVolume.Step);

    [Theory]
    [InlineData(98, 65, "Safe volume 65")]
    [InlineData(60, 80, "Max 60")]
    public void The_note_names_the_limit_that_stopped_vol_up(int max, int safe, string expected) =>
        Assert.Equal(expected, CinemaHudVolume.CapNote(max, safe));

    [Theory]
    [InlineData("HUD_PLAY_PAUSE", CinemaHudCommand.PlayPause)]
    [InlineData("HUD_VOL_DOWN", CinemaHudCommand.VolumeDown)]
    [InlineData("HUD_VOL_UP", CinemaHudCommand.VolumeUp)]
    [InlineData("HUD_MUTE", CinemaHudCommand.Mute)]
    public void The_hud_buttons_messages_are_commands(string message, CinemaHudCommand expected)
    {
        Assert.True(YouTubeCinemaWindow.TryParseHudCommand(message, out var command));
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData("SHOW_BARS")]
    [InlineData("VIDEO_ENDED")]
    [InlineData("hud_mute")]
    [InlineData(null)]
    public void Other_messages_are_not_hud_commands(string? message) =>
        Assert.False(YouTubeCinemaWindow.TryParseHudCommand(message, out _));
}
