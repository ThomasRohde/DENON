using System.Windows;
using System.Windows.Media;
using Denon.App.Desktop.Services;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Infrastructure.Theming;

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
        ApplyMaterials();

        // The dials are meter materials (the skin's set for the theme): a skin or theme switch gives new faces,
        // accents, print and fonts.
        Loaded += (_, _) =>
        {
            ThemeManager.SkinChanged += OnMaterialsChanged;
            ThemeManager.ThemeChanged += OnMaterialsChanged;
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.SkinChanged -= OnMaterialsChanged;
            ThemeManager.ThemeChanged -= OnMaterialsChanged;
        };
    }

    private void OnMaterialsChanged(object? sender, EventArgs e)
    {
        ApplyMaterials();
        var source = _lastSource ?? AudioMeterSource.Off;
        _lastSource = null;
        ApplySource(source);
        LeftDial.Restyle();
        RightDial.Restyle();
    }

    private void ApplyMaterials()
    {
        LeftDial.Accent = MeterPalette.Current.LeftAccent;
        RightDial.Accent = MeterPalette.Current.RightAccent;
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

    /// <summary>Color of the source print under "VU" (meter material: the skin's set for the theme).</summary>
    public static Color SourceColor(AudioMeterSource source) => source switch
    {
        AudioMeterSource.Denon => MeterPalette.Current.SourceDenon,
        AudioMeterSource.Room => MeterPalette.Current.SourceRoom,
        AudioMeterSource.Pc => MeterPalette.Current.SourcePc,
        AudioMeterSource.Muted => MeterPalette.Current.SourceMuted,
        AudioMeterSource.Simulated => MeterPalette.Current.SourceSimulated,
        _ => MeterPalette.Current.SourceIdle,
    };

    public static string SourceToolTip(AudioMeterSource source) => source switch
    {
        AudioMeterSource.Denon => "Real audio this app streams to the Denon (PC Cast, YouTube, jukebox), lined up with the speakers by the measured HEOS delay.",
        AudioMeterSource.Room => "Listening via PC microphone / Line-In (opt-in Room mode) while the Denon plays an external source (TV, HDMI, Chromecast…).",
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
        var text = source switch
        {
            AudioMeterSource.Denon => "DENON · SYNCED",
            AudioMeterSource.Room => "ROOM · MIC",
            _ => AudioMeterSourceRouter.BadgeText(source),
        };
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
