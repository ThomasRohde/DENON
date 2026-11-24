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
    public void Window_limits_fit_a_small_high_scaled_screen()
    {
        // 1366 × 768 at 150 % with the taskbar: about 911 × 512 DIPs, smaller than the window's own minimum.
        var (minWidth, minHeight, maxWidth, maxHeight) = WindowFitter.Limits(new System.Windows.Rect(0, 0, 911, 512));
        Assert.True(minWidth <= 911 && maxWidth <= 911, $"width {minWidth}..{maxWidth}");
        Assert.True(minHeight <= 512 && maxHeight <= 512, $"height {minHeight}..{maxHeight}");
        Assert.True(minWidth <= maxWidth && minHeight <= maxHeight);

        // A large screen keeps the window's own minimum and leaves a margin.
        var large = WindowFitter.Limits(new System.Windows.Rect(0, 0, 2560, 1392));
        Assert.Equal(WindowFitter.MinWindowWidth, large.MinWidth);
        Assert.Equal(WindowFitter.MinWindowHeight, large.MinHeight);
        Assert.True(large.MaxWidth < 2560 && large.MaxHeight < 1392);
    }

    [Fact]
    public void Window_minimums_match_the_main_window()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "DenonDesktop.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        var xaml = File.ReadAllText(Path.Combine(root, "Denon.App.Desktop", "MainWindow.xaml"));
        Assert.Contains($"MinWidth=\"{WindowFitter.MinWindowWidth}\"", xaml, StringComparison.Ordinal);
        Assert.Contains($"MinHeight=\"{WindowFitter.MinWindowHeight}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_is_the_release_number_without_the_commit()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
        Assert.Equal("Version " + AppInfo.Version, AppInfo.VersionLabel);
        Assert.StartsWith(AppInfo.Version + "|", AppInfo.InstallStamp, StringComparison.Ordinal);
    }
}
