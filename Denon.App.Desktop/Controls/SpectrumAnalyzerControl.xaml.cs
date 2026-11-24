using System.Windows.Media;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Infrastructure.Theming;
using Denon.App.Desktop.Services;
using Color = System.Windows.Media.Color;

namespace Denon.App.Desktop.Controls;

/// <summary>31-band LED spectrum (RTA) with gravity peak caps and bloom (<see cref="SpectrumLedView"/>).</summary>
public partial class SpectrumAnalyzerControl : System.Windows.Controls.UserControl
{
    private const int BandCount = 31;
    private const double PeakHoldSeconds = 0.45;
    private const double PeakGravity = 2.4; // level units per second², after the hold

    private readonly SpectrumState _state = new(BandCount);
    private readonly double[] _peakVelocity = new double[BandCount];
    private readonly double[] _peakHoldLeft = new double[BandCount];
    private AudioMeterSource? _lastSource;

    public SpectrumAnalyzerControl()
    {
        InitializeComponent();
        ApplyMaterials();
        BarsView.State = _state;
        GlowView.State = _state;
        ApplySource(AudioMeterSource.Off);

        // The pill takes theme colors: repaint it on the next frame after a theme switch.
        // The LED screen is a skin material: a skin switch gives a new screen and segments.
        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged += OnThemeChanged;
            ThemeManager.SkinChanged += OnSkinChanged;
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.SkinChanged -= OnSkinChanged;
        };
    }

    private void ApplyMaterials()
    {
        LedScreen.Background = Frozen(new SolidColorBrush(MeterPalette.Current.LedScreen));
        LedScreen.BorderBrush = Frozen(new SolidColorBrush(MeterPalette.Current.LedScreenEdge));
    }

    private void OnSkinChanged(object? sender, EventArgs e)
    {
        ApplyMaterials();
        BarsView.Restyle();
        GlowView.Restyle();
        OnThemeChanged(sender, e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var source = _lastSource ?? AudioMeterSource.Off;
        _lastSource = null;
        ApplySource(source);
    }

    public void SetBandsImmediate(double[] bands, double deltaSeconds, AudioMeterSource source)
    {
        if (bands.Length < BandCount)
        {
            return;
        }

        ApplySource(source);

        var dt = Math.Clamp(deltaSeconds, 0.001, 0.08);
        var attack = 1.0 - Math.Exp(-dt / 0.010);
        var release = 1.0 - Math.Exp(-dt / 0.110);
        var levels = _state.Levels;
        var peaks = _state.Peaks;

        for (var i = 0; i < BandCount; i++)
        {
            var target = Math.Clamp(bands[i], 0.0, 1.0);
            levels[i] += (target - levels[i]) * (target > levels[i] ? attack : release);
            if (levels[i] < 0.004)
            {
                levels[i] = 0;
            }

            // Peak caps: jump up, hold, then fall with gravity.
            if (levels[i] >= peaks[i])
            {
                peaks[i] = levels[i];
                _peakVelocity[i] = 0;
                _peakHoldLeft[i] = PeakHoldSeconds;
            }
            else if (_peakHoldLeft[i] > 0)
            {
                _peakHoldLeft[i] -= dt;
            }
            else
            {
                _peakVelocity[i] += PeakGravity * dt;
                peaks[i] = Math.Max(levels[i], peaks[i] - (_peakVelocity[i] * dt));
            }
        }

        BarsView.InvalidateVisual();
        GlowView.InvalidateVisual();
    }

    private void ApplySource(AudioMeterSource source)
    {
        if (_lastSource == source)
        {
            return;
        }

        _lastSource = source;
        var color = PillColor(source);
        SignalModeLabel.Text = AudioMeterSourceRouter.RtaLabel(source);
        SignalModeLabel.Foreground = Frozen(new SolidColorBrush(color));
        SourcePill.Background = Frozen(new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B)));
        SourcePill.BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B)));
        SourcePill.ToolTip = AnalogVuMeterControl.SourceToolTip(source);
    }

    /// <summary>
    /// The source pill sits on the themed panel, not on the LED screen, so it takes theme colors (the VU dials print
    /// the source on their dark face with meter colors instead).
    /// </summary>
    private static Color PillColor(AudioMeterSource source) => source switch
    {
        AudioMeterSource.Denon => ThemeResources.Color("AccentEmeraldBright"),
        AudioMeterSource.Pc => ThemeResources.Color("AccentAmberBright"),
        AudioMeterSource.Muted => ThemeResources.Color("AccentCrimsonBright"),
        AudioMeterSource.Simulated => ThemeResources.Color("TextMuted"),
        _ => ThemeResources.Color("TextFaint"),
    };

    private static SolidColorBrush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
