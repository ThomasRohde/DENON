using System.Security.Cryptography;

namespace Denon.App.Core.Models;

public enum McpPermissionTier
{
    Observer = 0,
    SafeControl = 1,
    FullOrchestration = 2,
}

public sealed class McpServerSettings
{
    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = 18926;

    public bool AllowLanConnections { get; set; } = false;

    public bool RequireBearerAuth { get; set; } = true;

    public string BearerToken { get; set; } = GenerateSecureToken();

    public McpPermissionTier PermissionTier { get; set; } = McpPermissionTier.FullOrchestration;

    /// <summary>
    /// Hard safety ceiling (0-98 scale, default 65 = -15.0 dB) enforced on every MCP volume mutation.
    /// </summary>
    public int MaxSafeVolume { get; set; } = 65;

    public bool EnableRoomDoctorSuggestions { get; set; } = true;

    /// <summary>
    /// Write one line per tool call to logs\mcp-audit-*.jsonl: time, address, client, tool, the argument names (never
    /// their values) and the outcome. On by default: the lines hold no values or tokens, and the files are capped.
    /// </summary>
    public bool AuditLogEnabled { get; set; } = true;

    /// <summary>Tools turned off by name, on top of the permission tier. They are not listed and a call is refused.</summary>
    public List<string> DeniedTools { get; set; } = [];

    /// <summary>Plugins whose tools are turned off (the plugin_* tools and the plugin's own named tools).</summary>
    public List<string> DeniedPluginIds { get; set; } = [];

    public McpServerSettings Clone() =>
        new()
        {
            Enabled = Enabled,
            Port = Port,
            AllowLanConnections = AllowLanConnections,
            RequireBearerAuth = RequireBearerAuth,
            BearerToken = BearerToken,
            PermissionTier = PermissionTier,
            MaxSafeVolume = MaxSafeVolume,
            EnableRoomDoctorSuggestions = EnableRoomDoctorSuggestions,
            AuditLogEnabled = AuditLogEnabled,
            DeniedTools = [.. DeniedTools],
            DeniedPluginIds = [.. DeniedPluginIds],
        };

    public static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return "ddn_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record RoomStateSnapshot(
    DateTimeOffset CapturedAt,
    string Reason,
    bool IsPoweredOn,
    int Volume,
    bool IsMuted,
    string InputSource,
    string SoundMode,
    bool? Zone2On,
    string ReceiverId = "",
    string ReceiverName = "");

public sealed record RoomInsight(
    string Id,
    string Severity,
    string Category,
    string Title,
    string Explanation,
    string FixLabel,
    string FixActionKey,
    IReadOnlyDictionary<string, string>? FixParameters = null);

public sealed class RoomHabitsMemory
{
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public int TotalObservations { get; set; }

    public Dictionary<string, VolumeHabitBucket> VolumeByTimeBlock { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> InputUsageCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> SoundModeUsageCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> SceneUsageCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> PluginActionCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class VolumeHabitBucket
{
    public int SampleCount { get; set; }

    public double AverageVolume { get; set; }

    public int MinVolume { get; set; } = 98;

    public int MaxVolume { get; set; }
}

public sealed record McpActivityEntry(
    DateTimeOffset Timestamp,
    string Method,
    string Detail,
    string Outcome,
    long DurationMs)
{
    public string TimeLabel => Timestamp.LocalDateTime.ToString("HH:mm:ss");
}

/// <summary>A tool the MCP server can serve, for the Tool access switches in Settings.</summary>
public sealed record McpToolCatalogEntry(
    string Name,
    string Title,
    string? PluginId,
    string? PluginName,
    bool ReadOnly);
