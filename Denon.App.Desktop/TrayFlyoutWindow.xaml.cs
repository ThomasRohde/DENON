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
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 8, workArea.Right - Width - 8);
        var estimatedHeight = ActualHeight > 0 ? ActualHeight : 440;
        Top = Math.Max(workArea.Top + 8, workArea.Bottom - estimatedHeight - 8);
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
