using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Denon.App.PluginSdk;
using Denon.Plugin.Chromecast.Cast;

namespace Denon.Plugin.Chromecast;

public sealed class ChromecastPlugin : IDenonPlugin, IPluginViewProvider
{
    private const string EventStream = "media";
    private const int MaxImageBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, CastDeviceClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _idleTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _automatedSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _configGate = new(1, 1);

    private IPluginContext? _context;
    private IPluginReceiverControl? _receiverControl;
    private ChromecastConfig _config = new();
    private CancellationTokenSource? _lifetime;
    private Task? _discoveryLoop;
    private Task? _tickLoop;
    private CastWatchLog? _watchLog;
    private bool _warnedNoControl;

    public PluginManifest Manifest { get; } = new(
        Id: "chromecast",
        Name: "Chromecast & Google Cast",
        Version: typeof(ChromecastPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", // From <Version> in the .csproj.
        Author: "Thomas Klok Rohde",
        Description: "Finds Chromecast and Google Cast devices on the LAN (mDNS), shows what they play, " +
                     "controls playback and volume over Cast v2 (TLS :8009), and drives the Denon receiver " +
                     "when a Cast session starts or ends (power, input, sound mode, Smart Scene, idle power-off).",
        Category: "Streaming Sources",
        AccentHex: "#F59E0B",
        IconGlyph: "CAST",
        Capabilities: PluginCapabilities.DashboardUi
                      | PluginCapabilities.ReceiverEvents
                      | PluginCapabilities.SceneAutomation
                      | PluginCapabilities.Actions
                      | PluginCapabilities.LocalNetwork
                      | PluginCapabilities.ReceiverControl);

    internal event Action? DevicesChanged;

    internal event Action<string, CastDeviceState>? DeviceStateChanged;

    internal bool AutomationsEnabled => _config.AutomationsEnabled;

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _receiverControl = context.ReceiverControl;
        _config = await context.Storage.LoadConfigAsync(static () => new ChromecastConfig(), cancellationToken);
        _config.Devices ??= [];
        _watchLog = new CastWatchLog((name, data) => RecordRaw(name, data, "external"));
        context.ReportStatus($"{_config.Devices.Count} device(s) configured.", PluginHealthState.Idle);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _lifetime?.Cancel();
        _lifetime = new CancellationTokenSource();
        List<CastDeviceConfig> devices;
        lock (_gate)
        {
            devices = _config.Devices.Where(d => d.Enabled).ToList();
        }

        foreach (var device in devices)
        {
            StartClient(device);
        }

        var token = _lifetime.Token;
        _tickLoop = Task.Run(() => TickLoopAsync(token));
        if (_config.AutoDiscover)
        {
            _discoveryLoop = Task.Run(() => DiscoveryLoopAsync(token));
        }

        ReportAggregateStatus();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _watchLog?.EndAll(DateTimeOffset.UtcNow, StopReason());
        _lifetime?.Cancel();
        foreach (var timer in _idleTimers.Values)
        {
            timer.Cancel();
        }

        _idleTimers.Clear();
        List<CastDeviceClient> clients;
        lock (_gate)
        {
            clients = _clients.Values.ToList();
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            await client.DisposeAsync();
        }

        foreach (var loop in new[] { _discoveryLoop, _tickLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch
            {
            }
        }

        _context?.ReportStatus("Stopped.", PluginHealthState.Idle);
        DevicesChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifetime?.Dispose();
    }

    // Parameters and hints give MCP clients a real input schema and safety hints.
    private static readonly PluginActionParameter DeviceParam = new(
        "device",
        "string",
        "Cast device name, id or IP (for example \"Stuen 2\"). Optional: default is the device that plays, " +
        "then the device with an active app, then the only device.");

    private static readonly PluginActionParameter[] DeviceOnly = [DeviceParam];

    /// <summary>"app_shutdown" when Denon Desktop exits, else "plugin_stopped" (the plugin was stopped or disabled).</summary>
    private string StopReason() => _context?.IsAppShuttingDown == true ? "app_shutdown" : "plugin_stopped";

    public IReadOnlyList<PluginActionDescriptor> GetActions() =>
    [
        new("list-devices", "List Cast devices", "Returns all known Cast devices and their live state as JSON.", "Cast")
            { Parameters = [], IsReadOnly = true },
        new("discover", "Discover Cast devices", "Runs mDNS discovery now and adds new devices.", "Cast")
            { Parameters = [], IsReadOnly = false, IsDestructive = false },
        new("add-device", "Add Cast device by IP", "Adds a device by LAN address.", "Cast")
        {
            Parameters =
            [
                new("host", "string", "Private LAN IPv4 address of the Cast device, for example 192.168.1.20.", Required: true),
                new("name", "string", "Display name. Optional: default is the name the device reports."),
            ],
            IsDestructive = false,
        },
        new("now-playing", "Now playing", "App, title, artist, player state, volume and artwork (image) of a Cast device.", "Cast")
        {
            Parameters = [DeviceParam, new("includeImage", "boolean", "Return the artwork as an image. Default true.")],
            IsReadOnly = true,
        },
        new("play", "Play", "Resumes playback.", "Playback") { Parameters = DeviceOnly, IsDestructive = false },
        new("pause", "Pause", "Pauses playback.", "Playback") { Parameters = DeviceOnly, IsDestructive = false },
        new("toggle-play", "Play / Pause", "Toggles playback.", "Playback") { Parameters = DeviceOnly, IsDestructive = false },
        new("stop", "Stop media", "Stops the media session (the program closes; it cannot be resumed).", "Playback")
            { Parameters = DeviceOnly, IsDestructive = true },
        new("quit-app", "Quit app", "Closes the running Cast app.", "Playback") { Parameters = DeviceOnly, IsDestructive = true },
        new("pause-all", "Pause all Cast devices", "Pauses every Cast device that plays.", "Playback") { Parameters = [], IsDestructive = false },
        new("set-volume", "Set Cast volume", "Sets the Cast device volume.", "Volume")
        {
            Parameters = [new("level", "integer", "Volume 0-100.", Required: true), DeviceParam],
            IsDestructive = false,
        },
        new("volume-up", "Cast volume up", "Raises the volume by 5.", "Volume") { Parameters = DeviceOnly, IsDestructive = false },
        new("volume-down", "Cast volume down", "Lowers the volume by 5.", "Volume") { Parameters = DeviceOnly, IsDestructive = false },
        new("mute", "Mute Cast device", "Mutes the Cast device.", "Volume") { Parameters = DeviceOnly, IsDestructive = false },
        new("unmute", "Unmute Cast device", "Unmutes the Cast device.", "Volume") { Parameters = DeviceOnly, IsDestructive = false },
    ];

    public async Task<PluginActionResult> ExecuteActionAsync(
        string actionId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            switch (actionId)
            {
                case "list-devices":
                    return PluginActionResult.OkWithData(
                        $"{GetDeviceViews().Count} Cast device(s).",
                        JsonSerializer.Serialize(GetDeviceViews().Select(v => ToDto(v.Config, v.State)), JsonOptions));
                case "discover":
                    var added = await DiscoverNowAsync(cancellationToken);
                    return PluginActionResult.Ok($"Discovery finished. {added} new device(s).");
                case "add-device":
                    var host = Param(parameters, "host") ?? throw new ArgumentException("Parameter 'host' is required.");
                    var config = await AddManualDeviceAsync(host, Param(parameters, "name"), cancellationToken);
                    return PluginActionResult.Ok($"Added {config.Name} ({config.Host}).");
                case "pause-all":
                    var paused = 0;
                    foreach (var c in SnapshotClients().Where(c => c.State.IsPlaying))
                    {
                        await c.PauseAsync(cancellationToken);
                        paused++;
                    }

                    return PluginActionResult.Ok($"Paused {paused} device(s).");
            }

            var (client, device) = ResolveDevice(parameters);
            switch (actionId)
            {
                case "now-playing":
                    return await NowPlayingAsync(client, device, parameters, cancellationToken);
                case "play":
                    await client.PlayAsync(cancellationToken);
                    break;
                case "pause":
                    await client.PauseAsync(cancellationToken);
                    break;
                case "toggle-play":
                    await client.TogglePlayAsync(cancellationToken);
                    break;
                case "stop":
                    await client.StopMediaAsync(cancellationToken);
                    break;
                case "quit-app":
                    await client.QuitAppAsync(cancellationToken);
                    break;
                case "set-volume":
                    var levelText = Param(parameters, "level") ?? throw new ArgumentException("Parameter 'level' (0-100) is required.");
                    if (!double.TryParse(levelText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var level))
                    {
                        throw new ArgumentException("Parameter 'level' must be a number from 0 to 100.");
                    }

                    await client.SetVolumeAsync(level / 100.0, cancellationToken);
                    break;
                case "volume-up":
                    await client.SetVolumeAsync(client.State.VolumeLevel + 0.05, cancellationToken);
                    break;
                case "volume-down":
                    await client.SetVolumeAsync(client.State.VolumeLevel - 0.05, cancellationToken);
                    break;
                case "mute":
                    await client.SetMutedAsync(true, cancellationToken);
                    break;
                case "unmute":
                    await client.SetMutedAsync(false, cancellationToken);
                    break;
                default:
                    return PluginActionResult.Fail($"Unknown action '{actionId}'.");
            }

            return PluginActionResult.Ok($"{actionId} sent to {device.Name}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PluginActionResult.Fail(ex.Message);
        }
    }

    public async Task OnReceiverEventAsync(PluginReceiverEvent receiverEvent, CancellationToken cancellationToken = default)
    {
        if (receiverEvent.EventType != PluginReceiverEventType.PowerChanged || receiverEvent.Snapshot.IsPoweredOn)
        {
            return;
        }

        foreach (var (config, state) in GetDeviceViews())
        {
            if (!config.Automation.PauseWhenReceiverPowersOff || !state.IsPlaying || !_config.AutomationsEnabled)
            {
                continue;
            }

            var client = GetClient(config.Id);
            if (client is null)
            {
                continue;
            }

            try
            {
                await client.PauseAsync(cancellationToken);
                _context?.Logger.Info($"Paused {config.Name} because the receiver powered off.");
                RecordEvent("automation.paused_on_receiver_off", config, state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _context?.Logger.Warning($"Could not pause {config.Name}: {ex.Message}");
            }
        }
    }

    public object? CreateDashboardView() => new ChromecastDashboardView(this);

    // ----- API for the dashboard view -----

    internal IReadOnlyList<(CastDeviceConfig Config, CastDeviceState State)> GetDeviceViews()
    {
        lock (_gate)
        {
            return _config.Devices
                .Select(d => (d, _clients.TryGetValue(d.Id, out var c) ? c.State : new CastDeviceState(d.Id)))
                .ToList();
        }
    }

    internal IReadOnlyList<PluginSceneInfo> GetReceiverScenes()
    {
        try
        {
            return _receiverControl?.GetScenes() ?? [];
        }
        catch
        {
            return [];
        }
    }

    internal async Task RunDeviceCommandAsync(string deviceId, Func<CastDeviceClient, Task> command)
    {
        var client = GetClient(deviceId) ?? throw new InvalidOperationException("The device is not active.");
        await command(client);
    }

    internal async Task<int> DiscoverNowAsync(CancellationToken cancellationToken = default)
    {
        var found = await MdnsCastDiscovery.DiscoverAsync(TimeSpan.FromSeconds(4), cancellationToken);
        var added = 0;
        var restart = new List<CastDeviceConfig>();
        await _configGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                foreach (var device in found)
                {
                    var existing = _config.Devices.FirstOrDefault(d => d.Id.Equals(device.Id, StringComparison.OrdinalIgnoreCase))
                                   ?? _config.Devices.FirstOrDefault(d => d.Host == device.Host && d.Source == "manual");
                    if (existing is null)
                    {
                        var config = new CastDeviceConfig
                        {
                            Id = device.Id,
                            Name = device.Name,
                            Host = device.Host,
                            Port = device.Port,
                            Model = device.Model,
                            Source = "mdns",
                        };
                        _config.Devices.Add(config);
                        restart.Add(config);
                        added++;
                        continue;
                    }

                    var moved = existing.Host != device.Host || existing.Port != device.Port;
                    existing.Id = device.Id;
                    existing.Name = device.Name;
                    existing.Model = device.Model;
                    existing.Host = device.Host;
                    existing.Port = device.Port;
                    existing.Source = "mdns";
                    if (moved)
                    {
                        restart.Add(existing);
                    }
                }
            }

            if (found.Count > 0)
            {
                await SaveConfigCoreAsync(cancellationToken);
            }
        }
        finally
        {
            _configGate.Release();
        }

        foreach (var config in restart.Where(c => c.Enabled))
        {
            await RestartClientAsync(config);
        }

        _context?.Logger.Info($"mDNS discovery: {found.Count} device(s) answered, {added} new.");
        DevicesChanged?.Invoke();
        ReportAggregateStatus();
        return added;
    }

