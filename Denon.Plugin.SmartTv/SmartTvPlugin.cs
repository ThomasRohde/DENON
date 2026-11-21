using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Denon.App.PluginSdk;

namespace Denon.Plugin.SmartTv;

public sealed class SmartTvPlugin : IDenonPlugin, IPluginViewProvider
{
    private readonly LgWebOsClient _client = new();
    private IPluginContext? _context;
    private SmartTvConfig _config = new();
    private SmartTvViewModel? _viewModel;
    private bool _isStarted;

    public PluginManifest Manifest { get; } = new(
        Id: "smart-tv",
        Name: "Smart TV Control (LG webOS)",
        Version: "1.3.0",
        Author: "Denon Desktop",
        Description: "Control LG webOS Smart TVs over LAN (SSAP WebSocket :3000) & Wake-on-LAN: power, HDMI inputs, streaming apps, on-screen toast alerts, and Denon Smart Scene coordination.",
        Category: "Home Theater TV",
        AccentHex: "#38BDF8",
        IconGlyph: "TV",
        Capabilities: PluginCapabilities.DashboardUi
                      | PluginCapabilities.ReceiverEvents
                      | PluginCapabilities.SceneAutomation
                      | PluginCapabilities.Actions
                      | PluginCapabilities.LocalNetwork);

