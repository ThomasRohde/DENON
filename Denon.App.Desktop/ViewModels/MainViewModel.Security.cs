using System.Collections.ObjectModel;
using Denon.App.Core.Mcp;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

/// <summary>One switch in the Tool access list on the Brain page: a tool, or all tools of one plugin.</summary>
public sealed class McpAccessSwitch(string key, string label, string detail, bool isAllowed) : ObservableObject
{
    private bool _isAllowed = isAllowed;

    /// <summary>The saved state when the switch was built.</summary>
    public bool SavedAllowed { get; } = isAllowed;

    /// <summary>Changed on the page and not saved yet.</summary>
    public bool IsPending => IsAllowed != SavedAllowed;

    /// <summary>The tool name, or the plugin id.</summary>
    public string Key { get; } = key;

    public string Label { get; } = label;

    public string Detail { get; } = detail;

    public bool IsAllowed
    {
        get => _isAllowed;
        set => SetProperty(ref _isAllowed, value);
    }
}

/// <summary>
/// Least-privilege settings: the MCP tool switches and the audit log (Brain page), the webhook token (Cast &amp; Remote
/// page), and LAN only for both servers.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _mcpAuditLogEnabled = true;
    private bool _isLanOnly;
    private string _webhookToken = string.Empty;
    private RelayCommand? _openMcpAuditFolderCommand;
    private RelayCommand? _copyWebhookTokenCommand;
    private AsyncRelayCommand? _regenerateWebhookTokenCommand;
    private RelayCommand? _setAllMcpToolsCommand;
    private string _mcpSwitchesSignature = string.Empty;

    /// <summary>The room and media tools, each with its own switch.</summary>
    public ObservableCollection<McpAccessSwitch> McpToolSwitches { get; } = [];

    /// <summary>One switch per plugin: its plugin_* tools and its named tools.</summary>
    public ObservableCollection<McpAccessSwitch> McpPluginSwitches { get; } = [];

    public bool HasMcpPluginSwitches => McpPluginSwitches.Count > 0;

    public string McpToolAccessSummary
    {
        get
        {
            var offTools = McpToolSwitches.Count(static s => !s.IsAllowed);
            var offPlugins = McpPluginSwitches.Count(static s => !s.IsAllowed);
            return offTools == 0 && offPlugins == 0
                ? "All tools are on. The permission tier still applies."
                : $"{offTools} tool(s) and {offPlugins} plugin(s) off. Save & Apply to use the change.";
        }
    }

    public bool McpAuditLogEnabled
    {
        get => _mcpAuditLogEnabled;
        set => SetProperty(ref _mcpAuditLogEnabled, value);
    }

    public RelayCommand OpenMcpAuditFolderCommand => _openMcpAuditFolderCommand ??= new RelayCommand(_ => OpenFolder(_mcpServer.AuditLogDirectory));

    /// <summary>"all" turns every switch on, "none" turns every switch off (Save &amp; Apply still needed).</summary>
    public RelayCommand SetAllMcpToolsCommand => _setAllMcpToolsCommand ??= new RelayCommand(param =>
    {
        var allowed = !string.Equals(param as string, "none", StringComparison.OrdinalIgnoreCase);
        foreach (var item in McpToolSwitches.Concat(McpPluginSwitches))
        {
            item.IsAllowed = allowed;
        }
    });

    /// <summary>LAN only for the Phone Remote server and the MCP server. Saved at once.</summary>
    public bool IsLanOnly
    {
        get => _isLanOnly;
        set
        {
            if (SetProperty(ref _isLanOnly, value))
            {
                _ = SaveLanOnlyAsync(value);
            }
        }
    }

    public string WebhookToken
    {
        get => _webhookToken;
        private set
        {
            if (SetProperty(ref _webhookToken, value))
            {
                NotifySecretDisplays();
            }
        }
    }

    public RelayCommand CopyWebhookTokenCommand => _copyWebhookTokenCommand ??= new RelayCommand(_ =>
    {
        if (!string.IsNullOrEmpty(WebhookToken) && TrySetClipboard(WebhookToken))
        {
            StatusMessage = "Copied the webhook token. Use it in the Shortcut's Authorization: Bearer header.";
        }
    });

    public AsyncRelayCommand RegenerateWebhookTokenCommand => _regenerateWebhookTokenCommand ??= new AsyncRelayCommand(_ => RegenerateWebhookTokenAsync());

    /// <summary>
    /// Reads the security settings into the page fields: at start, after Save &amp; Apply, and after an import (all
    /// saved values, unsaved switches dropped).
    /// </summary>
    private void SyncSecurityFieldsFromSettings(AppSettings settings)
    {
        SyncInstantSecurityFields(settings);
        McpAuditLogEnabled = settings.Mcp.AuditLogEnabled;
        RefreshMcpToolSwitches(keepPending: false);
    }

    /// <summary>
    /// The fields that save at once (LAN only, the webhook token) follow every settings change, so the page never
    /// shows a value the servers no longer use.
    /// </summary>
    private void SyncInstantSecurityFields(AppSettings settings)
    {
        if (_isLanOnly != settings.Security.LanOnly)
        {
            _isLanOnly = settings.Security.LanOnly;
            OnPropertyChanged(nameof(IsLanOnly));
        }

        WebhookToken = settings.Remote.WebhookToken;
    }

    /// <summary>
    /// Builds the Tool access switches from the server's tools and the saved deny lists. With
    /// <paramref name="keepPending"/>, a switch changed on the page and not saved keeps its value, and nothing is
    /// rebuilt when the tools and the saved lists are the same (a plugin's status report).
    /// </summary>
    private void RefreshMcpToolSwitches() => RefreshMcpToolSwitches(keepPending: true);

    private void RefreshMcpToolSwitches(bool keepPending)
    {
        if (_mcpServer is null)
        {
            return;
        }

        var cfg = _settingsStore.Current.Mcp;
        var catalog = _mcpServer.GetToolCatalog();
        var signature = string.Join("|", catalog.Select(static t => $"{t.Name}:{t.PluginId}:{t.PluginName}:{t.ReadOnly}")) +
                        "#" + string.Join(",", cfg.DeniedTools) + "#" + string.Join(",", cfg.DeniedPluginIds);
        if (keepPending && signature == _mcpSwitchesSignature)
        {
            return;
        }

        _mcpSwitchesSignature = signature;
        var pending = keepPending
            ? McpToolSwitches.Concat(McpPluginSwitches).Where(static s => s.IsPending)
                .GroupBy(static s => s.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static g => g.Key, static g => g.First().IsAllowed, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in McpToolSwitches.Concat(McpPluginSwitches))
        {
            item.PropertyChanged -= OnMcpSwitchChanged;
        }

        McpToolSwitches.Clear();
        foreach (var tool in catalog.Where(static t => t.PluginId is null).OrderBy(static t => t.Name, StringComparer.Ordinal))
        {
            McpToolSwitches.Add(new McpAccessSwitch(
                tool.Name,
                tool.Name,
                tool.ReadOnly ? $"{tool.Title} (read only)" : tool.Title,
                !cfg.DeniedTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase)));
        }

        McpPluginSwitches.Clear();
        foreach (var plugin in catalog.Where(static t => t.PluginId is not null)
                     .GroupBy(static t => t.PluginId!, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static g => g.First().PluginName, StringComparer.OrdinalIgnoreCase))
        {
            var name = plugin.First().PluginName ?? plugin.Key;
            McpPluginSwitches.Add(new McpAccessSwitch(
                plugin.Key,
                name,
                $"{plugin.Count()} tool(s): {string.Join(", ", plugin.Select(static t => t.Name).Take(4))}{(plugin.Count() > 4 ? ", …" : string.Empty)}",
                !McpToolAccess.IsPluginDenied(cfg, plugin.Key)));
        }

        foreach (var item in McpToolSwitches.Concat(McpPluginSwitches))
        {
            if (pending.TryGetValue(item.Key, out var allowed))
            {
                item.IsAllowed = allowed;
            }

            item.PropertyChanged += OnMcpSwitchChanged;
        }

        OnPropertyChanged(nameof(HasMcpPluginSwitches));
        OnPropertyChanged(nameof(McpToolAccessSummary));
    }

    private void OnMcpSwitchChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(McpToolAccessSummary));

    /// <summary>
    /// The deny lists from the switches. A name that has no switch now (a plugin that is off, a tool that is gone)
    /// keeps its saved state.
    /// </summary>
    private void ApplyMcpToolSwitches(McpServerSettings cfg)
    {
        var shownTools = McpToolSwitches.Select(static s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        cfg.DeniedTools =
        [
            .. cfg.DeniedTools.Where(t => !shownTools.Contains(t)),
            .. McpToolSwitches.Where(static s => !s.IsAllowed).Select(static s => s.Key),
        ];

        var shownPlugins = McpPluginSwitches.Select(static s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        cfg.DeniedPluginIds =
        [
            .. cfg.DeniedPluginIds.Where(p => !shownPlugins.Contains(p)),
            .. McpPluginSwitches.Where(static s => !s.IsAllowed).Select(static s => s.Key),
        ];
    }

    /// <summary>Saves LAN only. A failed save is logged and the checkbox shows the saved value again.</summary>
    private async Task SaveLanOnlyAsync(bool lanOnly)
    {
        bool savedToDisk;
        try
        {
            (_, savedToDisk) = await _settingsStore.UpdateQuietAsync(s => s.Security.LanOnly = lanOnly);
        }
        catch (Exception ex)
        {
            _logStore.Warning($"Could not save LAN only: {ex.Message}");
            SyncInstantSecurityFields(_settingsStore.Current);
            StatusMessage = $"Could not save LAN only: {ex.Message}";
            McpStatusMessage = $"⚠ {StatusMessage}";
            return;
        }

        StatusMessage = lanOnly
            ? "LAN only is on: the Phone Remote and the MCP server answer only this PC and the local network."
            : "LAN only is off.";
        McpStatusMessage = $"✓ {StatusMessage}";
        if (!savedToDisk)
        {
            StatusMessage += " It is saved with your next Save & Sync (the settings file is being recovered).";
        }
    }

    private async Task RegenerateWebhookTokenAsync()
    {
        var (updated, savedToDisk) = await _settingsStore.UpdateQuietAsync(s => s.Remote.WebhookToken = RemoteAccessSettings.GeneratePairingToken("hook"));
        WebhookToken = updated.Remote.WebhookToken;
        StatusMessage = "Made a new webhook token. Update it in your Shortcuts; the old one no longer works.";
        if (!savedToDisk)
        {
            StatusMessage += " It is saved with your next Save & Sync (the settings file is being recovered).";
        }
    }
}
