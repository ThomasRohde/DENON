using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Denon.App.PluginSdk;

namespace Denon.Plugin.HomeAssistant;

public sealed class HomeAssistantConfig
{
    public string BaseUrl { get; set; } = "http://homeassistant.local:8123";

    public string AccessToken { get; set; } = string.Empty;

    public string WebhookId { get; set; } = string.Empty;

    public bool DemoMode { get; set; }

    public bool PushReceiverEventsToHa { get; set; } = true;

    /// <summary>
    /// Keep a WebSocket open to Home Assistant, so tiles change when an entity changes. REST stays the way to read
    /// the full list (at start, on Refresh and when the socket connects again).
    /// </summary>
    public bool LiveUpdates { get; set; } = true;
}

public sealed record HaEntityItem(string EntityId, string FriendlyName, string Domain, string State)
{
    public string DomainBadge => Domain.ToLowerInvariant() switch
    {
        "scene" => "SC",
        "script" => "SR",
        "switch" => "SW",
        "light" => "LT",
        "input_boolean" => "TG",
        _ => "HA",
    };

    public bool IsOn => State.Equals("on", StringComparison.OrdinalIgnoreCase);

    public string StateLabel => Domain.ToLowerInvariant() switch
    {
        "scene" => "Activate",
        "script" => "Run",
        _ => IsOn ? "ON" : "OFF",
    };
}

public sealed class HomeAssistantPlugin : IDenonPlugin, IPluginViewProvider
{
    private IPluginContext? _context;
    private HttpClient? _http;
    private HomeAssistantConfig _config = new();
    private HomeAssistantViewModel? _viewModel;
    private bool _isStarted;
    private readonly SemaphoreSlim _liveGate = new(1, 1);
    private HomeAssistantLiveSocket? _live;
    private int _liveGeneration;
    private string? _rejectedToken;
    private string? _lastLoggedLive = "off";

    public PluginManifest Manifest { get; } = new(
        Id: "home-assistant",
        Name: "Home Assistant Bridge",
        Version: typeof(HomeAssistantPlugin).Assembly.GetName().Version!.ToString(3),
        Author: "Denon Desktop",
        Description: "Connect Denon Desktop to your Home Assistant server over REST & Webhooks: trigger HA scenes/scripts/switches and push real-time Denon AVR telemetry sensors.",
        Category: "Smart Home Hub",
        AccentHex: "#10B981",
        IconGlyph: "HA",
        Capabilities: PluginCapabilities.DashboardUi
                      | PluginCapabilities.ReceiverEvents
                      | PluginCapabilities.SceneAutomation
                      | PluginCapabilities.Actions
                      | PluginCapabilities.LocalNetwork);

    internal HomeAssistantConfig Config => _config;

    internal HomeAssistantViewModel? ViewModel => _viewModel;

    /// <summary>Timing for the WebSocket. Tests set short times.</summary>
    internal HomeAssistantLiveSocketOptions LiveOptions { get; set; } = new();

