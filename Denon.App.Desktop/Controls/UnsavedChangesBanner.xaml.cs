using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace Denon.App.Desktop.Controls;

/// <summary>The unsaved-changes banner of the Settings, Scenes and Stations pages (#76).</summary>
public partial class UnsavedChangesBanner : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(UnsavedChangesBanner),
        new PropertyMetadata(string.Empty, OnMessageChanged));

    public static readonly DependencyProperty AreaProperty = DependencyProperty.Register(
        nameof(Area),
        typeof(string),
        typeof(UnsavedChangesBanner),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SaveLabelProperty = DependencyProperty.Register(
        nameof(SaveLabel),
        typeof(string),
        typeof(UnsavedChangesBanner),
        new PropertyMetadata("Save"));

    public UnsavedChangesBanner()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>The banner text. Empty hides the banner.</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>The editor area the Discard button loads again: "Settings", "Scenes" or "Stations".</summary>
    public string Area
    {
        get => (string)GetValue(AreaProperty);
        set => SetValue(AreaProperty, value);
    }

    /// <summary>The text of the save button, the same as the page's own save button.</summary>
    public string SaveLabel
    {
        get => (string)GetValue(SaveLabelProperty);
        set => SetValue(SaveLabelProperty, value);
    }

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var banner = (UnsavedChangesBanner)d;
        banner.Visibility = string.IsNullOrEmpty(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible;
    }
}
