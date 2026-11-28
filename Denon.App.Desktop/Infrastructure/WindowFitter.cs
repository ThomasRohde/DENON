using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure.Theming;

using Size = System.Windows.Size;

namespace Denon.App.Desktop.Infrastructure;

/// <summary>
/// Sizes the main window for its monitor and the skin: as tall as the Control Deck needs to show without scroll
/// bars, never larger than the monitor's work area, and centered on it. Also saves and restores the user's own
/// placement (<see cref="UiSettings.Window"/>).
/// </summary>
public static class WindowFitter
{
    /// <summary>Width of the Default skin's layout (the README screenshots); other skins scale it.</summary>
    public const double DesignWidth = 1360;

    /// <summary>The main window's own minimum size (MainWindow.xaml), used where the monitor has room for it.</summary>
    public const double MinWindowWidth = 960;

    public const double MinWindowHeight = 580;

    /// <summary>Free room around the window on its monitor, in DIPs.</summary>
    private const double ScreenMargin = 24;

    /// <summary>Extra height over the exact fit, so a line of text more does not bring the scroll bar back.</summary>
    private const double Slack = 8;

    /// <summary>A monitor's work area (without the taskbar) in device pixels, and its DPI scale (1 = 96 DPI).</summary>
    public readonly record struct Monitor(Rect WorkAreaPx, double Scale)
    {
        public Rect WorkArea => new(WorkAreaPx.X / Scale, WorkAreaPx.Y / Scale, WorkAreaPx.Width / Scale, WorkAreaPx.Height / Scale);
    }

    /// <summary>The result of a fit: the window size in DIPs, and whether the deck shows without scroll bars.</summary>
    public readonly record struct Fit(double Width, double Height, bool NoScroll);

    /// <summary>The monitor under the mouse pointer: where Windows opens a new app.</summary>
    public static Monitor MonitorAtPointer()
    {
        GetCursorPos(out var p);
        return MonitorInfo(MonitorFromPoint(p, MonitorDefaultToNearest));
    }

    /// <summary>The monitor that has most of the window.</summary>
    public static Monitor MonitorOf(Window window) =>
        MonitorInfo(MonitorFromWindow(new WindowInteropHelper(window).EnsureHandle(), MonitorDefaultToNearest));

