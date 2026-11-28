using Denon.App.Desktop;
using Denon.App.Desktop.Infrastructure;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// Cinema opens only web pages (#29 defense in depth), the artwork cache refuses network shares (#29), and new
/// scene and station ids never collide (#71).
/// </summary>
public sealed class CinemaAndDesktopFixTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://accounts.google.com/signin", true)] // Sign-in hops stay allowed.
    [InlineData("about:blank", true)]
    [InlineData("file://attacker/share/x.html", false)]
    [InlineData(@"\\attacker\share\run.exe", false)]
    [InlineData("search-ms:query=x", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(null, false)]
    public void Cinema_opens_only_web_pages(string? uri, bool allowed) =>
        Assert.Equal(allowed, YouTubeCinemaWindow.IsWebAddress(uri));

    [Theory]
    [InlineData("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", true)]
    [InlineData("file:///C:/Users/me/AppData/Local/DenonDesktop/logos/dr1.png", true)] // A TV channel logo on this PC.
    [InlineData("file://attacker/share/a.png", false)] // A network share leaks the login hash.
    [InlineData(@"\\attacker\share\a.png", false)]
    [InlineData("ms-appx:///a.png", false)]
    public void Artwork_loads_from_the_web_or_this_pc_only(string uri, bool allowed) =>
        Assert.Equal(allowed, ArtworkImageCache.IsAllowedSource(new Uri(uri)));

    [Fact]
    public void A_new_id_never_repeats_an_existing_one()
    {
        // After deleting scene-2 of three, "scene-3" (count + 1) already exists.
        Assert.Equal("scene-3-2", UniqueId.From("scene-3", ["scene-1", "scene-3"]));
        Assert.Equal("movie-copy-3", UniqueId.From("movie-copy", ["movie", "movie-copy", "Movie-Copy-2"]));
        Assert.Equal("station-4", UniqueId.From("station-4", ["station-1", null, ""]));
    }
}
