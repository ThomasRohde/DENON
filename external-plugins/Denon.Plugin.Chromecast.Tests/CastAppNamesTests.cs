using Denon.Plugin.Chromecast.Cast;
using Xunit;

namespace Denon.Plugin.Chromecast.Tests;

public sealed class CastAppNamesTests
{
    [Theory]
    [InlineData("233637DE", "YouTube")]
    [InlineData("233637de", "YouTube")] // App ids compare without case.
    [InlineData("CC1AD845", "Default Media Receiver")]
    [InlineData("E8C28D3C", "Backdrop")]
    [InlineData("A0181CF1", "DR TV")]
    public void A_known_app_id_gives_its_name(string appId, string expected)
    {
        Assert.Equal(expected, CastAppNames.Friendly(appId, "whatever the device says"));
    }

    [Theory]
    [InlineData("AXIS 10 PROD - www.drtv-platform.dr.dk", "DR TV")]
    [InlineData("TV2PLAY receiver", "TV 2 Play")]
    [InlineData("HBO Max", "Max")]
    public void A_technical_display_name_gets_a_clean_name(string displayName, string expected)
    {
        Assert.Equal(expected, CastAppNames.Friendly("0000ABCD", displayName));
    }

    [Fact]
    public void An_unknown_app_keeps_its_own_name()
    {
        Assert.Equal("Some Radio", CastAppNames.Friendly("0000ABCD", "Some Radio"));
    }

    [Fact]
    public void No_id_and_no_name_gives_null()
    {
        Assert.Null(CastAppNames.Friendly(null, null));
        Assert.Equal("  ", CastAppNames.Friendly(null, "  "));
    }
}
