using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Denon.App.PluginSdk;

namespace Denon.Plugin.SmartTv;

public sealed class SmartTvPlugin : IDenonPlugin, IPluginViewProvider
{
    private static readonly JsonSerializerOptions ComputerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly LgWebOsClient _client = new();
    private IPluginContext? _context;
    private SmartTvConfig _config = new();
    private SmartTvDashboardViewModel? _viewModel;
    private LgTvLiveState? _lastTelemetryState;

    // The app starting (or the plugin restarting) is not the TV changing. A connection soon after start is the TV as
    // it already was: for a short while after it, the first app, channel and sound output are taken as they are,
    // without events. A connection later is a real power-on.
    private static readonly TimeSpan StartConnectWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BaselineSettle = TimeSpan.FromSeconds(20);
    private DateTimeOffset _startedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _baselineUntil = DateTimeOffset.MinValue;
    private bool _baselineTaken;
    private bool _isStarted;
    private ChannelLogoCache? _logoCache;
    private CancellationTokenSource? _logoCts;
    private IReadOnlyList<LgTvChannel> _channels = [];
    private string? _lastLoggedProgramKey;

    public SmartTvPlugin()
    {
        _client.StateChanged += OnTvStateChanged;
        _client.ClientKeyUpdated += OnClientKeyUpdated;
        _client.ChannelListLoaded += OnChannelListLoaded;
        _client.DiagnosticLog += msg => _context?.Logger.Info($"[LG webOS] {msg}");
    }

    public PluginManifest Manifest { get; } = new(
        Id: "smart-tv",
        Name: "Smart TV Control (LG webOS)",
        Version: "2.4.1",
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
        _logoCache = new ChannelLogoCache(
            context.Storage.PluginDataDirectory,
            () => context.CreateHttpClient(timeout: TimeSpan.FromSeconds(20)),
            context.Logger.Info,
            context.Logger.Warning);
        _client.Configure(_config.TvIp, _config.ClientKey, _config.DemoMode, _config.LastModelName, _config.LastWebOsVersion);
        _viewModel = new SmartTvDashboardViewModel(this);
        _viewModel.SyncFromConfig(_config);
        _viewModel.ApplyLiveState(_client.CurrentState);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _isStarted = true;
        _startedAt = DateTimeOffset.UtcNow;
        _baselineTaken = false;
        _baselineUntil = DateTimeOffset.MinValue;
        _lastTelemetryState = null;
        _client.Configure(_config.TvIp, _config.ClientKey, _config.DemoMode, _config.LastModelName, _config.LastWebOsVersion);
        _client.StartBackgroundMonitor();
        UpdateHealthStatus();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _isStarted = false; // EmitTvTelemetryDeltas writes nothing from here: the plugin stopping is not the TV turning off.
        _logoCts?.Cancel();
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
        // Parameters and hints give MCP clients a typed schema. Destructive stays unset (null) where the
        // host's id guess already applies (power_off, close_app), so permission tiers do not change.
        new("tv.power_on", "TV Power On", "Turns the LG TV on: Wake-on-LAN every second for up to 15 s, then HDMI-CEC through the Chromecast (the TV then goes to Live TV). Waits until the TV answers, up to about 35 s.", "Power") { Parameters = [], IsDestructive = false },
        new("tv.power_off", "TV Power Off", "Put the LG webOS TV into standby.", "Power") { Parameters = [] },
        new("tv.screen_off", "Screen Off (Audio Only)", "Blank the TV screen while keeping HDMI eARC / music audio playing.", "Display") { Parameters = [], IsDestructive = false },
        new("tv.screen_on", "Screen On", "Wake the TV screen back on from Audio-Only Screen Off mode.", "Display") { Parameters = [], IsDestructive = false },
        new("tv.audio_earc", "TV Audio → HDMI ARC / eARC", "Switch LG TV audio output to HDMI ARC / eARC (Denon AVR).", "Audio") { Parameters = [], IsDestructive = false },
        new("tv.audio_speakers", "TV Audio → Internal Speakers", "Switch LG TV audio output to internal TV speakers.", "Audio") { Parameters = [], IsDestructive = false },
        new("tv.audio_optical", "TV Audio → Optical Out", "Switch LG TV audio output to Digital Optical.", "Audio") { Parameters = [], IsDestructive = false },
        new("tv.audio_bluetooth", "TV Audio → Bluetooth", "Switch LG TV audio output to Bluetooth device.", "Audio") { Parameters = [], IsDestructive = false },
        new("tv.mute", "TV Mute Audio", "Mute internal TV speakers (great when using Denon AVR).", "Audio") { Parameters = [], IsDestructive = false },
        new("tv.unmute", "TV Unmute Audio", "Unmute TV speakers.", "Audio") { Parameters = [], IsDestructive = false },
        new("tv.hdmi1", "TV → HDMI 1", "Switch TV input to HDMI 1.", "Inputs") { Parameters = [], IsDestructive = false },
        new("tv.hdmi2", "TV → HDMI 2 (eARC)", "Switch TV input to HDMI 2.", "Inputs") { Parameters = [], IsDestructive = false },
        new("tv.hdmi3", "TV → HDMI 3", "Switch TV input to HDMI 3.", "Inputs") { Parameters = [], IsDestructive = false },
        new("tv.hdmi4", "TV → HDMI 4", "Switch TV input to HDMI 4.", "Inputs") { Parameters = [], IsDestructive = false },
        new("tv.livetv", "TV → Live TV", "Switch back to Live TV tuner.", "Inputs") { Parameters = [], IsDestructive = false },
        new("tv.youtube", "TV → Launch YouTube", "Open YouTube app on the Smart TV (or cast a video via 'contentId').", "Apps")
        {
            Parameters = [new("contentId", "string", "Optional YouTube video URL or id to play. Without it, YouTube opens.")],
            IsDestructive = false,
        },
        new("tv.netflix", "TV → Launch Netflix", "Open Netflix app on the Smart TV.", "Apps") { Parameters = [], IsDestructive = false },
        new("tv.plex", "TV → Launch Plex", "Open Plex app on the Smart TV.", "Apps") { Parameters = [], IsDestructive = false },
        new("tv.spotify", "TV → Launch Spotify", "Open Spotify app on the Smart TV.", "Apps") { Parameters = [], IsDestructive = false },
        new("tv.disney", "TV → Launch Disney+", "Open Disney+ app on the Smart TV.", "Apps") { Parameters = [], IsDestructive = false },
        new("tv.close_app", "TV Close Current App", "Close the currently active foreground app on the TV.", "Apps") { Parameters = [] },
        new("tv.play", "TV Media Play", "Send Play transport command to the TV.", "Remote") { Parameters = [], IsDestructive = false },
        new("tv.pause", "TV Media Pause", "Send Pause transport command to the TV.", "Remote") { Parameters = [], IsDestructive = false },
        new("tv.toast", "TV Test Toast Alert", "Display a Denon Desktop notification banner on the TV screen.", "OSD")
        {
            Parameters = [new("message", "string", "Text for the banner. Default: a Denon Desktop test message.")],
            IsDestructive = false,
        },
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
                    if (_config.DemoMode)
                    {
                        return ReportAction("Demo mode: the simulated TV is on (no packets sent).");
                    }

                    return await PowerOnAsync(cancellationToken);

                case "tv.power_off":
                    await _client.PowerOffAsync(cancellationToken);
                    return ReportAction("Sent Power Off command to TV.");

                case "tv.screen_off":
                    await _client.TurnOffScreenAsync(cancellationToken);
                    return ReportAction("Turned TV screen OFF (Audio-Only Mode).");

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
                    var replace = parameters is not null && parameters.TryGetValue("replace", out var r) && r.Equals("true", StringComparison.OrdinalIgnoreCase);
                    await _client.SendKeyboardTextAsync(text, pressEnter, replace, cancellationToken);
                    return ReportAction($"Typed \"{text}\" on TV keyboard.");
                }

