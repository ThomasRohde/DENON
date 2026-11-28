using Denon.App.Core.Models;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The sound mode buttons: the real modes, the shortcuts where the receiver chooses (Auto, Movie, Music, Game), and
/// the note that says which mode the receiver picked.
/// </summary>
public sealed class SoundModeCatalogTests
{
    [Fact]
    public void The_real_modes_come_first_and_the_shortcuts_last()
    {
        var codes = DenonCatalog.SoundModes.Select(m => m.Code).ToArray();
        Assert.Equal(["STEREO", "MCH STEREO", "DIRECT", "PURE DIRECT", "AUTO", "MOVIE", "MUSIC", "GAME"], codes);
    }

    [Theory]
    [InlineData("AUTO", true)]
    [InlineData("MOVIE", true)]
    [InlineData("MUSIC", true)]
    [InlineData("GAME", true)]
    [InlineData("STEREO", false)]
    [InlineData("MCH STEREO", false)]
    [InlineData("DIRECT", false)]
    [InlineData("PURE DIRECT", false)]
    public void Only_the_shortcuts_let_the_receiver_choose(string code, bool receiverChooses) =>
        Assert.Equal(receiverChooses, DenonCatalog.SoundModes.Single(m => m.Code == code).ReceiverChooses);

    [Theory]
    [InlineData("STEREO", "Stereo")]
    [InlineData("mch stereo", "Multi-Ch Stereo")]
    [InlineData(" PURE DIRECT ", "Pure Direct")]
    [InlineData("DOLBY SURROUND", "DOLBY SURROUND")] // a mode with no button keeps the receiver's name
    [InlineData("MUSIC", "MUSIC")] // a shortcut is never a mode the receiver reports
    [InlineData("", "an unknown mode")]
    [InlineData(null, "an unknown mode")]
    public void A_reported_mode_gets_a_readable_name(string? reported, string expected) =>
        Assert.Equal(expected, DenonCatalog.DescribeSoundMode(reported));

    [Fact]
    public void The_note_names_the_shortcut_and_the_mode_the_receiver_chose() =>
        Assert.Equal(
            "Music: the receiver chose Stereo for this source.",
            DenonCatalog.ReceiverChoiceNote("Music", "STEREO"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_note_until_the_receiver_reports_a_mode(string? reported) =>
        Assert.Null(DenonCatalog.ReceiverChoiceNote("Auto", reported));
}
