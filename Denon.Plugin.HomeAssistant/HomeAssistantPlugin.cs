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

    public PluginManifest Manifest { get; } = new(
        Id: "home-assistant",
        Name: "Home Assistant Bridge",
        Version: "1.3.0",
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

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _http = context.CreateHttpClient(allowLocalSelfSignedTls: true, timeout: TimeSpan.FromSeconds(6));
        _config = await context.Storage.LoadConfigAsync(static () => new HomeAssistantConfig(), cancellationToken);
        _viewModel = new HomeAssistantViewModel(this);
        _viewModel.SyncFromConfig(_config);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
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

    public ValueTask DisposeAsync()
    {
        _http?.Dispose();
        _http = null;
        return ValueTask.CompletedTask;
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
                var id = item.TryGetProperty("entity_id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                var dot = id.IndexOf('.');
                if (dot <= 0) continue;
                var domain = id[..dot].ToLowerInvariant();
                if (domain is not ("scene" or "script" or "switch" or "light" or "input_boolean"))
                {
                    continue;
                }

                var state = item.TryGetProperty("state", out var stEl) ? stEl.GetString() ?? string.Empty : string.Empty;
                var friendly = item.TryGetProperty("attributes", out var attrEl) &&
                               attrEl.TryGetProperty("friendly_name", out var fnEl)
                    ? fnEl.GetString() ?? id
                    : id;

                list.Add(new HaEntityItem(id, friendly, domain, state));
            }
        }

        // Keep scenes, scripts and switches when there are many lights and helpers: a cap after an
        // alphabetical sort used to drop every scene.
        var ordered = list
            .OrderBy(static e => DomainRank(e.Domain))
            .ThenBy(static e => e.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxEntities)
            .ToList();
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

    private static int DomainRank(string domain) => domain switch
    {
        "scene" => 0,
        "script" => 1,
        "switch" => 2,
        "light" => 3,
        _ => 4,
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
        if (candidate is not ("scene" or "script" or "switch" or "light" or "input_boolean"))
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
        _config = updated;
        if (_context is not null)
        {
            await _context.Storage.SaveConfigAsync(_config);
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

    public void UpdateEntityState(string entityId, string newState)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var idx = _allEntities.FindIndex(e => e.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _allEntities[idx] = _allEntities[idx] with { State = newState };
                RebuildFilteredEntities();
            }
        });
    }

    public void ApplyEntities(IReadOnlyList<HaEntityItem> items) =>
        Application.Current?.Dispatcher.InvokeAsync(() =>
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
        Application.Current?.Dispatcher.InvokeAsync(() => StatusMessage = $"{DateTime.Now:HH:mm:ss} • {msg}");

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
        });
        StatusMessage = error == HomeAssistantPlugin.NotConfiguredMessage
            ? $"✓ Saved. {error}"
            : error is not null
            ? $"⚠ Saved, but connection check failed: {error}"
            : quiet
                ? "✓ Home Assistant event push setting saved."
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
