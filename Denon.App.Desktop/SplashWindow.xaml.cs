using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Rectangle = System.Windows.Shapes.Rectangle;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Infrastructure.Theming;

namespace Denon.App.Desktop;

/// <summary>Why the splash shows: the first start on this PC, a new version, or the same version installed again.</summary>
public enum SplashKind
{
    FirstStart,
    Updated,
    Reinstalled,
}

/// <summary>
/// The splash screen on the first start after an install, upgrade or reinstall: logo, a short welcome, an animated
/// spectrum and the start-up step. It stays at least <see cref="MinimumShown"/>, then fades out over the main window.
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>Long enough to read the welcome, short enough not to hold anyone up.</summary>
    public static readonly TimeSpan MinimumShown = TimeSpan.FromSeconds(2.6);

    private const int BarCount = 32;

    private readonly Stopwatch _shown = new();

    public SplashWindow(SplashKind kind)
    {
        InitializeComponent();
        VersionText.Text = "v" + AppInfo.Version;
        KindText.Text = kind switch
        {
            SplashKind.Updated => $"Updated to version {AppInfo.Version}",
            SplashKind.Reinstalled => "Welcome back",
            _ => "Welcome",
        };

        BuildBars();
        Loaded += (_, _) =>
        {
            _shown.Start();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase() });
            var grow = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(700)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
            LogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            LogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        };
    }

    /// <summary>Which splash, if any, this start should show: null when this install has shown it already.</summary>
    public static SplashKind? KindFor(string? shownFor, bool firstStartOnThisPc)
    {
        if (string.Equals(shownFor, AppInfo.InstallStamp, StringComparison.Ordinal))
        {
            return null;
        }

        if (firstStartOnThisPc)
        {
            return SplashKind.FirstStart;
        }

        var shownVersion = shownFor?.Split('|')[0];
        return string.Equals(shownVersion, AppInfo.Version, StringComparison.Ordinal) ? SplashKind.Reinstalled : SplashKind.Updated;
    }

    /// <summary>The start-up step under the spectrum.</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Waits out the minimum time, fades out and closes.</summary>
    public async Task FadeOutAndCloseAsync()
    {
        var left = MinimumShown - _shown.Elapsed;
        if (left > TimeSpan.Zero)
        {
            await Task.Delay(left);
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        var done = new TaskCompletionSource();
        fade.Completed += (_, _) => done.TrySetResult();
        BeginAnimation(OpacityProperty, fade);
        await done.Task;
        Close();
    }

    /// <summary>
    /// One rounded bar per band; each breathes between two heights on its own period, so the row moves like a
    /// spectrum (a low-mid hump, quieter highs) without any audio.
    /// </summary>
    private void BuildBars()
    {
        var random = new Random(7);
        var gap = SkinMetrics.Scale(4, SkinMetrics.Current.SizeScale);
        for (var i = 0; i < BarCount; i++)
        {
            Bars.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            var t = i / (double)(BarCount - 1);
            var shape = 0.35 + (0.6 * Math.Exp(-Math.Pow((t - 0.3) / 0.28, 2))) + (0.05 * random.NextDouble());
            var scale = new ScaleTransform(1, shape * 0.4);
            var bar = new Rectangle
            {
                RadiusX = gap / 2,
                RadiusY = gap / 2,
                Margin = new Thickness(gap / 2, 0, gap / 2, 0),
                RenderTransformOrigin = new System.Windows.Point(0.5, 1),
                RenderTransform = scale,
                Opacity = 0.92,
            };
            bar.SetResourceReference(Shape.FillProperty, "SplashBarBrush");
            System.Windows.Controls.Grid.SetColumn(bar, i);
            Bars.Children.Add(bar);

            var breathe = new DoubleAnimation(shape * 0.25, Math.Min(1, shape * (0.85 + (0.25 * random.NextDouble()))),
                TimeSpan.FromMilliseconds(420 + random.Next(520)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(random.Next(400)),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        }
    }
}
