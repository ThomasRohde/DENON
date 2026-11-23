using System.Windows;

namespace Denon.App.Desktop.Infrastructure;

/// <summary>
/// Selected state for chip buttons (the selected input, sound mode, filter, sub-tab, …). Set
/// <c>infra:Chip.IsActive</c> with a binding; the theme styles <c>ChipButton</c> (cyan) and
/// <c>SuccessChipButton</c> (green, for an "on" state) light the chip while it is true.
/// </summary>
public static class Chip
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(Chip), new FrameworkPropertyMetadata(false));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}
