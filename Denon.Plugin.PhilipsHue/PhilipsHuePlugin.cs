using Denon.App.PluginSdk;
using Denon.Plugin.PhilipsHue.Models;
using Denon.Plugin.PhilipsHue.Services;
using Denon.Plugin.PhilipsHue.ViewModels;
using Denon.Plugin.PhilipsHue.Views;

namespace Denon.Plugin.PhilipsHue;

public sealed class PhilipsHuePlugin : IDenonPlugin, IPluginViewProvider, IAudioReactivePlugin
{
    private static readonly (double X, double Y, string Hex)[] BeatPalette =
    [
        (0.168, 0.340, "#00D4FF"), // Hi-Fi Cyan
        (0.561, 0.328, "#FB7185"), // Sunset Coral
        (0.195, 0.115, "#818CF8"), // Deep Indigo
        (0.510, 0.410, "#F59E0B"), // Warm Amber
        (0.385, 0.165, "#EC4899"), // Neon Magenta
        (0.214, 0.655, "#10B981"), // Emerald Pulse
    ];

    private IPluginContext? _context;
    private HueBridgeClient? _client;
    private HueEntertainmentStreamClient? _entertainmentClient;
    private HueDashboardViewModel? _viewModel;
    private HuePluginConfig _config = new();
    private bool _isStarted;

    /// <summary>Cancelled by StopAsync: the startup sync (refresh, then the DTLS stream) ends with the plugin.</summary>
    private CancellationTokenSource? _lifetimeCts;

    private Task _startupSync = Task.CompletedTask;

    /// <summary>One stream start at a time: a second start would close the first one's socket.</summary>
    private readonly SemaphoreSlim _dtlsStartGate = new(1, 1);

    // Real-time Music Sync state
    private volatile bool _musicSyncActive;
    private volatile bool _demoBeatRunning;
    private CancellationTokenSource? _demoBeatCts;
    private IReadOnlyList<HueRoomGroup> _cachedRooms = [];
    private IReadOnlyList<HueLightState> _cachedLights = [];
    private IReadOnlyList<HueEntertainmentArea> _entertainmentAreas = [];
    private string _albumDominantHex = "#00D4FF";
    private string _albumSecondaryHex = "#F59E0B";
    private string _albumTrackTitle = string.Empty;
    private long _lastSyncPushTickMs;
    private int _paletteIndex;
    private int _roundRobinLightIdx;
    private int _inFlightSyncPushes;

    public PluginManifest Manifest { get; } = new(
        Id: "philips-hue",
        Name: "Philips Hue Lighting",
        Version: "1.2.0",
        Author: "Denon Desktop",
        Description: "Control Philips Hue rooms, lights, colors, and scenes over your LAN — with real-time FFT Music Sync, Album-Art Ambient colors, and automatic Denon AVR Smart Scene lighting.",
        Category: "Smart Lighting",
        AccentHex: "#F59E0B",
        IconGlyph: "HUE",
        Capabilities: PluginCapabilities.DashboardUi
                      | PluginCapabilities.ReceiverEvents
                      | PluginCapabilities.SceneAutomation
                      | PluginCapabilities.Actions
                      | PluginCapabilities.LocalNetwork
                      | PluginCapabilities.AudioReactive
                      | PluginCapabilities.AlbumArtSync);

    internal HuePluginConfig Config => _config;

