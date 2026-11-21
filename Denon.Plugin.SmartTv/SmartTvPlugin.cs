using System.Net.Http;
using Denon.App.PluginSdk;

namespace Denon.Plugin.SmartTv;

public sealed class SmartTvPlugin : IDenonPlugin, IPluginViewProvider
{
    private readonly LgWebOsClient _client = new();
    private IPluginContext? _context;
    private SmartTvConfig _config = new();
    private SmartTvDashboardViewModel? _viewModel;
    private bool _isStarted;

    public SmartTvPlugin()
    {
        _client.StateChanged += OnTvStateChanged;
        _client.ClientKeyUpdated += OnClientKeyUpdated;
        _client.DiagnosticLog += msg => _context?.Logger.Info($"[LG webOS] {msg}");
    }

    public PluginManifest Manifest { get; } = new(
        Id: "smart-tv",
        Name: "Smart TV Control (LG webOS)",
        Version: "2.0.0",
        Author: "Denon Desktop",
        Description: "Full persistent LG webOS SSAP (wss://:3001) controller: live TV state subscriptions, auto-discovered apps & HDMI inputs, Magic Remote D-Pad & keyboard, OLED Screen Off, eARC Audio Output switching, YouTube deep-link casting, SSDP/ARP auto-discovery, Wake-on-LAN, and Denon AVR coordination.",
        Category: "Home Theater TV",
        AccentHex: "#38BDF8",
        IconGlyph: "TV",
        Capabilities: PluginCapabilities.DashboardUi
                      | PluginCapabilities.ReceiverEvents
                      | PluginCapabilities.SceneAutomation
                      | PluginCapabilities.Actions
                      | PluginCapabilities.LocalNetwork);

    internal SmartTvConfig Config => _config;