    /// <summary>
    /// The smallest window height (at the skin's width) that shows the Control Deck without scroll bars, limited to
    /// the work area. Call on the UI thread with the deck page shown; the window may still be hidden.
    /// </summary>
    public static Fit Measure(Window window, FrameworkElement deck, Rect workArea)
    {
        var (_, minHeight, maxWidth, maxHeight) = Limits(workArea);
        var width = Math.Min(maxWidth, Math.Round(DesignWidth * SkinMetrics.Current.SizeScale));

        // The deck may be hidden behind another page (a skin switch in Settings): show it for the measure only.
        using var shown = new ShownForMeasure(deck);
        if (!ShowsWithoutScroll(window, deck, width, maxHeight))
        {
            return new Fit(width, maxHeight, NoScroll: false);
        }

        // Binary search for the smallest height without scroll bars (to 4 DIPs).
        double low = minHeight, high = maxHeight;
        if (ShowsWithoutScroll(window, deck, width, low))
        {
            high = low;
        }

        while (high - low > 4)
        {
            var mid = Math.Round((low + high) / 2);
            if (ShowsWithoutScroll(window, deck, width, mid))
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        return new Fit(width, Math.Min(maxHeight, high + Slack), NoScroll: true);
    }

    /// <summary>
    /// Lets the bindings of a window that has not shown yet attach (they attach on the dispatcher, after the first
    /// layout creates the templates), so a fit measures the deck with its content, not with empty lists.
    /// </summary>
    public static async Task LoadContentAsync(Window window)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            if (window.Content is UIElement root)
            {
                root.Measure(new Size(DesignWidth * SkinMetrics.Current.SizeScale, window.Height));
                root.Arrange(new Rect(root.DesiredSize));
            }

            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>
    /// The window's size limits on a work area: its own minimums where the monitor has room for them (a small,
    /// high-scaled screen gets smaller ones, so the title bar and buttons stay on screen), and the work area less
    /// a margin as the maximum.
    /// </summary>
    public static (double MinWidth, double MinHeight, double MaxWidth, double MaxHeight) Limits(Rect workArea)
    {
        var minWidth = Math.Min(MinWindowWidth, workArea.Width);
        var minHeight = Math.Min(MinWindowHeight, workArea.Height);
        return (minWidth, minHeight,
            Math.Max(minWidth, workArea.Width - (2 * ScreenMargin)),
            Math.Max(minHeight, workArea.Height - (2 * ScreenMargin)));
    }

    /// <summary>Sets the window's minimum size for its monitor (see <see cref="Limits"/>).</summary>
    public static void ApplyMinimums(Window window, Rect workArea)
    {
        var (minWidth, minHeight, _, _) = Limits(workArea);
        window.MinWidth = minWidth;
        window.MinHeight = minHeight;
    }

    /// <summary>Gives the window a size and centers it in the work area (normal state).</summary>
    public static void Place(Window window, Monitor monitor, double width, double height)
    {
        MoveOnto(window, monitor);
        var area = monitor.WorkArea;
        ApplyMinimums(window, area);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = width;
        window.Height = height;
        window.Left = Math.Round(area.Left + ((area.Width - width) / 2));
        window.Top = Math.Round(area.Top + ((area.Height - height) / 2));
    }

    /// <summary>
    /// Refits a shown window to the skin on its own monitor (after a skin switch): the same center where it fits,
    /// moved inside the work area where it does not. A maximized window keeps its state. With
    /// <paramref name="growOnly"/> it only gets taller (the deck grew), never smaller.
    /// </summary>
    public static void Refit(Window window, FrameworkElement deck, bool growOnly = false)
    {
        if (window.WindowState != WindowState.Normal)
        {
            return;
        }

        var monitor = MonitorOf(window);
        var area = monitor.WorkArea;
        ApplyMinimums(window, area);
        var fit = Measure(window, deck, area);
        if (growOnly && fit.Height <= window.ActualHeight + 1)
        {
            // Measure arranged the content at trial sizes: lay it out again at the window's own size.
            if (window.Content is UIElement content)
            {
                content.InvalidateMeasure();
                content.InvalidateArrange();
            }

            return;
        }

        var centerX = window.Left + (window.ActualWidth / 2);
        var centerY = window.Top + (window.ActualHeight / 2);
        window.Width = fit.Width;
        window.Height = fit.Height;
        window.Left = Math.Round(Math.Clamp(centerX - (fit.Width / 2), area.Left, Math.Max(area.Left, area.Right - fit.Width)));
        window.Top = Math.Round(Math.Clamp(centerY - (fit.Height / 2), area.Top, Math.Max(area.Top, area.Bottom - fit.Height)));
    }

    /// <summary>The window's normal bounds and state, to save.</summary>
    public static WindowPlacementSettings Capture(Window window)
    {
        // The set size, not the rendered one: rendered sizes are whole device pixels, and rounding them back to
        // DIPs would shrink the window a pixel at each start.
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, double.IsNaN(window.Width) ? window.ActualWidth : window.Width,
                double.IsNaN(window.Height) ? window.ActualHeight : window.Height)
            : window.RestoreBounds;
        return new WindowPlacementSettings
        {
            Left = Math.Round(bounds.Left),
            Top = Math.Round(bounds.Top),
            Width = Math.Round(bounds.Width),
            Height = Math.Round(bounds.Height),
            Maximized = window.WindowState == WindowState.Maximized,
            Scale = VisualTreeHelper.GetDpi(window).DpiScaleX,
        };
    }

    /// <summary>
    /// Restores a saved placement when most of it is still on a monitor (a monitor may be gone or changed).
    /// Returns false when the window should fit itself instead.
    /// </summary>
    public static bool TryRestore(Window window, WindowPlacementSettings? saved)
    {
        if (saved is not { Width: > 0, Height: > 0, Scale: > 0 })
        {
            return false;
        }

        var px = new RECT
        {
            Left = (int)Math.Round(saved.Left * saved.Scale),
            Top = (int)Math.Round(saved.Top * saved.Scale),
            Right = (int)Math.Round((saved.Left + saved.Width) * saved.Scale),
            Bottom = (int)Math.Round((saved.Top + saved.Height) * saved.Scale),
        };
        var monitor = MonitorInfo(MonitorFromRect(ref px, MonitorDefaultToNearest));
        var savedPx = new Rect(px.Left, px.Top, px.Right - px.Left, px.Bottom - px.Top);
        var visible = Rect.Intersect(savedPx, monitor.WorkAreaPx);
        if (visible.IsEmpty || visible.Width * visible.Height < 0.5 * savedPx.Width * savedPx.Height)
        {
            return false;
        }

        MoveOnto(window, monitor);
        var area = monitor.WorkArea;
        ApplyMinimums(window, area);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = Math.Min(Math.Max(window.MinWidth, savedPx.Width / monitor.Scale), area.Width);
        window.Height = Math.Min(Math.Max(window.MinHeight, savedPx.Height / monitor.Scale), area.Height);
        window.Left = Math.Clamp(savedPx.Left / monitor.Scale, area.Left, Math.Max(area.Left, area.Right - window.Width));
        window.Top = Math.Clamp(savedPx.Top / monitor.Scale, area.Top, Math.Max(area.Top, area.Bottom - window.Height));
        if (saved.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }

        return true;
    }