    public bool IsAudioSyncActive => _isStarted && _musicSyncActive && (_config.DemoMode || IsConfigured);

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        var sharedHttp = context.CreateHttpClient(allowLocalSelfSignedTls: true, timeout: TimeSpan.FromSeconds(6));
        _client = new HueBridgeClient(sharedHttp);
        _entertainmentClient = new HueEntertainmentStreamClient(sharedHttp);
        _config = await context.Storage.LoadConfigAsync(static () => new HuePluginConfig(), cancellationToken);
        _config.MusicSync ??= new HueMusicSyncConfig();
        _musicSyncActive = _config.MusicSync.Enabled;
        _viewModel = new HueDashboardViewModel(this);
        _viewModel.SyncFromPluginConfig(_config);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _isStarted = true;
        _lifetimeCts?.Dispose();
        _lifetimeCts = new CancellationTokenSource();
        var lifetime = _lifetimeCts.Token;
        if (_config.DemoMode || IsConfigured)
        {
            _context?.ReportStatus($"Connecting to {(_config.DemoMode ? "Demo Bridge" : _config.BridgeIp)}...", PluginHealthState.Idle);
            _startupSync = Task.Run(async () =>
            {
                try
                {
                    using var initCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    initCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await RefreshBridgeStateAsync(initCts.Token);
                    // Music Sync was on when the app closed: start the 50 Hz stream again, not only the 10 Hz REST path.
                    if (_musicSyncActive && !lifetime.IsCancellationRequested)
                    {
                        await EnsureDtlsStreamAsync(lifetime);
                        _viewModel?.SetDtlsStreamingState(_entertainmentClient?.IsStreaming == true);
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    // The plugin stopped during the startup sync.
                }
                catch (Exception ex)
                {
                    _context?.Logger.Warning($"Initial Hue sync deferred: {ex.Message}");
                    _context?.ReportStatus($"Bridge unreachable ({_config.BridgeIp})", PluginHealthState.Warning);
                }
            }, CancellationToken.None);
        }
        else
        {
            _context?.ReportStatus("Ready to discover & pair Hue Bridge (or try Demo Mode)", PluginHealthState.Idle);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _isStarted = false;
        StopDemoBeatSimulator();

        await EndStartupSyncAsync();

        if (_entertainmentClient is not null)
        {
            await _entertainmentClient.StopAsync();
        }
        _context?.ReportStatus("Stopped", PluginHealthState.Disabled);
    }

    public object? CreateDashboardView()
    {
        _viewModel ??= new HueDashboardViewModel(this);
        _viewModel.SyncFromPluginConfig(_config);
        _viewModel.ApplyAlbumArtPalette(_albumDominantHex, _albumSecondaryHex, _albumTrackTitle);
        _viewModel.ApplyEntertainmentAreas(_entertainmentAreas);
        return new HueDashboardView
        {
            DataContext = _viewModel,
        };
    }

    private static readonly PluginActionParameter[] RoomOnly =
    [
        new("roomId", "string", "Hue room/group id. Default: the automation target room set in the Hue dashboard."),
    ];

    public IReadOnlyList<PluginActionDescriptor> GetActions() =>
    [
        // Parameters and hints give MCP clients a typed schema. Destructive stays unset (null) where the
        // host's id guess already applies, so permission tiers do not change.
        new("hue.all_on", "All Hue Lights On", "Turn on every light on the paired Philips Hue Bridge.", "Master")
            { Parameters = [], IsDestructive = false },
        new("hue.all_off", "All Hue Lights Off", "Turn off every light on the paired Philips Hue Bridge.", "Master")
            { Parameters = [] },
        new("hue.movie_dim", "Cinema Dim (15%)", "Dim the target room to warm 15% cinema ambience.", "Ambience")
            { Parameters = RoomOnly, IsDestructive = false },
        new("hue.candlelight", "Late Night Candlelight", "Set the target room to soft 24% candlelight glow.", "Ambience")
            { Parameters = RoomOnly, IsDestructive = false },
        new("hue.hifi_cyan", "Hi-Fi Cyan Glow", "Set the target room to signature Denon cyan ambience.", "Ambience")
            { Parameters = RoomOnly, IsDestructive = false },
        new("hue.daylight", "Full Daylight (100%)", "Set the target room to crisp 100% daylight.", "Ambience")
            { Parameters = RoomOnly, IsDestructive = false },
        new("hue.music_sync_on", "Start Hue Music Sync", "Enable real-time FFT beat-reactive Hue lighting.", "Music Sync")
            { Parameters = [], IsDestructive = false },
        new("hue.music_sync_off", "Stop Hue Music Sync", "Disable real-time FFT beat-reactive Hue lighting.", "Music Sync")
            { Parameters = [], IsDestructive = false },
        new("hue.music_sync_toggle", "Toggle Hue Music Sync", "Toggle real-time beat-reactive Hue lighting on or off.", "Music Sync")
            { Parameters = [], IsDestructive = false },
        new("hue.set_color", "Set Custom Hue Color / Warmth", "Set any custom hex color (#RRGGBB), Kelvin warmth (2000-6500K), and brightness % on a room or all lights.", "Ambience")
        {
            Parameters =
            [
                new("hex", "string", "Color as #RRGGBB. Takes priority over kelvin."),
                new("kelvin", "integer", "White temperature 2000-6500 K (used when hex is not given)."),
                new("brightness", "integer", "Brightness 1-100 %."),
                new("room", "string", "Room name or id, or \"all\". Default: the automation target room."),
            ],
            IsDestructive = false,
        },
        new("hue.refresh", "Sync Bridge State", "Refresh rooms, lights, and scenes from the Hue Bridge.", "System")
            { Parameters = [], IsReadOnly = true },
    ];

    public async Task<PluginActionResult> ExecuteActionAsync(
        string actionId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isStarted)
        {
            return PluginActionResult.Fail("Philips Hue plugin is currently stopped.");
        }

        if (!_config.DemoMode && !IsConfigured)
        {
            return PluginActionResult.Fail("No Philips Hue Bridge paired (pair a Bridge or enable Demo Mode in the Hue dashboard).");
        }

        var targetRoom = parameters is not null && parameters.TryGetValue("roomId", out var explicitRoom) && !string.IsNullOrWhiteSpace(explicitRoom)
            ? explicitRoom
            : ResolveAutomationGroupId();

        switch (actionId.ToLowerInvariant())
        {
            case "hue.all_on":
                await SetGroupStateAsync("0", isOn: true, cancellationToken: cancellationToken);
                return PluginActionResult.Ok("Turned all Hue lights ON.");

            case "hue.all_off":
                await SetMusicSyncEnabledAsync(false, cancellationToken);
                await SetGroupStateAsync("0", isOn: false, cancellationToken: cancellationToken);
                return PluginActionResult.Ok("Turned all Hue lights OFF.");

            case "hue.movie_dim":
                await ApplyNamedPresetAsync(targetRoom, "cinema", cancellationToken);
                return PluginActionResult.Ok("Applied Cinema Dim (15%).");

            case "hue.candlelight":
                await ApplyNamedPresetAsync(targetRoom, "candle", cancellationToken);
                return PluginActionResult.Ok("Applied Late Night Candlelight.");

            case "hue.hifi_cyan":
                await ApplyNamedPresetAsync(targetRoom, "cyan", cancellationToken);
                return PluginActionResult.Ok("Applied Hi-Fi Cyan Glow.");

            case "hue.daylight":
                await ApplyNamedPresetAsync(targetRoom, "daylight", cancellationToken);
                return PluginActionResult.Ok("Applied Full Daylight (100%).");

            case "hue.music_sync_on":
                await SetMusicSyncEnabledAsync(true, cancellationToken);
                return PluginActionResult.Ok($"Hue Music Sync started ({_config.MusicSync.Mode}).");

            case "hue.music_sync_off":
                await SetMusicSyncEnabledAsync(false, cancellationToken);
                return PluginActionResult.Ok("Hue Music Sync stopped.");

            case "hue.music_sync_toggle":
                await SetMusicSyncEnabledAsync(!_musicSyncActive, cancellationToken);
                return PluginActionResult.Ok(_musicSyncActive ? "Hue Music Sync started." : "Hue Music Sync stopped.");

            case "hue.set_color":
            {
                var groupId = ResolveRequestedGroupId(parameters, targetRoom);
                int? brightness = parameters is not null && parameters.TryGetValue("brightness", out var briStr) && int.TryParse(briStr, out var briVal)
                    ? Math.Clamp(briVal, 1, 100)
                    : null;
                double? x = null;
                double? y = null;
                int? mirek = null;

                if (parameters is not null && parameters.TryGetValue("hex", out var hex) && !string.IsNullOrWhiteSpace(hex))
                {
                    var xy = HueColorConverter.HexToXy(hex.Trim());
                    x = xy.X;
                    y = xy.Y;
                }
                else if (parameters is not null && parameters.TryGetValue("kelvin", out var kStr) && int.TryParse(kStr, out var kelvin) && kelvin > 0)
                {
                    mirek = Math.Clamp((int)Math.Round(1_000_000.0 / kelvin), 153, 500);
                }

                await SetGroupStateAsync(
                    groupId,
                    isOn: true,
                    brightnessPercent: brightness,
                    colorTempMirek: mirek,
                    x: x,
                    y: y,
                    cancellationToken: cancellationToken);

                var parts = new List<string>();
                if (x.HasValue) parts.Add($"hex {parameters!["hex"]}");
                if (mirek.HasValue) parts.Add($"{parameters!["kelvin"]}K");
                if (brightness.HasValue) parts.Add($"{brightness.Value}%");
                if (!_config.DemoMode)
                {
                    _context?.Events.RecordEvent(
                        "color.set",
                        new Dictionary<string, object?>
                        {
                            ["groupId"] = groupId,
                            ["hex"] = x.HasValue ? parameters!["hex"] : null,
                            ["kelvin"] = mirek.HasValue ? parameters!["kelvin"] : null,
                            ["brightness"] = brightness,
                        },
                        origin: "plugin");
                }

                return PluginActionResult.Ok($"Set Philips Hue group '{groupId}' to {(parts.Count > 0 ? string.Join(" @ ", parts) : "ON")}.");
            }

            case "hue.refresh":
                await RefreshBridgeStateAsync(cancellationToken);
                return PluginActionResult.Ok("Synced rooms, lights, and scenes from Hue Bridge.");

            default:
                return PluginActionResult.Fail($"Unknown Hue action '{actionId}'.");
        }
    }