                case "tv.open_url":
                {
                    var url = parameters is not null && parameters.TryGetValue("url", out var u) ? u : string.Empty;
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return PluginActionResult.Fail("Missing 'url' parameter.");
                    }

                    var opened = await _client.OpenBrowserUrlAsync(url, cancellationToken);
                    return ReportAction($"Opened {opened} in LG webOS Web Browser.");
                }

                case "tv.computer":
                {
                    var payloadJson = parameters is not null && parameters.TryGetValue("payloadJson", out var pj) ? pj : "{}";
                    return await ExecuteTvComputerAsync(payloadJson, cancellationToken);
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

    /// <summary>Keeps the page's tab for the next visit. Only the stored file changes: no reconnect.</summary>
    internal async Task SaveDashboardTabAsync(string tab)
    {
        if (_config.DashboardTab == tab || _context is null)
        {
            return;
        }

        _config.DashboardTab = tab;
        try
        {
            await _context.Storage.SaveConfigAsync(_config);
        }
        catch (Exception ex)
        {
            _context.Logger.Warning($"Could not keep the Smart TV page tab: {ex.Message}");
        }
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
        EmitTvTelemetryDeltas(state);
        LogProgramChange(state);
        UpdateHealthStatus();
    }

    /// <summary>Fetches the logos of the TV's channel list (once per list; see <see cref="ChannelLogoCache"/>).</summary>
    private void OnChannelListLoaded(IReadOnlyList<LgTvChannel> channels)
    {
        _channels = channels;
        RefreshChannelLogos();
    }

    internal void RefreshChannelLogos()
    {
        if (!_isStarted || !_config.ChannelLogos || _logoCache is not { } cache || _channels.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _logoCts, cts)?.Cancel();
        var token = cts.Token;
        var names = _channels.Select(c => c.Name).ToList();
        var overrides = new Dictionary<string, string>(_config.ChannelLogoOverrides, StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(async () =>
        {
            try
            {
                await cache.EnsureAsync(names, overrides, token);
                UpdateHealthStatus();
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    /// <summary>Logs the channel and program once per program (Live TV only).</summary>
    private void LogProgramChange(LgTvLiveState state)
    {
        if (state.IsDemo || !state.IsLiveTv || string.IsNullOrWhiteSpace(state.CurrentChannelName))
        {
            return;
        }

        var key = $"{state.CurrentChannelId}|{state.CurrentChannelName}|{state.CurrentProgram?.Name}|{state.CurrentProgram?.Start:O}";
        if (key == _lastLoggedProgramKey)
        {
            return;
        }

        _lastLoggedProgramKey = key;
        var dvb = DvbIds(state.SignalChannelId) is { } ids ? $", DVB {ids}" : string.Empty;
        var type = string.IsNullOrWhiteSpace(state.ChannelTypeName) ? string.Empty : $", {state.ChannelTypeName}";
        var channel = $"{state.CurrentChannelName} (Ch {state.CurrentChannelNumber}{dvb}{type})";
        var program = state.CurrentProgram is { } p
            ? $"{p.Name} {p.Start.LocalDateTime:HH:mm}–{p.End.LocalDateTime:HH:mm}" +
              (state.NextProgram is { } n ? $"; next {n.Start.LocalDateTime:HH:mm} {n.Name}" : string.Empty)
            : "no guide data yet";
        var logo = _config.ChannelLogos && _logoCache?.TryGetLogoPath(state.CurrentChannelName) is { } path
            ? $"; logo {System.IO.Path.GetFileName(path)}"
            : string.Empty;
        _context?.Logger.Info($"Live TV: {channel}: {program}{logo}.");
    }

    /// <summary>"2190_101_65024" (TSID_SID_ONID, as the TV writes it) as "ONID 65024 / TSID 2190 / SID 101".</summary>
    internal static string? DvbIds(string? signalChannelId)
    {
        var parts = (signalChannelId ?? string.Empty).Split('_');
        return parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(char.IsDigit))
            ? $"ONID {parts[2]} / TSID {parts[0]} / SID {parts[1]}"
            : null;
    }

    /// <summary>
    /// Structured now-showing details for the host (hero, watch log, Insights): the channel, its DVB IDs, the current
    /// and next program and the cached logo while Live TV shows a channel; the app otherwise.
    /// </summary>
    internal Dictionary<string, string> BuildStatusDetails(LgTvLiveState live)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        if (live.IsLiveTv && (!string.IsNullOrWhiteSpace(live.CurrentChannelName) || !string.IsNullOrWhiteSpace(live.CurrentChannelNumber)))
        {
            details["kind"] = "live_tv";
            Add(details, "channelName", live.CurrentChannelName);
            Add(details, "channelNumber", live.CurrentChannelNumber);
            Add(details, "channelId", live.CurrentChannelId);
            Add(details, "signalChannelId", live.SignalChannelId);
            Add(details, "channelType", live.ChannelTypeName);
            if (live.CurrentProgram is { } p)
            {
                details["programName"] = p.Name;
                details["programStart"] = p.Start.ToString("O", CultureInfo.InvariantCulture);
                details["programEnd"] = p.End.ToString("O", CultureInfo.InvariantCulture);
            }

            if (live.NextProgram is { } n)
            {
                details["nextProgramName"] = n.Name;
                details["nextProgramStart"] = n.Start.ToString("O", CultureInfo.InvariantCulture);
                details["nextProgramEnd"] = n.End.ToString("O", CultureInfo.InvariantCulture);
            }

            if (_config.ChannelLogos && _logoCache?.TryGetLogoPath(live.CurrentChannelName) is { } logo)
            {
                details["channelLogo"] = logo;
            }
        }
        else if (!string.IsNullOrWhiteSpace(live.ForegroundAppId))
        {
            details["kind"] = "app";
            details["appId"] = live.ForegroundAppId;
            Add(details, "appTitle", live.ForegroundAppTitle);
        }

        return details;

        static void Add(Dictionary<string, string> d, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                d[key] = value.Trim();
            }
        }
    }

    /// <summary>A reconnect after at least this long offline counts as a power-on; a shorter one as a network drop.</summary>
    internal static readonly TimeSpan PowerOnGap = TimeSpan.FromSeconds(20);

    private string? _appBeforeDisconnect;
    private DateTimeOffset _disconnectedAt;
    private bool _standbyBeforeDisconnect;

    /// <summary>webOS power states of a TV going to standby ("Suspend", "Active Standby", "Power Off").</summary>
    internal static bool IsStandbyPowerState(string? powerState) =>
        powerState is not null &&
        (powerState.Contains("Standby", StringComparison.OrdinalIgnoreCase) ||
         powerState.Contains("Suspend", StringComparison.OrdinalIgnoreCase) ||
         powerState.Contains("Power Off", StringComparison.OrdinalIgnoreCase));

    private void EmitTvTelemetryDeltas(LgTvLiveState state)
    {
        if (state.IsDemo || _context is null || !_isStarted)
        {
            return;
        }

        var prev = _lastTelemetryState;
        _lastTelemetryState = state;
        if (prev is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (prev.IsConnected && !state.IsConnected)
        {
            // The client forgets the foreground app on a disconnect. Keep it here, so a reconnect after a short
            // network drop (the TV stayed on) is not taken for a power-on.
            _appBeforeDisconnect = prev.ForegroundAppId;
            _disconnectedAt = now;
            _standbyBeforeDisconnect = IsStandbyPowerState(prev.PowerState);
        }

        if (!_baselineTaken && state.IsConnected)
        {
            _baselineTaken = true;
            if (now - _startedAt <= StartConnectWindow)
            {
                _baselineUntil = now + BaselineSettle; // The TV was already on when the app started.
            }
        }

        if (now < _baselineUntil)
        {
            return;
        }

        if (prev.IsConnected != state.IsConnected)
        {
            _context.Events.RecordEvent(
                "power.changed",
                new Dictionary<string, object?>
                {
                    ["from"] = prev.IsConnected ? "on" : "standby",
                    ["to"] = state.IsConnected ? "on" : "standby",
                    ["powerState"] = state.PowerState,
                    ["model"] = state.ModelName,
                },
                origin: "auto");
        }

        if (!state.IsConnected)
        {
            return;
        }

        if (prev.IsScreenOff != state.IsScreenOff)
        {
            _context.Events.RecordEvent(
                "screen.changed",
                new Dictionary<string, object?>
                {
                    ["screenOff"] = state.IsScreenOff,
                    ["model"] = state.ModelName,
                },
                origin: "auto");
        }

        var fromAppId = prev.ForegroundAppId;
        if (string.IsNullOrWhiteSpace(fromAppId) && _appBeforeDisconnect is { } before &&
            !string.IsNullOrWhiteSpace(state.ForegroundAppId))
        {
            // The first app after a reconnect. A power-on reports fromAppId "" ("TV on → Live TV" keys on it); a
            // short drop reports the app from before, and nothing when it is the same app.
            _appBeforeDisconnect = null;
            var wasPowerOn = _standbyBeforeDisconnect || now - _disconnectedAt >= PowerOnGap;
            fromAppId = wasPowerOn ? string.Empty : before;
        }

        if (!string.Equals(fromAppId, state.ForegroundAppId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(state.ForegroundAppId))
        {
            _context.Events.RecordEvent(
                "app.changed",
                new Dictionary<string, object?>
                {
                    ["fromAppId"] = fromAppId,
                    ["toAppId"] = state.ForegroundAppId,
                    ["appTitle"] = state.ForegroundAppTitle,
                    ["model"] = state.ModelName,
                },
                origin: "auto");
        }

        var prevChan = $"{prev.CurrentChannelNumber}|{prev.CurrentChannelName}";
        var curChan = $"{state.CurrentChannelNumber}|{state.CurrentChannelName}";
        if (!string.Equals(prevChan, curChan, StringComparison.OrdinalIgnoreCase) &&
            (!string.IsNullOrWhiteSpace(state.CurrentChannelNumber) || !string.IsNullOrWhiteSpace(state.CurrentChannelName)))
        {
            _context.Events.RecordEvent(
                "channel.changed",
                new Dictionary<string, object?>
                {
                    ["channelNumber"] = state.CurrentChannelNumber,
                    ["channelName"] = state.CurrentChannelName,
                    ["channelId"] = state.CurrentChannelId,
                    ["signalChannelId"] = state.SignalChannelId,
                    ["channelType"] = state.ChannelTypeName,
                    ["channelLogo"] = _config.ChannelLogos && _logoCache?.TryGetLogoPath(state.CurrentChannelName) is { } logoPath
                        ? System.IO.Path.GetFileName(logoPath)
                        : null,
                    ["model"] = state.ModelName,
                },
                origin: "auto");
        }

        if (!string.Equals(prev.SoundOutput, state.SoundOutput, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(state.SoundOutput))
        {
            _context.Events.RecordEvent(
                "audio_output.changed",
                new Dictionary<string, object?>
                {
                    ["from"] = prev.SoundOutput,
                    ["to"] = state.SoundOutput,
                    ["label"] = state.SoundOutputLabel,
                },
                origin: "auto");
        }
    }

    private async Task<PluginActionResult> ExecuteTvComputerAsync(string payloadJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
        var root = doc.RootElement;
        var action = GetJsonString(root, "action", "screenshot").ToLowerInvariant();
        var screenshotAfter = GetJsonBool(root, "screenshot_after") ?? false;
        var showCursor = GetJsonBool(root, "show_cursor") ?? true;
        var width = Math.Clamp(GetJsonInt(root, "width") ?? 960, 320, 1920);
        var height = Math.Clamp(GetJsonInt(root, "height") ?? 540, 180, 1080);
        var settleMs = Math.Clamp(GetJsonInt(root, "settle_ms") ?? 350, 0, 5000);

        var executed = new List<string>();
        LgTvScreenshotResult? shot = null;
        object? systemSettingsResult = null;

        if (action == "batch")
        {
            if (!root.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array)
            {
                return PluginActionResult.Fail("Action 'batch' requires a 'steps' array of sub-actions.");
            }

            var stepCount = 0;
            foreach (var step in stepsEl.EnumerateArray())
            {
                if (++stepCount > 25)
                {
                    executed.Add("Stopped after max 25 batch steps.");
                    break;
                }

                if (step.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var subAction = GetJsonString(step, "action").ToLowerInvariant();
                if (subAction == "screenshot")
                {
                    var sw = Math.Clamp(GetJsonInt(step, "width") ?? width, 320, 1920);
                    var sh = Math.Clamp(GetJsonInt(step, "height") ?? height, 180, 1080);
                    var sc = GetJsonBool(step, "show_cursor") ?? showCursor;
                    shot = await _client.CaptureScreenshotAsync(sw, sh, sc, ct);
                    executed.Add(shot.Success ? $"Captured screenshot ({shot.Width}x{shot.Height}, {shot.ByteLength} bytes)" : $"Screenshot failed: {shot.ErrorMessage}");
                }
                else if (subAction == "get_settings")
                {
                    (systemSettingsResult, var msg) = await ExecuteGetSystemSettingsAsync(step, ct);
                    executed.Add(msg);
                }
                else if (subAction == "set_settings")
                {
                    (systemSettingsResult, var msg) = await ExecuteSetSystemSettingsAsync(step, ct);
                    executed.Add(msg);
                }
                else
                {
                    var summary = await ExecuteSingleComputerStepAsync(subAction, step, ct);
                    executed.Add(summary);
                }
            }
        }
        else if (action == "screenshot")
        {
            shot = await _client.CaptureScreenshotAsync(width, height, showCursor, ct);
            executed.Add(shot.Success
                ? $"Captured TV screenshot ({shot.Width}x{shot.Height}, {shot.ByteLength} bytes)."
                : $"Screenshot failed: {shot.ErrorMessage}");
        }
        else if (action == "get_state")
        {
            executed.Add("Read live LG webOS TV state.");
        }
        else if (action == "get_settings")
        {
            (systemSettingsResult, var msg) = await ExecuteGetSystemSettingsAsync(root, ct);
            executed.Add(msg);
        }
        else if (action == "set_settings")
        {
            (systemSettingsResult, var msg) = await ExecuteSetSystemSettingsAsync(root, ct);
            executed.Add(msg);
        }
        else
        {
            var summary = await ExecuteSingleComputerStepAsync(action, root, ct);
            executed.Add(summary);
        }

        if (screenshotAfter && shot is null)
        {
            if (settleMs > 0)
            {
                await Task.Delay(settleMs, ct);
            }

            shot = await _client.CaptureScreenshotAsync(width, height, showCursor, ct);
            executed.Add(shot.Success
                ? $"Captured post-action screenshot ({shot.Width}x{shot.Height}, {shot.ByteLength} bytes)."
                : $"Post-action screenshot failed: {shot.ErrorMessage}");
        }

        var live = _client.CurrentState;
        var (cx, cy, calibrated) = _client.GetNormalizedCursor();

        var dataObj = new
        {
            action,
            stepsExecuted = executed,
            tv = new
            {
                connected = live.IsConnected,
                model = live.ModelName,
                webOsVersion = live.WebOsVersion,
                powerState = live.PowerState,
                isScreenOff = live.IsScreenOff,
                foregroundAppId = live.ForegroundAppId,
                foregroundAppTitle = live.ForegroundAppTitle,
                nowShowing = live.NowShowingSummary,
                soundOutput = live.SoundOutput,
                soundOutputLabel = live.SoundOutputLabel,
                pointerConnected = live.IsPointerConnected,
            },
            cursor = new
            {
                x = cx,
                y = cy,
                coordinateSpace = "0..1000 x 0..1000",
                calibrated,
            },
            screenshot = shot is null
                ? null
                : new
                {
                    captured = shot.Success,
                    mimeType = shot.MimeType,
                    width = shot.Width,
                    height = shot.Height,
                    byteLength = shot.ByteLength,
                    error = shot.ErrorMessage,
                },
            systemSettings = systemSettingsResult,
            installedApps = live.Apps.Take(20).Select(a => new { id = a.Id, title = a.Title, active = a.IsActive }).ToArray(),
            inputs = live.Inputs.Select(i => new { id = i.Id, appId = i.AppId, label = i.Label, connected = i.IsConnected, active = i.IsActive }).ToArray(),
        };

        var summaryText = string.Join(" → ", executed);
        _context?.Logger.Info($"[TV Computer] {summaryText}");
        _viewModel?.SetLastActivity(summaryText);

        var dataJson = JsonSerializer.Serialize(dataObj, ComputerJsonOptions);
        return PluginActionResult.OkWithData(
            summaryText,
            dataJson: dataJson,
            imageBase64: shot?.Success == true ? shot.ImageBase64 : null,
            imageMimeType: shot?.Success == true ? shot.MimeType : null);
    }

    private async Task<string> ExecuteSingleComputerStepAsync(string action, JsonElement el, CancellationToken ct)
    {
        switch (action)
        {
            case "open_url":
            {
                var url = GetJsonString(el, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    throw new ArgumentException("Action 'open_url' requires 'url'.");
                }

                var opened = await _client.OpenBrowserUrlAsync(url, ct);
                return $"Opened browser URL {opened}";
            }

            case "launch_app":
            {
                var app = GetJsonString(el, "app");
                if (string.IsNullOrWhiteSpace(app))
                {
                    app = GetJsonString(el, "app_id");
                }

                if (string.IsNullOrWhiteSpace(app))
                {
                    throw new ArgumentException("Action 'launch_app' requires 'app' (app name, alias, or webOS appId).");
                }

                var contentId = GetJsonString(el, "content_id");
                var title = await _client.LaunchAppAsync(app, string.IsNullOrWhiteSpace(contentId) ? null : contentId, ct);
                return string.IsNullOrWhiteSpace(contentId)
                    ? $"Launched app '{title}'"
                    : $"Launched '{title}' with content '{contentId}'";
            }

            case "close_app":
                await _client.CloseCurrentAppAsync(ct);
                return "Closed foreground app";

            case "move":
            {
                var dx = GetJsonInt(el, "dx") ?? 0;
                var dy = GetJsonInt(el, "dy") ?? 0;
                var drag = GetJsonBool(el, "drag") ?? false;
                await _client.MovePointerRelativeAsync(dx, dy, drag, ct);
                return drag ? $"Dragged pointer by ({dx}, {dy})" : $"Moved pointer by ({dx}, {dy})";
            }

            case "move_to":
            {
                var x = GetJsonInt(el, "x") ?? throw new ArgumentException("Action 'move_to' requires integer 'x' (0..1000).");
                var y = GetJsonInt(el, "y") ?? throw new ArgumentException("Action 'move_to' requires integer 'y' (0..1000).");
                var recal = GetJsonBool(el, "recalibrate") ?? false;
                await _client.MovePointerToNormalizedAsync(x, y, recal, ct);
                return $"Moved pointer to ({Math.Clamp(x, 0, 1000)}, {Math.Clamp(y, 0, 1000)})";
            }

            case "click":
            {
                var x = GetJsonInt(el, "x");
                var y = GetJsonInt(el, "y");
                if (x.HasValue && y.HasValue)
                {
                    var recal = GetJsonBool(el, "recalibrate") ?? false;
                    await _client.MovePointerToNormalizedAsync(x.Value, y.Value, recal, ct);
                    await Task.Delay(70, ct);
                    await _client.ClickPointerAsync(ct);
                    return $"Clicked at ({Math.Clamp(x.Value, 0, 1000)}, {Math.Clamp(y.Value, 0, 1000)})";
                }

                await _client.ClickPointerAsync(ct);
                return "Clicked at current pointer position";
            }

            case "scroll":
            {
                var dx = GetJsonInt(el, "dx") ?? 0;
                var dy = GetJsonInt(el, "dy") ?? -3;
                var repeat = Math.Clamp(GetJsonInt(el, "repeat") ?? 1, 1, 15);
                for (var i = 0; i < repeat; i++)
                {
                    await _client.ScrollPointerAsync(dx, dy, ct);
                    if (i + 1 < repeat)
                    {
                        await Task.Delay(90, ct);
                    }
                }

                return repeat == 1 ? $"Scrolled ({dx}, {dy})" : $"Scrolled ({dx}, {dy}) x{repeat}";
            }

            case "button":
            {
                var btn = GetJsonString(el, "button");
                if (string.IsNullOrWhiteSpace(btn))
                {
                    throw new ArgumentException("Action 'button' requires 'button' (e.g. HOME, BACK, ENTER, UP, DOWN, LEFT, RIGHT).");
                }

                var repeat = Math.Clamp(GetJsonInt(el, "repeat") ?? 1, 1, 15);
                for (var i = 0; i < repeat; i++)
                {
                    await _client.SendButtonAsync(btn, ct);
                    if (i + 1 < repeat)
                    {
                        await Task.Delay(130, ct);
                    }
                }

                return repeat == 1 ? $"Pressed {btn.ToUpperInvariant()}" : $"Pressed {btn.ToUpperInvariant()} x{repeat}";
            }

            case "type":
            {
                var text = GetJsonString(el, "text");
                if (string.IsNullOrEmpty(text))
                {
                    throw new ArgumentException("Action 'type' requires 'text'.");
                }

                var pressEnter = GetJsonBool(el, "press_enter") ?? true;
                var replace = GetJsonBool(el, "replace") ?? false;
                await _client.SendKeyboardTextAsync(text, pressEnter, replace, ct);
                return pressEnter ? $"Typed \"{text}\" + Enter" : $"Typed \"{text}\"";
            }

            case "backspace":
            {
                var count = Math.Clamp(GetJsonInt(el, "count") ?? 1, 1, 50);
                await _client.DeleteKeyboardCharactersAsync(count, ct);
                return $"Deleted {count} character(s)";
            }

            case "wait":
            {
                var ms = Math.Clamp(GetJsonInt(el, "ms") ?? 1000, 50, 10000);
                await Task.Delay(ms, ct);
                return $"Waited {ms}ms";
            }

            default:
                throw new ArgumentException($"Unknown tv_computer action '{action}'. Use screenshot, get_state, get_settings, set_settings, open_url, launch_app, close_app, move, move_to, click, scroll, button, type, backspace, wait, or batch.");
        }
    }

    private async Task<(object Data, string Summary)> ExecuteGetSystemSettingsAsync(JsonElement el, CancellationToken ct)
    {
        var category = GetJsonString(el, "category", "picture").Trim().ToLowerInvariant();
        var keys = new List<string>();
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("keys", out var keysEl) && keysEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in keysEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    keys.Add(item.GetString()!.Trim());
                }
            }
        }

        if (category == "all")
        {
            var results = new Dictionary<string, object?>();
            var errors = new Dictionary<string, string>();
            foreach (var cat in new[] { "picture", "sound" })
            {
                try
                {
                    var res = await _client.GetSystemSettingsAsync(cat, null, ct);
                    results[cat] = ExtractSettingsBody(res);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    errors[cat] = ex.Message;
                }
            }

            var data = new
            {
                category = "all",
                categories = results,
                errors = errors.Count > 0 ? errors : null,
            };
            var msg = errors.Count == 0
                ? "Read LG webOS picture & sound system settings."
                : $"Read LG webOS settings ({results.Count} category ok, {errors.Count} restricted).";
            return (data, msg);
        }

        try
        {
            var raw = await _client.GetSystemSettingsAsync(category, keys.Count > 0 ? keys : null, ct);
            var data = new
            {
                category,
                supported = true,
                settings = ExtractSettingsBody(raw),
            };
            return (data, $"Read LG webOS '{category}' system settings.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var data = new
            {
                category,
                supported = false,
                error = ex.Message,
                fallbackHint = "Use action='launch_app', app='settings' + D-pad navigation with screenshot_after=true to inspect this setting visually.",
            };
            return (data, $"Direct SSAP get_settings('{category}') was restricted by TV firmware; use on-screen Settings fallback.");
        }
    }

    private async Task<(object Data, string Summary)> ExecuteSetSystemSettingsAsync(JsonElement el, CancellationToken ct)
    {
        var category = GetJsonString(el, "category", "picture").Trim().ToLowerInvariant();
        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty("settings", out var settingsEl) ||
            settingsEl.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Action 'set_settings' requires a 'settings' JSON object (e.g. {\"backlight\": 70, \"pictureMode\": \"expert2\"}).");
        }

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in settingsEl.EnumerateObject())
        {
            map[prop.Name] = ConvertJsonValue(prop.Value);
        }

        if (map.Count == 0)
        {
            throw new ArgumentException("Action 'set_settings' requires at least one key/value entry inside 'settings'.");
        }

        var raw = await _client.SetSystemSettingsAsync(category, map, ct);
        var data = new
        {
            category,
            applied = true,
            requested = map,
            response = raw.ValueKind == JsonValueKind.Undefined ? (object?)null : raw,
        };
        return (data, $"Updated LG webOS '{category}' settings ({string.Join(", ", map.Keys)}).");
    }

    private static object? ExtractSettingsBody(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("settings", out var sEl))
        {
            return sEl.Clone();
        }

        return raw.ValueKind == JsonValueKind.Undefined ? null : raw.Clone();
    }

    private static object? ConvertJsonValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt32(out var i) ? i : (el.TryGetInt64(out var l) ? l : el.GetDouble()),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.Clone(),
    };

    private static string GetJsonString(JsonElement el, string name, string fallback = "")
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null)
        {
            return prop.ValueKind == JsonValueKind.String ? (prop.GetString() ?? fallback) : prop.ToString();
        }

        return fallback;
    }

    private static int? GetJsonInt(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var i)) return i;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var i2)) return i2;
        }

        return null;
    }

    private static bool? GetJsonBool(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind is JsonValueKind.True or JsonValueKind.False) return prop.GetBoolean();
            if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var b)) return b;
        }

        return null;
    }

    /// <summary>How long Wake-on-LAN packets go out before the HDMI-CEC fallback.</summary>
    internal static readonly TimeSpan WakeOnLanWindow = TimeSpan.FromSeconds(15);

    /// <summary>How long the TV gets to answer after the Chromecast woke it over HDMI-CEC.</summary>
    internal static readonly TimeSpan CecWakeWindow = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _powerOnGate = new(1, 1);

    /// <summary>
    /// Turns the TV on. Wake-on-LAN over Wi-Fi often misses one packet (the TV's radio sleeps between beacons), so
    /// packets go out every second for up to 15 s, to the broadcast address and straight to the TV, until the TV
    /// answers. If it does not, the Chromecast wakes it over HDMI-CEC; then the TV goes to Live TV (CEC selected the
    /// Chromecast's input) and the Chromecast app closes. One wake at a time: more taps while it runs do nothing.
    /// </summary>
    private async Task<PluginActionResult> PowerOnAsync(CancellationToken ct)
    {
        if (_client.IsConnected)
        {
            return ReportAction("The TV is already on.");
        }

        if (!await _powerOnGate.WaitAsync(0, ct))
        {
            return PluginActionResult.Ok("The TV is already waking.");
        }

        try
        {
            var hasMac = new string(_config.MacAddress.Where(Uri.IsHexDigit).ToArray()).Length == 12;
            var canConnect = !string.IsNullOrWhiteSpace(_config.TvIp) && !string.IsNullOrWhiteSpace(_config.ClientKey);
            if (!hasMac && !canConnect)
            {
                return PluginActionResult.Fail("Enter the TV's MAC address (or click Auto-MAC) so Wake-on-LAN can reach it.");
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (hasMac)
            {
                while (clock.Elapsed < WakeOnLanWindow)
                {
                    await _client.SendWakeOnLanAsync(_config.MacAddress, _config.TvIp, ct);
                    if (!canConnect)
                    {
                        return ReportAction("Sent Wake-on-LAN magic packets to TV.");
                    }

                    if (await TryConnectAsync(ct))
                    {
                        return ReportAction($"The TV woke on Wake-on-LAN after {clock.Elapsed.TotalSeconds:0} s.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }

            if (!canConnect || _context is null)
            {
                return PluginActionResult.Fail("The TV did not answer Wake-on-LAN.");
            }

            // The Chromecast on this TV: the one chosen on the Smart TV page, else the only one known or the only one
            // online. Never "the one that plays" (Cast picks that without a device), which may sit on another TV.
            var (castDevice, noDevice) = PickCecCastDevice(
                _config.CecWakeCastDevice,
                string.IsNullOrWhiteSpace(_config.CecWakeCastDevice) ? await GetCastDevicesAsync(ct) : null);
            if (castDevice is null)
            {
                return PluginActionResult.Fail(
                    $"The TV did not answer Wake-on-LAN in {WakeOnLanWindow.TotalSeconds:0} s, and no Chromecast can wake it: {noDevice}");
            }

            var wake = await _context.RunPluginActionAsync(
                "chromecast", "wake-tv", new Dictionary<string, string> { ["device"] = castDevice }, ct);
            if (!wake.Success)
            {
                return PluginActionResult.Fail(
                    $"The TV did not answer Wake-on-LAN in {WakeOnLanWindow.TotalSeconds:0} s, and the Chromecast could not wake it: {wake.Message}");
            }

            _context.Logger.Info($"The TV did not answer Wake-on-LAN in {WakeOnLanWindow.TotalSeconds:0} s. {wake.Message}");
            // Only the session the wake started is ours to close. A cast the user already runs stays, and so does
            // its input: no Live TV then.
            var (launched, wakeSession) = ReadWakeResult(wake.DataJson);
            var closeWakeSession = launched && wakeSession is not null
                ? new Dictionary<string, string> { ["sessionId"] = wakeSession, ["device"] = castDevice }
                : null;
            var cec = System.Diagnostics.Stopwatch.StartNew();
            while (cec.Elapsed < CecWakeWindow)
            {
                if (await TryConnectAsync(ct))
                {
                    if (!launched)
                    {
                        return ReportAction("The TV is on (the Chromecast already casts).");
                    }

                    // CEC switched the TV to the Chromecast's input. Close the empty wake app first: only when it still
                    // owned the Chromecast go to Live TV. A cast the user started meanwhile keeps its input.
                    if (closeWakeSession is null)
                    {
                        return ReportAction("The Chromecast woke the TV over HDMI-CEC; it stays on the Chromecast input (the wake session is not known).");
                    }

                    var quit = await _context.RunPluginActionAsync("chromecast", "quit-app", closeWakeSession, ct);
                    if (!ReadClosed(quit))
                    {
                        return ReportAction("The Chromecast woke the TV over HDMI-CEC. A cast started meanwhile, so the TV stays on it.");
                    }

                    try
                    {
                        await _client.LaunchAppAsync("com.webos.app.livetv", null, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _context.Logger.Warning($"Live TV after the HDMI-CEC wake failed: {ex.Message}");
                    }

                    return ReportAction($"The Chromecast woke the TV over HDMI-CEC (Wake-on-LAN did not answer). Switched to Live TV.");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            if (closeWakeSession is not null)
            {
                await _context.RunPluginActionAsync("chromecast", "quit-app", closeWakeSession, ct);
            }

            return PluginActionResult.Fail(
                "The TV did not answer Wake-on-LAN or HDMI-CEC. On the TV, turn on SimpLink (HDMI-CEC) and \"Turn on via Wi-Fi\".");
        }
        finally
        {
            _powerOnGate.Release();
        }
    }

    /// <summary>The Cast devices the Chromecast plugin knows (enabled ones only), or null when it does not run.</summary>
    internal async Task<IReadOnlyList<CastDeviceChoice>?> GetCastDevicesAsync(CancellationToken ct = default)
    {
        if (_context is null)
        {
            return null;
        }

        var list = await _context.RunPluginActionAsync("chromecast", "list-devices", null, ct);
        return list.Success ? ReadCastDevices(list.DataJson) : null;
    }

    /// <summary>Reads the Chromecast plugin's list-devices data. Disabled devices are left out.</summary>
    internal static IReadOnlyList<CastDeviceChoice> ReadCastDevices(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var devices = new List<CastDeviceChoice>();
            foreach (var d in doc.RootElement.EnumerateArray())
            {
                var id = d.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) ||
                    (d.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False))
                {
                    continue;
                }

                var name = d.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var connected = d.TryGetProperty("connection", out var c) && c.ValueKind == JsonValueKind.String &&
                                string.Equals(c.GetString(), "Connected", StringComparison.OrdinalIgnoreCase);
                devices.Add(new CastDeviceChoice(id, string.IsNullOrWhiteSpace(name) ? id : name, connected));
            }

            return devices;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The Chromecast that wakes this TV over HDMI-CEC: the one chosen on the Smart TV page; else the only one known;
    /// else the only one online. With several online, none: the person chooses (another one may sit on another TV).
    /// Returns the device (id, name or IP) or the reason there is none.
    /// </summary>
    internal static (string? Device, string? Reason) PickCecCastDevice(
        string? chosen, IReadOnlyList<CastDeviceChoice>? devices)
    {
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            return (chosen.Trim(), null);
        }

        if (devices is null)
        {
            return (null, "the Chromecast plugin does not run.");
        }

        if (devices.Count == 0)
        {
            return (null, "the Chromecast plugin knows no Cast device.");
        }

        if (devices.Count == 1)
        {
            return (devices[0].Id, null);
        }

        var online = devices.Where(d => d.IsConnected).ToList();
        if (online.Count == 1)
        {
            return (online[0].Id, null);
        }

        return online.Count == 0
            ? (null, $"no Chromecast is online ({string.Join(", ", devices.Select(d => d.Name))}).")
            : (null, $"several Chromecasts are online ({string.Join(", ", online.Select(d => d.Name))}). " +
                     "Choose the Chromecast on this TV on the Smart TV page.");
    }

    /// <summary>True when the Chromecast's quit-app closed the session (its data says "closed": true).</summary>
    internal static bool ReadClosed(PluginActionResult quit)
    {
        if (!quit.Success || string.IsNullOrWhiteSpace(quit.DataJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(quit.DataJson);
            return doc.RootElement.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads the Chromecast's wake-tv data: did it start a session, and which one.</summary>
    internal static (bool Launched, string? SessionId) ReadWakeResult(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return (false, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            var launched = root.TryGetProperty("launched", out var l) && l.ValueKind == JsonValueKind.True;
            var session = root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return (launched, string.IsNullOrWhiteSpace(session) ? null : session);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    /// <summary>True when the TV accepts a TCP connection on its SSAP port (3001, or 3000) within the timeout.</summary>
    internal static async Task<bool> IsTvPortOpenAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        foreach (var port in new[] { 3001, 3000 })
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(timeout);
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync(host.Trim(), port, attempt.Token);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // No answer in time on this port: try the other one (a TV may drop 3001 but serve 3000).
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Refused or unreachable: try the other port.
            }
        }

        return false;
    }

    /// <summary>One connect try. True when the TV answers.</summary>
    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        if (_client.IsConnected)
        {
            return true;
        }

        // A short TCP probe tells whether the TV is up. Only then connect, with no short timeout: cancelling the
        // connect mid-way would also cancel the state hydration and leave the session without push subscriptions.
        if (!await IsTvPortOpenAsync(_config.TvIp, TimeSpan.FromMilliseconds(1500), ct))
        {
            return false;
        }

        try
        {
            await _client.RefreshStateAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Up but not ready yet: the next try or the background monitor connects.
        }

        return _client.IsConnected;
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
            _context?.ReportStatus($"LIVE • {label} • {live.NowShowingSummary}{screenTag}", PluginHealthState.Connected, BuildStatusDetails(live));
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