    /// <summary>
    /// Makes a hidden element and its hidden ancestors visible while it is measured, then puts their visibility back
    /// (a bound visibility is read again from its binding). Nothing renders in between.
    /// </summary>
    private sealed class ShownForMeasure : IDisposable
    {
        private readonly List<(UIElement Element, Visibility Was)> _changed = [];

        public ShownForMeasure(DependencyObject element)
        {
            for (var node = element; node is not null and not Window; node = VisualTreeHelper.GetParent(node))
            {
                if (node is UIElement { Visibility: not Visibility.Visible } hidden)
                {
                    _changed.Add((hidden, hidden.Visibility));
                    hidden.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
                }
            }
        }

        public void Dispose()
        {
            foreach (var (element, was) in _changed)
            {
                if (BindingOperations.GetBindingExpressionBase(element, UIElement.VisibilityProperty) is { } binding)
                {
                    binding.UpdateTarget();
                }
                else
                {
                    element.SetCurrentValue(UIElement.VisibilityProperty, was);
                }
            }
        }
    }

    /// <summary>True when a scroll area of the shown deck has more content than room (after a layout pass).</summary>
    public static bool DeckScrolls(FrameworkElement deck) =>
        ScrollViewers(deck).Any(sv => sv.ActualHeight > 0 && sv.ExtentHeight - sv.ViewportHeight > 0.5);

    private static bool ShowsWithoutScroll(Window window, FrameworkElement deck, double width, double height)
    {
        // The window content fills the whole window (custom chrome), so measure it at the window size.
        if (window.Content is not UIElement root)
        {
            return true;
        }

        // Measure and arrange only (no UpdateLayout: a shown window would lay itself out again at its real size).
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        return !ScrollViewers(deck).Any(Overflows);
    }

    /// <summary>
    /// True when a scroll area's content is taller than the room it has. Read from the presenter, not from
    /// <see cref="ScrollViewer.ExtentHeight"/>, which only refreshes after a full layout pass.
    /// </summary>
    private static bool Overflows(ScrollViewer sv)
    {
        if (sv.RenderSize.Height <= 0 || FindPresenter(sv) is not { } presenter)
        {
            return false;
        }

        return presenter.Content is UIElement content && content.DesiredSize.Height > presenter.RenderSize.Height + 0.5;
    }

    private static ScrollContentPresenter? FindPresenter(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollContentPresenter presenter)
            {
                return presenter;
            }

            if (child is not ScrollViewer && FindPresenter(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<ScrollViewer> ScrollViewers(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { Visibility: not Visibility.Visible })
            {
                continue;
            }

            if (child is ScrollViewer { VerticalScrollBarVisibility: not ScrollBarVisibility.Disabled } sv)
            {
                yield return sv;
            }

            foreach (var nested in ScrollViewers(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Moves the hidden window onto the monitor first, so WPF uses that monitor's DPI for the size and position
    /// in DIPs that follow.
    /// </summary>
    private static void MoveOnto(Window window, Monitor monitor)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, (int)monitor.WorkAreaPx.Left, (int)monitor.WorkAreaPx.Top, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private static Monitor MonitorInfo(IntPtr monitor)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var work = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        return new Monitor(work, DpiScale(monitor));
    }

    /// <summary>The scale WPF uses for this monitor: its own DPI when the app is per-monitor aware, else the system DPI.</summary>
    private static double DpiScale(IntPtr monitor)
    {
        try
        {
            var awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
            if (awareness == 2 && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0)
            {
                return dpiX / 96.0;
            }

            return awareness == 0 ? 1.0 : GetDpiForSystem() / 96.0;
        }
        catch (Exception)
        {
            return 1.0;
        }
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