    public async Task OnReceiverEventAsync(PluginReceiverEvent receiverEvent, CancellationToken cancellationToken = default)
    {
        if (!_isStarted || (!_config.DemoMode && !IsConfigured))
        {
            return;
        }

        var rules = _config.Automation;
        var targetRoom = ResolveAutomationGroupId();

        switch (receiverEvent.EventType)
        {
            case PluginReceiverEventType.SceneActivated when rules.SyncWithSmartScenes:
                await HandleSceneAutomationAsync(targetRoom, receiverEvent.SceneId, receiverEvent.SceneName, cancellationToken);
                break;

            case PluginReceiverEventType.PowerChanged:
                if (receiverEvent.Snapshot.IsPoweredOn && rules.TurnOnWithReceiverPower)
                {
                    await SetGroupStateAsync(targetRoom, isOn: true, cancellationToken: cancellationToken);
                    LogAndNotifyAutomation("Denon Power ON → turned Hue room ON.");
                }
                else if (!receiverEvent.Snapshot.IsPoweredOn && rules.TurnOffWithReceiverStandby)
                {
                    await SetMusicSyncEnabledAsync(false, cancellationToken);
                    await SetGroupStateAsync(targetRoom, isOn: false, cancellationToken: cancellationToken);
                    LogAndNotifyAutomation("Denon Standby → turned Hue room OFF.");
                }

                break;

            case PluginReceiverEventType.PlaybackChanged when rules.CinemaPauseLights:
                if (string.Equals(receiverEvent.Snapshot.PlayState, "Paused", StringComparison.OrdinalIgnoreCase))
                {
                    await SetGroupStateAsync(targetRoom, isOn: true, brightnessPercent: rules.PauseBrightenPercent, cancellationToken: cancellationToken);
                    LogAndNotifyAutomation($"Playback Paused → brightened Hue to {rules.PauseBrightenPercent}%.");
                }
                else if (string.Equals(receiverEvent.Snapshot.PlayState, "Playing", StringComparison.OrdinalIgnoreCase))
                {
                    await SetGroupStateAsync(targetRoom, isOn: true, brightnessPercent: rules.ResumeDimPercent, cancellationToken: cancellationToken);
                    LogAndNotifyAutomation($"Playback Resumed → dimmed Hue to {rules.ResumeDimPercent}%.");
                }

                break;
        }
    }

