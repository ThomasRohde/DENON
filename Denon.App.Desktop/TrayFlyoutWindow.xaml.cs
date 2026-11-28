using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Denon.App.Desktop.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Denon.App.Desktop;

public partial class TrayFlyoutWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Action? _openMainWindow;
    private DateTimeOffset _lastHiddenAt = DateTimeOffset.MinValue;

    public TrayFlyoutWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public void Configure(Action openMainWindow) => _openMainWindow = openMainWindow;

    public void ToggleNearTray()
    {
        if (IsVisible)
        {
            HideFlyout();
            return;
        }

        // Prevent immediate reopen if user clicked the tray icon to dismiss it
        if ((DateTimeOffset.UtcNow - _lastHiddenAt).TotalMilliseconds < 250)
        {
            return;
        }

        PositionNearTaskbar();
        Show();
        UpdateLayout();
        PositionNearTaskbar();
        Activate();
    }

    private void PositionNearTaskbar()
    {
        // The monitor under the pointer: the user just clicked the tray icon there.
        var monitor = Denon.App.Desktop.Infrastructure.WindowFitter.MonitorAtPointer();
        var workArea = monitor.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var estimatedHeight = ActualHeight > 0 ? ActualHeight : 440;
        var pointer = GetPointerDip(monitor.Scale);

        // Next to the pointer's corner of the work area: right or left, bottom or top, so a taskbar on any side works.
        var nearRight = pointer.X >= workArea.Left + (workArea.Width / 2);
        var nearBottom = pointer.Y >= workArea.Top + (workArea.Height / 2);
        Left = nearRight ? Math.Max(workArea.Left + 8, workArea.Right - width - 8) : workArea.Left + 8;
        Top = nearBottom ? Math.Max(workArea.Top + 8, workArea.Bottom - estimatedHeight - 8) : workArea.Top + 8;
    }

    /// <param name="monitorScale">The pointer monitor's scale, for the first call before the window has a source.</param>
    private System.Windows.Point GetPointerDip(double monitorScale)
    {
        var device = System.Windows.Forms.Cursor.Position;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is { } target)
        {
            return target.TransformFromDevice.Transform(new System.Windows.Point(device.X, device.Y));
        }

        // Same units as the work area (DIPs on that monitor).
        var scale = monitorScale > 0 ? monitorScale : 1;
        return new System.Windows.Point(device.X / scale, device.Y / scale);
    }

    private void HideFlyout()
    {
        _lastHiddenAt = DateTimeOffset.UtcNow;
        Hide();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            HideFlyout();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFlyout();
            e.Handled = true;
        }
    }

    private void OpenFullWindowButton_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _openMainWindow?.Invoke();
    }

    private void VolumeSlider_DragStarted(object sender, DragStartedEventArgs e) =>
        _viewModel.BeginVolumeDrag();

    private void VolumeSlider_DragCompleted(object sender, DragCompletedEventArgs e) =>
        _viewModel.EndVolumeDrag();

    private void VolumeArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _viewModel.AdjustVolumeByWheel(e.Delta > 0 ? 2 : -2);
        e.Handled = true;
    }
}
