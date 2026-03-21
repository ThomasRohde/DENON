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
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly Action<ReceiverStateSnapshot> _stateChangedHandler;
    private readonly Action<Denon.App.Core.Models.AppSettings> _settingsChangedHandler;
    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _window;
    private Func<Task>? _exitAsync;
    private Forms.ToolStripMenuItem? _statusItem;
    private Forms.ToolStripMenuItem? _muteItem;

    public TrayIconController(
        ReceiverController receiverController,
        ReceiverStateStore stateStore,
        AppSettingsStore settingsStore,
        AppLogStore logStore)
    {
        _receiverController = receiverController;
        _stateStore = stateStore;
        _settingsStore = settingsStore;
        _logStore = logStore;
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
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Denon Desktop",
        };
        _notifyIcon.DoubleClick += async (_, _) => await ShowWindowAsync();

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

    public void Dispose()
    {
        _stateStore.StateChanged -= _stateChangedHandler;
        _settingsStore.SettingsChanged -= _settingsChangedHandler;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
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

        var menu = new Forms.ContextMenuStrip();
        _statusItem = new Forms.ToolStripMenuItem("Receiver: unavailable") { Enabled = false };
        var openItem = new Forms.ToolStripMenuItem("Open", null, async (_, _) => await ShowWindowAsync());
        var refreshItem = new Forms.ToolStripMenuItem("Refresh", null, async (_, _) =>
            await ExecuteTrayActionAsync("Refresh", token => _receiverController.RefreshAsync(token)));
        var playItem = new Forms.ToolStripMenuItem("Play", null, async (_, _) =>
            await ExecuteTrayActionAsync("Play", token => _receiverController.SetPlayStateAsync("play", token)));
        var pauseItem = new Forms.ToolStripMenuItem("Pause", null, async (_, _) =>
            await ExecuteTrayActionAsync("Pause", token => _receiverController.SetPlayStateAsync("pause", token)));
        var stopItem = new Forms.ToolStripMenuItem("Stop", null, async (_, _) =>
            await ExecuteTrayActionAsync("Stop", token => _receiverController.SetPlayStateAsync("stop", token)));
        _muteItem = new Forms.ToolStripMenuItem("Mute", null, async (_, _) =>
            await ExecuteTrayActionAsync("Toggle mute", token => _receiverController.ToggleMuteAsync(token)));
        var lowVolumeItem = new Forms.ToolStripMenuItem("Low Volume", null, async (_, _) =>
            await ExecuteTrayActionAsync("Low volume", token => _receiverController.SetLowVolumeAsync(token)));

        var stationMenu = new Forms.ToolStripMenuItem("Stations");
        foreach (var station in _settingsStore.Current.Receiver.Stations)
        {
            var capturedStation = station.Clone();
            stationMenu.DropDownItems.Add(
                new Forms.ToolStripMenuItem(capturedStation.DisplayName, null, async (_, _) =>
                    await ExecuteTrayActionAsync(
                        $"Play {capturedStation.DisplayName}",
                        token => _receiverController.PlayPresetAsync(capturedStation, useLowVolume: true, unmute: true, token))));
        }

        var exitItem = new Forms.ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync());

        menu.Items.AddRange(
        [
            _statusItem,
            new Forms.ToolStripSeparator(),
            openItem,
            refreshItem,
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

    private void UpdateFromSnapshot(ReceiverStateSnapshot snapshot)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var statusText = snapshot.IsConnected && snapshot.Status is not null
            ? $"{snapshot.Status.Player.Name} | {(snapshot.Status.IsPoweredOn && snapshot.Status.IsMainZoneOn ? snapshot.Status.PlayState : "standby")} | Vol {snapshot.Status.Volume}"
            : "Receiver unavailable";

        if (_statusItem is not null)
        {
            _statusItem.Text = statusText;
        }

        if (_muteItem is not null)
        {
            _muteItem.Text = snapshot.Status?.IsMuted == true ? "Unmute" : "Mute";
        }

        _notifyIcon.Text = Truncate(statusText, 63);
    }

    private async Task ShowWindowAsync()
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
}