    public void OnAudioFrame(in PluginAudioFrame frame)
    {
        if (!IsAudioSyncActive)
        {
            return;
        }

        // Update live 3-band FFT & Beat meters in the Hue Dashboard UI
        _viewModel?.UpdateLiveAudioMeters(frame.BassEnergy, frame.MidEnergy, frame.TrebleEnergy, frame.IsBeat, frame.IsLiveSignal);

        if (!frame.IsLiveSignal)
        {
            return;
        }

        DispatchMusicSyncFrame(frame);
    }

    public async Task OnAlbumArtPaletteChangedAsync(PluginAlbumArtPalette palette, CancellationToken cancellationToken = default)
    {
        _albumDominantHex = palette.DominantHex;
        _albumSecondaryHex = palette.SecondaryHex;
        _albumTrackTitle = palette.TrackTitle;
        _viewModel?.ApplyAlbumArtPalette(palette.DominantHex, palette.SecondaryHex, palette.TrackTitle);

        // If Album-Art Ambient mode is enabled, immediately bathe the room in the new track's cover color
        if (IsAudioSyncActive && _config.MusicSync.Mode == HueMusicSyncMode.AlbumArtAmbient)
        {
            var (x, y) = HueColorConverter.HexToXy(palette.DominantHex);
            var targetRoom = ResolveAutomationGroupId();
            try
            {
                await SetGroupStateAsync(
                    targetRoom,
                    isOn: true,
                    brightnessPercent: Math.Clamp((_config.MusicSync.MinBrightness + _config.MusicSync.MaxBrightness) / 2, 15, 90),
                    x: x,
                    y: y,
                    cancellationToken: cancellationToken);
                LogAndNotifyAutomation($"Album-Art Ambient → {palette.DominantHex} ({palette.TrackTitle})");
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopDemoBeatSimulator();
        if (_entertainmentClient is not null)
        {
            await _entertainmentClient.DisposeAsync();
            _entertainmentClient = null;
        }
        _client?.Dispose();
        _client = null;
    }

    internal async Task<IReadOnlyList<HueDiscoveredBridge>> DiscoverBridgesAsync(CancellationToken cancellationToken = default)
    {
        EnsureClient();
        var bridges = await _client!.DiscoverBridgesAsync(cancellationToken);
        _context?.Logger.Info($"Hue discovery completed: found {bridges.Count} bridge(s).");
        return bridges;
    }

    internal async Task<HuePairingResult> TryPairBridgeAsync(string bridgeIp, bool useHttps, CancellationToken cancellationToken = default)
    {
        EnsureClient();
        var result = await _client!.TryPairAsync(bridgeIp, useHttps, cancellationToken);
        if (result.Success && !string.IsNullOrWhiteSpace(result.ApplicationKey))
        {
            _config.BridgeIp = bridgeIp.Trim();
            _config.ApplicationKey = result.ApplicationKey;
            if (!string.IsNullOrWhiteSpace(result.ClientKey))
            {
                _config.ClientKey = result.ClientKey;
            }

            _config.UseHttps = useHttps;
            _config.DemoMode = false;
            await SaveConfigAsync(cancellationToken);
            _viewModel?.SyncFromPluginConfig(_config);
            _context?.Logger.Info($"Paired with Philips Hue Bridge at {_config.BridgeIp}.");
        }

        return result;
    }

    internal async Task SetDemoModeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _config.DemoMode = enabled;
        if (!enabled)
        {
            StopDemoBeatSimulator();
        }

        await SaveConfigAsync(cancellationToken);
        _viewModel?.SyncFromPluginConfig(_config);

        if (enabled || IsConfigured)
        {
            await RefreshBridgeStateAsync(cancellationToken);
        }
        else
        {
            _context?.ReportStatus("Ready to discover & pair Hue Bridge (or try Demo Mode)", PluginHealthState.Idle);
        }
    }

    /// <summary>
    /// Cancels the startup sync and waits for it. When it is starting the stream right now, a stop (or an unlink
    /// that clears the keys) must come after it, or the bridge area stays active.
    /// </summary>
    private async Task EndStartupSyncAsync()
    {
        _lifetimeCts?.Cancel();
        try { await _startupSync; } catch { /* already logged, or cancelled */ }
    }

    internal async Task DisconnectBridgeAsync(CancellationToken cancellationToken = default)
    {
        await EndStartupSyncAsync();
        StopDemoBeatSimulator();
        if (_entertainmentClient is not null)
        {
            await _entertainmentClient.StopAsync(); // Release the area now, not after the bridge's timeout.
        }

        _musicSyncActive = false;
        _config.MusicSync.Enabled = false;
        _config.ApplicationKey = string.Empty;
        _config.ClientKey = string.Empty;
        _config.DemoMode = false;
        await SaveConfigAsync(cancellationToken);
        _viewModel?.SyncFromPluginConfig(_config);
        _context?.ReportStatus("Bridge unlinked — ready to pair", PluginHealthState.Idle);
    }

    /// <summary>
    /// Starts the 50 Hz Entertainment (DTLS) stream on the chosen area, or the first one. Without it, Music Sync
    /// falls back to REST at about 10 Hz. Logs why when the stream does not start.
    /// </summary>
    private async Task EnsureDtlsStreamAsync(CancellationToken cancellationToken)
    {
        // The startup sync and a "Music Sync on" (button or action) can both get here during one handshake:
        // the second waits, then sees the stream running and returns.
        await _dtlsStartGate.WaitAsync(cancellationToken);
        try
        {
            await StartDtlsStreamLockedAsync(cancellationToken);
        }
        finally
        {
            _dtlsStartGate.Release();
        }
    }

    private async Task StartDtlsStreamLockedAsync(CancellationToken cancellationToken)
    {
        if (_config.DemoMode || _entertainmentClient is null || string.IsNullOrWhiteSpace(_config.ClientKey) ||
            _entertainmentClient.IsStreaming)
        {
            return;
        }

        var area = _entertainmentAreas.FirstOrDefault(a =>
                       string.Equals(a.Id, _config.MusicSync.SelectedEntertainmentAreaId, StringComparison.OrdinalIgnoreCase))
                   ?? _entertainmentAreas.FirstOrDefault();
        if (area is null)
        {
            _context?.Logger.Info("Hue Music Sync uses REST: the bridge has no entertainment area.");
            return;
        }

        if (await _entertainmentClient.StartAsync(_config, area, cancellationToken))
        {
            // The start takes a moment. When the plugin stopped or Music Sync went off meanwhile, release the area.
            if (!_isStarted || !_musicSyncActive || _config.DemoMode || string.IsNullOrWhiteSpace(_config.ClientKey))
            {
                await _entertainmentClient.StopAsync();
                return;
            }

            _context?.Logger.Info($"Started 50 Hz Hue Entertainment DTLS stream on '{area.Name}' ({area.ChannelCount} ch).");
        }
        else
        {
            _context?.Logger.Warning($"Hue Entertainment DTLS stream on '{area.Name}' did not start; Music Sync uses REST.");
        }
    }

    internal async Task RefreshBridgeStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureClient();
        var (rooms, lights, scenes) = await _client!.FetchFullStateAsync(_config, cancellationToken);
        _cachedRooms = rooms;
        _cachedLights = lights;
        _entertainmentAreas = await _client.FetchEntertainmentAreasAsync(_config, cancellationToken);

        _viewModel?.ApplyBridgeState(rooms, lights, scenes, _config.SelectedRoomId);
        _viewModel?.ApplyEntertainmentAreas(_entertainmentAreas);

        var activeCount = lights.Count(l => l.IsOn);
        var modeTag = _config.DemoMode ? "Demo Bridge" : _config.BridgeIp;
        var syncTag = _musicSyncActive ? $" • Sync: {_config.MusicSync.Mode}" : string.Empty;
        _context?.ReportStatus(
            $"{modeTag} • {activeCount}/{lights.Count} lights on{syncTag}",
            PluginHealthState.Connected);
    }

