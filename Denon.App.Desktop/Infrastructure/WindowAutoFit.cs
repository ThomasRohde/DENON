using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Denon.App.Desktop.Infrastructure;

/// <summary>
/// Keeps a fitted main window fitted: when the Control Deck grows (the receiver connects, inputs and meters
/// appear) the window grows with it, on its monitor. It stops for good once the user moves or resizes the window,
/// so their own size is never undone.
/// </summary>
public sealed class WindowAutoFit
{
    private const int WmEnterSizeMove = 0x0231;

    private readonly Window _window;
    private readonly FrameworkElement _deck;
    private readonly DispatcherTimer _timer;

    public WindowAutoFit(Window window, FrameworkElement deck, bool userSized)
    {
        _window = window;
        _deck = deck;
        UserSized = userSized;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Check();

        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        source?.AddHook(OnMessage);
        if (!userSized)
        {
            _timer.Start();
        }
    }

    /// <summary>True once the user moved or resized the window (saved with the placement).</summary>
    public bool UserSized { get; private set; }

    private void Check()
    {
        if (!_window.IsVisible || _window.WindowState != WindowState.Normal || !_deck.IsVisible)
        {
            return;
        }

        if (WindowFitter.DeckScrolls(_deck))
        {
            WindowFitter.Refit(_window, _deck, growOnly: true);
        }
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // The user starts to drag or resize the window: from now on it is their size.
        if (msg == WmEnterSizeMove && !UserSized)
        {
            UserSized = true;
            _timer.Stop();
        }

        return IntPtr.Zero;
    }
}
