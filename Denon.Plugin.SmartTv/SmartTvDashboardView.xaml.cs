using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Denon.Plugin.SmartTv;

public partial class SmartTvDashboardView : UserControl
{
    private const double TrackpadSensitivity = 2.1;

    private bool _isDraggingTrackpad;
    private Point _lastTrackpadPos;
    private double _dragDistanceAccum;
    private double _pendingDx;
    private double _pendingDy;
    private DateTime _lastFlushUtc = DateTime.MinValue;
    private CancellationTokenSource? _markerCts;

    public SmartTvDashboardView()
    {
        InitializeComponent();
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    private SmartTvDashboardViewModel? ViewModel => DataContext as SmartTvDashboardViewModel;

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel?.SetViewVisible(true);
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel?.SetViewVisible(false);
    }

    private void PreviewSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || !vm.HasPreviewImage || PreviewImageControl.ActualWidth <= 1 || PreviewImageControl.ActualHeight <= 1)
        {
            return;
        }

        var imgPos = e.GetPosition(PreviewImageControl);
        if (imgPos.X < 0 || imgPos.Y < 0 || imgPos.X > PreviewImageControl.ActualWidth || imgPos.Y > PreviewImageControl.ActualHeight)
        {
            return;
        }

        var normX = (int)Math.Clamp(Math.Round(imgPos.X / PreviewImageControl.ActualWidth * 1000.0), 0, 1000);
        var normY = (int)Math.Clamp(Math.Round(imgPos.Y / PreviewImageControl.ActualHeight * 1000.0), 0, 1000);

        var canvasPos = e.GetPosition(ClickOverlayCanvas);
        ShowClickMarker(canvasPos);
        _ = vm.ClickPreviewAtNormalizedAsync(normX, normY);
    }

    private void PreviewSurface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ViewModel?.SendButtonCommand.Execute("BACK");
    }

    private void PreviewSurface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var dy = e.Delta > 0 ? 3 : -3;
        _ = ViewModel?.ScrollPointerAsync(0, dy);
    }

    private void Trackpad_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.NotifyUserInteraction();
        _isDraggingTrackpad = true;
        _lastTrackpadPos = e.GetPosition(TrackpadSurface);
        _dragDistanceAccum = 0;
        _pendingDx = 0;
        _pendingDy = 0;
        TrackpadSurface.CaptureMouse();
        TrackpadSurface.BorderBrush = HostTheme.Brush("AccentCyanBright");
    }

    private void Trackpad_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingTrackpad)
        {
            return;
        }

        var pos = e.GetPosition(TrackpadSurface);
        var dx = pos.X - _lastTrackpadPos.X;
        var dy = pos.Y - _lastTrackpadPos.Y;
        _lastTrackpadPos = pos;

        _dragDistanceAccum += Math.Abs(dx) + Math.Abs(dy);
        _pendingDx += dx * TrackpadSensitivity;
        _pendingDy += dy * TrackpadSensitivity;

        var now = DateTime.UtcNow;
        if ((now - _lastFlushUtc).TotalMilliseconds >= 35)
        {
            FlushTrackpadMove(now);
        }
    }

    private void Trackpad_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingTrackpad)
        {
            return;
        }

        _isDraggingTrackpad = false;
        TrackpadSurface.ReleaseMouseCapture();
        TrackpadSurface.BorderBrush = HostTheme.Brush("Ink12");

        if (_dragDistanceAccum < 6.0)
        {
            _pendingDx = 0;
            _pendingDy = 0;
            _ = ViewModel?.ClickPointerAsync();
        }
        else
        {
            FlushTrackpadMove(DateTime.UtcNow);
        }
    }

    private void Trackpad_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ViewModel?.SendButtonCommand.Execute("BACK");
    }

    private void Trackpad_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var dy = e.Delta > 0 ? 3 : -3;
        _ = ViewModel?.ScrollPointerAsync(0, dy);
    }

    private void FlushTrackpadMove(DateTime now)
    {
        var sendDx = (int)Math.Round(_pendingDx);
        var sendDy = (int)Math.Round(_pendingDy);
        if (sendDx == 0 && sendDy == 0)
        {
            return;
        }

        _pendingDx -= sendDx;
        _pendingDy -= sendDy;
        _lastFlushUtc = now;
        _ = ViewModel?.MovePointerRelativeAsync(sendDx, sendDy);
    }

    private void ShowClickMarker(Point pos)
    {
        _markerCts?.Cancel();
        _markerCts?.Dispose();
        var cts = new CancellationTokenSource();
        _markerCts = cts;

        Canvas.SetLeft(ClickMarker, pos.X - (ClickMarker.Width / 2));
        Canvas.SetTop(ClickMarker, pos.Y - (ClickMarker.Height / 2));
        ClickMarker.Visibility = Visibility.Visible;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(550, cts.Token);
                Dispatcher.Invoke(() => ClickMarker.Visibility = Visibility.Collapsed);
            }
            catch
            {
            }
        }, cts.Token);
    }
}