    internal async Task SetMusicSyncEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _musicSyncActive = enabled;
        _config.MusicSync.Enabled = enabled;
        if (!enabled)
        {
            // The startup sync keeps running (it reads the rooms, lights and areas). If it is starting the stream
            // now, that start sees Music Sync off when it finishes and stops the stream itself.
            StopDemoBeatSimulator();
            if (_entertainmentClient is not null)
            {
                await _entertainmentClient.StopAsync();
            }
        }
        else
        {
            if (!_config.DemoMode && IsConfigured && (_entertainmentAreas.Count == 0 || _cachedLights.Count == 0))
            {
                // The first bridge read did not finish (or failed): read it now, so the stream has an area and the
                // REST path has lights.
                try
                {
                    await RefreshBridgeStateAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _context?.Logger.Warning($"Hue bridge read before Music Sync failed: {ex.Message}");
                }
            }

            await EnsureDtlsStreamAsync(cancellationToken);
        }

        await SaveConfigAsync(cancellationToken);
        _viewModel?.SetDtlsStreamingState(_entertainmentClient?.IsStreaming == true);
        _viewModel?.SyncFromPluginConfig(_config);

        var modeTag = _config.DemoMode ? "Demo Bridge" : _config.BridgeIp;
        var transportTag = _entertainmentClient?.IsStreaming == true ? "50 Hz DTLS" : "LAN Sync";
        if (!_config.DemoMode)
        {
            _context?.Events.RecordEvent(
                "music_sync.changed",
                new Dictionary<string, object?>
                {
                    ["enabled"] = enabled,
                    ["mode"] = _config.MusicSync.Mode.ToString(),
                    ["transport"] = transportTag,
                },
                origin: "plugin");
        }

