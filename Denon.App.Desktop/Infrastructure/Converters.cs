using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Denon.App.Core.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Denon.App.Desktop.Infrastructure;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter ?? System.Windows.Data.Binding.DoNothing : System.Windows.Data.Binding.DoNothing;
}

/// <summary>Visible when the value equals the parameter (case-insensitive), else collapsed. Used for page and sub-tab switching.</summary>
public sealed class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static Brush InfoBrush => ThemeResources.Brush("AccentCyanBright");
    private static Brush WarningBrush => ThemeResources.Brush("AccentAmber");
    private static Brush ErrorBrush => ThemeResources.Brush("AccentCrimsonBright");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            AppLogLevel.Warning => WarningBrush,
            AppLogLevel.Error => ErrorBrush,
            _ => InfoBrush,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullableBoolToBrushConverter : IValueConverter
{
    private static Brush SuccessBrush => ThemeResources.Brush("AccentEmerald");
    private static Brush FailureBrush => ThemeResources.Brush("AccentCrimsonBright");
    private static Brush NeutralBrush => ThemeResources.Brush("TextMuted");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            true => SuccessBrush,
            false => FailureBrush,
            _ => NeutralBrush,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SafeImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return ArtworkImageCache.Get(uri);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
