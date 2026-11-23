using System.Windows;
using System.Windows.Media;
using Denon.App.Desktop.Services;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace Denon.App.Desktop.Controls;

/// <summary>Stereo pair of backlit analog VU dials (<see cref="VuDial"/>).</summary>
public partial class AnalogVuMeterControl : UserControl
{
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

    private AudioMeterSource? _lastSource;

    public AnalogVuMeterControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Per-frame update from the render loop (CompositionTarget.Rendering): two needle angles and the
    /// lamps; no bindings or allocations.
    /// </summary>
    public void SetLevelsImmediate(double leftLevel, double rightLevel, AudioMeterSource source)
    {
        LeftDial.SetLevel(leftLevel);
        RightDial.SetLevel(rightLevel);
        ApplySource(source);
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

    /// <summary>Color of the source print under "VU".</summary>
    public static Color SourceColor(AudioMeterSource source) => source switch
    {
        AudioMeterSource.Denon => Color.FromRgb(52, 211, 153),
        AudioMeterSource.Pc => Color.FromRgb(251, 191, 36),
        AudioMeterSource.Muted => Color.FromRgb(248, 113, 113),
        AudioMeterSource.Simulated => Color.FromRgb(148, 163, 184),
        _ => Color.FromRgb(100, 116, 139),
    };

    public static string SourceToolTip(AudioMeterSource source) => source switch
    {
        AudioMeterSource.Denon => "Real audio this app streams to the Denon (PC Cast, YouTube, jukebox), lined up with the speakers by the measured HEOS delay.",
        AudioMeterSource.Simulated => "The Denon plays a source the app cannot hear (TV, HDMI, Spotify…). Simulated from the Denon volume.",
        AudioMeterSource.Muted => "The Denon is muted.",
        AudioMeterSource.Pc => "The Denon is off or idle. Showing this PC's own audio output, not the Denon.",
        _ => "Nothing plays.",
    };

    private void ApplySource(AudioMeterSource source)
    {
        if (_lastSource == source)
        {
            return;
        }

        _lastSource = source;
        var text = source == AudioMeterSource.Denon ? "DENON · SYNCED" : AudioMeterSourceRouter.BadgeText(source);
        var color = SourceColor(source);
        LeftDial.SetSource(text, color);
        RightDial.SetSource(text, color);
        ToolTip = SourceToolTip(source);
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnalogVuMeterControl control)
        {
            control.LeftDial.SetLevel(control.LeftLevel);
            control.RightDial.SetLevel(control.RightLevel);
        }
    }

    private static void OnLiveSignalChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnalogVuMeterControl control)
        {
            // The per-frame source (SetLevelsImmediate) owns the source print; repaint on the next frame.
            control._lastSource = null;
        }
    }
}