        _context?.ReportStatus(
            enabled
                ? $"{modeTag} • {transportTag} LIVE ({_config.MusicSync.Mode})"
                : $"{modeTag} • {_cachedLights.Count(l => l.IsOn)}/{_cachedLights.Count} lights on",
            _config.DemoMode || IsConfigured ? PluginHealthState.Connected : PluginHealthState.Idle);
    }

    internal async Task UpdateMusicSyncConfigAsync(HueMusicSyncConfig updated, CancellationToken cancellationToken = default)
    {
        _config.MusicSync = updated;
        _musicSyncActive = updated.Enabled;
        await SaveConfigAsync(cancellationToken);
        _viewModel?.SyncFromPluginConfig(_config);
    }

    internal void ToggleDemoBeatSimulator()
    {
        if (_demoBeatRunning)
        {
            StopDemoBeatSimulator();
            return;
        }

        _demoBeatRunning = true;
        _musicSyncActive = true;
        _config.MusicSync.Enabled = true;
        _viewModel?.SyncFromPluginConfig(_config);
        _viewModel?.SetDemoBeatState(true);

        _demoBeatCts = new CancellationTokenSource();
        var token = _demoBeatCts.Token;

        _ = Task.Run(async () =>
        {
            var tick = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var isBeat = tick % 12 == 0; // ~125 BPM at 40ms/tick
                    var phase = (tick % 12) / 12.0;
                    var bass = isBeat ? 0.94 : Math.Max(0.12, Math.Exp(-phase * 4.2) * 0.85);
                    var mid = 0.35 + (0.42 * Math.Abs(Math.Sin(tick * 0.19)));
                    var treble = 0.25 + (0.50 * Math.Abs(Math.Cos(tick * 0.37)));

                    var frame = new PluginAudioFrame(
                        LeftLevel: bass,
                        RightLevel: (bass + mid) * 0.5,
                        BassEnergy: bass,
                        MidEnergy: mid,
                        TrebleEnergy: treble,
                        BeatIntensity: isBeat ? 0.95 : bass * 0.3,
                        IsBeat: isBeat,
                        IsLiveSignal: true,
                        TimestampMs: Environment.TickCount64);

                    OnAudioFrame(in frame);
                    tick++;
                    await Task.Delay(40, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _demoBeatRunning = false;
                _viewModel?.SetDemoBeatState(false);
            }
        }, token);
    }

    internal async Task SetLightStateAsync(
        string lightId,
        bool? isOn = null,
        int? brightnessPercent = null,
        int? colorTempMirek = null,
        double? x = null,
        double? y = null,
        CancellationToken cancellationToken = default)
    {
        EnsureClient();
        await _client!.SetLightStateAsync(_config, lightId, isOn, brightnessPercent, colorTempMirek, x, y, cancellationToken: cancellationToken);
        await RefreshBridgeStateAsync(cancellationToken);
    }

    internal async Task SetGroupStateAsync(
        string groupId,
        bool? isOn = null,
        int? brightnessPercent = null,
        int? colorTempMirek = null,
        double? x = null,
        double? y = null,
        CancellationToken cancellationToken = default)
    {
        EnsureClient();
        await _client!.SetGroupActionAsync(_config, groupId, isOn, brightnessPercent, colorTempMirek, x, y, cancellationToken: cancellationToken);
        await RefreshBridgeStateAsync(cancellationToken);
    }

    internal async Task ApplyColorPresetAsync(string groupId, HueColorPreset preset, CancellationToken cancellationToken = default)
    {
        EnsureClient();
        await _client!.SetGroupActionAsync(
            _config,
            groupId,
            isOn: true,
            brightnessPercent: preset.BrightnessPercent,
            colorTempMirek: preset.ColorTempMirek,
            x: preset.X,
            y: preset.Y,
            transitionDeciseconds: 8,
            cancellationToken: cancellationToken);
        await RefreshBridgeStateAsync(cancellationToken);
    }

    internal async Task ActivateNativeSceneAsync(string groupId, string sceneId, CancellationToken cancellationToken = default)
    {
        EnsureClient();
        await _client!.SetGroupActionAsync(_config, groupId, sceneId: sceneId, cancellationToken: cancellationToken);
        await RefreshBridgeStateAsync(cancellationToken);
    }

    internal async Task SaveSelectedRoomAsync(string roomId)
    {
        _config.SelectedRoomId = roomId;
        await SaveConfigAsync();
    }

    internal async Task UpdateAutomationRulesAsync(HueAutomationRules rules)
    {
        _config.Automation = rules;
        await SaveConfigAsync();
    }

    private void DispatchMusicSyncFrame(in PluginAudioFrame frame)
    {
        if (_client is null)
        {
            return;
        }

        var syncCfg = _config.MusicSync;
        var sens = Math.Clamp(syncCfg.Sensitivity, 0.4, 2.5);
        var minBri = Math.Clamp(syncCfg.MinBrightness, 5, 90);
        var maxBri = Math.Clamp(syncCfg.MaxBrightness, minBri + 5, 100);
        var bass = Math.Clamp(frame.BassEnergy * sens, 0.0, 1.0);
        var mid = Math.Clamp(frame.MidEnergy * sens, 0.0, 1.0);
        var treble = Math.Clamp(frame.TrebleEnergy * sens, 0.0, 1.0);

        if (frame.IsBeat)
        {
            _paletteIndex = (_paletteIndex + 1) % BeatPalette.Length;
        }

        // Fast Path: True 50 Hz Hue Entertainment DTLS/UDP streaming (20ms cadence, zero REST overhead!)
        if (_entertainmentClient is { IsStreaming: true })
        {
            var nowDtls = Environment.TickCount64;
            if (nowDtls - _lastSyncPushTickMs < 20)
            {
                return;
            }

            _lastSyncPushTickMs = nowDtls;
            var chCount = Math.Clamp(_entertainmentClient.ActiveChannelCount, 1, 20);
            Span<(byte R, byte G, byte B)> rgbChannels = stackalloc (byte, byte, byte)[chCount];
            for (var ch = 0; ch < chCount; ch++)
            {
                rgbChannels[ch] = ComputeSyncChannelRgb(syncCfg.Mode, ch, bass, mid, treble, frame.IsBeat, minBri, maxBri);
            }

            if (!_entertainmentClient.TrySendRgbFrame(rgbChannels))
            {
                _viewModel?.SetDtlsStreamingState(false);
            }

            return;
        }

        // Fallback Path: High-speed LAN REST staggered multi-light updates (~10-12 Hz) or Demo Mode
        var minIntervalMs = _config.DemoMode ? 65 : 95;
        if (frame.IsBeat)
        {
            minIntervalMs = _config.DemoMode ? 45 : 70;
        }

        var now = Environment.TickCount64;
        if (now - _lastSyncPushTickMs < minIntervalMs)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _inFlightSyncPushes, 1, 0) != 0)
        {
            return;
        }

        _lastSyncPushTickMs = now;

        var targetLights = GetTargetSyncLightIds();
        if (targetLights.Count == 0)
        {
            Interlocked.Exchange(ref _inFlightSyncPushes, 0);
            return;
        }

        var lightIdx = _roundRobinLightIdx++ % targetLights.Count;
        var targetLightId = targetLights[lightIdx];

        int bri;
        double x, y;
        int transitionDs;

        switch (syncCfg.Mode)
        {
            case HueMusicSyncMode.SpectrumSplit:
            {
                // Split lights across Bass (deep magenta/amber), Mids (cyan/indigo), Treble (electric blue/emerald)
                var band = lightIdx % 3;
                var energy = band switch { 0 => bass, 1 => mid, _ => treble };
                bri = (int)Math.Round(minBri + ((maxBri - minBri) * energy));
                (x, y, _) = band switch
                {
                    0 => frame.IsBeat ? (0.561, 0.328, "#FB7185") : (0.510, 0.410, "#F59E0B"),
                    1 => (0.168, 0.340, "#00D4FF"),
                    _ => (0.195, 0.115, "#818CF8"),
                };
                transitionDs = 1;
                break;
            }

            case HueMusicSyncMode.AlbumArtAmbient:
            {
                var loudness = Math.Clamp((bass * 0.55) + (mid * 0.35) + (treble * 0.10), 0.0, 1.0);
                bri = (int)Math.Round(minBri + ((maxBri - minBri) * loudness));
                var hex = (lightIdx % 2 == 0) ? _albumDominantHex : _albumSecondaryHex;
                (x, y) = HueColorConverter.HexToXy(hex);
                transitionDs = 2;
                break;
            }

            case HueMusicSyncMode.CinemaSubtle:
            {
                var calmEnergy = Math.Clamp((bass * 0.5) + (mid * 0.5), 0.0, 1.0);
                var subtleCeil = Math.Min(maxBri, 60);
                bri = (int)Math.Round(minBri + ((subtleCeil - minBri) * calmEnergy));
                (x, y) = frame.IsBeat ? (0.485, 0.415) : (0.455, 0.405);
                transitionDs = 3;
                break;
            }

            default: // BassPulse
            {
                var punch = frame.IsBeat ? Math.Min(1.0, bass * 1.25 + 0.15) : bass;
                bri = (int)Math.Round(minBri + ((maxBri - minBri) * punch));
                (x, y, _) = BeatPalette[_paletteIndex];
                transitionDs = frame.IsBeat ? 0 : 1;
                break;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _client.PushFastSyncLightAsync(_config, targetLightId, bri, x, y, transitionDs);
                if (_config.DemoMode)
                {
                    var (rooms, lights, scenes) = await _client.FetchFullStateAsync(_config);
                    _cachedRooms = rooms;
                    _cachedLights = lights;
                    _viewModel?.ApplyLiveLightPreviews(lights);
                }
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _inFlightSyncPushes, 0);
            }
        });
    }

    private (byte R, byte G, byte B) ComputeSyncChannelRgb(
        HueMusicSyncMode mode,
        int channelIdx,
        double bass,
        double mid,
        double treble,
        bool isBeat,
        int minBri,
        int maxBri)
    {
        string hex;
        double energy;

        switch (mode)
        {
            case HueMusicSyncMode.SpectrumSplit:
            {
                var band = channelIdx % 3;
                energy = band switch { 0 => bass, 1 => mid, _ => treble };
                hex = band switch
                {
                    0 => isBeat ? "#FB7185" : "#F59E0B",
                    1 => "#00D4FF",
                    _ => "#818CF8",
                };
                break;
            }

            case HueMusicSyncMode.AlbumArtAmbient:
                energy = Math.Clamp((bass * 0.55) + (mid * 0.35) + (treble * 0.10), 0.0, 1.0);
                hex = (channelIdx % 2 == 0) ? _albumDominantHex : _albumSecondaryHex;
                break;

            case HueMusicSyncMode.CinemaSubtle:
                energy = Math.Clamp((bass * 0.5) + (mid * 0.5), 0.0, 1.0) * 0.6;
                hex = isBeat ? "#F59E0B" : "#D97706";
                break;

            default:
                energy = isBeat ? Math.Min(1.0, bass * 1.25 + 0.15) : bass;
                hex = BeatPalette[_paletteIndex].Hex;
                break;
        }

        var scale = Math.Clamp((minBri + ((maxBri - minBri) * energy)) / 100.0, 0.05, 1.0);
        var clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 &&
            byte.TryParse(clean[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(clean[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(clean[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return (
                (byte)Math.Clamp((int)Math.Round(r * scale), 0, 255),
                (byte)Math.Clamp((int)Math.Round(g * scale), 0, 255),
                (byte)Math.Clamp((int)Math.Round(b * scale), 0, 255));
        }

        return (0, 180, 255);
    }

    private IReadOnlyList<string> GetTargetSyncLightIds()
    {
        var groupId = ResolveAutomationGroupId();
        if (!string.IsNullOrWhiteSpace(groupId) && groupId != "0")
        {
            var room = _cachedRooms.FirstOrDefault(r => string.Equals(r.Id, groupId, StringComparison.OrdinalIgnoreCase));
            if (room is { LightIds.Count: > 0 })
            {
                return room.LightIds;
            }
        }

        return _cachedLights.Select(static l => l.Id).ToList();
    }

    private void StopDemoBeatSimulator()
    {
        _demoBeatCts?.Cancel();
        _demoBeatCts?.Dispose();
        _demoBeatCts = null;
        _demoBeatRunning = false;
        _viewModel?.SetDemoBeatState(false);
    }

    private async Task HandleSceneAutomationAsync(string targetRoom, string? sceneId, string? sceneName, CancellationToken cancellationToken)
    {
        var key = $"{sceneId} {sceneName}".ToLowerInvariant();
        string presetId;
        if (key.Contains("movie") || key.Contains("cine"))
        {
            presetId = "cinema";
        }
        else if (key.Contains("late") || key.Contains("night") || key.Contains("nite"))
        {
            presetId = "candle";
        }
        else if (key.Contains("hifi") || key.Contains("hi-fi") || key.Contains("audiophile") || key.Contains("pure"))
        {
            presetId = "cyan";
        }
        else if (key.Contains("morning") || key.Contains("jazz") || key.Contains("coffee"))
        {
            presetId = "warm-white";
        }
        else if (key.Contains("party"))
        {
            presetId = "sunset";
        }
        else
        {
            presetId = "warm-white";
        }

        await ApplyNamedPresetAsync(targetRoom, presetId, cancellationToken);
        LogAndNotifyAutomation($"Smart Scene '{sceneName ?? sceneId}' → applied '{presetId}' Hue ambience.");
    }

    private async Task ApplyNamedPresetAsync(string groupId, string presetId, CancellationToken cancellationToken)
    {
        var preset = HueColorPreset.Defaults.FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase))
                     ?? HueColorPreset.Defaults[0];
        await ApplyColorPresetAsync(groupId, preset, cancellationToken);
        if (!_config.DemoMode)
        {
            _context?.Events.RecordEvent(
                "preset.applied",
                new Dictionary<string, object?>
                {
                    ["groupId"] = groupId,
                    ["presetId"] = preset.Id,
                    ["presetName"] = preset.Name,
                },
                origin: "plugin");
        }
    }

    private string ResolveRequestedGroupId(IReadOnlyDictionary<string, string>? parameters, string fallbackGroupId)
    {
        if (parameters is null || !parameters.TryGetValue("room", out var roomRaw) || string.IsNullOrWhiteSpace(roomRaw))
        {
            return fallbackGroupId;
        }

        var trimmed = roomRaw.Trim();
        if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) || trimmed == "0")
        {
            return "0";
        }

        var matched = _cachedRooms.FirstOrDefault(r =>
            r.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
            r.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

        return matched?.Id ?? fallbackGroupId;
    }

    private string ResolveAutomationGroupId()
    {
        if (!string.IsNullOrWhiteSpace(_config.Automation.TargetRoomId))
        {
            return _config.Automation.TargetRoomId;
        }

        if (!string.IsNullOrWhiteSpace(_config.SelectedRoomId))
        {
            return _config.SelectedRoomId;
        }

        return "0";
    }

    private void LogAndNotifyAutomation(string message)
    {
        _context?.Logger.Info(message);
        _viewModel?.NotifyAutomationEvent(message);
    }

    private bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.BridgeIp) && !string.IsNullOrWhiteSpace(_config.ApplicationKey);

    private void EnsureClient()
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Philips Hue plugin has not been initialized yet.");
        }
    }

    private Task SaveConfigAsync(CancellationToken cancellationToken = default) =>
        _context?.Storage.SaveConfigAsync(_config, cancellationToken) ?? Task.CompletedTask;
}
