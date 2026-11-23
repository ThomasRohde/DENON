using System.Drawing;
using System.Windows;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;
using Forms = System.Windows.Forms;

namespace Denon.App.Desktop.Services;

public sealed class TrayIconController : IDisposable
{
    private readonly ReceiverController _receiverController;
    private readonly ReceiverStateStore _stateStore;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppLogStore _logStore;
    private readonly TrayFlyoutWindow _flyoutWindow;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly Action<ReceiverStateSnapshot> _stateChangedHandler;
    private readonly Action<AppSettings> _settingsChangedHandler;
    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _window;
    private Func<Task>? _exitAsync;
    private Forms.ToolStripMenuItem? _statusItem;
    private Forms.ToolStripMenuItem? _powerItem;
    private Forms.ToolStripMenuItem? _muteItem;
    private Icon? _trayDefaultIcon;
    private Icon? _trayActiveIcon;
    private Icon? _trayMutedIcon;
    private Icon? _trayStandbyIcon;
    private string _currentIconState = string.Empty;

    public TrayIconController(
        ReceiverController receiverController,
        ReceiverStateStore stateStore,
        AppSettingsStore settingsStore,
        AppLogStore logStore,
        TrayFlyoutWindow flyoutWindow)
    {
        _receiverController = receiverController;
        _stateStore = stateStore;
        _settingsStore = settingsStore;
        _logStore = logStore;
        _flyoutWindow = flyoutWindow;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _stateChangedHandler = snapshot => _dispatcher.InvokeAsync(() => UpdateFromSnapshot(snapshot));
        _settingsChangedHandler = _ => _dispatcher.InvokeAsync(RebuildMenu);
        _stateStore.StateChanged += _stateChangedHandler;
        _settingsStore.SettingsChanged += _settingsChangedHandler;
    }

    public void Initialize(MainWindow window, Func<Task> exitAsync)
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        _window = window;
        _exitAsync = exitAsync;
        _flyoutWindow.Configure(() => _ = ShowWindowAsync());

