using System.Drawing;
using System.Windows;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
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

        var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/denon_icon.ico"))?.Stream;

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = iconStream is not null ? new Icon(iconStream) : SystemIcons.Application,
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
            BackColor = Color.FromArgb(18, 22, 31),
            ForeColor = Color.FromArgb(241, 245, 249),
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9.5f),
        };

        _statusItem = CreateItem("Receiver: connecting...", enabled: false);
        var openItem = CreateItem("Open Control Deck", onClick: async () => await ShowWindowAsync());
        var refreshItem = CreateItem("Refresh Telemetry", onClick: () => ExecuteTrayActionAsync("Refresh", token => _receiverController.RefreshAsync(token)));
        _powerItem = CreateItem("Power On / Standby", onClick: () => ExecuteTrayActionAsync("Toggle power", token => _receiverController.TogglePowerAsync(token)));

        var inputsMenu = CreateItem("Switch Input");
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

        var stationMenu = CreateItem("Play Station");
        foreach (var station in _settingsStore.Current.Receiver.Stations)
        {
            var capturedStation = station.Clone();
            stationMenu.DropDownItems.Add(
                CreateItem(capturedStation.DisplayName, onClick: () =>
                    ExecuteTrayActionAsync(
                        $"Play {capturedStation.DisplayName}",
                        token => _receiverController.PlayPresetAsync(capturedStation, useLowVolume: true, unmute: true, token))));
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

    private static Forms.ToolStripMenuItem CreateItem(string text, bool enabled = true, Func<Task>? onClick = null)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            Enabled = enabled,
            ForeColor = enabled ? Color.FromArgb(241, 245, 249) : Color.FromArgb(148, 163, 184),
            BackColor = Color.FromArgb(18, 22, 31),
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
        public DarkTrayMenuRenderer() : base(new DarkColorTable())
        {
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? (e.Item.Selected ? Color.FromArgb(0, 212, 255) : Color.FromArgb(241, 245, 249))
                : Color.FromArgb(148, 163, 184);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(148, 163, 184);
            base.OnRenderArrow(e);
        }

        private sealed class DarkColorTable : Forms.ProfessionalColorTable
        {
            private static readonly Color Bg = Color.FromArgb(18, 22, 31);
            private static readonly Color Border = Color.FromArgb(38, 46, 64);
            private static readonly Color Hover = Color.FromArgb(20, 42, 58);

            public override Color ToolStripDropDownBackground => Bg;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Color.FromArgb(0, 140, 180);
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
        }
    }
}