    internal SmartTvConfig Config => _config;

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _config = await context.Storage.LoadConfigAsync(static () => new SmartTvConfig(), cancellationToken);
        _viewModel = new SmartTvViewModel(this);
        _viewModel.SyncFromConfig(_config);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _isStarted = true;
        UpdateHealthStatus();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _isStarted = false;
        _context?.ReportStatus("Stopped", PluginHealthState.Disabled);
        return Task.CompletedTask;
    }

    public object? CreateDashboardView()
    {
        _viewModel ??= new SmartTvViewModel(this);
        _viewModel.SyncFromConfig(_config);
        return new SmartTvDashboardView { DataContext = _viewModel };
    }

    public IReadOnlyList<PluginActionDescriptor> GetActions() =>
    [
        new("tv.power_on", "TV Wake-on-LAN (Power On)", "Send a Wake-on-LAN magic packet to power on the TV.", "Power"),
        new("tv.power_off", "TV Power Off", "Put the LG webOS TV into standby.", "Power"),
        new("tv.hdmi1", "TV → HDMI 1", "Switch TV input to HDMI 1.", "Inputs"),
        new("tv.hdmi2", "TV → HDMI 2 (eARC)", "Switch TV input to HDMI 2.", "Inputs"),
        new("tv.hdmi3", "TV → HDMI 3", "Switch TV input to HDMI 3.", "Inputs"),
        new("tv.youtube", "TV → Launch YouTube", "Open YouTube app on the Smart TV.", "Apps"),
        new("tv.plex", "TV → Launch Plex", "Open Plex app on the Smart TV.", "Apps"),
        new("tv.mute", "TV Mute Audio", "Mute internal TV speakers (great when using Denon AVR).", "Audio"),
        new("tv.toast", "TV Test Toast Alert", "Display a Denon Desktop notification banner on the TV screen.", "OSD"),
    ];

    public async Task<PluginActionResult> ExecuteActionAsync(
        string actionId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isStarted)
        {
            return PluginActionResult.Fail("Smart TV plugin is disabled.");
        }

        if (!_config.DemoMode && string.IsNullOrWhiteSpace(_config.TvIp) && !actionId.Equals("tv.power_on", StringComparison.OrdinalIgnoreCase))
        {
            return PluginActionResult.Fail("Configure your TV IP address or enable Demo Mode in the Smart TV plugin.");
        }

        try
        {
            switch (actionId.ToLowerInvariant())
            {
                case "tv.power_on":
                    if (!_config.DemoMode)
                    {
                        await _client.SendWakeOnLanAsync(_config.MacAddress, cancellationToken);
                    }
                    return ReportAction("Sent Wake-on-LAN magic packet to TV.");

                case "tv.power_off":
                    await _client.SendCommandAsync(_config, "ssap://system/turnOff", null, cancellationToken);
                    return ReportAction("Sent Power Off command to TV.");

                case "tv.hdmi1":
                    await _client.SendCommandAsync(_config, "ssap://system.launcher/launch", new { id = "com.webos.app.hdmi1" }, cancellationToken);
                    return ReportAction("Switched TV to HDMI 1.");

                case "tv.hdmi2":
                    await _client.SendCommandAsync(_config, "ssap://system.launcher/launch", new { id = "com.webos.app.hdmi2" }, cancellationToken);
                    return ReportAction("Switched TV to HDMI 2.");

                case "tv.hdmi3":
                    await _client.SendCommandAsync(_config, "ssap://system.launcher/launch", new { id = "com.webos.app.hdmi3" }, cancellationToken);
                    return ReportAction("Switched TV to HDMI 3.");

                case "tv.youtube":
                    await _client.SendCommandAsync(_config, "ssap://system.launcher/launch", new { id = "youtube.leanback.v4" }, cancellationToken);
                    return ReportAction("Launched YouTube on TV.");

                case "tv.plex":
                    await _client.SendCommandAsync(_config, "ssap://system.launcher/launch", new { id = "cdp-30" }, cancellationToken);
                    return ReportAction("Launched Plex on TV.");

                case "tv.mute":
                    await _client.SendCommandAsync(_config, "ssap://audio/setMute", new { mute = true }, cancellationToken);
                    return ReportAction("Muted internal TV speakers.");

                case "tv.toast":
                    var text = parameters is not null && parameters.TryGetValue("message", out var msg) && !string.IsNullOrWhiteSpace(msg)
                        ? msg
                        : "Denon Desktop connected to Smart TV";
                    await _client.SendCommandAsync(_config, "ssap://system.notifications/createToast", new { message = text }, cancellationToken);
                    return ReportAction($"Displayed TV toast: \"{text}\".");

                default:
                    return PluginActionResult.Fail($"Unknown Smart TV action '{actionId}'.");
            }
        }
        catch (Exception ex)
        {
            return PluginActionResult.Fail(ex.Message);
        }
    }

    public async Task OnReceiverEventAsync(PluginReceiverEvent receiverEvent, CancellationToken cancellationToken = default)
    {
        if (!_isStarted || (!_config.DemoMode && string.IsNullOrWhiteSpace(_config.TvIp)))
        {
            return;
        }

        try
        {
            if (receiverEvent.EventType == PluginReceiverEventType.PowerChanged)
            {
                if (receiverEvent.Snapshot.IsPoweredOn && _config.WakeWithDenonPowerOn && !string.IsNullOrWhiteSpace(_config.MacAddress))
                {
                    await ExecuteActionAsync("tv.power_on", null, cancellationToken);
                }
                else if (!receiverEvent.Snapshot.IsPoweredOn && _config.TurnOffWithDenonStandby)
                {
                    await ExecuteActionAsync("tv.power_off", null, cancellationToken);
                }
            }
            else if (receiverEvent.EventType == PluginReceiverEventType.SceneActivated && _config.ShowSceneToastOnTv)
            {
                await ExecuteActionAsync("tv.toast", new Dictionary<string, string>
                {
                    ["message"] = $"Denon AVR • Scene: {receiverEvent.SceneName ?? receiverEvent.SceneId}",
                }, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _context?.Logger.Warning($"Smart TV automation warning: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal async Task<string> PairTvAsync(string tvIp)
    {
        var key = await _client.PairAsync(tvIp, _config.ClientKey);
        _config.TvIp = tvIp.Trim();
        _config.ClientKey = key;
        _config.DemoMode = false;
        await SaveConfigAsync();
        UpdateHealthStatus();
        return "Paired with LG webOS TV! Client key saved.";
    }

    internal async Task SaveConfigAsync(SmartTvConfig? updated = null)
    {
        if (updated is not null)
        {
            _config = updated;
        }

        if (_context is not null)
        {
            await _context.Storage.SaveConfigAsync(_config);
        }

        UpdateHealthStatus();
    }

    private PluginActionResult ReportAction(string message)
    {
        _context?.Logger.Info(message);
        _viewModel?.SetLastActivity(message);
        return PluginActionResult.Ok(message);
    }

    private void UpdateHealthStatus()
    {
        if (_config.DemoMode)
        {
            _context?.ReportStatus($"Demo TV ({_config.TvName}) ready", PluginHealthState.Connected);
        }
        else if (!string.IsNullOrWhiteSpace(_config.TvIp) && !string.IsNullOrWhiteSpace(_config.ClientKey))
        {
            _context?.ReportStatus($"Paired • {_config.TvName} ({_config.TvIp})", PluginHealthState.Connected);
        }
        else if (!string.IsNullOrWhiteSpace(_config.TvIp))
        {
            _context?.ReportStatus($"TV IP set ({_config.TvIp}) — click Pair", PluginHealthState.Warning);
        }
        else
        {
            _context?.ReportStatus("Enter TV IP & MAC (or try Demo Mode)", PluginHealthState.Idle);
        }
    }
}

public sealed class SmartTvViewModel : INotifyPropertyChanged
{
    private readonly SmartTvPlugin _plugin;
    private string _tvName = "Living Room OLED TV";
    private string _tvIp = string.Empty;
    private string _macAddress = string.Empty;
    private bool _demoMode;
    private bool _wakeWithDenon;
    private bool _offWithStandby;
    private bool _showSceneToast = true;
    private string _statusMessage = "Enter your LG webOS TV IP & click Pair (accept prompt on TV), or enable Demo Mode.";

    public SmartTvViewModel(SmartTvPlugin plugin)
    {
        _plugin = plugin;
        PairCommand = new SimpleAsyncCmd(_ => PairAsync());
        SaveCommand = new SimpleAsyncCmd(_ => SaveAsync());
        ToggleDemoCommand = new SimpleAsyncCmd(_ => ToggleDemoAsync());
        RunActionCommand = new SimpleAsyncCmd(p => p is string act ? RunActionAsync(act) : Task.CompletedTask);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand PairCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ToggleDemoCommand { get; }
    public ICommand RunActionCommand { get; }

    public string TvName { get => _tvName; set => Set(ref _tvName, value); }
    public string TvIp { get => _tvIp; set => Set(ref _tvIp, value); }
    public string MacAddress { get => _macAddress; set => Set(ref _macAddress, value); }
    public bool DemoMode
    {
        get => _demoMode;
        private set
        {
            if (Set(ref _demoMode, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DemoButtonLabel)));
            }
        }
    }

    public string DemoButtonLabel => DemoMode ? "Exit Demo Mode" : "Try Demo Mode";
    public bool WakeWithDenon { get => _wakeWithDenon; set => Set(ref _wakeWithDenon, value); }
    public bool OffWithStandby { get => _offWithStandby; set => Set(ref _offWithStandby, value); }
    public bool ShowSceneToast { get => _showSceneToast; set => Set(ref _showSceneToast, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public void SyncFromConfig(SmartTvConfig cfg)
    {
        TvName = cfg.TvName;
        TvIp = cfg.TvIp;
        MacAddress = cfg.MacAddress;
        DemoMode = cfg.DemoMode;
        WakeWithDenon = cfg.WakeWithDenonPowerOn;
        OffWithStandby = cfg.TurnOffWithDenonStandby;
        ShowSceneToast = cfg.ShowSceneToastOnTv;
    }

    public void SetLastActivity(string msg) =>
        Application.Current?.Dispatcher.InvokeAsync(() => StatusMessage = $"{DateTime.Now:HH:mm:ss} • {msg}");

    private async Task PairAsync()
    {
        if (string.IsNullOrWhiteSpace(TvIp))
        {
            StatusMessage = "Enter your LG webOS TV IP address first.";
            return;
        }

        StatusMessage = $"Connecting to LG TV at {TvIp}... Accept the pairing prompt on your TV screen.";
        try
        {
            StatusMessage = await _plugin.PairTvAsync(TvIp);
            SyncFromConfig(_plugin.Config);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Pairing failed: {ex.Message}";
        }
    }

    private async Task SaveAsync()
    {
        await _plugin.SaveConfigAsync(new SmartTvConfig
        {
            TvName = TvName,
            TvIp = TvIp.Trim(),
            MacAddress = MacAddress.Trim(),
            ClientKey = _plugin.Config.ClientKey,
            DemoMode = DemoMode,
            WakeWithDenonPowerOn = WakeWithDenon,
            TurnOffWithDenonStandby = OffWithStandby,
            ShowSceneToastOnTv = ShowSceneToast,
        });
        StatusMessage = "Saved Smart TV settings.";
    }

    private async Task ToggleDemoAsync()
    {
        DemoMode = !DemoMode;
        await SaveAsync();
        StatusMessage = DemoMode ? "Smart TV Demo Simulator active — test inputs, power, and scene toasts!" : "Exited Demo Mode.";
    }

    private async Task RunActionAsync(string actionId)
    {
        var result = await _plugin.ExecuteActionAsync(actionId);
        StatusMessage = result.Success ? $"✓ {result.Message}" : $"⚠ {result.Message}";
    }

    private bool Set<T>(ref T field, T val, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, val)) return false;
        field = val;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        return true;
    }

    private sealed class SimpleAsyncCmd(Func<object?, Task> exec) : ICommand
    {
        private bool _busy;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? p) => !_busy;
        public async void Execute(object? p)
        {
            if (_busy) return;
            _busy = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try { await exec(p); }
            finally { _busy = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        }
    }
}
