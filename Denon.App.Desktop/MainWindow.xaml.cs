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

        // When the update panel opens, focus moves into it (the content behind it is disabled).
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsUpdatePanelOpen) && viewModel.IsUpdatePanelOpen)
            {
                Dispatcher.BeginInvoke(
                    () => UpdatePanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
        };
        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering += OnCompositionRendering;
            _viewModel.SetWindowVisibleForMeters(IsVisible && WindowState != WindowState.Minimized);
        };
        Unloaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _viewModel.SetWindowVisibleForMeters(false);
        };
        IsVisibleChanged += (_, _) =>
            _viewModel.SetWindowVisibleForMeters(IsVisible && WindowState != WindowState.Minimized);
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
            var (left, right, source) = _viewModel.StepVuMeterFrame(dt);
            ControlDeckPage.HeroVuMeter.SetLevelsImmediate(left, right, source);
        }

        if (_viewModel.ShowRtaSpectrum)
        {
            var rtaSource = _viewModel.StepSpectrumFrame(_spectrumBandsBuffer, dt);
            ControlDeckPage.HeroSpectrumAnalyzer.SetBandsImmediate(_spectrumBandsBuffer, dt, rtaSource);
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
        _viewModel.SetWindowVisibleForMeters(IsVisible && WindowState != WindowState.Minimized);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

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

        // The update panel is modal: Escape closes it, Tab stays inside it, and the page shortcuts wait.
        if (_viewModel.IsUpdatePanelOpen)
        {
            if (actualKey == Key.Escape)
            {
                _viewModel.CloseUpdatePanelCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        var isTyping = Keyboard.FocusedElement is TextBox or PasswordBox;
        var isInteractiveControl = Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase
            or System.Windows.Controls.Primitives.Selector
            or System.Windows.Controls.Slider
            or System.Windows.Controls.ComboBox
            or System.Windows.Controls.MenuItem;
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

        // Let focused buttons, checkboxes, sliders and menus handle Space/Enter natively instead of hijacking Play/Pause!
        if (isInteractiveControl && modifiers == ModifierKeys.None && actualKey is Key.Space or Key.Enter)
        {
            return;
        }

        if (_viewModel.TryExecuteInAppShortcut(actualKey, modifiers))
        {
            e.Handled = true;
        }
    }
}
