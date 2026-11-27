using System.Windows;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace Denon.App.Desktop.Controls;

/// <summary>
/// A grid of cards that wraps: as many columns as fit at <see cref="MinItemWidth"/>, all columns the same width,
/// all rows the same height, and the same <see cref="Spacing"/> between columns and between rows. A card is never
/// wider than <see cref="MaxItemWidth"/>, so a few cards do not stretch across a wide window. Used for lists that
/// grow (the plugin list): 3 cards or 30 keep one tidy grid.
/// </summary>
public sealed class EvenWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(EvenWrapPanel),
        new FrameworkPropertyMetadata(240.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxItemWidthProperty = DependencyProperty.Register(
        nameof(MaxItemWidth), typeof(double), typeof(EvenWrapPanel),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(EvenWrapPanel),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The narrowest a card may be. The number of columns follows from it.</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>The widest a card may be.</summary>
    public double MaxItemWidth
    {
        get => (double)GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    /// <summary>The gap between two cards, across and down.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Columns and card width for a number of cards in an available width.</summary>
    internal static (int Columns, double ItemWidth) Fit(double availableWidth, int count, double minItemWidth, double maxItemWidth, double spacing)
    {
        if (count <= 0)
        {
            return (0, 0);
        }

        minItemWidth = Math.Max(1, minItemWidth);
        spacing = Math.Max(0, spacing);
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
        {
            // No limit (inside a horizontal scroll): one row at the smallest width.
            return (count, minItemWidth);
        }

        var columns = Math.Clamp((int)Math.Floor((availableWidth + spacing) / (minItemWidth + spacing)), 1, count);
        var itemWidth = (availableWidth - ((columns - 1) * spacing)) / columns;
        return (columns, Math.Max(0, Math.Min(itemWidth, Math.Max(minItemWidth, maxItemWidth))));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        var (columns, itemWidth) = Fit(availableSize.Width, count, MinItemWidth, MaxItemWidth, Spacing);
        if (columns == 0)
        {
            return default;
        }

        var rowHeight = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        var rows = (int)Math.Ceiling(count / (double)columns);
        return new Size((columns * itemWidth) + ((columns - 1) * Spacing), (rows * rowHeight) + ((rows - 1) * Spacing));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = InternalChildren.Count;
        var (columns, itemWidth) = Fit(finalSize.Width, count, MinItemWidth, MaxItemWidth, Spacing);
        if (columns == 0)
        {
            return finalSize;
        }

        var rowHeight = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        for (var i = 0; i < count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            InternalChildren[i].Arrange(new Rect(column * (itemWidth + Spacing), row * (rowHeight + Spacing), itemWidth, rowHeight));
        }

        return finalSize;
    }
}
