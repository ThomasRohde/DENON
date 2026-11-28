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
    private int _lastDiscoveryAnswered = -1;
    private static readonly TimeSpan PlaybackHold = TimeSpan.FromSeconds(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _playbackHolds = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _lastRecordedPlayback = new(StringComparer.Ordinal);

    /// <summary>How long after a wake-tv launch a new default-receiver session counts as the wake's own.</summary>
    private static readonly TimeSpan WakePendingWindow = TimeSpan.FromSeconds(15);

    // Per device: a wake-tv launch in progress, and the session it started. Those sessions skip the receiver hooks.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _wakePendingUntil = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _wakeSessions = new(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="state"/> is the default-receiver session a wake-tv started: its session id, or a new
    /// one while the launch is pending. With <paramref name="claim"/>, a pending launch takes that session id.
    /// </summary>
    private bool IsWakeSession(string deviceId, CastDeviceState state, bool claim)
    {
        if (!string.Equals(state.AppId, CastDeviceClient.DefaultMediaReceiverAppId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_wakeSessions.TryGetValue(deviceId, out var known) && string.Equals(known, state.SessionId, StringComparison.Ordinal))
        {
            return true;
        }

        if (claim && state.SessionId is { } sessionId &&
            _wakePendingUntil.TryRemove(deviceId, out var until) && DateTimeOffset.UtcNow <= until)
        {
            _wakeSessions[deviceId] = sessionId;
            return true;
        }

        return false;
    }
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
        var previous = _lifetime;
        _lifetime = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
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

        // A held playback change must not write an event after the stop. Each hold's own task removes and disposes it.
        foreach (var hold in _playbackHolds.Values)
        {
            try
            {
                hold.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It had just finished.
            }
        }

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
        "then the device with an active app, then the only device. \"only\": the only device, never another.");

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
        new("quit-app", "Quit app", "Closes the running Cast app. With 'sessionId', only when that session still runs.", "Playback")
        {
            Parameters = [DeviceParam, new("sessionId", "string", "Optional: close only this session (for example the one wake-tv started).")],
            IsDestructive = true,
        },
        new("wake-tv", "Wake the TV (HDMI-CEC)", "Starts the Cast default receiver, so HDMI-CEC turns on the TV and selects this device's input. Does nothing when an app already runs. Data: launched, sessionId; close it with quit-app and that sessionId.", "Cast")
            { Parameters = DeviceOnly, IsDestructive = false },
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
                    var onlySession = Param(parameters, "sessionId");
                    // Data "closed" tells a caller (the TV wake) whether its session still owned the device.
                    var closed = await client.QuitAppAsync(onlySession, cancellationToken);
                    return PluginActionResult.OkWithData(
                        closed ? $"Closed the app on {device.Name}." : $"{device.Name}: that session no longer runs, so nothing closed.",
                        JsonSerializer.Serialize(new { closed }, JsonOptions));
                case "wake-tv":
                    if (client.State.HasActiveApp)
                    {
                        // A cast already runs: the device is the active source, so the TV is on or waking. It is
                        // the user's session: launched=false tells the caller not to close it.
                        return PluginActionResult.OkWithData(
                            $"{device.Name} already runs {client.State.AppName ?? "an app"}.",
                            JsonSerializer.Serialize(new { launched = false }, JsonOptions));
                    }

                    // The status push of the new session can come before the launch answer: mark the wake first.
                    _wakePendingUntil[device.Id] = DateTimeOffset.UtcNow + WakePendingWindow;
                    var wakeSession = await client.LaunchDefaultReceiverAsync(cancellationToken);
                    if (wakeSession is not null)
                    {
                        _wakeSessions[device.Id] = wakeSession;
                        _wakePendingUntil.TryRemove(device.Id, out _);
                    }

                    return PluginActionResult.OkWithData(
                        $"Started the default receiver on {device.Name}, so HDMI-CEC wakes the TV.",
                        JsonSerializer.Serialize(new { launched = true, sessionId = wakeSession }, JsonOptions));
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
        int added;
        var restart = new List<DiscoveryRestart>();
        await _configGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                added = MergeDiscovered(_config.Devices, found, restart);
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

        foreach (var (config, oldId) in restart)
        {
            if (config.Enabled)
            {
                await RestartClientAsync(config, oldId);
            }
            else
            {
                MoveDeviceState(oldId, config.Id);
            }
        }

        // Only a change is worth a line: discovery runs every few minutes and usually finds the same devices.
        if (added > 0 || restart.Count > 0 || found.Count != _lastDiscoveryAnswered)
        {
            _context?.Logger.Info($"mDNS discovery: {found.Count} device(s) answered, {added} new.");
        }

        _lastDiscoveryAnswered = found.Count;
        DevicesChanged?.Invoke();
        ReportAggregateStatus();
        return added;
    }

    /// <summary>A device whose client must (re)start after discovery. OldId is the id its client runs under now.</summary>
    internal sealed record DiscoveryRestart(CastDeviceConfig Config, string OldId);

    /// <summary>
    /// Merges an mDNS answer into the device list. A device known by id, or a manual device on the same host, gets
    /// the mDNS id and details. A device that is new, moved, or got another id goes into <paramref name="restart"/>
    /// with the id it had, so its client can stop under that id. Returns the number of new devices.
    /// </summary>
    internal static int MergeDiscovered(
        List<CastDeviceConfig> devices,
        IEnumerable<DiscoveredCastDevice> found,
        List<DiscoveryRestart> restart)
    {
        var added = 0;
        foreach (var device in found)
        {
            var existing = devices.FirstOrDefault(d => d.Id.Equals(device.Id, StringComparison.OrdinalIgnoreCase))
                           ?? devices.FirstOrDefault(d => d.Host == device.Host && d.Source == "manual");
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
                devices.Add(config);
                restart.Add(new DiscoveryRestart(config, config.Id));
                added++;
                continue;
            }

            var oldId = existing.Id;
            var moved = existing.Host != device.Host || existing.Port != device.Port;
            existing.Id = device.Id;
            existing.Name = device.Name;
            existing.Model = device.Model;
            existing.Host = device.Host;
            existing.Port = device.Port;
            existing.Source = "mdns";
            if (moved || !string.Equals(oldId, device.Id, StringComparison.Ordinal))
            {
                restart.Add(new DiscoveryRestart(existing, oldId));
            }
        }

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
        string id;
        lock (_gate)
        {
            id = config.Id;
            if (_clients.ContainsKey(id))
            {
                return;
            }

            client = new CastDeviceClient(id, config.Host, config.Port);
            _clients[id] = client;
        }

        // The id this client runs under: when discovery gives the device a new id, the old client's last events
        // must not land on the new id.
        client.StateChanged += (previous, current) => OnClientStateChanged(id, previous, current);
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

    /// <summary>Stops the client under <paramref name="oldId"/> (its watch session ends there), then starts it under the config's id.</summary>
    private async Task RestartClientAsync(CastDeviceConfig config, string oldId)
    {
        await StopClientAsync(oldId);
        MoveDeviceState(oldId, config.Id);
        if (_lifetime is { IsCancellationRequested: false })
        {
            StartClient(config);
        }
    }

    /// <summary>
    /// Moves what the plugin keeps per device to the device's new id (discovery re-keyed it). A wake-tv launch in
    /// progress and its session stay the wake's own, and a session that already ran its automation does not run it
    /// again. A held playback change is dropped: the new client records the state it connects with.
    /// </summary>
    private void MoveDeviceState(string oldId, string newId)
    {
        if (string.Equals(oldId, newId, StringComparison.Ordinal))
        {
            return;
        }

        MoveKey(_wakePendingUntil, oldId, newId);
        MoveKey(_wakeSessions, oldId, newId);
        MoveSessionKeys(_automatedSessions, oldId, newId);
        _lastRecordedPlayback.TryRemove(oldId, out _);
        if (_playbackHolds.TryGetValue(oldId, out var hold))
        {
            try
            {
                hold.Cancel(); // Its own task removes and disposes it.
            }
            catch (ObjectDisposedException)
            {
                // It had just finished.
            }
        }
    }

    /// <summary>Moves the value under <paramref name="oldId"/> to <paramref name="newId"/>, if there is one.</summary>
    internal static void MoveKey<T>(ConcurrentDictionary<string, T> map, string oldId, string newId)
    {
        if (map.TryRemove(oldId, out var value))
        {
            map[newId] = value;
        }
    }

    /// <summary>Renames the "deviceId:sessionId" keys of <paramref name="oldId"/> to <paramref name="newId"/>.</summary>
    internal static void MoveSessionKeys(ConcurrentDictionary<string, byte> map, string oldId, string newId)
    {
        var prefix = oldId + ":";
        foreach (var key in map.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (map.TryRemove(key, out var value))
            {
                map[newId + ":" + key[prefix.Length..]] = value;
            }
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
                // A wake-tv session only turns the TV on: no receiver automation (scene, input, sound mode).
                if (!IsWakeSession(deviceId, current, claim: true))
                {
                    _ = Task.Run(() => OnSessionStartedAsync(config, current));
                }
            }

            if (sessionEnded)
            {
                if (IsWakeSession(deviceId, previous, claim: false))
                {
                    _wakeSessions.TryRemove(deviceId, out _);
                }
                else
                {
                    OnSessionEnded(config, previous);
                }
            }
        }

        if (!string.IsNullOrEmpty(current.PlayerState) &&
            !string.Equals(PlaybackOf(previous.PlayerState), PlaybackOf(current.PlayerState), StringComparison.OrdinalIgnoreCase))
        {
            if (previous.Connection != CastConnectionState.Connected || !_lastRecordedPlayback.ContainsKey(deviceId))
            {
                // The first state after a connect (the app starting) is how it already was, not a change. The
                // media status can come a moment after the connection, so "nothing recorded yet" counts too.
                _lastRecordedPlayback[deviceId] = PlaybackOf(current.PlayerState);
            }
            else
            {
                HoldPlaybackChange(config, deviceId);
            }
        }

        // The status text carries "what plays" for the plugin card and the host's now-playing hero.
        if (previous.HasActiveApp != current.HasActiveApp || previous.AppName != current.AppName ||
            previous.Title != current.Title || previous.IsPlaying != current.IsPlaying)
        {
            ReportAggregateStatus();
        }
    }

    /// <summary>Buffering is part of playing: PLAYING → BUFFERING → PLAYING is no change.</summary>
    private static string? PlaybackOf(string? playerState) =>
        string.Equals(playerState, "BUFFERING", StringComparison.OrdinalIgnoreCase) ? "PLAYING" : playerState;

    /// <summary>
    /// Writes cast.playback_changed only when the new state holds for <see cref="PlaybackHold"/>, so a stream that
    /// stutters (PLAYING, BUFFERING, PLAYING within seconds) writes nothing.
    /// </summary>
    private void HoldPlaybackChange(CastDeviceConfig config, string deviceId)
    {
        var hold = new CancellationTokenSource();
        if (_playbackHolds.TryGetValue(deviceId, out var earlier))
        {
            try
            {
                earlier.Cancel(); // Its own task removes and disposes it.
            }
            catch (ObjectDisposedException)
            {
                // It had just finished.
            }
        }

        _playbackHolds[deviceId] = hold;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PlaybackHold, hold.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _playbackHolds.TryRemove(new KeyValuePair<string, CancellationTokenSource>(deviceId, hold));
                hold.Dispose();
            }

            if (GetClient(deviceId)?.State is not { } state || string.IsNullOrEmpty(state.PlayerState))
            {
                return;
            }

            var playback = PlaybackOf(state.PlayerState);
            var last = _lastRecordedPlayback.GetValueOrDefault(deviceId);
            if (!string.Equals(last, playback, StringComparison.OrdinalIgnoreCase))
            {
                _lastRecordedPlayback[deviceId] = playback;
                // The state as it is compared (BUFFERING written as PLAYING), so the next real change is not lost and
                // an automation on playerState=PLAYING sees the resume.
                RecordEvent("cast.playback_changed", config, state with { PlayerState = playback ?? state.PlayerState }, origin: "external");
            }
        });
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
        if (string.Equals(key, "only", StringComparison.OrdinalIgnoreCase))
        {
            // "The only device, or none": never the one that plays, which may sit on another TV (the TV wake).
            match = views.Count == 1
                ? views[0]
                : throw new ArgumentException(
                    $"Several Cast devices are known ({string.Join(", ", views.Select(v => v.Config.Name))}). Give the 'device' parameter (name, id or IP).");
        }
        else if (key is null)
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
        if (!includeImage ||
            !Uri.TryCreate(state.ImageUrl, UriKind.Absolute, out var imageUri) ||
            !CastArtwork.IsAllowedImageUri(imageUri))
        {
            return PluginActionResult.OkWithData(message, json);
        }

        try
        {
            // The device chose the URL: CastArtwork refuses this PC and link-local targets and stops at the size limit.
            return await CastArtwork.FetchAsync(imageUri, MaxImageBytes, ct) is { } image
                ? PluginActionResult.OkWithData(message, json, Convert.ToBase64String(image.Bytes), image.MediaType)
                : PluginActionResult.OkWithData(message, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A slow or failing artwork server (also its own timeout) still gives the text answer.
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
