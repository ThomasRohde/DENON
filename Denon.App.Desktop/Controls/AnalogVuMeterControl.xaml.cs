using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace Denon.App.Desktop.Controls;

public partial class AnalogVuMeterControl : UserControl
{
    private static readonly Brush PeakOffBrush = Freeze(Color.FromRgb(59, 21, 24));
    private static readonly Brush PeakOnBrush = Freeze(Color.FromRgb(239, 68, 68));

    public static readonly DependencyProperty LeftLevelProperty = DependencyProperty.Register(
        nameof(LeftLevel),
        typeof(double),
        typeof(AnalogVuMeterControl),
        new PropertyMetadata(0.0, OnLevelChanged));

    public static readonly DependencyProperty RightLevelProperty = DependencyProperty.Register(
        nameof(RightLevel),
        typeof(double),
        typeof(AnalogVuMeterControl),
        new PropertyMetadata(0.0, OnLevelChanged));

    public static readonly DependencyProperty IsLiveSignalProperty = DependencyProperty.Register(
        nameof(IsLiveSignal),
        typeof(bool),
        typeof(AnalogVuMeterControl),
        new PropertyMetadata(false, OnLiveSignalChanged));

    public AnalogVuMeterControl()
    {
        InitializeComponent();
    }

    public double LeftLevel
    {
        get => (double)GetValue(LeftLevelProperty);
        set => SetValue(LeftLevelProperty, value);
    }

    public double RightLevel
    {
        get => (double)GetValue(RightLevelProperty);
        set => SetValue(RightLevelProperty, value);
    }

    public bool IsLiveSignal
    {
        get => (bool)GetValue(IsLiveSignalProperty);
        set => SetValue(IsLiveSignalProperty, value);
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnalogVuMeterControl control)
        {
            control.UpdateNeedles();
        }
    }

    private static void OnLiveSignalChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnalogVuMeterControl control)
        {
            control.ModeBadgeText.Text = control.IsLiveSignal ? "LIVE" : "SYNC";
        }
    }

    private void UpdateNeedles()
    {
        var left = Math.Clamp(LeftLevel, 0.0, 1.0);
        var right = Math.Clamp(RightLevel, 0.0, 1.0);

        LeftNeedleTransform.Angle = -44.0 + (left * 88.0);
        RightNeedleTransform.Angle = -44.0 + (right * 88.0);

        PeakLedLeft.Fill = left >= 0.84 ? PeakOnBrush : PeakOffBrush;
        PeakLedRight.Fill = right >= 0.84 ? PeakOnBrush : PeakOffBrush;
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