    internal HaLiveState LiveState => _live?.State ?? HaLiveState.Off;

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _http = context.CreateHttpClient(allowLocalSelfSignedTls: true, timeout: TimeSpan.FromSeconds(6));
        _config = await context.Storage.LoadConfigAsync(static () => new HomeAssistantConfig(), cancellationToken);
        _viewModel = new HomeAssistantViewModel(this);
        _viewModel.SyncFromConfig(_config);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _isStarted = true;
        if (_config.DemoMode || IsConfigured)
        {
            _context?.ReportStatus("Connecting to Home Assistant...", PluginHealthState.Idle);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var initCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await FetchEntitiesAsync(initCts.Token);
                }
                catch (Exception ex)
                {
                    _context?.ReportStatus($"HA unreachable: {ex.Message}", PluginHealthState.Warning);
                }
            });
        }
        else
        {
            _context?.ReportStatus("Enter Home Assistant URL & Long-Lived Token (or Demo Mode)", PluginHealthState.Idle);
        }

        await RestartLiveSocketAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _isStarted = false;
        await RestartLiveSocketAsync();
        _context?.ReportStatus("Stopped", PluginHealthState.Disabled);
    }

    public object? CreateDashboardView()
    {
        _viewModel ??= new HomeAssistantViewModel(this);
        _viewModel.SyncFromConfig(_config);
        return new HomeAssistantDashboardView { DataContext = _viewModel };
    }

    public IReadOnlyList<PluginActionDescriptor> GetActions() =>
    [
        // Parameters and hints give MCP clients a typed schema.
        new("ha.sync_sensor", "Push Denon Status to HA", "Update sensor.denon_desktop_receiver in Home Assistant right now.", "Telemetry")
            { Parameters = [], IsDestructive = false },
        new("ha.fire_webhook", "Fire Configured Webhook", "Send a JSON event payload to your configured Home Assistant webhook.", "Webhook")
            { Parameters = [], IsDestructive = false },
        new("ha.refresh", "Refresh HA Entities", "Reload scenes, scripts, and switches from Home Assistant.", "System")
            { Parameters = [], IsReadOnly = true },
        new("ha.trigger_entity", "Trigger HA Entity", "Activate a Home Assistant scene, run a script, or toggle a switch.", "Entities")
        {
            Parameters = [new("entityId", "string", "Entity id, for example scene.movie_time, script.close_theater_curtains or switch.subwoofer_smart_plug.", Required: true)],
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
            return PluginActionResult.Fail("Home Assistant plugin is disabled.");
        }

        if (!_config.DemoMode && !IsConfigured)
        {
            return PluginActionResult.Fail("Configure Home Assistant URL & Long-Lived Access Token (or enable Demo Mode).");
        }

        try
        {
            switch (actionId.ToLowerInvariant())
            {
                case "ha.sync_sensor":
                    await PushSensorStateAsync(_context?.Receiver.CurrentSnapshot ?? PluginReceiverSnapshot.Empty, null, cancellationToken);
                    return PluginActionResult.Ok("Pushed live Denon AVR telemetry to Home Assistant.");

                case "ha.fire_webhook":
                    await TriggerWebhookAsync("manual_trigger", null, cancellationToken);
                    return PluginActionResult.Ok("Fired Home Assistant webhook.");

                case "ha.refresh":
                    var entities = await FetchEntitiesAsync(cancellationToken);
                    return PluginActionResult.Ok($"Loaded {entities.Count} Home Assistant scenes/scripts/switches.");

                case "ha.trigger_entity":
                    var entityId = parameters is not null && parameters.TryGetValue("entityId", out var eid) ? eid : null;
                    if (string.IsNullOrWhiteSpace(entityId))
                    {
                        return PluginActionResult.Fail("Missing 'entityId' parameter.");
                    }

                    await TriggerEntityAsync(entityId, cancellationToken);
                    return PluginActionResult.Ok($"Triggered Home Assistant entity '{entityId}'.");

                default:
                    return PluginActionResult.Fail($"Unknown Home Assistant action '{actionId}'.");
            }
        }
        catch (Exception ex)
        {
            return PluginActionResult.Fail(ex.Message);
        }
    }

    public async Task OnReceiverEventAsync(PluginReceiverEvent receiverEvent, CancellationToken cancellationToken = default)
    {
        if (!_isStarted || !_config.PushReceiverEventsToHa || (!_config.DemoMode && !IsConfigured))
        {
            return;
        }

        try
        {
            await PushSensorStateAsync(receiverEvent.Snapshot, receiverEvent.SceneName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(_config.WebhookId))
            {
                await TriggerWebhookAsync(receiverEvent.EventType.ToString(), receiverEvent.SceneName, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _context?.Logger.Warning($"Home Assistant push warning: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _isStarted = false;
        await RestartLiveSocketAsync();
        _http?.Dispose();
        _http = null;
    }

    internal async Task<IReadOnlyList<HaEntityItem>> FetchEntitiesAsync(CancellationToken ct = default)
    {
        if (_config.DemoMode)
        {
            IReadOnlyList<HaEntityItem> demo =
            [
                new("scene.movie_time", "Movie Time Blinds & Lights", "scene", "scening"),
                new("scene.evening_relax", "Evening Relax", "scene", "scening"),
                new("script.close_theater_curtains", "Close Theater Curtains", "script", "off"),
                new("switch.subwoofer_smart_plug", "Subwoofer Smart Plug", "switch", "on"),
                new("switch.amp_bias_strip", "Amplifier Cooling Fan", "switch", "on"),
                new("input_boolean.guest_mode", "Guest Mode", "input_boolean", "off"),
                new("input_boolean.vacation_mode", "Vacation Mode", "input_boolean", "on"),
            ];
            _viewModel?.ApplyEntities(demo);
            _context?.ReportStatus($"Demo HA • {demo.Count} controllable entities", PluginHealthState.Connected);
            return demo;
        }

        EnsureHttp();
        using var req = BuildRequest(HttpMethod.Get, "/api/states");
        using var resp = await _http!.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<HaEntityItem>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (HomeAssistantWebSocketProtocol.ParseState(item) is { } entity && IsListedDomain(entity.Domain))
                {
                    list.Add(entity);
                }
            }
        }

        var ordered = OrderForDashboard(list).Take(MaxEntities).ToList();
        _viewModel?.ApplyEntities(ordered);
        var countText = list.Count > MaxEntities ? $"{ordered.Count} of {list.Count}" : $"{ordered.Count}";
        _context?.ReportStatus($"Connected • {countText} entities synced", PluginHealthState.Connected);
        return ordered;
    }

    internal async Task TriggerEntityAsync(string entityId, CancellationToken ct = default)
    {
        if (!TryParseEntityId(entityId, out var domain))
        {
            throw new ArgumentException(
                $"'{entityId}' is not a scene, script, switch, light or input_boolean entity id.", nameof(entityId));
        }

        entityId = entityId.Trim();
        if (_config.DemoMode)
        {
            var current = _viewModel?.GetEntity(entityId);
            if (current is not null && current.Domain is "switch" or "light" or "input_boolean")
            {
                var nextState = current.IsOn ? "off" : "on";
                _viewModel?.UpdateEntityState(entityId, nextState);
            }

            _viewModel?.SetStatus($"Demo triggered '{entityId}'.");
            return;
        }

        EnsureHttp();
        var service = domain is "scene" or "script" ? "turn_on" : "toggle";

        using var req = BuildRequest(HttpMethod.Post, $"/api/services/{domain}/{service}", new { entity_id = entityId });
        using var resp = await _http!.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        _viewModel?.SetStatus($"Called {domain}.{service} on {entityId}.");
        if (IsLive)
        {
            // The state_changed event updates the tile. No REST read is needed.
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, CancellationToken.None);
                if (_isStarted)
                {
                    await FetchEntitiesAsync(CancellationToken.None);
                }
            }
            catch { }
        });
    }

    internal const string NotConfiguredMessage = "Enter the Home Assistant URL and a long-lived access token to connect.";

    /// <summary>Most entities the dashboard lists. Scenes, scripts and switches come first.</summary>
    internal const int MaxEntities = 300;

    /// <summary>The entity domains the dashboard lists and can trigger.</summary>
    internal static bool IsListedDomain(string? domain) =>
        domain is "scene" or "script" or "switch" or "light" or "input_boolean";

    /// <summary>
    /// Scenes, scripts and switches first, then by name. This keeps scenes when there are many lights and helpers:
    /// a cap after an alphabetical sort used to drop every scene.
    /// </summary>
    internal static IEnumerable<HaEntityItem> OrderForDashboard(IEnumerable<HaEntityItem> items) => items
        .OrderBy(static e => DomainRank(e.Domain))
        .ThenBy(static e => e.FriendlyName, StringComparer.CurrentCultureIgnoreCase);

    private static int DomainRank(string domain) => domain switch
    {
        "scene" => 0,
        "script" => 1,
        "switch" => 2,
        "light" => 3,
        "input_boolean" => 4,
        _ => 5,
    };

    /// <summary>
    /// True for "domain.object_id" where the domain is one this plugin lists (scene, script, switch, light,
    /// input_boolean) and the object id has only a-z, 0-9 and "_", as in Home Assistant. The id goes into the
    /// service call, so any other text (another domain such as cover.garage_door, or "%2e%2e/" to reach other
    /// API paths) is refused before a request is made.
    /// </summary>
    public static bool TryParseEntityId(string? entityId, out string domain)
    {
        domain = string.Empty;
        var id = entityId?.Trim() ?? string.Empty;
        var dot = id.IndexOf('.');
        if (dot <= 0 || dot == id.Length - 1)
        {
            return false;
        }

        var candidate = id[..dot];
        if (!IsListedDomain(candidate))
        {
            return false;
        }

        if (!id[(dot + 1)..].All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        {
            return false;
        }

        domain = candidate;
        return true;
    }

    /// <returns>
    /// Null when saved and the entities synced (or Demo Mode); <see cref="NotConfiguredMessage"/> when the URL or the
    /// token is missing, so nothing was checked; otherwise why the connection check failed.
    /// </returns>
    internal async Task<string?> SaveConfigAsync(HomeAssistantConfig updated)
    {
        var previous = _config;
        _config = updated;
        if (_context is not null)
        {
            await _context.Storage.SaveConfigAsync(_config);
        }

        // A change that the socket uses, or a save while it is not live (a retry), starts it again.
        var liveSettingsChanged = previous.BaseUrl != updated.BaseUrl || previous.AccessToken != updated.AccessToken ||
                                  previous.DemoMode != updated.DemoMode || previous.LiveUpdates != updated.LiveUpdates;
        if (liveSettingsChanged || _live?.State is null or HaLiveState.Disconnected or HaLiveState.AuthRejected or HaLiveState.Off)
        {
            await RestartLiveSocketAsync();
        }

        if (_config.DemoMode || IsConfigured)
        {
            try
            {
                await FetchEntitiesAsync();
            }
            catch (Exception ex)
            {
                _context?.ReportStatus($"Connection failed: {ex.Message}", PluginHealthState.Error);
                return ex.Message;
            }
        }
        else
        {
            _context?.ReportStatus("Enter Home Assistant URL & Token (or Demo Mode)", PluginHealthState.Idle);
            return NotConfiguredMessage;
        }

        return null;
    }

    /// <summary>True while the WebSocket is subscribed to state changes.</summary>
    internal bool IsLive => _live?.State == HaLiveState.Live;

    /// <summary>
    /// Stops the current socket and starts a new one when the plugin runs, is not in Demo Mode, has a URL and a
    /// token, and has live updates on. A token that Home Assistant rejected is not used again until it changes.
    /// </summary>
    private async Task RestartLiveSocketAsync()
    {
        await _liveGate.WaitAsync();
        try
        {
            var old = _live;
            _live = null;
            var generation = ++_liveGeneration;
            if (old is not null)
            {
                await old.DisposeAsync();
            }

            var token = _config.AccessToken.Trim();
            string? offText = !_isStarted ? "Live updates: off"
                : _config.DemoMode ? "Live updates: not used in Demo Mode"
                : !_config.LiveUpdates ? "Live updates: off"
                : !IsConfigured || _http is null ? "Live updates: not connected"
                : null;
            var uri = offText is null ? HomeAssistantWebSocketProtocol.TryBuildWebSocketUri(_config.BaseUrl) : null;
            if (offText is null && uri is null)
            {
                offText = "Live updates: the URL must start with http:// or https://";
            }

            if (offText is null && token == _rejectedToken)
            {
                _viewModel?.SetLiveStatus(RejectedText);
                return;
            }

            if (offText is not null || uri is null)
            {
                LogLive("off", "Home Assistant live updates: off");
                _viewModel?.SetLiveStatus(offText ?? "Live updates: off");
                return;
            }

            _rejectedToken = null;
            _live = new HomeAssistantLiveSocket(
                uri,
                token,
                _http,
                ct => generation == _liveGeneration ? OnLiveSubscribedAsync(ct) : Task.CompletedTask,
                (entityId, state) =>
                {
                    if (generation == _liveGeneration)
                    {
                        _viewModel?.ApplyStateChange(entityId, state);
                    }
                },
                (state, reason) => OnLiveStateChanged(generation, token, state, reason),
                LiveOptions);
            _live.Start();
        }
        finally
        {
            _liveGate.Release();
        }
    }

    private const string RejectedText = "Home Assistant rejected the token. Live updates stopped. Enter a new token.";

    private async Task OnLiveSubscribedAsync(CancellationToken ct)
    {
        try
        {
            await FetchEntitiesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _context?.ReportStatus($"HA unreachable: {ex.Message}", PluginHealthState.Warning);
        }
    }

    private void OnLiveStateChanged(int generation, string token, HaLiveState state, string? reason)
    {
        if (generation != _liveGeneration)
        {
            return;
        }

        switch (state)
        {
            case HaLiveState.Connecting:
                _viewModel?.SetLiveStatus("Live updates: connecting...");
                break;
            case HaLiveState.Live:
                LogLive("live", "Home Assistant live updates: connected");
                _viewModel?.SetLiveStatus("Live updates: on");
                break;
            case HaLiveState.Disconnected:
                LogLive("down", $"Home Assistant live updates: lost ({reason})");
                _viewModel?.SetLiveStatus($"Live updates: lost ({reason}). Trying again...");
                break;
            case HaLiveState.AuthRejected:
                _rejectedToken = token;
                LogLive("rejected", "Home Assistant live updates: token rejected");
                _viewModel?.SetLiveStatus(RejectedText);
                _context?.ReportStatus("Home Assistant rejected the token", PluginHealthState.Error);
                break;
            case HaLiveState.Off:
                LogLive("off", "Home Assistant live updates: off");
                break;
        }
    }

    /// <summary>Writes one short line when the live state moves to a new kind, so a retry loop does not fill the log.</summary>
    private void LogLive(string kind, string line)
    {
        if (_lastLoggedLive == kind)
        {
            return;
        }

        _lastLoggedLive = kind;
        if (kind == "down" || kind == "rejected")
        {
            _context?.Logger.Warning(line);
        }
        else
        {
            _context?.Logger.Info(line);
        }
    }

    private async Task PushSensorStateAsync(PluginReceiverSnapshot snap, string? sceneName, CancellationToken ct)
    {
        if (_config.DemoMode)
        {
            _viewModel?.SetStatus($"Demo HA sensor pushed: {(snap.IsPoweredOn ? "on" : "standby")} • Vol {snap.Volume}");
            return;
        }

        EnsureHttp();
        var payload = new
        {
            state = snap.IsPoweredOn ? snap.PlayState.ToLowerInvariant() : "standby",
            attributes = new
            {
                friendly_name = "Denon Desktop AVR",
                model = snap.ModelName,
                volume = snap.Volume,
                muted = snap.IsMuted,
                input_source = snap.InputSource,
                sound_mode = snap.SoundMode,
                now_playing = snap.NowPlayingTitle,
                last_scene = sceneName ?? string.Empty,
            },
        };

        using var req = BuildRequest(HttpMethod.Post, "/api/states/sensor.denon_desktop_receiver", payload);
        using var resp = await _http!.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task TriggerWebhookAsync(string eventName, string? sceneName, CancellationToken ct)
    {
        if (_config.DemoMode)
        {
            _viewModel?.SetStatus($"Demo HA webhook fired: {eventName}");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.WebhookId))
        {
            throw new InvalidOperationException("Enter a Home Assistant Webhook ID first.");
        }

        EnsureHttp();
        var baseUrl = _config.BaseUrl.Trim().TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/webhook/{Uri.EscapeDataString(_config.WebhookId.Trim())}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                source = "denon_desktop",
                event_type = eventName,
                scene = sceneName ?? string.Empty,
                timestamp = DateTimeOffset.Now,
            }), Encoding.UTF8, "application/json"),
        };

        using var resp = await _http!.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, object? body = null)
    {
        var baseUrl = _config.BaseUrl.Trim().TrimEnd('/');
        var req = new HttpRequestMessage(method, $"{baseUrl}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken.Trim());
        if (body is not null)
        {
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        return req;
    }

    internal PluginReceiverSnapshot CurrentReceiverSnapshot => _context?.Receiver.CurrentSnapshot ?? PluginReceiverSnapshot.Empty;

    private bool IsConfigured => !string.IsNullOrWhiteSpace(_config.BaseUrl) && !string.IsNullOrWhiteSpace(_config.AccessToken);

    private void EnsureHttp()
    {
        if (_http is null) throw new InvalidOperationException("Home Assistant plugin not initialized.");
    }
}

public sealed class HomeAssistantViewModel : INotifyPropertyChanged
{
    private readonly HomeAssistantPlugin _plugin;
    private readonly List<HaEntityItem> _allEntities = [];
    private string _baseUrl = "http://homeassistant.local:8123";
    private string _accessToken = string.Empty;
    private string _webhookId = string.Empty;
    private bool _demoMode;
    private bool _pushEvents = true;
    private bool _liveUpdates = true;
    private string _liveStatusText = string.Empty;
    private bool _isSetupExpanded = true;
    private bool _suppressAutoSave;
    private string _selectedDomainFilter = "all";
    private string _searchQuery = string.Empty;
    private string _statusMessage = "Enter your Home Assistant URL & Long-Lived Access Token, or try Demo Mode.";

    public HomeAssistantViewModel(HomeAssistantPlugin plugin)
    {
        _plugin = plugin;
        SaveAndConnectCommand = new AsyncCmd(_ => SaveAsync(), ShowError);
        RefreshCommand = new AsyncCmd(_ => RefreshAsync(), ShowError);
        ToggleDemoCommand = new AsyncCmd(_ => ToggleDemoAsync(), ShowError);
        ToggleSetupCommand = new AsyncCmd(_ =>
        {
            IsSetupExpanded = !IsSetupExpanded;
            return Task.CompletedTask;
        }, ShowError);
        SetDomainFilterCommand = new AsyncCmd(p =>
        {
            SelectedDomainFilter = p?.ToString() ?? "all";
            return Task.CompletedTask;
        }, ShowError);
        TriggerEntityCommand = new AsyncCmd(p => p is HaEntityItem item ? _plugin.TriggerEntityAsync(item.EntityId) : Task.CompletedTask, ShowError);
        PushNowCommand = new AsyncCmd(async _ =>
        {
            var res = await _plugin.ExecuteActionAsync("ha.sync_sensor");
            StatusMessage = res.Success ? $"✓ {res.Message}" : $"⚠ {res.Message}";
            Raise(nameof(TelemetryPreviewText));
        }, ShowError);
    }

    /// <summary>A failed tile click or save (Home Assistant offline, a bad token) shows here instead of closing the app.</summary>
    private void ShowError(Exception ex) => StatusMessage = $"{DateTime.Now:HH:mm:ss} • ⚠ {ex.Message}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<HaEntityItem> Entities { get; } = [];

    public ICommand SaveAndConnectCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToggleDemoCommand { get; }
    public ICommand ToggleSetupCommand { get; }
    public ICommand SetDomainFilterCommand { get; }
    public ICommand TriggerEntityCommand { get; }
    public ICommand PushNowCommand { get; }

    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value); }
    public string AccessToken { get => _accessToken; set => Set(ref _accessToken, value); }
    public string WebhookId { get => _webhookId; set => Set(ref _webhookId, value); }

    public bool PushEvents
    {
        get => _pushEvents;
        set
        {
            if (Set(ref _pushEvents, value) && !_suppressAutoSave)
            {
                _ = SaveAsync(quiet: true);
            }
        }
    }

    /// <summary>Keep a WebSocket open so tiles change when Home Assistant changes. Saved at once, like <see cref="PushEvents"/>.</summary>
    public bool LiveUpdates
    {
        get => _liveUpdates;
        set
        {
            if (Set(ref _liveUpdates, value) && !_suppressAutoSave)
            {
                _ = SaveAsync(quiet: true);
            }
        }
    }

    /// <summary>The state of the WebSocket in short words, for example "Live updates: on".</summary>
    public string LiveStatusText { get => _liveStatusText; private set => Set(ref _liveStatusText, value); }

    public bool DemoMode
    {
        get => _demoMode;
        private set
        {
            if (Set(ref _demoMode, value))
            {
                Raise(nameof(DemoButtonLabel));
                Raise(nameof(ConnectionBadgeText));
                Raise(nameof(IsConnectedOrDemo));
            }
        }
    }

    public bool IsSetupExpanded
    {
        get => _isSetupExpanded;
        set
        {
            if (Set(ref _isSetupExpanded, value))
            {
                Raise(nameof(SetupToggleLabel));
            }
        }
    }

    public string SetupToggleLabel => IsSetupExpanded ? "Hide Setup ▴" : "Setup ▾";

    public bool IsConnectedOrDemo => DemoMode || _allEntities.Count > 0;

    public bool HasEntities => Entities.Count > 0;

    public string EntitiesCountLabel => _allEntities.Count > 0 ? $"{_allEntities.Count} synced" : "Not synced yet";

    public string ConnectionBadgeText => DemoMode
        ? "DEMO SIMULATOR"
        : (_allEntities.Count > 0 ? "CONNECTED" : "NEEDS SETUP");

    public string SelectedDomainFilter
    {
        get => _selectedDomainFilter;
        set
        {
            if (Set(ref _selectedDomainFilter, value))
            {
                RebuildFilteredEntities();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (Set(ref _searchQuery, value))
            {
                RebuildFilteredEntities();
            }
        }
    }

    public string TelemetryPreviewText
    {
        get
        {
            var s = _plugin.CurrentReceiverSnapshot;
            var state = s.IsPoweredOn ? s.PlayState.ToLowerInvariant() : "standby";
            return $"sensor.denon_desktop_receiver → state: \"{state}\" • model: \"{s.ModelName}\" • vol: {s.Volume} • input: \"{s.InputSource}\" • mode: \"{s.SoundMode}\"";
        }
    }

    public string DemoButtonLabel => DemoMode ? "Exit Demo" : "Try Demo";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public void SyncFromConfig(HomeAssistantConfig cfg)
    {
        _suppressAutoSave = true;
        try
        {
            BaseUrl = cfg.BaseUrl;
            AccessToken = cfg.AccessToken;
            WebhookId = cfg.WebhookId;
            DemoMode = cfg.DemoMode;
            PushEvents = cfg.PushReceiverEventsToHa;
            LiveUpdates = cfg.LiveUpdates;
            IsSetupExpanded = !(cfg.DemoMode || (!string.IsNullOrWhiteSpace(cfg.BaseUrl) && !string.IsNullOrWhiteSpace(cfg.AccessToken)));
            Raise(nameof(TelemetryPreviewText));
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    public HaEntityItem? GetEntity(string entityId) =>
        _allEntities.FirstOrDefault(e => e.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase));

    public void UpdateEntityState(string entityId, string newState) =>
        RunOnUi(() =>
        {
            var idx = _allEntities.FindIndex(e => e.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _allEntities[idx] = _allEntities[idx] with { State = newState };
                RebuildFilteredEntities();
            }
        });

    /// <summary>
    /// Applies one state_changed event from the WebSocket: a known entity gets its new state, a new entity of a
    /// listed domain is added, and a null state removes the entity.
    /// </summary>
    public void ApplyStateChange(string entityId, HaEntityItem? newState) =>
        RunOnUi(() =>
        {
            var idx = _allEntities.FindIndex(e => e.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase));
            if (newState is null)
            {
                if (idx < 0)
                {
                    return;
                }

                _allEntities.RemoveAt(idx);
            }
            else if (!HomeAssistantPlugin.IsListedDomain(newState.Domain))
            {
                return;
            }
            else if (idx >= 0)
            {
                var old = _allEntities[idx];
                if (old == newState)
                {
                    return; // Only an attribute changed (for example the brightness of a light).
                }

                _allEntities[idx] = newState;
                if (old.FriendlyName == newState.FriendlyName)
                {
                    // Same name: the filter keeps the same tiles, so replace the one tile in place.
                    var shown = Entities.IndexOf(old);
                    if (shown >= 0)
                    {
                        Entities[shown] = newState;
                    }

                    return;
                }
            }
            else
            {
                if (_allEntities.Count >= HomeAssistantPlugin.MaxEntities)
                {
                    return;
                }

                _allEntities.Add(newState);
                var ordered = HomeAssistantPlugin.OrderForDashboard(_allEntities).ToList();
                _allEntities.Clear();
                _allEntities.AddRange(ordered);
            }

            RebuildFilteredEntities();
            Raise(nameof(ConnectionBadgeText));
            Raise(nameof(IsConnectedOrDemo));
            Raise(nameof(EntitiesCountLabel));
        });

    public void SetLiveStatus(string text) => RunOnUi(() => LiveStatusText = text);

    public void ApplyEntities(IReadOnlyList<HaEntityItem> items) =>
        RunOnUi(() =>
        {
            _allEntities.Clear();
            _allEntities.AddRange(items);
            RebuildFilteredEntities();
            if (_allEntities.Count > 0)
            {
                IsSetupExpanded = false;
            }

            Raise(nameof(ConnectionBadgeText));
            Raise(nameof(IsConnectedOrDemo));
            Raise(nameof(EntitiesCountLabel));
            Raise(nameof(TelemetryPreviewText));
        });

    public void SetStatus(string msg) =>
        RunOnUi(() => StatusMessage = $"{DateTime.Now:HH:mm:ss} • {msg}");

    /// <summary>Runs on the UI thread, the same way for REST reads and WebSocket events. Without an app (tests) it runs at once.</summary>
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.InvokeAsync(action);
        }
    }

    private void RebuildFilteredEntities()
    {
        var domain = (_selectedDomainFilter ?? "all").ToLowerInvariant();
        var q = (_searchQuery ?? string.Empty).Trim();

        var filtered = _allEntities.Where(e =>
            (domain == "all" || e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(q) ||
             e.FriendlyName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             e.EntityId.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        Entities.Clear();
        foreach (var item in filtered)
        {
            Entities.Add(item);
        }

        Raise(nameof(HasEntities));
    }

    private async Task SaveAsync(bool quiet = false)
    {
        var error = await _plugin.SaveConfigAsync(new HomeAssistantConfig
        {
            BaseUrl = BaseUrl.Trim(),
            AccessToken = AccessToken.Trim(),
            WebhookId = WebhookId.Trim(),
            DemoMode = DemoMode,
            PushReceiverEventsToHa = PushEvents,
            LiveUpdates = LiveUpdates,
        });
        StatusMessage = error == HomeAssistantPlugin.NotConfiguredMessage
            ? $"✓ Saved. {error}"
            : error is not null
            ? $"⚠ Saved, but connection check failed: {error}"
            : quiet
                ? "✓ Home Assistant setting saved."
                : "✓ Saved Home Assistant configuration & synced entities.";
    }

    private async Task RefreshAsync()
    {
        try
        {
            var items = await _plugin.FetchEntitiesAsync();
            StatusMessage = $"✓ Synced {items.Count} Home Assistant scenes, scripts & switches.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ Refresh failed: {ex.Message}";
        }
    }

    private async Task ToggleDemoAsync()
    {
        DemoMode = !DemoMode;
        await SaveAsync();
        StatusMessage = DemoMode ? "Home Assistant Demo Mode active — trigger virtual blinds, plugs & scenes!" : "Exited Demo Mode.";
    }

    private bool Set<T>(ref T field, T val, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, val)) return false;
        field = val;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        return true;
    }

    private void Raise(string prop) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    /// <summary>
    /// An ICommand for async work. Execute is async void, so an exception it lets out would close the app:
    /// it goes to <paramref name="onError"/> instead.
    /// </summary>
    private sealed class AsyncCmd(Func<object?, Task> exec, Action<Exception> onError) : ICommand
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
            catch (Exception ex) { onError(ex); }
            finally { _busy = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        }
    }
}
