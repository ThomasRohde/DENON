using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using Application = System.Windows.Application;

namespace Denon.App.Desktop.Infrastructure.Theming;

/// <summary>
/// The spacing and size scales of the current skin (<c>SpaceScale</c> and <c>SizeScale</c> in
/// <c>Themes/Skins/&lt;Id&gt;.xaml</c>). Views write spacing and sizes in Default-skin pixels with
/// <see cref="SpaceExtension"/> and <see cref="SizeExtension"/>; each is a live binding to this object, so a skin
/// switch moves every margin, padding and size at once (docs/theming.md).
/// <para>The instance is also in the application resources as <c>SkinMetrics</c>, so plugins can bind to it by
/// name without a reference to the app (no Plugin SDK change).</para>
/// </summary>
public sealed class SkinMetrics : INotifyPropertyChanged
{
    public const string ResourceKey = "SkinMetrics";

    private SkinMetrics()
    {
    }

    public static SkinMetrics Current { get; } = new();

    /// <summary>Multiplies margins and padding (1 in the Default skin).</summary>
    public double SpaceScale { get; private set; } = 1;

    /// <summary>Multiplies widths and heights of controls, icons and columns (1 in the Default skin).</summary>
    public double SizeScale { get; private set; } = 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A Default-skin value on a scale, rounded to half pixels (the Default scale 1 keeps it exact).</summary>
    public static double Scale(double value, double scale) =>
        scale == 1 || double.IsNaN(value) || double.IsInfinity(value) ? value : Math.Round(value * scale * 2, MidpointRounding.AwayFromZero) / 2;

    /// <summary>Reads the scales of the skin now in the application resources.</summary>
    internal void Reload()
    {
        var resources = Application.Current?.Resources;
        Set(resources?["SpaceScale"] is double space ? space : 1, resources?["SizeScale"] is double size ? size : 1);
    }

    internal void Set(double spaceScale, double sizeScale)
    {
        if (spaceScale != SpaceScale)
        {
            SpaceScale = spaceScale;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpaceScale)));
        }

        if (sizeScale != SizeScale)
        {
            SizeScale = sizeScale;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeScale)));
        }
    }
}

/// <summary>
/// Spacing in Default-skin pixels, scaled live by the skin: <c>Margin="{skin:Space 0,0,0,12}"</c>. Takes one, two or
/// four values, like a <see cref="Thickness"/>. Works on properties and in style setters and templates.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class SpaceExtension : MarkupExtension
{
    private readonly double[] _values;

    public SpaceExtension(double uniform) => _values = [uniform];

    public SpaceExtension(double horizontal, double vertical) => _values = [horizontal, vertical];

    public SpaceExtension(double left, double top, double right, double bottom) => _values = [left, top, right, bottom];

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        SkinBinding.Create(nameof(SkinMetrics.SpaceScale), _values).ProvideValue(serviceProvider);
}

/// <summary>
/// A width or height in Default-skin pixels, scaled live by the skin: <c>Width="{skin:Size 32}"</c>. Works for
/// <see cref="double"/> and <see cref="GridLength"/> (column and row definitions).
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class SizeExtension : MarkupExtension
{
    private readonly double _value;

    public SizeExtension(double value) => _value = value;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        SkinBinding.Create(nameof(SkinMetrics.SizeScale), [_value]).ProvideValue(serviceProvider);
}

/// <summary>Bindings to the skin scales for code: <c>SkinBinding.Space(element, MarginProperty, 0, 8)</c>.</summary>
public static class SkinBinding
{
    public static void Space(DependencyObject target, DependencyProperty property, params double[] values) =>
        BindingOperations.SetBinding(target, property, Create(nameof(SkinMetrics.SpaceScale), values));

    public static void Size(DependencyObject target, DependencyProperty property, double value) =>
        BindingOperations.SetBinding(target, property, Create(nameof(SkinMetrics.SizeScale), [value]));

    internal static System.Windows.Data.Binding Create(string scale, double[] values) => new(scale)
    {
        Source = SkinMetrics.Current,
        Mode = BindingMode.OneWay,
        Converter = new ScaledValueConverter(values),
    };

    private sealed class ScaledValueConverter(double[] values) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var scale = value is double s ? s : 1;
            double At(int i) => SkinMetrics.Scale(values[i], scale);

            if (targetType == typeof(Thickness))
            {
                return values.Length switch
                {
                    1 => new Thickness(At(0)),
                    2 => new Thickness(At(0), At(1), At(0), At(1)),
                    _ => new Thickness(At(0), At(1), At(2), At(3)),
                };
            }

            if (targetType == typeof(GridLength))
            {
                return new GridLength(At(0));
            }

            return At(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
