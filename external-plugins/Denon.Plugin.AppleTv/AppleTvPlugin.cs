using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Denon.App.PluginSdk;
using Denon.Plugin.AppleTv.Discovery;
using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Hap;
using Denon.Plugin.AppleTv.Protocol.Mrp;

namespace Denon.Plugin.AppleTv;

public sealed class AppleTvPlugin : IDenonPlugin, IPluginViewProvider
{
    private const string EventStream = "media";
    private const string PairingName = "Denon Desktop";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly PluginActionParameter DeviceParam = new(
        "device",
        "string",
        "Apple TV name, id or IP (for example \"Stuen (2)\"). Optional: default is the Apple TV that plays, then the only paired one.");

    private static readonly PluginActionParameter[] DeviceOnly = [DeviceParam];

    private readonly object _gate = new();
    private readonly Dictionary<string, AppleTvDeviceClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CompanionPairing> _pairings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sleepTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _configGate = new(1, 1);

    private IPluginContext? _context;
    private IPluginReceiverControl? _receiverControl;
    private AppleTvConfig _config = new();
    private CancellationTokenSource? _lifetime;
    private Task? _discoveryLoop;
    private Task? _tickLoop;
    private AppleTvWatchLog? _watchLog;

    public PluginManifest Manifest { get; } = new(
        Id: "apple-tv",
        Name: "Apple TV",
        Version: typeof(AppleTvPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        Author: "Thomas Klok Rohde",
        Description: "Pairs with Apple TV (PIN), shows what it plays (app, title, play state) over the encrypted AirPlay " +
                     "remote-control channel, controls power, keys and apps over Companion, logs watch sessions, and " +
                     "drives the Denon receiver when the Apple TV wakes or sleeps. Native .NET, no Python.",
        Category: "Streaming Sources",
        AccentHex: "#A3A3A3",
        IconGlyph: "ATV",
        Capabilities: PluginCapabilities.DashboardUi
                      | PluginCapabilities.ReceiverEvents
                      | PluginCapabilities.SceneAutomation
                      | PluginCapabilities.Actions
                      | PluginCapabilities.LocalNetwork
                      | PluginCapabilities.ReceiverControl);

    internal event Action? DevicesChanged;

    internal event Action<string, AppleTvDeviceState>? DeviceStateChanged;

    internal bool AutomationsEnabled => _config.AutomationsEnabled;

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _receiverControl = context.ReceiverControl;
        _config = await context.Storage.LoadConfigAsync(static () => new AppleTvConfig(), cancellationToken);
        _config.Devices ??= [];
        _watchLog = new AppleTvWatchLog((name, data) => RecordRaw(name, data, "external"));
        context.ReportStatus($"{_config.Devices.Count} Apple TV(s) configured.", PluginHealthState.Idle);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _lifetime?.Cancel();
        _lifetime = new CancellationTokenSource();
        foreach (var device in SnapshotConfigs().Where(d => d.Enabled && d.IsPaired))
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
        _watchLog?.EndAll(DateTimeOffset.UtcNow, _context?.IsAppShuttingDown == true ? "app_shutdown" : "plugin_stopped");
        _lifetime?.Cancel();
        foreach (var timer in _sleepTimers.Values)
        {
            timer.Cancel();
        }

        _sleepTimers.Clear();
        foreach (var pairing in _pairings.Values)
        {
            await pairing.DisposeAsync();
        }

        _pairings.Clear();
        List<AppleTvDeviceClient> clients;
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
            if (loop is not null)
            {
                try
                {
                    await loop.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                }
                catch
                {
                }
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

    // ---------------------------------------------------------------- actions (MCP tools, Smart Scenes, hotkeys)

    public IReadOnlyList<PluginActionDescriptor> GetActions() =>
    [
        new("list-devices", "List Apple TVs", "All known Apple TVs with pairing, power and now-playing state as JSON.", "Apple TV")
            { Parameters = [], IsReadOnly = true },
        new("now-playing", "Now playing", "App, title, series/episode and play state of an Apple TV.", "Apple TV")
            { Parameters = DeviceOnly, IsReadOnly = true },
        new("list-apps", "List apps", "Launchable apps (bundle id and name).", "Apple TV")
            { Parameters = DeviceOnly, IsReadOnly = true },
        new("discover", "Discover Apple TVs", "Runs DNS-SD discovery now and adds new Apple TVs.", "Apple TV")
            { Parameters = [], IsReadOnly = false, IsDestructive = false },
        new("play", "Play", "Resumes playback.", "Playback") { Parameters = DeviceOnly, IsDestructive = false },
        new("pause", "Pause", "Pauses playback.", "Playback") { Parameters = DeviceOnly, IsDestructive = false },
        new("play-pause", "Play / Pause", "Presses the remote's play/pause button.", "Playback") { Parameters = DeviceOnly, IsDestructive = false },
        new("key", "Remote button", "Presses a remote button.", "Remote")
        {
            Parameters =
            [
                new("button", "string", "up, down, left, right, select, menu, home, playpause, volumeup, volumedown, …", Required: true),
                DeviceParam,
            ],
            IsDestructive = false,
        },
        new("launch-app", "Launch app", "Opens an app by bundle id (see list-apps) or a URL.", "Apple TV")
        {
            Parameters = [new("app", "string", "Bundle id, for example dk.dr.tv, or a URL.", Required: true), DeviceParam],
            IsDestructive = false,
        },
        new("power-on", "Wake Apple TV", "Wakes the Apple TV (the TV may turn on via HDMI-CEC).", "Power")
            { Parameters = DeviceOnly, IsDestructive = false },
        new("power-off", "Sleep Apple TV", "Puts the Apple TV to sleep (playback stops).", "Power")
            { Parameters = DeviceOnly, IsDestructive = true },
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
                    var views = GetDeviceViews();
                    return PluginActionResult.OkWithData($"{views.Count} Apple TV(s).", JsonSerializer.Serialize(views.Select(v => ToDto(v.Config, v.State)), JsonOptions));
                case "discover":
                    var added = await DiscoverNowAsync(cancellationToken);
                    return PluginActionResult.Ok($"Discovery finished. {added} new Apple TV(s).");
            }

            var (client, device) = ResolveDevice(parameters);
            switch (actionId)
            {
                case "now-playing":
                    var state = client.State;
                    return PluginActionResult.OkWithData(DescribeNowPlaying(device, state), JsonSerializer.Serialize(ToDto(device, state), JsonOptions));
                case "list-apps":
                    var apps = await client.FetchAppsAsync(cancellationToken);
                    return PluginActionResult.OkWithData(
                        $"{apps.Count} app(s) on {device.Name}.",
                        JsonSerializer.Serialize(apps.OrderBy(a => a.Value, StringComparer.OrdinalIgnoreCase).Select(a => new { id = a.Key, name = a.Value }), JsonOptions));
                case "play":
                    await client.PlayAsync(cancellationToken);
                    break;
                case "pause":
                    await client.PauseAsync(cancellationToken);
                    break;
                case "play-pause":
                    await client.PressAsync(HidCommand.PlayPause, cancellationToken);
                    break;
                case "key":
                    var button = Param(parameters, "button") ?? throw new ArgumentException("Parameter 'button' is required.");
                    if (!Enum.TryParse<HidCommand>(button.Replace("_", "").Replace("-", ""), ignoreCase: true, out var key))
                    {
                        throw new ArgumentException($"Unknown button '{button}'. Use: {string.Join(", ", Enum.GetNames<HidCommand>()).ToLowerInvariant()}.");
                    }

                    await client.PressAsync(key, cancellationToken);
                    break;
                case "launch-app":
                    await client.LaunchAppAsync(Param(parameters, "app") ?? throw new ArgumentException("Parameter 'app' is required."), cancellationToken);
                    break;
                case "power-on":
                    await client.SetPowerAsync(true, cancellationToken);
                    break;
                case "power-off":
                    await client.SetPowerAsync(false, cancellationToken);
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
        if (receiverEvent.EventType != PluginReceiverEventType.PowerChanged || receiverEvent.Snapshot.IsPoweredOn || !_config.AutomationsEnabled)
        {
            return;
        }

        foreach (var (config, state) in GetDeviceViews())
        {
            if (!config.Automation.PauseWhenReceiverPowersOff || !state.IsPlaying || GetClient(config.Id) is not { } client)
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

    public object? CreateDashboardView() => new AppleTvDashboardView(this);

    // ---------------------------------------------------------------- API for the dashboard

    internal IReadOnlyList<(AppleTvDeviceConfig Config, AppleTvDeviceState State)> GetDeviceViews()
    {
        lock (_gate)
        {
            return _config.Devices
                .Select(d => (d, _clients.TryGetValue(d.Id, out var c) ? c.State : new AppleTvDeviceState(d.Id)))
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

    internal bool IsPairing(string deviceId) => _pairings.ContainsKey(deviceId);

    /// <summary>Starts pairing: the Apple TV shows a PIN.</summary>
    internal async Task StartPairingAsync(string deviceId)
    {
        var config = FindConfig(deviceId) ?? throw new InvalidOperationException("Unknown Apple TV.");
        if (_pairings.TryRemove(deviceId, out var old))
        {
            await old.DisposeAsync();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _pairings[deviceId] = await CompanionPairing.StartAsync(config.Host, config.CompanionPort, PairingName, timeout.Token);
        DevicesChanged?.Invoke();
    }

    /// <summary>Finishes pairing with the PIN, stores the credentials and connects.</summary>
    internal async Task FinishPairingAsync(string deviceId, string pin)
    {
        if (!_pairings.TryRemove(deviceId, out var pairing))
        {
            throw new InvalidOperationException("Start pairing first.");
        }

        HapCredentials credentials;
        await using (pairing)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            credentials = await pairing.FinishAsync(pin.Trim(), timeout.Token);
        }

        AppleTvDeviceConfig? config;
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

                config.Credentials = credentials.ToString();
                config.Identity = CompanionIdentity.Create(PairingName);
                config.PairedUtc = DateTimeOffset.UtcNow;
                config.Enabled = true;
            }

            await SaveConfigCoreAsync(CancellationToken.None);
        }
        finally
        {
            _configGate.Release();
        }

        _context?.Logger.Info($"Paired with {config.Name} ({credentials.ToSafeString()}).");
        await RestartClientAsync(config);
        DevicesChanged?.Invoke();
    }

    internal async Task CancelPairingAsync(string deviceId)
    {
        if (_pairings.TryRemove(deviceId, out var pairing))
        {
            await pairing.DisposeAsync();
        }

        DevicesChanged?.Invoke();
    }

    internal async Task UnpairAsync(string deviceId)
    {
        await StopClientAsync(deviceId);
        await MutateConfigAsync(deviceId, c =>
        {
            c.Credentials = string.Empty;
            c.Identity = null;
            c.PairedUtc = null;
        });
        DevicesChanged?.Invoke();
        ReportAggregateStatus();
    }

    internal async Task RunDeviceCommandAsync(string deviceId, Func<AppleTvDeviceClient, Task> command)
    {
        var client = GetClient(deviceId) ?? throw new InvalidOperationException("The Apple TV is not connected (pair it first).");
        await command(client);
    }

    internal async Task SaveDeviceSettingsAsync(string deviceId, bool enabled, AppleTvAutomationConfig automation)
    {
        var config = await MutateConfigAsync(deviceId, c =>
        {
            c.Enabled = enabled;
            c.Automation = automation;
        });
        if (config is null)
        {
            return;
        }

        if (!enabled)
        {
            await StopClientAsync(deviceId);
        }
        else if (GetClient(deviceId) is null && config.IsPaired && _lifetime is { IsCancellationRequested: false })
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

    internal async Task<int> DiscoverNowAsync(CancellationToken cancellationToken = default)
    {
        var known = SnapshotConfigs().Select(c => IPAddress.TryParse(c.Host, out var ip) ? ip : null).OfType<IPAddress>().ToList();
        var found = (await AppleDeviceDiscovery.DiscoverAsync(TimeSpan.FromSeconds(4), known, cancellationToken))
            .Where(d => d.Kind == AppleDeviceKind.AppleTv && d.HasCompanion)
            .ToList();

        var added = 0;
        var restart = new List<AppleTvDeviceConfig>();
        await _configGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                foreach (var device in found)
                {
                    var id = device.DeviceId ?? device.Address.ToString();
                    var companionPort = device.Service(AppleServiceTypes.Companion)!.Port;
                    var airPlayPort = device.Service(AppleServiceTypes.AirPlay)?.Port is > 0 and var p ? p : 7000;
                    var existing = _config.Devices.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (existing is null)
                    {
                        _config.Devices.Add(new AppleTvDeviceConfig
                        {
                            Id = id,
                            Name = device.Name,
                            Host = device.Address.ToString(),
                            CompanionPort = companionPort,
                            AirPlayPort = airPlayPort,
                            Model = device.ModelName,
                            OsVersion = device.OsVersion ?? string.Empty,
                        });
                        added++;
                        continue;
                    }

                    var moved = existing.Host != device.Address.ToString() || existing.CompanionPort != companionPort || existing.AirPlayPort != airPlayPort;
                    existing.Name = device.Name;
                    existing.Host = device.Address.ToString();
                    existing.CompanionPort = companionPort;
                    existing.AirPlayPort = airPlayPort;
                    existing.Model = device.ModelName;
                    existing.OsVersion = device.OsVersion ?? existing.OsVersion;
                    if (moved && existing.IsPaired && existing.Enabled)
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

        foreach (var config in restart)
        {
            await RestartClientAsync(config);
        }

        _context?.Logger.Info($"Apple TV discovery: {found.Count} answered, {added} new.");
        DevicesChanged?.Invoke();
        ReportAggregateStatus();
        return added;
    }

    // ---------------------------------------------------------------- internals

    private void StartClient(AppleTvDeviceConfig config)
    {
        if (!config.IsPaired)
        {
            return;
        }

        AppleTvDeviceClient client;
        lock (_gate)
        {
            if (_clients.ContainsKey(config.Id))
            {
                return;
            }

            client = new AppleTvDeviceClient(config.Id, config.Host, config.CompanionPort, config.AirPlayPort,
                HapCredentials.Parse(config.Credentials), config.Identity!);
            _clients[config.Id] = client;
        }

        client.StateChanged += (previous, current) => OnClientStateChanged(config.Id, previous, current);
        client.Diagnostic += message => _context?.Logger.Info($"[{config.Name}] {message}");
        client.Start();
    }

    private async Task StopClientAsync(string deviceId)
    {
        AppleTvDeviceClient? client;
        lock (_gate)
        {
            _clients.Remove(deviceId, out client);
        }

        if (_sleepTimers.TryRemove(deviceId, out var timer))
        {
            timer.Cancel();
        }

        _watchLog?.EndAll(DateTimeOffset.UtcNow, "device_disabled", deviceId);
        if (client is not null)
        {
            await client.DisposeAsync();
        }
    }

    private async Task RestartClientAsync(AppleTvDeviceConfig config)
    {
        await StopClientAsync(config.Id);
        if (_lifetime is { IsCancellationRequested: false } && config.Enabled)
        {
            StartClient(config);
        }
    }

    private AppleTvDeviceClient? GetClient(string deviceId)
    {
        lock (_gate)
        {
            return _clients.GetValueOrDefault(deviceId);
        }
    }

    private AppleTvDeviceConfig? FindConfig(string deviceId)
    {
        lock (_gate)
        {
            return _config.Devices.FirstOrDefault(d => d.Id == deviceId);
        }
    }

    private List<AppleTvDeviceConfig> SnapshotConfigs()
    {
        lock (_gate)
        {
            return [.. _config.Devices];
        }
    }

    private async Task<AppleTvDeviceConfig?> MutateConfigAsync(string deviceId, Action<AppleTvDeviceConfig> change)
    {
        await _configGate.WaitAsync();
        try
        {
            AppleTvDeviceConfig? config;
            lock (_gate)
            {
                config = _config.Devices.FirstOrDefault(d => d.Id == deviceId);
                if (config is null)
                {
                    return null;
                }

                change(config);
            }

            await SaveConfigCoreAsync(CancellationToken.None);
            return config;
        }
        finally
        {
            _configGate.Release();
        }
    }

    private void OnClientStateChanged(string deviceId, AppleTvDeviceState previous, AppleTvDeviceState current)
    {
        DeviceStateChanged?.Invoke(deviceId, current);
        var config = FindConfig(deviceId);
        if (config is null || _context is null)
        {
            return;
        }

        if (GetClient(deviceId) is not null)
        {
            _watchLog?.Update(config, current, _context.Receiver.CurrentSnapshot, DateTimeOffset.UtcNow);
        }

        if (previous.Link != current.Link && current.Link != AppleTvLink.Connecting)
        {
            RecordEvent(current.Link == AppleTvLink.Connected ? "appletv.connected" : "appletv.disconnected", config, current, origin: "external");
        }

        // Power transitions only while connected: a reconnect must not look like a wake.
        if (previous.Link == AppleTvLink.Connected && current.Link == AppleTvLink.Connected && previous.Power != current.Power &&
            current.Power != AttentionState.Unknown && previous.Power != AttentionState.Unknown)
        {
            if (!previous.IsAwake && current.IsAwake)
            {
                RecordEvent("appletv.woke", config, current, origin: "external");
                _ = Task.Run(() => OnWokeAsync(config));
            }
            else if (previous.IsAwake && current.Power == AttentionState.Asleep)
            {
                RecordEvent("appletv.slept", config, current, origin: "external");
                OnSlept(config);
            }
        }

        if (previous.NowPlaying.State != current.NowPlaying.State && current.HasMedia)
        {
            RecordEvent("appletv.playback_changed", config, current, origin: "external");
        }

        if (previous.Link != current.Link || previous.Power != current.Power || previous.HasMedia != current.HasMedia ||
            previous.IsPlaying != current.IsPlaying || previous.NowPlaying.Title != current.NowPlaying.Title ||
            previous.AppName != current.AppName)
        {
            ReportAggregateStatus();
        }
    }

    private async Task OnWokeAsync(AppleTvDeviceConfig config)
    {
        if (_sleepTimers.TryRemove(config.Id, out var timer))
        {
            timer.Cancel();
        }

        var automation = config.Automation;
        if (!_config.AutomationsEnabled || !automation.HasWakeActions || _receiverControl is not { } control || _context is null)
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
                    return;
                }

                snapshot = await control.SetPowerAsync(true, ct);
                steps.Add("power on");
                await Task.Delay(TimeSpan.FromSeconds(2.5), ct); // The AVR ignores commands while it starts.
            }

            if (!string.IsNullOrWhiteSpace(automation.SceneOnWake))
            {
                snapshot = await control.RunSceneAsync(automation.SceneOnWake, ct);
                steps.Add($"scene {automation.SceneOnWake}");
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
                _context.Logger.Info($"{config.Name} woke: {string.Join(", ", steps)}.");
                RecordRaw("automation.appletv_wake", new Dictionary<string, object?> { ["appleTvDevice"] = config.Name, ["steps"] = steps }, "plugin");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _context.Logger.Warning($"Wake automation for {config.Name} failed after [{string.Join(", ", steps)}]: {ex.Message}");
        }
    }

    private void OnSlept(AppleTvDeviceConfig config)
    {
        var minutes = config.Automation.PowerOffAfterSleepMinutes;
        if (!_config.AutomationsEnabled || minutes <= 0 || _receiverControl is null || _context is null)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime?.Token ?? CancellationToken.None);
        if (_sleepTimers.TryRemove(config.Id, out var old))
        {
            old.Cancel();
        }

        _sleepTimers[config.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), cts.Token);
                var client = GetClient(config.Id);
                var snapshot = _context.Receiver.CurrentSnapshot;
                var input = config.Automation.ReceiverInput;
                if (client is null || client.State.IsAwake || !snapshot.IsPoweredOn)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(input) && !string.Equals(snapshot.InputSource, input, StringComparison.OrdinalIgnoreCase))
                {
                    return; // Somebody uses another source now.
                }

                await _receiverControl.SetPowerAsync(false, cts.Token);
                _context.Logger.Info($"{config.Name} slept for {minutes} min. Receiver powered off.");
                RecordRaw("automation.appletv_sleep_power_off", new Dictionary<string, object?> { ["appleTvDevice"] = config.Name, ["minutes"] = minutes }, "plugin");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _context?.Logger.Warning($"Power-off after sleep for {config.Name} failed: {ex.Message}");
            }
            finally
            {
                _sleepTimers.TryRemove(new KeyValuePair<string, CancellationTokenSource>(config.Id, cts));
                cts.Dispose();
            }
        });
    }

    private (AppleTvDeviceClient Client, AppleTvDeviceConfig Config) ResolveDevice(IReadOnlyDictionary<string, string>? parameters)
    {
        var key = Param(parameters, "device");
        var views = GetDeviceViews().Where(v => GetClient(v.Config.Id) is not null).ToList();
        if (views.Count == 0)
        {
            throw new InvalidOperationException("No Apple TV is connected. Pair one in the plugin dashboard.");
        }

        (AppleTvDeviceConfig Config, AppleTvDeviceState State) match;
        if (key is null)
        {
            match = views.FirstOrDefault(v => v.State.IsPlaying);
            if (match.Config is null)
            {
                match = views.Count == 1 ? views[0] : throw new ArgumentException("Several Apple TVs are connected. Give the 'device' parameter.");
            }
        }
        else
        {
            match = views.FirstOrDefault(v =>
                v.Config.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                v.Config.Id.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                v.Config.Host == key);
            if (match.Config is null)
            {
                match = views.FirstOrDefault(v => v.Config.Name.Contains(key, StringComparison.OrdinalIgnoreCase));
            }

            if (match.Config is null)
            {
                throw new ArgumentException($"No Apple TV matches '{key}'.");
            }
        }

        return (GetClient(match.Config.Id)!, match.Config);
    }

    private static string DescribeNowPlaying(AppleTvDeviceConfig config, AppleTvDeviceState state)
    {
        if (state.Link != AppleTvLink.Connected)
        {
            return $"{config.Name}: not connected.";
        }

        if (!state.IsAwake)
        {
            return $"{config.Name}: asleep.";
        }

        if (!state.HasMedia)
        {
            return $"{config.Name}: awake, nothing plays.";
        }

        var np = state.NowPlaying;
        return $"{config.Name}: {state.AppName ?? np.AppBundleId}" +
               (np.Title is null ? "" : $" — {np.Title}") +
               (np.Season is null ? "" : $" (S{np.Season}E{np.Episode})") +
               $" ({np.State.ToString().ToLowerInvariant()})";
    }

    private void RecordEvent(string name, AppleTvDeviceConfig config, AppleTvDeviceState state, string origin = "plugin") =>
        RecordRaw(name, new Dictionary<string, object?>
        {
            ["appleTvDevice"] = config.Name,
            ["deviceId"] = config.Id,
            ["power"] = state.Power.ToString(),
            ["app"] = state.AppName,
            ["appId"] = state.NowPlaying.AppBundleId,
            ["playbackState"] = state.NowPlaying.State.ToString(),
            ["title"] = state.NowPlaying.Title,
        }, origin);

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
                    _watchLog?.Tick(() => context.Receiver.CurrentSnapshot, DateTimeOffset.UtcNow);
                }
            }
        }
        catch (OperationCanceledException)
        {
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
                _context?.Logger.Warning($"Apple TV discovery failed: {ex.Message}");
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

    /// <summary>
    /// Status text read by the host's now-playing hero (keep the format stable):
    /// "APPLE TV • &lt;device&gt; • &lt;app&gt;[ • &lt;title&gt;]" or "APPLE TV PAUSED • …".
    /// </summary>
    internal static string FormatPlayingStatus(string deviceName, AppleTvDeviceState state)
    {
        static string Clean(string? s) => (s ?? string.Empty).Replace(" • ", " - ").Trim();

        var app = Clean(state.AppName ?? state.NowPlaying.AppBundleId);
        var title = Clean(state.NowPlaying.Title);
        var parts = new List<string> { state.IsPlaying ? "APPLE TV" : "APPLE TV PAUSED", Clean(deviceName), app.Length > 0 ? app : "Apple TV app" };
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
        var active = views.Where(v => v.Config.Enabled && v.State.HasMedia).OrderByDescending(v => v.State.IsPlaying).FirstOrDefault();
        if (active.Config is not null)
        {
            _context.ReportStatus(FormatPlayingStatus(active.Config.Name, active.State), PluginHealthState.Connected);
            return;
        }

        var paired = views.Count(v => v.Config.Enabled && v.Config.IsPaired);
        var connected = views.Where(v => v.State.Link == AppleTvLink.Connected).ToList();
        if (views.Count == 0)
        {
            _context.ReportStatus("No Apple TV yet. Waiting for discovery.", PluginHealthState.Idle);
        }
        else if (paired == 0)
        {
            _context.ReportStatus($"{views.Count} Apple TV(s) found. Pair one in the dashboard.", PluginHealthState.Idle);
        }
        else if (connected.Count == paired)
        {
            var power = connected.Count == 1 ? $" ({(connected[0].State.IsAwake ? "awake" : "asleep")})" : string.Empty;
            _context.ReportStatus($"{connected.Count} Apple TV connected{power}.", PluginHealthState.Connected);
        }
        else if (connected.Count > 0)
        {
            _context.ReportStatus($"{connected.Count} of {paired} Apple TVs connected.", PluginHealthState.Warning);
        }
        else
        {
            _context.ReportStatus($"Cannot reach {paired} Apple TV(s).", PluginHealthState.Warning);
        }
    }

    private Task SaveConfigCoreAsync(CancellationToken ct) => _context?.Storage.SaveConfigAsync(_config, ct) ?? Task.CompletedTask;

    private static string? Param(IReadOnlyDictionary<string, string>? parameters, string key) =>
        parameters is not null && parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static object ToDto(AppleTvDeviceConfig config, AppleTvDeviceState state) => new
    {
        id = config.Id,
        name = config.Name,
        host = config.Host,
        model = config.Model,
        osVersion = config.OsVersion,
        enabled = config.Enabled,
        paired = config.IsPaired,
        link = state.Link.ToString(),
        power = state.Power.ToString(),
        nowPlayingLinked = state.NowPlayingLinked,
        app = state.AppName,
        appId = state.NowPlaying.AppBundleId,
        playbackState = state.NowPlaying.State.ToString(),
        title = state.NowPlaying.Title,
        series = state.NowPlaying.SeriesName,
        season = state.NowPlaying.Season,
        episode = state.NowPlaying.Episode,
        artist = state.NowPlaying.Artist,
        durationSec = state.NowPlaying.DurationSeconds,
        elapsedSec = state.NowPlaying.ElapsedSeconds,
        imageUrl = state.NowPlaying.ArtworkUrl, // read by the host's now-playing hero
        error = state.Error,
    };
}
