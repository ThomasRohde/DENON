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
