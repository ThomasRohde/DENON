using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Denon.App.Desktop.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace Denon.App.Desktop;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly MainViewModel _viewModel;
    private readonly Stopwatch _vuStopwatch = Stopwatch.StartNew();
    private readonly double[] _spectrumBandsBuffer = new double[31];
    private double _lastVuFrameSeconds;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        StateChanged += OnWindowStateChanged;
        Loaded += (_, _) => CompositionTarget.Rendering += OnCompositionRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnCompositionRendering;
        ApplyDwmStyling();
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || !_viewModel.IsAnyAudioMeterVisible || _viewModel.ActiveSection != "Control")
        {
            _lastVuFrameSeconds = _vuStopwatch.Elapsed.TotalSeconds;
            return;
        }

        var now = _vuStopwatch.Elapsed.TotalSeconds;
        var dt = now - _lastVuFrameSeconds;
        _lastVuFrameSeconds = now;

        if (_viewModel.ShowAnalogVu)
        {
            var (left, right, isLive) = _viewModel.StepVuMeterFrame(dt);
            HeroVuMeter.SetLevelsImmediate(left, right, isLive);
        }

        if (_viewModel.ShowRtaSpectrum)
        {
            var (isLiveRta, isSimRta) = _viewModel.StepSpectrumFrame(_spectrumBandsBuffer, dt);
            HeroSpectrumAnalyzer.SetBandsImmediate(_spectrumBandsBuffer, dt, isLiveRta, isSimRta);
        }
    }

    private void ApplyDwmStyling()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            var dark = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            var round = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        }
        catch
        {
            // Ignore on older Windows versions
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // Compensate for WindowChrome maximize overscan so window edges stay crisp
        WindowRootBorder.Padding = WindowState == WindowState.Maximized
            ? new Thickness(7)
            : new Thickness(0);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void VolumeSlider_DragStarted(object sender, DragStartedEventArgs e) =>
        _viewModel.BeginVolumeDrag();

    private void VolumeSlider_DragCompleted(object sender, DragCompletedEventArgs e) =>
        _viewModel.EndVolumeDrag();

    private void VolumeCard_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var delta = e.Delta > 0 ? 2 : -2;
        _viewModel.AdjustVolumeByWheel(delta);
        e.Handled = true;
    }

    private void ConsoleInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SendConsoleCommand.CanExecute(null))
        {
            _viewModel.SendConsoleCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void YouTubeSearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SearchYouTubeCommand.CanExecute(null))
        {
            _viewModel.SearchYouTubeCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RadioBrowserSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SearchRadioBrowserCommand.CanExecute(null))
        {
            _viewModel.SearchRadioBrowserCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void LocalDropZone_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void LocalDropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths &&
            paths.Length > 0)
        {
            _viewModel.HandleDroppedLocalPaths(paths);
        }

        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Interactive shortcut recorder in Settings takes priority over all other key handling
        if (_viewModel.IsRecordingAnyShortcut)
        {
            if (_viewModel.TryCaptureRecordedShortcut(actualKey, modifiers))
            {
                e.Handled = true;
            }

            return;
        }

        var isTyping = Keyboard.FocusedElement is TextBox or PasswordBox;
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);

        // F1 anywhere opens context-aware Help & Guide for the current section or active plugin
        if (actualKey == Key.F1 && modifiers == ModifierKeys.None)
        {
            _viewModel.OpenContextualHelp();
            e.Handled = true;
            return;
        }

        if (ctrl && actualKey == Key.R)
        {
            if (_viewModel.RefreshCommand.CanExecute(null))
            {
                _viewModel.RefreshCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (ctrl && modifiers == ModifierKeys.Control)
        {
            switch (actualKey)
            {
                case Key.D1:
                    _viewModel.ActiveSection = "Control";
                    e.Handled = true;
                    return;
                case Key.D2:
                    _viewModel.ActiveSection = "YouTube";
                    e.Handled = true;
                    return;
                case Key.D3:
                    _viewModel.ActiveSection = "Acoustics";
                    e.Handled = true;
                    return;
                case Key.D4:
                    _viewModel.ActiveSection = "CastRemote";
                    e.Handled = true;
                    return;
                case Key.D5:
                    _viewModel.ActiveSection = "Stations";
                    e.Handled = true;
                    return;
                case Key.D6:
                    _viewModel.ActiveSection = "Plugins";
                    e.Handled = true;
                    return;
                case Key.D7:
                    _viewModel.ActiveSection = "Settings";
                    e.Handled = true;
                    return;
                case Key.D8:
                    _viewModel.ActiveSection = "Diagnostics";
                    e.Handled = true;
                    return;
                case Key.D9:
                    _viewModel.OpenContextualHelp();
                    e.Handled = true;
                    return;
            }
        }

        if (isTyping && modifiers == ModifierKeys.None && actualKey is not (>= Key.F1 and <= Key.F24))
        {
            return;
        }

        if (_viewModel.TryExecuteInAppShortcut(actualKey, modifiers))
        {
            e.Handled = true;
        }
    }
}
