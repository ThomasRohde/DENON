using Denon.App.Desktop;
using Denon.App.Desktop.Infrastructure;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>The first-start experience: when the splash screen shows, and the version the status bar shows.</summary>
public sealed class FirstStartTests
{
    [Fact]
    public void Splash_shows_once_per_install()
    {
        Assert.Equal(SplashKind.FirstStart, SplashWindow.KindFor(null, firstStartOnThisPc: true));
        Assert.Null(SplashWindow.KindFor(AppInfo.InstallStamp, firstStartOnThisPc: false));
    }

    [Fact]
    public void A_new_version_says_updated_and_the_same_version_again_says_welcome_back()
    {
        Assert.Equal(SplashKind.Updated, SplashWindow.KindFor("1.0.0|1", firstStartOnThisPc: false));
        Assert.Equal(SplashKind.Updated, SplashWindow.KindFor(null, firstStartOnThisPc: false));
        Assert.Equal(SplashKind.Reinstalled, SplashWindow.KindFor($"{AppInfo.Version}|1", firstStartOnThisPc: false));
    }

    [Fact]
    public void Version_is_the_release_number_without_the_commit()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
        Assert.Equal("Version " + AppInfo.Version, AppInfo.VersionLabel);
        Assert.StartsWith(AppInfo.Version + "|", AppInfo.InstallStamp, StringComparison.Ordinal);
    }
}
