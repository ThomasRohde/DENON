using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Denon.Plugin.PhilipsHue;

/// <summary>
/// Spacing and sizes from the Denon Desktop skin (docs/theming.md in the host), live, with no Plugin SDK change:
/// the host puts its skin scales in the application resources as <c>SkinMetrics</c> (an object with
/// <c>SpaceScale</c> and <c>SizeScale</c> that raises <c>PropertyChanged</c>), and these bind to it by name.
/// Write values in Default-skin pixels. Without a host (unit tests) the scale is 1.
/// </summary>
internal static class HostSkin
{
    /// <summary>Binds a <see cref="Thickness"/> property (margin, padding) to the skin's spacing scale.</summary>
    public static void Space(DependencyObject target, DependencyProperty property, params double[] values) =>
        BindingOperations.SetBinding(target, property, Binding("SpaceScale", values));

    /// <summary>Binds a width or height (double or GridLength) to the skin's size scale.</summary>
    public static void Size(DependencyObject target, DependencyProperty property, double value) =>
        BindingOperations.SetBinding(target, property, Binding("SizeScale", [value]));

    /// <summary>
    /// Binds a property to a host token live (<c>FontSize06</c>, <c>Radius04</c>, <c>MonoFont</c>) and returns the
    /// element, for use after an object initializer: <c>new TextBlock { … }.Token(TextElement.FontSizeProperty, "FontSize10")</c>.
    /// </summary>
    public static T Token<T>(this T element, DependencyProperty property, string key)
        where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    /// <summary>
    /// For pages built in code: turns the literal margins, padding and sizes of an element tree (written in
    /// Default-skin pixels) into live skin bindings, and returns the root. Call it on each tree after building it;
    /// values that are already bound and sizes up to 2 px (hairlines) stay as they are.
    /// </summary>
    public static T Follow<T>(T root)
        where T : DependencyObject
    {
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            foreach (var property in SpaceProperties)
            {
                if (node.ReadLocalValue(property) is Thickness t && (t.Left != 0 || t.Top != 0 || t.Right != 0 || t.Bottom != 0))
                {
                    BindingOperations.SetBinding(node, property, Binding("SpaceScale", [t.Left, t.Top, t.Right, t.Bottom]));
                }
            }

            foreach (var property in SizeProperties)
            {
                if (node.ReadLocalValue(property) is double d && d > 2 && !double.IsInfinity(d))
                {
                    BindingOperations.SetBinding(node, property, Binding("SizeScale", [d]));
                }
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                pending.Push(child);
            }
        }

        return root;
    }

    private static readonly DependencyProperty[] SpaceProperties =
    [
        FrameworkElement.MarginProperty, System.Windows.Controls.Control.PaddingProperty,
        System.Windows.Controls.Border.PaddingProperty, System.Windows.Controls.TextBlock.PaddingProperty,
    ];

    private static readonly DependencyProperty[] SizeProperties =
    [
        FrameworkElement.WidthProperty, FrameworkElement.HeightProperty, FrameworkElement.MinWidthProperty,
        FrameworkElement.MinHeightProperty, FrameworkElement.MaxWidthProperty, FrameworkElement.MaxHeightProperty,
    ];

    internal static System.Windows.Data.Binding Binding(string scale, double[] values) => new(scale)
    {
        Source = Application.Current?.TryFindResource("SkinMetrics") ?? NoHost.Instance,
        Mode = BindingMode.OneWay,
        Converter = new ScaledValueConverter(values),
    };

    private static double Scale(double value, double scale) =>
        scale == 1 || double.IsNaN(value) || double.IsInfinity(value) ? value : Math.Round(value * scale * 2, MidpointRounding.AwayFromZero) / 2;

    private sealed class NoHost : INotifyPropertyChanged
    {
        public static readonly NoHost Instance = new();

        public double SpaceScale => 1;

        public double SizeScale => 1;

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private sealed class ScaledValueConverter(double[] values) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var scale = value is double s ? s : 1;
            double At(int i) => Scale(values[i], scale);

            if (targetType == typeof(Thickness))
            {
                return values.Length switch
                {
                    1 => new Thickness(At(0)),
                    2 => new Thickness(At(0), At(1), At(0), At(1)),
                    _ => new Thickness(At(0), At(1), At(2), At(3)),
                };
            }

            return targetType == typeof(GridLength) ? new GridLength(At(0)) : At(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

/// <summary>Spacing in Default-skin pixels, scaled live by the host skin: <c>Margin="{skin:Space 0,0,0,12}"</c>.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class SpaceExtension : MarkupExtension
{
    private readonly double[] _values;

    public SpaceExtension(double uniform) => _values = [uniform];

    public SpaceExtension(double horizontal, double vertical) => _values = [horizontal, vertical];

    public SpaceExtension(double left, double top, double right, double bottom) => _values = [left, top, right, bottom];

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        HostSkin.Binding("SpaceScale", _values).ProvideValue(serviceProvider);
}

/// <summary>A width or height in Default-skin pixels, scaled live by the host skin: <c>Width="{skin:Size 32}"</c>.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class SizeExtension : MarkupExtension
{
    private readonly double _value;

    public SizeExtension(double value) => _value = value;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        HostSkin.Binding("SizeScale", [_value]).ProvideValue(serviceProvider);
}