    internal async Task<CastDeviceConfig> AddManualDeviceAsync(string host, string? name, CancellationToken cancellationToken = default)
    {
        host = host.Trim();
        if (!IPAddress.TryParse(host, out var address) || !CastChannel.IsPrivateLanAddress(address))
        {
            throw new ArgumentException("Give a private LAN IPv4 address, for example 192.168.1.20.");
        }

        CastDeviceConfig config;
        await _configGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (_config.Devices.Any(d => d.Host == host))
                {
                    throw new InvalidOperationException($"{host} is already in the list.");
                }

                config = new CastDeviceConfig
                {
                    Id = $"ip-{host}",
                    Name = string.IsNullOrWhiteSpace(name) ? $"Cast {host}" : name.Trim(),
                    Host = host,
                    Source = "manual",
                };
                _config.Devices.Add(config);
            }

            await SaveConfigCoreAsync(cancellationToken);
        }
        finally
        {
            _configGate.Release();
        }

        if (_lifetime is { IsCancellationRequested: false })
        {
            StartClient(config);
        }

        DevicesChanged?.Invoke();
        return config;
    }

    internal async Task SaveDeviceSettingsAsync(string deviceId, bool enabled, CastAutomationConfig automation)
    {
        CastDeviceConfig? config;
        await _configGate.WaitAsync();
        try
        {
            lock (_gate)
            {
                config = _config.Devices.FirstOrDefault(d => d.Id == deviceId);
                if (config is null)
                {
                    return;
                }

                config.Enabled = enabled;
                config.Automation = automation;
            }

            await SaveConfigCoreAsync(CancellationToken.None);
        }
        finally
        {
            _configGate.Release();
        }

        if (!enabled)
        {
            await StopClientAsync(deviceId);
        }
        else if (GetClient(deviceId) is null && _lifetime is { IsCancellationRequested: false })
        {
            StartClient(config);
        }

        DevicesChanged?.Invoke();
    }

    internal async Task RemoveDeviceAsync(string deviceId)
    {
        await StopClientAsync(deviceId);
        await _configGate.WaitAsync();
        try
        {
            lock (_gate)
            {
                _config.Devices.RemoveAll(d => d.Id == deviceId);
            }

            await SaveConfigCoreAsync(CancellationToken.None);
        }
        finally
        {
            _configGate.Release();
        }

        DevicesChanged?.Invoke();
    }

    internal async Task SetAutomationsEnabledAsync(bool enabled)
    {
        await _configGate.WaitAsync();
        try
        {
            _config.AutomationsEnabled = enabled;
            await SaveConfigCoreAsync(CancellationToken.None);
        }
        finally
        {
            _configGate.Release();
        }
    }

    // ----- internals -----

    private void StartClient(CastDeviceConfig config)
    {
        CastDeviceClient client;
        lock (_gate)
        {
            if (_clients.ContainsKey(config.Id))
            {
                return;
            }

            client = new CastDeviceClient(config.Id, config.Host, config.Port);
            _clients[config.Id] = client;
        }

        client.StateChanged += (previous, current) => OnClientStateChanged(config.Id, previous, current);
        client.Diagnostic += message => _context?.Logger.Info($"[{config.Name}] {message}");
        client.Start();
    }

    private async Task StopClientAsync(string deviceId)
    {
        CastDeviceClient? client;
        lock (_gate)
        {
            _clients.Remove(deviceId, out client);
        }

        if (_idleTimers.TryRemove(deviceId, out var timer))
        {
            timer.Cancel();
        }

        _watchLog?.EndAll(DateTimeOffset.UtcNow, "device_disabled", deviceId);
        if (client is not null)
        {
            await client.DisposeAsync();
        }
    }

    private async Task RestartClientAsync(CastDeviceConfig config)
    {
        await StopClientAsync(config.Id);
        if (_lifetime is { IsCancellationRequested: false })
        {
            StartClient(config);
        }
    }

    private CastDeviceClient? GetClient(string deviceId)
    {
        lock (_gate)
        {
            return _clients.GetValueOrDefault(deviceId);
        }
    }

    private List<CastDeviceClient> SnapshotClients()
    {
        lock (_gate)
        {
            return _clients.Values.ToList();
        }
    }

    private CastDeviceConfig? FindConfig(string deviceId)
    {
        lock (_gate)
        {
            return _config.Devices.FirstOrDefault(d => d.Id == deviceId);
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await DiscoverNowAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _context?.Logger.Warning($"mDNS discovery failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _config.DiscoveryIntervalMinutes)), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnClientStateChanged(string deviceId, CastDeviceState previous, CastDeviceState current)
    {
        DeviceStateChanged?.Invoke(deviceId, current);
        var config = FindConfig(deviceId);
        if (config is null)
        {
            return;
        }

        if (_context is not null && GetClient(deviceId) is not null)
        {
            _watchLog?.Update(config, current, _context.Receiver.CurrentSnapshot, DateTimeOffset.UtcNow);
        }

        if (previous.Connection != current.Connection &&
            current.Connection != CastConnectionState.Connecting)
        {
            RecordEvent(current.Connection == CastConnectionState.Connected ? "cast.connected" : "cast.disconnected", config, current, origin: "external");
            ReportAggregateStatus();
        }

        // Session transitions only while connected. A reconnect must not look like a new session.
        var wasConnected = previous.Connection == CastConnectionState.Connected;
        var isConnected = current.Connection == CastConnectionState.Connected;
        if (wasConnected && isConnected)
        {
            var sessionStarted = current.HasActiveApp && (!previous.HasActiveApp || previous.SessionId != current.SessionId);
            var sessionEnded = previous.HasActiveApp && !current.HasActiveApp;

            if (sessionStarted)
            {
                RecordEvent("cast.app_started", config, current, origin: "external");
                _ = Task.Run(() => OnSessionStartedAsync(config, current));
            }

            if (sessionEnded)
            {
                OnSessionEnded(config, previous);
            }
        }

        if (previous.PlayerState != current.PlayerState && !string.IsNullOrEmpty(current.PlayerState))
        {
            RecordEvent("cast.playback_changed", config, current, origin: "external");
        }

        // The status text carries "what plays" for the plugin card and the host's now-playing hero.
        if (previous.HasActiveApp != current.HasActiveApp || previous.AppName != current.AppName ||
            previous.Title != current.Title || previous.IsPlaying != current.IsPlaying)
        {
            ReportAggregateStatus();
        }
    }

    private async Task OnSessionStartedAsync(CastDeviceConfig config, CastDeviceState state)
    {
        if (_idleTimers.TryRemove(config.Id, out var timer))
        {
            timer.Cancel();
        }

        var automation = config.Automation;
        if (!_config.AutomationsEnabled || !automation.HasStartActions ||
            automation.IgnoredApps.Contains(state.AppName ?? "", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var control = _receiverControl;
        if (control is null || _context is null)
        {
            if (!_warnedNoControl)
            {
                _warnedNoControl = true;
                _context?.Logger.Warning("Automations need receiver control, but the host does not provide it.");
            }

            return;
        }

        if (!_automatedSessions.TryAdd($"{config.Id}:{state.SessionId}", 0))
        {
            return;
        }

        var ct = _lifetime?.Token ?? CancellationToken.None;
        var steps = new List<string>();
        try
        {
            var snapshot = _context.Receiver.CurrentSnapshot;
            if (!snapshot.IsPoweredOn)
            {
                if (!automation.PowerOnReceiver)
                {
                    _context.Logger.Info($"{config.Name} started {state.AppName}, but the receiver is off and power-on is not enabled.");
                    return;
                }

                snapshot = await control.SetPowerAsync(true, ct);
                steps.Add("power on");
                await Task.Delay(TimeSpan.FromSeconds(2.5), ct); // The AVR ignores commands while it starts.
            }

            if (!string.IsNullOrWhiteSpace(automation.SceneOnStart))
            {
                snapshot = await control.RunSceneAsync(automation.SceneOnStart, ct);
                steps.Add($"scene {automation.SceneOnStart}");
            }

            if (!string.IsNullOrWhiteSpace(automation.ReceiverInput) &&
                !string.Equals(snapshot.InputSource, automation.ReceiverInput, StringComparison.OrdinalIgnoreCase))
            {
                snapshot = await control.SetInputSourceAsync(automation.ReceiverInput, ct);
                steps.Add($"input {automation.ReceiverInput}");
            }

            if (!string.IsNullOrWhiteSpace(automation.SoundMode) &&
                !string.Equals(snapshot.SoundMode, automation.SoundMode, StringComparison.OrdinalIgnoreCase))
            {
                await control.SetSoundModeAsync(automation.SoundMode, ct);
                steps.Add($"sound mode {automation.SoundMode}");
            }

            if (steps.Count > 0)
            {
                _context.Logger.Info($"{config.Name} started {state.AppName}: {string.Join(", ", steps)}.");
                RecordEvent("automation.session_start", config, state, new Dictionary<string, object?> { ["steps"] = steps });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _context.Logger.Warning($"Automation for {config.Name} failed after [{string.Join(", ", steps)}]: {ex.Message}");
        }
    }

    private void OnSessionEnded(CastDeviceConfig config, CastDeviceState previous)
    {
        foreach (var key in _automatedSessions.Keys.Where(k => k.StartsWith(config.Id + ":", StringComparison.OrdinalIgnoreCase)))
        {
            _automatedSessions.TryRemove(key, out _);
        }

        var minutes = config.Automation.PowerOffAfterIdleMinutes;
        if (!_config.AutomationsEnabled || minutes <= 0 || _receiverControl is null)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime?.Token ?? CancellationToken.None);
        if (_idleTimers.TryRemove(config.Id, out var old))
        {
            old.Cancel();
        }

        _idleTimers[config.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), cts.Token);
                var client = GetClient(config.Id);
                var snapshot = _context!.Receiver.CurrentSnapshot;
                var input = config.Automation.ReceiverInput;
                if (client is null || client.State.HasActiveApp || !snapshot.IsPoweredOn)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(input) && !string.Equals(snapshot.InputSource, input, StringComparison.OrdinalIgnoreCase))
                {
                    return; // Somebody uses another source now.
                }

                await _receiverControl.SetPowerAsync(false, cts.Token);
                _context.Logger.Info($"{config.Name} was idle for {minutes} min. Receiver powered off.");
                RecordEvent("automation.idle_power_off", config, previous);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _context?.Logger.Warning($"Idle power-off for {config.Name} failed: {ex.Message}");
            }
            finally
            {
                _idleTimers.TryRemove(new KeyValuePair<string, CancellationTokenSource>(config.Id, cts));
                cts.Dispose();
            }
        });
    }

    private (CastDeviceClient Client, CastDeviceConfig Config) ResolveDevice(IReadOnlyDictionary<string, string>? parameters)
    {
        var key = Param(parameters, "device");
        var views = GetDeviceViews().Where(v => GetClient(v.Config.Id) is not null).ToList();
        if (views.Count == 0)
        {
            throw new InvalidOperationException("No Cast device is active. Run 'discover' or 'add-device'.");
        }

        (CastDeviceConfig Config, CastDeviceState State)? match;
        if (key is null)
        {
            match = views.FirstOrDefault(v => v.State.IsPlaying);
            if (match?.Config is null)
            {
                match = views.FirstOrDefault(v => v.State.HasActiveApp);
            }

            if (match?.Config is null)
            {
                match = views.Count == 1
                    ? views[0]
                    : throw new ArgumentException("Several Cast devices are known. Give the 'device' parameter (name, id or IP).");
            }
        }
        else
        {
            match = views.FirstOrDefault(v =>
                        v.Config.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                        v.Config.Id.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                        v.Config.Host == key);
            if (match?.Config is null)
            {
                match = views.FirstOrDefault(v => v.Config.Name.Contains(key, StringComparison.OrdinalIgnoreCase));
            }

            if (match?.Config is null)
            {
                throw new ArgumentException($"No Cast device matches '{key}'.");
            }
        }

        var config = match.Value.Config;
        return (GetClient(config.Id)!, config);
    }

    private async Task<PluginActionResult> NowPlayingAsync(
        CastDeviceClient client,
        CastDeviceConfig config,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct)
    {
        var state = client.State;
        var json = JsonSerializer.Serialize(ToDto(config, state), JsonOptions);
        var message = !state.HasActiveApp
            ? $"{config.Name}: idle."
            : $"{config.Name}: {state.AppName}" +
              (string.IsNullOrEmpty(state.Title) ? "" : $" — {state.Title}") +
              (string.IsNullOrEmpty(state.Subtitle) ? "" : $" / {state.Subtitle}") +
              (string.IsNullOrEmpty(state.PlayerState) ? "" : $" ({state.PlayerState.ToLowerInvariant()})");

        var includeImage = !string.Equals(Param(parameters, "includeImage"), "false", StringComparison.OrdinalIgnoreCase);
        if (!includeImage || _context is null ||
            !Uri.TryCreate(state.ImageUrl, UriKind.Absolute, out var imageUri) ||
            imageUri.Scheme is not ("http" or "https"))
        {
            return PluginActionResult.OkWithData(message, json);
        }

        try
        {
            using var http = _context.CreateHttpClient(timeout: TimeSpan.FromSeconds(5));
            using var response = await http.GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxImageBytes)
            {
                return PluginActionResult.OkWithData(message, json);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxImageBytes)
            {
                return PluginActionResult.OkWithData(message, json);
            }

            var mime = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return PluginActionResult.OkWithData(message, json, Convert.ToBase64String(bytes), mime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PluginActionResult.OkWithData(message, json);
        }
    }

    private void RecordEvent(
        string name,
        CastDeviceConfig config,
        CastDeviceState state,
        Dictionary<string, object?>? extra = null,
        string origin = "plugin")
    {
        var data = new Dictionary<string, object?>
        {
            ["castDevice"] = config.Name,
            ["deviceId"] = config.Id,
            ["app"] = state.AppName,
            ["playerState"] = state.PlayerState,
            ["title"] = state.Title,
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                data[key] = value;
            }
        }

        RecordRaw(name, data, origin);
    }

    private void RecordRaw(string name, Dictionary<string, object?> data, string origin)
    {
        try
        {
            _context?.Events.RecordEvent(name, data, EventStream, origin);
        }
        catch
        {
        }
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_context is { } context)
                {
                    _watchLog?.Tick(_ => context.Receiver.CurrentSnapshot, DateTimeOffset.UtcNow);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static string FormatCastStatus(string deviceName, CastDeviceState state)
    {
        static string Clean(string? s) => (s ?? string.Empty).Replace(" • ", " - ").Trim();

        var app = Clean(state.AppName);
        var title = Clean(state.Title);
        var parts = new List<string> { state.IsPlaying ? "CASTING" : "CAST PAUSED", Clean(deviceName), app.Length > 0 ? app : "Cast app" };
        if (title.Length > 0 && !title.Equals(app, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(title);
        }

        return string.Join(" • ", parts);
    }

    private void ReportAggregateStatus()
    {
        if (_context is null)
        {
            return;
        }

        var views = GetDeviceViews();
        var enabled = views.Count(v => v.Config.Enabled);
        var connected = views.Count(v => v.State.Connection == CastConnectionState.Connected);

        // Something plays (or is paused): report it. Format read by the host (NowPlayingResolver), keep it stable:
        // "CASTING • <device> • <app>[ • <title>]" or "CAST PAUSED • <device> • <app>[ • <title>]".
        var active = views
            .Where(v => v.Config.Enabled && v.State.HasActiveApp)
            .OrderByDescending(v => v.State.IsPlaying)
            .FirstOrDefault();
        if (active.Config is not null)
        {
            _context.ReportStatus(FormatCastStatus(active.Config.Name, active.State), PluginHealthState.Connected);
            return;
        }

        if (enabled == 0)
        {
            _context.ReportStatus("No Cast devices yet. Waiting for discovery.", PluginHealthState.Idle);
        }
        else if (connected == enabled)
        {
            _context.ReportStatus($"{connected} Cast device(s) connected.", PluginHealthState.Connected);
        }
        else if (connected > 0)
        {
            _context.ReportStatus($"{connected} of {enabled} Cast devices connected.", PluginHealthState.Warning);
        }
        else
        {
            _context.ReportStatus($"Cannot reach {enabled} Cast device(s).", PluginHealthState.Warning);
        }
    }

    private Task SaveConfigCoreAsync(CancellationToken ct) =>
        _context?.Storage.SaveConfigAsync(_config, ct) ?? Task.CompletedTask;

    private static string? Param(IReadOnlyDictionary<string, string>? parameters, string key) =>
        parameters is not null && parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static object ToDto(CastDeviceConfig config, CastDeviceState state) => new
    {
        id = config.Id,
        name = config.Name,
        host = config.Host,
        model = config.Model,
        enabled = config.Enabled,
        connection = state.Connection.ToString(),
        app = state.AppName,
        isIdleScreen = state.IsIdleScreen,
        playerState = state.PlayerState,
        title = state.Title,
        subtitle = state.Subtitle,
        imageUrl = state.ImageUrl,
        currentTime = state.CurrentTime,
        duration = state.Duration,
        volume = Math.Round(state.VolumeLevel * 100),
        muted = state.IsMuted,
        error = state.Error,
    };
}