    internal LgWebOsClient Client => _client;

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _config = await context.Storage.LoadConfigAsync(static () => new SmartTvConfig(), cancellationToken);
        _client.Configure(_config.TvIp, _config.ClientKey, _config.DemoMode, _config.LastModelName, _config.LastWebOsVersion);
        _viewModel = new SmartTvDashboardViewModel(this);
        _viewModel.SyncFromConfig(_config);
        _viewModel.ApplyLiveState(_client.CurrentState);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _isStarted = true;
        _client.Configure(_config.TvIp, _config.ClientKey, _config.DemoMode, _config.LastModelName, _config.LastWebOsVersion);
        _client.StartBackgroundMonitor();
        UpdateHealthStatus();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _isStarted = false;
        await _client.StopBackgroundMonitorAsync();
        _context?.ReportStatus("Stopped", PluginHealthState.Disabled);
    }

    public object? CreateDashboardView()
    {
        _viewModel ??= new SmartTvDashboardViewModel(this);
        _viewModel.SyncFromConfig(_config);
        _viewModel.ApplyLiveState(_client.CurrentState);
        return new SmartTvDashboardView { DataContext = _viewModel };
    }

    public IReadOnlyList<PluginActionDescriptor> GetActions() =>
    [
        new("tv.power_on", "TV Wake-on-LAN (Power On)", "Send multi-interface Wake-on-LAN magic packets to power on the LG TV.", "Power"),
        new("tv.power_off", "TV Power Off", "Put the LG webOS TV into standby.", "Power"),
        new("tv.screen_off", "OLED Screen Off (Audio Only)", "Blank the OLED panel while keeping HDMI eARC / music audio playing.", "Display"),
        new("tv.screen_on", "OLED Screen On", "Wake the TV screen back on from Audio-Only Screen Off mode.", "Display"),
        new("tv.audio_earc", "TV Audio → HDMI ARC / eARC", "Switch LG TV audio output to HDMI ARC / eARC (Denon AVR).", "Audio"),
        new("tv.audio_speakers", "TV Audio → Internal Speakers", "Switch LG TV audio output to internal TV speakers.", "Audio"),
        new("tv.audio_optical", "TV Audio → Optical Out", "Switch LG TV audio output to Digital Optical.", "Audio"),
        new("tv.audio_bluetooth", "TV Audio → Bluetooth", "Switch LG TV audio output to Bluetooth device.", "Audio"),
        new("tv.mute", "TV Mute Audio", "Mute internal TV speakers (great when using Denon AVR).", "Audio"),
        new("tv.unmute", "TV Unmute Audio", "Unmute TV speakers.", "Audio"),
        new("tv.hdmi1", "TV → HDMI 1", "Switch TV input to HDMI 1.", "Inputs"),
        new("tv.hdmi2", "TV → HDMI 2 (eARC)", "Switch TV input to HDMI 2.", "Inputs"),
        new("tv.hdmi3", "TV → HDMI 3", "Switch TV input to HDMI 3.", "Inputs"),
        new("tv.hdmi4", "TV → HDMI 4", "Switch TV input to HDMI 4.", "Inputs"),
        new("tv.livetv", "TV → Live TV", "Switch back to Live TV tuner.", "Inputs"),
        new("tv.youtube", "TV → Launch YouTube", "Open YouTube app on the Smart TV (or cast a video via 'contentId').", "Apps"),
        new("tv.netflix", "TV → Launch Netflix", "Open Netflix app on the Smart TV.", "Apps"),
        new("tv.plex", "TV → Launch Plex", "Open Plex app on the Smart TV.", "Apps"),
        new("tv.spotify", "TV → Launch Spotify", "Open Spotify app on the Smart TV.", "Apps"),
        new("tv.disney", "TV → Launch Disney+", "Open Disney+ app on the Smart TV.", "Apps"),
        new("tv.close_app", "TV Close Current App", "Close the currently active foreground app on the TV.", "Apps"),
        new("tv.play", "TV Media Play", "Send Play transport command to the TV.", "Remote"),
        new("tv.pause", "TV Media Pause", "Send Pause transport command to the TV.", "Remote"),
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
            return PluginActionResult.Fail("Configure your TV IP address (or click Auto-Discover / Try Demo Mode).");
        }

        try
        {
            switch (actionId.ToLowerInvariant())
            {
                case "tv.power_on":
                    if (!_config.DemoMode)
                    {
                        await _client.SendWakeOnLanAsync(_config.MacAddress, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(_config.TvIp) && !string.IsNullOrWhiteSpace(_config.ClientKey))
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(TimeSpan.FromSeconds(6), CancellationToken.None);
                                    await _client.RefreshStateAsync(CancellationToken.None);
                                }
                                catch
                                {
                                    // Background monitor will continue retrying if TV takes longer to boot.
                                }
                            });
                        }
                    }
                    return ReportAction("Sent Wake-on-LAN magic packets to TV.");

                case "tv.power_off":
                    await _client.PowerOffAsync(cancellationToken);
                    return ReportAction("Sent Power Off command to TV.");

                case "tv.screen_off":
                    await _client.TurnOffScreenAsync(cancellationToken);
                    return ReportAction("Turned OLED Screen OFF (Audio-Only Mode).");

                case "tv.screen_on":
                    await _client.TurnOnScreenAsync(cancellationToken);
                    return ReportAction("Woke TV screen ON.");

                case "tv.audio_earc":
                    await _client.SetSoundOutputAsync("external_arc", cancellationToken);
                    return ReportAction("Switched TV audio output to HDMI ARC / eARC.");

                case "tv.audio_speakers":
                    await _client.SetSoundOutputAsync("tv_speaker", cancellationToken);
                    return ReportAction("Switched TV audio output to Internal Speakers.");

                case "tv.audio_optical":
                    await _client.SetSoundOutputAsync("external_optical", cancellationToken);
                    return ReportAction("Switched TV audio output to Digital Optical.");

                case "tv.audio_bluetooth":
                    await _client.SetSoundOutputAsync("bt_soundbar", cancellationToken);
                    return ReportAction("Switched TV audio output to Bluetooth.");

                case "tv.mute":
                    await _client.SetMuteAsync(true, cancellationToken);
                    return ReportAction("Muted TV audio.");

                case "tv.unmute":
                    await _client.SetMuteAsync(false, cancellationToken);
                    return ReportAction("Unmuted TV audio.");

                case "tv.hdmi1":
                    await _client.SwitchInputAsync("com.webos.app.hdmi1", cancellationToken);
                    return ReportAction("Switched TV to HDMI 1.");

                case "tv.hdmi2":
                    await _client.SwitchInputAsync("com.webos.app.hdmi2", cancellationToken);
                    return ReportAction("Switched TV to HDMI 2.");

                case "tv.hdmi3":
                    await _client.SwitchInputAsync("com.webos.app.hdmi3", cancellationToken);
                    return ReportAction("Switched TV to HDMI 3.");

                case "tv.hdmi4":
                    await _client.SwitchInputAsync("com.webos.app.hdmi4", cancellationToken);
                    return ReportAction("Switched TV to HDMI 4.");

                case "tv.livetv":
                    await _client.LaunchAppAsync("com.webos.app.livetv", null, cancellationToken);
                    return ReportAction("Switched TV to Live TV.");

                case "tv.youtube":
                {
                    var contentId = parameters is not null && parameters.TryGetValue("contentId", out var cid) ? cid : null;
                    var title = await _client.LaunchAppAsync("youtube", contentId, cancellationToken);
                    return ReportAction(string.IsNullOrWhiteSpace(contentId)
                        ? $"Launched {title} on TV."
                        : $"Cast YouTube video ({LgWebOsClient.ExtractYouTubeVideoId(contentId)}) to TV.");
                }

                case "tv.netflix":
                    await _client.LaunchAppAsync("netflix", null, cancellationToken);
                    return ReportAction("Launched Netflix on TV.");

                case "tv.plex":
                    await _client.LaunchAppAsync("plex", null, cancellationToken);
                    return ReportAction("Launched Plex on TV.");

                case "tv.spotify":
                    await _client.LaunchAppAsync("spotify", null, cancellationToken);
                    return ReportAction("Launched Spotify on TV.");

                case "tv.disney":
                    await _client.LaunchAppAsync("disney", null, cancellationToken);
                    return ReportAction("Launched Disney+ on TV.");

                case "tv.launch_app":
                {
                    var appId = parameters is not null && parameters.TryGetValue("appId", out var aid) ? aid : string.Empty;
                    if (string.IsNullOrWhiteSpace(appId))
                    {
                        return PluginActionResult.Fail("Missing 'appId' parameter.");
                    }

                    var contentId = parameters is not null && parameters.TryGetValue("contentId", out var cid) ? cid : null;
                    var title = await _client.LaunchAppAsync(appId, contentId, cancellationToken);
                    return ReportAction($"Launched {title} on TV.");
                }

                case "tv.close_app":
                    await _client.CloseCurrentAppAsync(cancellationToken);
                    return ReportAction("Closed current foreground TV app.");

                case "tv.play":
                    await _client.SendMediaControlAsync("play", cancellationToken);
                    return ReportAction("Sent Play to TV.");

                case "tv.pause":
                    await _client.SendMediaControlAsync("pause", cancellationToken);
                    return ReportAction("Sent Pause to TV.");

                case "tv.button":
                {
                    var btn = parameters is not null && parameters.TryGetValue("button", out var b) ? b : string.Empty;
                    if (string.IsNullOrWhiteSpace(btn))
                    {
                        return PluginActionResult.Fail("Missing 'button' parameter.");
                    }

                    await _client.SendButtonAsync(btn, cancellationToken);
                    return ReportAction($"Sent remote button '{btn.ToUpperInvariant()}' to TV.");
                }

                case "tv.type_text":
                {
                    var text = parameters is not null && parameters.TryGetValue("text", out var t) ? t : string.Empty;
                    var pressEnter = parameters is null || !parameters.TryGetValue("enter", out var e) || !e.Equals("false", StringComparison.OrdinalIgnoreCase);
                    await _client.SendKeyboardTextAsync(text, pressEnter, cancellationToken);
                    return ReportAction($"Typed \"{text}\" on TV keyboard.");
                }

                case "tv.toast":
                {
                    var text = parameters is not null && parameters.TryGetValue("message", out var msg) && !string.IsNullOrWhiteSpace(msg)
                        ? msg
                        : "Denon Desktop connected to LG webOS TV";
                    await _client.SendToastAsync(text, cancellationToken);
                    return ReportAction($"Displayed TV toast: \"{text}\".");
                }

                default:
                    return PluginActionResult.Fail($"Unknown Smart TV action '{actionId}'.");
            }
        }
        catch (Exception ex)
        {
            _context?.Logger.Warning($"Smart TV action '{actionId}' failed: {ex.Message}");
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
                if (receiverEvent.Snapshot.IsPoweredOn)
                {
                    if (_config.WakeWithDenonPowerOn && !string.IsNullOrWhiteSpace(_config.MacAddress))
                    {
                        await ExecuteActionAsync("tv.power_on", null, cancellationToken);
                    }

                    if (_client.IsConnected)
                    {
                        if (_config.SwitchToArcOnDenonPowerOn)
                        {
                            await ExecuteActionAsync("tv.audio_earc", null, cancellationToken);
                        }

                        if (_config.MuteTvSpeakersOnDenonPowerOn)
                        {
                            await ExecuteActionAsync("tv.mute", null, cancellationToken);
                        }
                    }
                }
                else if (!receiverEvent.Snapshot.IsPoweredOn && _config.TurnOffWithDenonStandby && _client.IsConnected)
                {
                    await ExecuteActionAsync("tv.power_off", null, cancellationToken);
                }
            }
            else if (receiverEvent.EventType == PluginReceiverEventType.SceneActivated && _client.IsConnected)
            {
                var sceneLabel = receiverEvent.SceneName ?? receiverEvent.SceneId ?? "Scene";
                if (_config.ShowSceneToastOnTv)
                {
                    await ExecuteActionAsync("tv.toast", new Dictionary<string, string>
                    {
                        ["message"] = $"Denon AVR • {sceneLabel}",
                    }, cancellationToken);
                }

                if (_config.ScreenOffOnMusicScene &&
                    (sceneLabel.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
                     sceneLabel.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
                     sceneLabel.Contains("Stereo", StringComparison.OrdinalIgnoreCase) ||
                     sceneLabel.Contains("Direct", StringComparison.OrdinalIgnoreCase) ||
                     sceneLabel.Contains("Late Night", StringComparison.OrdinalIgnoreCase)))
                {
                    await ExecuteActionAsync("tv.screen_off", null, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _context?.Logger.Warning($"Smart TV automation warning: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    internal async Task<IReadOnlyList<LgTvDiscoveredDevice>> DiscoverTvsAsync(CancellationToken ct = default)
    {
        using var http = _context?.CreateHttpClient(allowLocalSelfSignedTls: true, timeout: TimeSpan.FromSeconds(3))
                         ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        return await LgTvDiscoveryService.DiscoverAsync(http, TimeSpan.FromSeconds(2.8), ct);
    }

    internal async Task<string> PairTvAsync(string tvIp, bool forcePairing = false)
    {
        var cleanIp = tvIp.Trim();
        var key = await _client.PairAsync(cleanIp, _config.ClientKey, forcePairing);
        _config.TvIp = cleanIp;
        _config.ClientKey = key;
        _config.DemoMode = false;

        if (string.IsNullOrWhiteSpace(_config.MacAddress))
        {
            var autoMac = await LgTvDiscoveryService.ResolveMacAddressAsync(cleanIp);
            if (!string.IsNullOrWhiteSpace(autoMac))
            {
                _config.MacAddress = autoMac;
            }
        }

        if (!string.IsNullOrWhiteSpace(_client.CurrentState.ModelName))
        {
            _config.LastModelName = _client.CurrentState.ModelName;
            if (string.IsNullOrWhiteSpace(_config.TvName) || _config.TvName is "TV" or "Living Room OLED TV")
            {
                _config.TvName = _client.CurrentState.ModelName;
            }
        }

        if (!string.IsNullOrWhiteSpace(_client.CurrentState.WebOsVersion))
        {
            _config.LastWebOsVersion = _client.CurrentState.WebOsVersion;
        }

        await SaveConfigAsync();
        UpdateHealthStatus();

        var appsCount = _client.CurrentState.Apps.Count;
        var inputsCount = _client.CurrentState.Inputs.Count;
        return $"Paired & LIVE ({_client.CurrentState.TransportLabel})! Discovered {inputsCount} inputs & {appsCount} installed apps. Client key saved.";
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

        _client.Configure(_config.TvIp, _config.ClientKey, _config.DemoMode, _config.LastModelName, _config.LastWebOsVersion);
        UpdateHealthStatus();
    }

    private void OnClientKeyUpdated(string newKey)
    {
        if (string.IsNullOrWhiteSpace(newKey) || string.Equals(_config.ClientKey, newKey, StringComparison.Ordinal))
        {
            return;
        }

        _config.ClientKey = newKey;
        _ = Task.Run(async () =>
        {
            try
            {
                if (_context is not null)
                {
                    await _context.Storage.SaveConfigAsync(_config);
                    _context.Logger.Info("Persisted LG webOS client-key to plugin config.");
                }
            }
            catch (Exception ex)
            {
                _context?.Logger.Warning($"Failed to persist LG client-key: {ex.Message}");
            }
        });
    }

    private void OnTvStateChanged(LgTvLiveState state)
    {
        var changedMeta = false;
        if (!state.IsDemo && !string.IsNullOrWhiteSpace(state.ModelName) &&
            !string.Equals(_config.LastModelName, state.ModelName, StringComparison.Ordinal))
        {
            _config.LastModelName = state.ModelName;
            changedMeta = true;
        }

        if (!state.IsDemo && !string.IsNullOrWhiteSpace(state.WebOsVersion) &&
            !string.Equals(_config.LastWebOsVersion, state.WebOsVersion, StringComparison.Ordinal))
        {
            _config.LastWebOsVersion = state.WebOsVersion;
            changedMeta = true;
        }

        if (changedMeta && _context is not null)
        {
            _ = _context.Storage.SaveConfigAsync(_config);
        }

        _viewModel?.ApplyLiveState(state);
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
        var live = _client.CurrentState;
        if (_config.DemoMode)
        {
            _context?.ReportStatus($"Demo • {live.NowShowingSummary} • {live.SoundOutputLabel}", PluginHealthState.Connected);
            return;
        }

        if (live.IsConnected)
        {
            var label = string.IsNullOrWhiteSpace(live.ModelName) ? _config.TvName : live.ModelName;
            var screenTag = live.IsScreenOff ? " • Screen Off" : string.Empty;
            _context?.ReportStatus($"LIVE • {label} • {live.NowShowingSummary}{screenTag}", PluginHealthState.Connected);
        }
        else if (!string.IsNullOrWhiteSpace(_config.TvIp) && !string.IsNullOrWhiteSpace(_config.ClientKey))
        {
            _context?.ReportStatus($"Paired (Standby) • {_config.TvName} ({_config.TvIp})", PluginHealthState.Idle);
        }
        else if (!string.IsNullOrWhiteSpace(_config.TvIp))
        {
            _context?.ReportStatus($"TV IP set ({_config.TvIp}) — click Pair", PluginHealthState.Warning);
        }
        else
        {
            _context?.ReportStatus("Click Auto-Discover TV or enter IP", PluginHealthState.Idle);
        }
    }
}
