using System.Windows.Media;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

public sealed class DashboardThemeTests
{
    [Fact]
    public void Host_theme_is_transparent_and_has_no_styles_without_a_host()
    {
        Assert.Same(Brushes.Transparent, HostTheme.Brush("Ink05"));
        Assert.Null(HostTheme.Style("PrimaryButton"));
    }
}
