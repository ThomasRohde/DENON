using System.Collections.ObjectModel;
using Denon.App.Core.Mcp;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private RoomIntelligenceService _roomIntelligence = null!;
    private DenonMcpHttpServer _mcpServer = null!;

    private bool _mcpEnabled = true;
    private string _mcpPortText = "18926";
    private bool _mcpAllowLan;
    private bool _mcpRequireAuth = true;
    private string _mcpBearerToken = string.Empty;
    private string _mcpPermissionTierName = nameof(McpPermissionTier.FullOrchestration);
    private int _mcpMaxSafeVolume = 65;
    private bool _mcpEnableRoomDoctor = true;

    private bool _isMcpRunning;
    private string _mcpStatusBadgeText = "STOPPED";
    private string _mcpEndpointText = "http://127.0.0.1:18926/mcp";
    private string _mcpToolsSummaryText = "0 tools • 4 resources • 3 templates • 4 prompts";
    private string _mcpClientConfigPreview = string.Empty;
    private string _mcpStatusMessage = "Streamable HTTP MCP Server ready.";
    private string _learnedHabitsSummary = "Learning your local listening patterns (100% local, zero cloud).";
    private string _lastRoomSnapshotLabel = "No undo snapshot yet";
    private bool _canUndoRoomState;

    public ObservableCollection<RoomInsight> RoomInsights { get; } = [];
    public ObservableCollection<McpActivityEntry> McpActivityFeed { get; } = [];

    public AsyncRelayCommand SaveAndRestartMcpCommand { get; private set; } = null!;
    public AsyncRelayCommand ToggleMcpEnabledCommand { get; private set; } = null!;
    public AsyncRelayCommand RegenerateMcpTokenCommand { get; private set; } = null!;
    public RelayCommand CopyMcpConfigCommand { get; private set; } = null!;
    public RelayCommand CopyMcpEndpointCommand { get; private set; } = null!;
    public RelayCommand CopyMcpTokenCommand { get; private set; } = null!;
    public RelayCommand ClearMcpActivityCommand { get; private set; } = null!;
    public RelayCommand SetMcpTierCommand { get; private set; } = null!;
    public AsyncRelayCommand RestorePreviousRoomStateCommand { get; private set; } = null!;
    public AsyncRelayCommand ExecuteRoomInsightCommand { get; private set; } = null!;
    public RelayCommand DismissRoomInsightCommand { get; private set; } = null!;
    public RelayCommand RunRoomDoctorCommand { get; private set; } = null!;

    public bool McpEnabled
    {
        get => _mcpEnabled;
        set => SetProperty(ref _mcpEnabled, value);
    }

    public string McpPortText
    {
        get => _mcpPortText;
        set => SetProperty(ref _mcpPortText, value);
    }

    public bool McpAllowLan
    {
        get => _mcpAllowLan;
        set => SetProperty(ref _mcpAllowLan, value);
    }

    public bool McpRequireAuth
    {
        get => _mcpRequireAuth;
        set => SetProperty(ref _mcpRequireAuth, value);
    }

    public string McpBearerToken
    {
        get => _mcpBearerToken;
        private set => SetProperty(ref _mcpBearerToken, value);
    }

    public string McpPermissionTierName
    {
        get => _mcpPermissionTierName;
        set => SetProperty(ref _mcpPermissionTierName, value);
    }

    public int McpMaxSafeVolume
    {
        get => _mcpMaxSafeVolume;
        set
        {
            var clamped = Math.Clamp(value, 20, 95);
            if (SetProperty(ref _mcpMaxSafeVolume, clamped))
            {
                OnPropertyChanged(nameof(McpMaxSafeVolumeLabel));
            }
        }
    }

    public string McpMaxSafeVolumeLabel => $"{McpMaxSafeVolume} ({RoomIntelligenceService.FormatDb(McpMaxSafeVolume)})";

    public bool McpEnableRoomDoctor
    {
        get => _mcpEnableRoomDoctor;
        set
        {
            if (SetProperty(ref _mcpEnableRoomDoctor, value))
            {
                OnPropertyChanged(nameof(ShowControlDeckInsights));
            }
        }
    }

    public bool IsMcpRunning
    {
        get => _isMcpRunning;
        private set
        {
            if (SetProperty(ref _isMcpRunning, value))
            {
                OnPropertyChanged(nameof(McpTitleBarBadge));
            }
        }
    }

    public string McpTitleBarBadge => IsMcpRunning ? $"MCP :{_settingsStore?.Current.Mcp.Port ?? 18926}" : "MCP OFF";

    public string McpStatusBadgeText
    {
        get => _mcpStatusBadgeText;
        private set => SetProperty(ref _mcpStatusBadgeText, value);
    }

    public string McpEndpointText
    {
        get => _mcpEndpointText;
        private set => SetProperty(ref _mcpEndpointText, value);
    }

    public string McpToolsSummaryText
    {
        get => _mcpToolsSummaryText;
        private set => SetProperty(ref _mcpToolsSummaryText, value);
    }

    public string McpClientConfigPreview
    {
        get => _mcpClientConfigPreview;
        private set => SetProperty(ref _mcpClientConfigPreview, value);
    }

    public string McpStatusMessage
    {
        get => _mcpStatusMessage;
        private set => SetProperty(ref _mcpStatusMessage, value);
    }

    public string LearnedHabitsSummary
    {
        get => _learnedHabitsSummary;
        private set => SetProperty(ref _learnedHabitsSummary, value);
    }

    public bool CanUndoRoomState
    {
        get => _canUndoRoomState;
        private set => SetProperty(ref _canUndoRoomState, value);
    }

    public string LastRoomSnapshotLabel
    {
        get => _lastRoomSnapshotLabel;
        private set => SetProperty(ref _lastRoomSnapshotLabel, value);
    }

    public bool HasRoomInsights => RoomInsights.Count > 0;

    // The screenshot showcase has no live receiver connection, so its "AVR offline" insight is not real.
    public bool ShowControlDeckInsights => McpEnableRoomDoctor && RoomInsights.Count > 0 && !_freezeVuForScreenshots;

    public RoomInsight? PrimaryRoomInsight => RoomInsights.FirstOrDefault();

    public bool HasMcpActivity => McpActivityFeed.Count > 0;

    private void InitializeMcpAndIntelligence(RoomIntelligenceService roomIntelligence, DenonMcpHttpServer mcpServer)
    {
        _roomIntelligence = roomIntelligence;
        _mcpServer = mcpServer;

        SaveAndRestartMcpCommand = new AsyncRelayCommand(_ => SaveAndApplyMcpSettingsAsync());
        ToggleMcpEnabledCommand = new AsyncRelayCommand(async _ =>
        {
            McpEnabled = !McpEnabled;
            await SaveAndApplyMcpSettingsAsync();
        });
        RegenerateMcpTokenCommand = new AsyncRelayCommand(_ => RegenerateMcpTokenAsync());
        CopyMcpConfigCommand = new RelayCommand(_ => CopyTextWithStatus(_mcpServer.BuildClientConfigJson(), "Copied MCP client JSON config (ready to paste into Antigravity, Claude, Cursor or VS Code)."));
        CopyMcpEndpointCommand = new RelayCommand(_ => CopyTextWithStatus(McpEndpointText, $"Copied MCP endpoint URL ({McpEndpointText})."));
        CopyMcpTokenCommand = new RelayCommand(_ => CopyTextWithStatus(McpBearerToken, "Copied MCP Bearer token to clipboard."));
        ClearMcpActivityCommand = new RelayCommand(_ =>
        {
            _mcpServer.ClearActivity();
            McpActivityFeed.Clear();
            OnPropertyChanged(nameof(HasMcpActivity));
        });
        SetMcpTierCommand = new RelayCommand(param =>
        {
            if (param is string tier)
            {
                McpPermissionTierName = tier;
            }
        });
        RestorePreviousRoomStateCommand = new AsyncRelayCommand(_ => RestorePreviousRoomStateAsync());
        ExecuteRoomInsightCommand = new AsyncRelayCommand(ExecuteRoomInsightAsync);
        DismissRoomInsightCommand = new RelayCommand(param =>
        {
            if (param is RoomInsight insight)
            {
                _roomIntelligence.DismissInsight(insight.Id);
            }
        });
        RunRoomDoctorCommand = new RelayCommand(_ =>
        {
            _roomIntelligence.ResetDismissedInsights();
            RefreshRoomIntelligenceUi();
            McpStatusMessage = RoomInsights.Count == 0
                ? "✓ Room Doctor scan complete — all devices & audio routing look healthy!"
                : $"Room Doctor found {RoomInsights.Count} actionable insight(s).";
        });

        _mcpServer.StatusChanged += () => { _ = _dispatcher.InvokeAsync(RefreshMcpServerUi); };
        _mcpServer.ActivityAdded += entry => { _ = _dispatcher.InvokeAsync(() => PrependMcpActivity(entry)); };
        _roomIntelligence.InsightsChanged += () => { _ = _dispatcher.InvokeAsync(RefreshRoomIntelligenceUi); };
        _roomIntelligence.UndoStackChanged += () => { _ = _dispatcher.InvokeAsync(RefreshRoomIntelligenceUi); };
        _roomIntelligence.HabitsChanged += () => { _ = _dispatcher.InvokeAsync(RefreshRoomIntelligenceUi); };

        SyncMcpFieldsFromSettings(_settingsStore.Current.Mcp);
        RefreshMcpServerUi();
        RefreshRoomIntelligenceUi();
    }

    private void SyncMcpFieldsFromSettings(McpServerSettings cfg)
    {
        McpEnabled = cfg.Enabled;
        McpPortText = cfg.Port.ToString();
        McpAllowLan = cfg.AllowLanConnections;
        McpRequireAuth = cfg.RequireBearerAuth;
        McpBearerToken = cfg.BearerToken;
        McpPermissionTierName = cfg.PermissionTier.ToString();
        McpMaxSafeVolume = cfg.MaxSafeVolume;
        McpEnableRoomDoctor = cfg.EnableRoomDoctorSuggestions;
    }

    private void RefreshMcpServerUi()
    {
        if (_mcpServer is null)
        {
            return;
        }

        IsMcpRunning = _mcpServer.IsRunning;
        var cfg = _settingsStore.Current.Mcp;
        McpEndpointText = string.IsNullOrWhiteSpace(_mcpServer.ListeningEndpoint)
            ? $"http://127.0.0.1:{cfg.Port}/mcp"
            : _mcpServer.ListeningEndpoint;

        McpStatusBadgeText = _mcpServer.IsRunning
            ? $"LISTENING • {cfg.PermissionTier}"
            : (!cfg.Enabled ? "DISABLED" : "STOPPED");

        McpToolsSummaryText = $"{_mcpServer.RegisteredToolCount} tools • 4 resources • 3 templates • 4 prompts";
        McpClientConfigPreview = _mcpServer.BuildClientConfigJson();

        if (!string.IsNullOrWhiteSpace(_mcpServer.LastError))
        {
            McpStatusMessage = $"⚠ {_mcpServer.LastError}";
        }
        else if (_mcpServer.IsRunning)
        {
            McpStatusMessage = $"Streamable HTTP MCP Server active at {McpEndpointText} (Max Safe Volume: {McpMaxSafeVolumeLabel}).";
        }
        else
        {
            McpStatusMessage = "MCP Server is currently stopped. Enable it and click Save & Apply to start listening.";
        }

        OnPropertyChanged(nameof(McpTitleBarBadge));
    }

    private void RefreshRoomIntelligenceUi()
    {
        if (_roomIntelligence is null)
        {
            return;
        }

        var insights = _roomIntelligence.ActiveInsights;
        RoomInsights.Clear();
        foreach (var item in insights)
        {
            RoomInsights.Add(item);
        }

        CanUndoRoomState = _roomIntelligence.CanUndo;
        LastRoomSnapshotLabel = _roomIntelligence.LastSnapshot is { } snap
            ? $"Before '{snap.Reason}' ({snap.CapturedAt.LocalDateTime:HH:mm:ss} • Vol {snap.Volume} • {snap.InputSource})"
            : "No undo snapshot yet (auto-captured before MCP & Room Doctor changes)";

        LearnedHabitsSummary = _roomIntelligence.GetHabitsHumanSummary();

        OnPropertyChanged(nameof(HasRoomInsights));
        OnPropertyChanged(nameof(ShowControlDeckInsights));
        OnPropertyChanged(nameof(PrimaryRoomInsight));
    }

    private void PrependMcpActivity(McpActivityEntry entry)
    {
        McpActivityFeed.Insert(0, entry);
        while (McpActivityFeed.Count > 40)
        {
            McpActivityFeed.RemoveAt(McpActivityFeed.Count - 1);
        }

        OnPropertyChanged(nameof(HasMcpActivity));
    }

    private async Task SaveAndApplyMcpSettingsAsync()
    {
        var port = int.TryParse(McpPortText, out var parsedPort) ? Math.Clamp(parsedPort, 1024, 65535) : 18926;
        var tier = Enum.TryParse<McpPermissionTier>(McpPermissionTierName, ignoreCase: true, out var parsedTier)
            ? parsedTier
            : McpPermissionTier.FullOrchestration;

        var updated = _settingsStore.Current.Clone();
        updated.Mcp.Enabled = McpEnabled;
        updated.Mcp.Port = port;
        updated.Mcp.AllowLanConnections = McpAllowLan;
        updated.Mcp.RequireBearerAuth = McpRequireAuth;
        updated.Mcp.PermissionTier = tier;
        updated.Mcp.MaxSafeVolume = McpMaxSafeVolume;
        updated.Mcp.EnableRoomDoctorSuggestions = McpEnableRoomDoctor;
        if (string.IsNullOrWhiteSpace(updated.Mcp.BearerToken))
        {
            updated.Mcp.BearerToken = McpServerSettings.GenerateSecureToken();
        }

        await _settingsStore.SaveAsync(updated);
        SyncMcpFieldsFromSettings(updated.Mcp);
        await _mcpServer.ApplySettingsAsync();
        RefreshMcpServerUi();
        McpStatusMessage = _mcpServer.IsRunning
            ? $"✓ Saved & listening on {McpEndpointText} (Tier: {tier}, Max Vol: {McpMaxSafeVolumeLabel})."
            : "✓ Saved MCP settings (server stopped).";
    }

    private async Task RegenerateMcpTokenAsync()
    {
        var updated = _settingsStore.Current.Clone();
        updated.Mcp.BearerToken = McpServerSettings.GenerateSecureToken();
        await _settingsStore.SaveAsync(updated);
        SyncMcpFieldsFromSettings(updated.Mcp);
        RefreshMcpServerUi();
        McpStatusMessage = "✓ Generated a new MCP Bearer token. Update your MCP client config.";
    }

    private async Task RestorePreviousRoomStateAsync()
    {
        var (ok, msg) = await _roomIntelligence.RestoreLastSnapshotAsync();
        McpStatusMessage = ok ? $"✓ {msg}" : $"⚠ {msg}";
        StatusMessage = msg;
        RefreshRoomIntelligenceUi();
    }

    private async Task ExecuteRoomInsightAsync(object? param)
    {
        if (param is not RoomInsight insight)
        {
            return;
        }

        if (insight.FixActionKey.StartsWith("open.plugin:", StringComparison.OrdinalIgnoreCase))
        {
            var pluginId = insight.FixActionKey["open.plugin:".Length..];
            var card = InstalledPlugins.FirstOrDefault(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            if (card is not null)
            {
                SelectedPlugin = card;
            }

            ActiveSection = "Plugins";
            return;
        }

        var (ok, msg) = await _roomIntelligence.ExecuteInsightFixAsync(insight.FixActionKey);
        McpStatusMessage = ok ? $"✓ {msg}" : $"⚠ {msg}";
        StatusMessage = msg;
        RefreshRoomIntelligenceUi();
    }

    private void CopyTextWithStatus(string text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            McpStatusMessage = $"✓ {successMessage}";
            StatusMessage = successMessage;
        }
        catch (Exception ex)
        {
            McpStatusMessage = $"⚠ Clipboard copy failed: {ex.Message}";
        }
    }
}