        _trayDefaultIcon = LoadResourceIcon("Assets/Icons/denon_tray.ico")
                           ?? LoadResourceIcon("denon_icon.ico")
                           ?? SystemIcons.Application;
        _trayActiveIcon = LoadResourceIcon("Assets/Icons/variants/active.ico") ?? _trayDefaultIcon;
        _trayMutedIcon = LoadResourceIcon("Assets/Icons/variants/muted.ico") ?? _trayDefaultIcon;
        _trayStandbyIcon = LoadResourceIcon("Assets/Icons/variants/power.ico") ?? _trayDefaultIcon;

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayDefaultIcon,
            Visible = true,
            Text = "Denon Desktop",
        };

        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                _ = _dispatcher.InvokeAsync(() => _flyoutWindow.ToggleNearTray());
            }
        };

        _notifyIcon.DoubleClick += async (_, _) =>
        {
            await _dispatcher.InvokeAsync(() => _flyoutWindow.Hide());
            await ShowWindowAsync();
        };

        RebuildMenu();
        UpdateFromSnapshot(_stateStore.Snapshot);
    }

    public void ShowBalloon(string title, string message)
    {
        if (_notifyIcon is null || !_settingsStore.Current.Ui.ShowNotifications)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(2500);
    }

    public async Task ShowWindowAsync()
    {
        if (_window is null)
        {
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (!_window.IsVisible)
            {
                _window.Show();
            }

            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Focus();
        });
    }

    public void Dispose()
    {
        _stateStore.StateChanged -= _stateChangedHandler;
        _settingsStore.SettingsChanged -= _settingsChangedHandler;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        DisposeIconSafe(ref _trayActiveIcon, _trayDefaultIcon);
        DisposeIconSafe(ref _trayMutedIcon, _trayDefaultIcon);
        DisposeIconSafe(ref _trayStandbyIcon, _trayDefaultIcon);
        DisposeIconSafe(ref _trayDefaultIcon, null);
    }

    private void RebuildMenu()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkTrayMenuRenderer(),
            BackColor = ThemeResources.DrawingColor("Ink04"),
            ForeColor = ThemeResources.DrawingColor("TextPrimary"),
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9.5f),
        };

        _statusItem = CreateItem("Receiver: connecting...", enabled: false);
        var openItem = CreateItem("Open Control Deck", onClick: async () => await ShowWindowAsync());
        var refreshItem = CreateItem("Refresh Telemetry", onClick: () => ExecuteTrayActionAsync("Refresh", token => _receiverController.RefreshAsync(token)));
        _powerItem = CreateItem("Power On / Standby", onClick: () => ExecuteTrayActionAsync("Toggle power", token => _receiverController.TogglePowerAsync(token)));

        var inputsMenu = CreateItem("Switch Input");
        ConfigureSubMenu(inputsMenu);
        foreach (var input in DenonCatalog.InputSources)
        {
            var code = input.Code;
            var label = input.DisplayName;
            inputsMenu.DropDownItems.Add(CreateItem(label, onClick: () =>
                ExecuteTrayActionAsync($"Switch to {label}", token => _receiverController.SetInputSourceAsync(code, token))));
        }

        var playItem = CreateItem("Play", onClick: () => ExecuteTrayActionAsync("Play", token => _receiverController.SetPlayStateAsync("play", token)));
        var pauseItem = CreateItem("Pause", onClick: () => ExecuteTrayActionAsync("Pause", token => _receiverController.SetPlayStateAsync("pause", token)));
        var stopItem = CreateItem("Stop", onClick: () => ExecuteTrayActionAsync("Stop", token => _receiverController.SetPlayStateAsync("stop", token)));
        _muteItem = CreateItem("Mute", onClick: () => ExecuteTrayActionAsync("Toggle mute", token => _receiverController.ToggleMuteAsync(token)));
        var lowVolumeItem = CreateItem("Safety Low Volume", onClick: () => ExecuteTrayActionAsync("Low volume", token => _receiverController.SetLowVolumeAsync(token)));

        var scenesMenu = CreateItem("Activate Scene");
        ConfigureSubMenu(scenesMenu);
        foreach (var scene in _settingsStore.Current.Receiver.Scenes)
        {
            var capturedScene = scene.Clone();
            scenesMenu.DropDownItems.Add(
                CreateItem($"[{capturedScene.Badge}] {capturedScene.Name}", onClick: () =>
                    ExecuteTrayActionAsync(
                        $"Scene {capturedScene.Name}",
                        token => _receiverController.ApplySceneAsync(capturedScene, token))));
        }

        var stationMenu = CreateItem("Play Station");
        ConfigureSubMenu(stationMenu);
        foreach (var station in _settingsStore.Current.Receiver.Stations)
        {
            var capturedStation = station.Clone();
            stationMenu.DropDownItems.Add(
                CreateItem(capturedStation.DisplayName, onClick: () =>
                    ExecuteTrayActionAsync(
                        $"Play {capturedStation.DisplayName}",
                        token => _receiverController.PlayPresetAsync(capturedStation, useLowVolume: false, unmute: true, token))));
        }

        var exitItem = CreateItem("Exit Denon Desktop", onClick: ExitAsync);

        menu.Items.AddRange(
        [
            _statusItem,
            new Forms.ToolStripSeparator(),
            openItem,
            refreshItem,
            _powerItem,
            inputsMenu,
            scenesMenu,
            new Forms.ToolStripSeparator(),
            playItem,
            pauseItem,
            stopItem,
            _muteItem,
            lowVolumeItem,
            stationMenu,
            new Forms.ToolStripSeparator(),
            exitItem,
        ]);

        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.ContextMenuStrip = menu;
    }

    private static void ConfigureSubMenu(Forms.ToolStripMenuItem parent)
    {
        if (parent.DropDown is Forms.ToolStripDropDownMenu dropDown)
        {
            dropDown.ShowImageMargin = false;
            dropDown.BackColor = ThemeResources.DrawingColor("Ink04");
            dropDown.ForeColor = ThemeResources.DrawingColor("TextPrimary");
            dropDown.Renderer = new DarkTrayMenuRenderer();
        }
    }

    private static Forms.ToolStripMenuItem CreateItem(string text, bool enabled = true, Func<Task>? onClick = null)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            Enabled = enabled,
            ForeColor = ThemeResources.DrawingColor(enabled ? "TextPrimary" : "TextMuted"),
            BackColor = ThemeResources.DrawingColor("Ink04"),
            Padding = new Forms.Padding(6, 3, 6, 3),
        };

        if (onClick is not null)
        {
            item.Click += async (_, _) => await onClick();
        }

        return item;
    }

    private void UpdateFromSnapshot(ReceiverStateSnapshot snapshot)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var isAwake = snapshot.Status is { IsPoweredOn: true, IsMainZoneOn: true };
        var statusText = snapshot.IsConnected && snapshot.Status is not null
            ? $"{snapshot.Status.Player.Name} | {(isAwake ? DenonCatalog.GetFriendlyInputName(snapshot.Status.InputSource) : "Standby")} | {DenonVolumeMath.FormatDecibels(snapshot.Status.Volume)}"
            : "Denon Receiver: Offline";

        if (_statusItem is not null)
        {
            _statusItem.Text = statusText;
        }

        if (_powerItem is not null)
        {
            _powerItem.Text = isAwake ? "Put in Standby" : "Power On Receiver";
        }

        if (_muteItem is not null)
        {
            _muteItem.Text = snapshot.Status?.IsMuted == true ? "Unmute" : "Mute";
        }

        _notifyIcon.Text = Truncate(statusText, 63);

        // Swap live tray icon to reflect real-time AVR state (Muted / Playing / Standby / Idle)
        var nextState = !snapshot.IsConnected || !isAwake
            ? "standby"
            : snapshot.Status?.IsMuted == true
                ? "muted"
                : string.Equals(snapshot.Status?.PlayState, "play", StringComparison.OrdinalIgnoreCase)
                    ? "active"
                    : "default";

        if (!string.Equals(_currentIconState, nextState, StringComparison.Ordinal))
        {
            _currentIconState = nextState;
            var nextIcon = nextState switch
            {
                "muted" => _trayMutedIcon,
                "active" => _trayActiveIcon,
                "standby" => _trayStandbyIcon,
                _ => _trayDefaultIcon,
            };
            if (nextIcon is not null)
            {
                _notifyIcon.Icon = nextIcon;
            }
        }
    }

    private static Icon? LoadResourceIcon(string relativeResourcePath)
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/{relativeResourcePath}"))?.Stream;
            if (stream is null)
            {
                return null;
            }

            using (stream)
            {
                return new Icon(stream);
            }
        }
        catch
        {
            return null;
        }
    }

    private static void DisposeIconSafe(ref Icon? icon, Icon? sharedFallback)
    {
        if (icon is not null && !ReferenceEquals(icon, sharedFallback) && !ReferenceEquals(icon, SystemIcons.Application))
        {
            try { icon.Dispose(); } catch { }
        }

        icon = null;
    }

    private async Task ExitAsync()
    {
        if (_exitAsync is null)
        {
            return;
        }

        await _exitAsync();
    }

    private async Task ExecuteTrayActionAsync(string actionName, Func<CancellationToken, Task> action)
    {
        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logStore.Error($"{actionName} failed from the tray.", exception);
            ShowBalloon("Denon Desktop", $"{actionName} failed: {exception.Message}");
        }
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..Math.Max(0, length - 3)] + "...";

    private sealed class DarkTrayMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static Color BgColor => ThemeResources.DrawingColor("Ink03");
        private static Color HoverFillColor => ThemeResources.DrawingColor("AccentCyanDim");
        private static Color HoverBorderColor => ThemeResources.DrawingColor("AccentCyan");
        private static Color SeparatorColor => ThemeResources.DrawingColor("Ink11");
        private static Color NormalTextColor => ThemeResources.DrawingColor("TextStrong");
        private static Color HoverTextColor => ThemeResources.DrawingColor("TextPrimary");
        private static Color HeaderTextColor => ThemeResources.DrawingColor("TextMuted");

        public DarkTrayMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(BgColor);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            if (e.Item.Selected && e.Item.Enabled)
            {
                using var fill = new SolidBrush(HoverFillColor);
                using var border = new Pen(HoverBorderColor, 1f);
                e.Graphics.FillRectangle(fill, rect);
                e.Graphics.DrawRectangle(border, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
            else
            {
                using var fill = new SolidBrush(BgColor);
                e.Graphics.FillRectangle(fill, new Rectangle(System.Drawing.Point.Empty, e.Item.Size));
            }
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            var color = !e.Item.Enabled
                ? HeaderTextColor
                : (e.Item.Selected ? HoverTextColor : NormalTextColor);

            Forms.TextRenderer.DrawText(
                e.Graphics,
                e.Text,
                e.TextFont,
                e.TextRectangle,
                color,
                e.TextFormat);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(SeparatorColor, 1f);
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Selected == true ? HoverBorderColor : HeaderTextColor;
            base.OnRenderArrow(e);
        }

        private sealed class DarkColorTable : Forms.ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => BgColor;
            public override Color MenuBorder => SeparatorColor;
            public override Color MenuItemBorder => HoverBorderColor;
            public override Color MenuItemSelected => HoverFillColor;
            public override Color MenuItemSelectedGradientBegin => HoverFillColor;
            public override Color MenuItemSelectedGradientEnd => HoverFillColor;
            public override Color ImageMarginGradientBegin => BgColor;
            public override Color ImageMarginGradientMiddle => BgColor;
            public override Color ImageMarginGradientEnd => BgColor;
            public override Color SeparatorDark => SeparatorColor;
            public override Color SeparatorLight => SeparatorColor;
        }
    }
}
