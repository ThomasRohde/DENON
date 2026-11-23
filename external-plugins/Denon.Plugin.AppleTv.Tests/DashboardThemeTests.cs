using System.Reflection;
using System.Windows;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

public sealed class DashboardThemeTests
{
    /// <summary>
    /// The dark ComboBox template is a XAML string with theme colors filled in at run time. A broken string falls
    /// back silently to the white default control, so parse it here (no host app: transparent).
    /// </summary>
    [Theory]
    [InlineData("DarkComboStyle")]
    public void Dark_templates_parse(string field)
    {
        Style? style = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var lazy = (Lazy<Style?>)typeof(AppleTvDashboardView)
                    .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!
                    .GetValue(null)!;
                style = lazy.Value;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.NotNull(style);
    }

    [Fact]
    public void Host_theme_is_transparent_without_a_host() =>
        Assert.Equal("#00FFFFFF", HostTheme.Hex("Ink05")); // Colors.Transparent
}
