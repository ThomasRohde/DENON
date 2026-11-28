using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Denon.App.Desktop.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Point = System.Windows.Point;

namespace Denon.App.Desktop.CommandPalette;

/// <summary>
/// The Ctrl+K command palette. A small window over the top of the main window: type, choose with Up and
/// Down, run with Enter or a click, close with Escape or by clicking somewhere else.
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private static CommandPaletteWindow? s_open;

    private readonly CommandPaletteViewModel _viewModel;
    private bool _closing;

    // False when the palette closes because another window took focus (a click elsewhere, Alt+Tab):
    // then the main window must not take focus back from the app the user chose.
    private bool _restoreOwnerFocus = true;

    private CommandPaletteWindow(Window owner, MainViewModel main)
    {
        InitializeComponent();
        Owner = owner;
        _viewModel = new CommandPaletteViewModel(main);
        _viewModel.CloseRequested += (_, _) => CloseOnce();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        DataContext = _viewModel;

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        };
    }

    /// <summary>Opens the palette over <paramref name="owner"/>, or brings the open one back to the front.</summary>
    public static void Open(Window owner, MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(main);

        if (s_open is { } existing)
        {
            existing.Activate();
            existing.SearchBox.Focus();
            return;
        }

        if (owner.WindowState == WindowState.Minimized)
        {
            owner.WindowState = WindowState.Normal;
        }

        var window = new CommandPaletteWindow(owner, main);
        window.PlaceOverOwner();
        s_open = window;
        window.Closed += (_, _) => s_open = null;
        window.Show();
        window.Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnClosed(e);
        if (_restoreOwnerFocus)
        {
            Owner?.Activate();
        }
    }

    private void CloseOnce()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_closing)
        {
            _restoreOwnerFocus = false;
        }

        CloseOnce();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        switch (key)
        {
            case Key.Escape:
                CloseOnce();
                e.Handled = true;
                break;
            case Key.Enter:
                _viewModel.RunSelected();
                e.Handled = true;
                break;
            case Key.Down:
                _viewModel.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                _viewModel.MoveSelection(PageSize());
                e.Handled = true;
                break;
            case Key.PageUp:
                _viewModel.MoveSelection(-PageSize());
                e.Handled = true;
                break;
            case Key.Home when modifiers == ModifierKeys.Control || !SearchBox.IsKeyboardFocused:
                _viewModel.SelectFirst();
                e.Handled = true;
                break;
            case Key.End when modifiers == ModifierKeys.Control || !SearchBox.IsKeyboardFocused:
                _viewModel.SelectLast();
                e.Handled = true;
                break;
            case Key.K when modifiers == ModifierKeys.Control:
                // Ctrl+K again closes the palette, like most apps with one.
                CloseOnce();
                e.Handled = true;
                break;
        }

        if (e.Handled && ResultsList.IsKeyboardFocusWithin && _viewModel.SelectedResult is { } selected
            && ResultsList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem container)
        {
            // Keep keyboard focus on the chosen row while the list has focus, so the focus outline follows it.
            container.Focus();
        }
    }

    private void ResultsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(ResultsList, source) is ListBoxItem { DataContext: CommandPaletteResultItem item })
        {
            _viewModel.SelectedResult = item;
            _viewModel.RunSelected();
            e.Handled = true;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.SelectedResult) && _viewModel.SelectedResult is { } selected)
        {
            ResultsList.ScrollIntoView(selected);
        }
        else if (e.PropertyName == nameof(CommandPaletteViewModel.StatusText))
        {
            // Screen readers say the chosen row and the match count: focus stays in the search box.
            Dispatcher.BeginInvoke(() =>
                (UIElementAutomationPeer.FromElement(StatusLine) ?? UIElementAutomationPeer.CreatePeerForElement(StatusLine))
                    ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
        }
    }

    private int PageSize()
    {
        if (ResultsList.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first && first.ActualHeight > 0)
        {
            return Math.Max(1, (int)(ResultsList.ActualHeight / first.ActualHeight) - 1);
        }

        return 8;
    }

    /// <summary>Centres the palette over the owner's client area, near its top.</summary>
    private void PlaceOverOwner()
    {
        if (Owner is not { } owner || PresentationSource.FromVisual(owner) is not { CompositionTarget: { } target })
        {
            return;
        }

        var fromDevice = target.TransformFromDevice;
        var topLeft = fromDevice.Transform(owner.PointToScreen(new Point(0, 0)));
        var bottomRight = fromDevice.Transform(owner.PointToScreen(new Point(owner.ActualWidth, owner.ActualHeight)));
        var ownerWidth = bottomRight.X - topLeft.X;
        var ownerHeight = bottomRight.Y - topLeft.Y;

        var width = double.IsNaN(Width) ? ActualWidth : Width;
        Left = topLeft.X + Math.Max(0, (ownerWidth - width) / 2);
        Top = topLeft.Y + Math.Max(24, ownerHeight * 0.12);
    }
}

/// <summary>
/// Opens the palette with Ctrl+K anywhere in the main window.
/// </summary>
/// <remarks>
/// TODO(#76): replace this class handler with a Ctrl+K KeyBinding in MainWindow (and add Ctrl+K to the
/// reserved gestures) after the desktop shell PR merges. It is a class handler so that this change
/// does not touch MainWindow.
/// </remarks>
public static class CommandPaletteHotkey
{
    private static bool s_registered;

    /// <summary>Registers the Ctrl+K handler once and returns the command that opens the palette.</summary>
    public static Infrastructure.RelayCommand CreateOpenCommand()
    {
        if (!s_registered)
        {
            s_registered = true;
            EventManager.RegisterClassHandler(typeof(MainWindow), UIElement.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(OnMainWindowPreviewKeyDown));
        }

        return new Infrastructure.RelayCommand(_ => OpenForMainWindow());
    }

    private static void OpenForMainWindow()
    {
        var window = System.Windows.Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
        if (window?.DataContext is MainViewModel main)
        {
            CommandPaletteWindow.Open(window, main);
        }
    }

    private static void OnMainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not MainWindow window || window.DataContext is not MainViewModel main)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.K || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        // The shortcut recorder and the update panel own the keyboard while they are open.
        if (main.IsRecordingAnyShortcut || main.IsUpdatePanelOpen)
        {
            return;
        }

        e.Handled = true;
        CommandPaletteWindow.Open(window, main);
    }
}
