using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Denon.App.Desktop.Controls;

public partial class SpectrumAnalyzerControl : System.Windows.Controls.UserControl
{
    private const int BandCount = 31;

    private readonly Rectangle[] _bars = new Rectangle[BandCount];
    private readonly Rectangle[] _peaks = new Rectangle[BandCount];
    private readonly double[] _smoothed = new double[BandCount];
    private readonly double[] _peakHold = new double[BandCount];
    private readonly LinearGradientBrush _barBrush;
    private readonly SolidColorBrush _peakBrush;
    private bool _barsBuilt;

    public SpectrumAnalyzerControl()
    {
        InitializeComponent();

        _barBrush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 1),
            EndPoint = new System.Windows.Point(0, 0),
        };
        _barBrush.GradientStops.Add(new GradientStop(Color.FromRgb(14, 165, 233), 0.0));
        _barBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 212, 255), 0.58));
        _barBrush.GradientStops.Add(new GradientStop(Color.FromRgb(245, 158, 11), 0.84));
        _barBrush.GradientStops.Add(new GradientStop(Color.FromRgb(239, 68, 68), 1.0));
        _barBrush.Freeze();

        _peakBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));
        _peakBrush.Freeze();
    }

    public void SetBandsImmediate(double[] bands, double deltaSeconds, bool isLiveSignal, bool isSimulatedPlaying)
    {
        if (!_barsBuilt)
        {
            BuildVisualBars();
        }

        var w = BarsCanvas.ActualWidth;
        var h = BarsCanvas.ActualHeight;
        if (w <= 10 || h <= 10 || bands.Length < BandCount)
        {
            return;
        }

        SignalModeLabel.Text = isLiveSignal
            ? "LIVE WASAPI FFT"
            : isSimulatedPlaying
                ? "AVR STREAM"
                : "STANDBY";

        var dt = Math.Clamp(deltaSeconds, 0.001, 0.08);
        var attack = 1.0 - Math.Exp(-dt / 0.012);
        var release = 1.0 - Math.Exp(-dt / 0.095);
        var peakFall = dt * 0.42;

        var slotWidth = w / BandCount;
        var barWidth = Math.Max(3.0, slotWidth - 3.0);

        for (var i = 0; i < BandCount; i++)
        {
            var target = Math.Clamp(bands[i], 0.0, 1.0);
            _smoothed[i] += (target - _smoothed[i]) * (target > _smoothed[i] ? attack : release);
            if (_smoothed[i] < 0.004) _smoothed[i] = 0;

            if (_smoothed[i] >= _peakHold[i])
            {
                _peakHold[i] = _smoothed[i];
            }
            else
            {
                _peakHold[i] = Math.Max(_smoothed[i], _peakHold[i] - peakFall);
            }

            var barH = Math.Max(2.0, _smoothed[i] * (h - 4.0));
            var left = (i * slotWidth) + 1.5;

            var rect = _bars[i];
            rect.Width = barWidth;
            rect.Height = barH;
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, h - barH);

            var peak = _peaks[i];
            peak.Width = barWidth;
            var peakY = Math.Max(0, h - Math.Max(3.0, _peakHold[i] * (h - 4.0)) - 3.0);
            Canvas.SetLeft(peak, left);
            Canvas.SetTop(peak, peakY);
            peak.Opacity = _peakHold[i] > 0.03 ? 0.92 : 0.12;
        }
    }

    private void BarsCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_barsBuilt)
        {
            BuildVisualBars();
        }
    }

    private void BuildVisualBars()
    {
        if (_barsBuilt)
        {
            return;
        }

        _barsBuilt = true;
        BarsCanvas.Children.Clear();

        for (var i = 0; i < BandCount; i++)
        {
            var bar = new Rectangle
            {
                Fill = _barBrush,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Width = 5,
                Height = 2,
            };
            var peak = new Rectangle
            {
                Fill = _peakBrush,
                RadiusX = 1,
                RadiusY = 1,
                Width = 5,
                Height = 2.5,
                Opacity = 0.12,
            };

            _bars[i] = bar;
            _peaks[i] = peak;
            BarsCanvas.Children.Add(bar);
            BarsCanvas.Children.Add(peak);
        }
    }
}
