namespace Denon.App.Core.Models;

public enum AutomationTriggerType
{
    AppLaunch = 0,
    TimeOfDay = 1,
}

public sealed class ScenePluginActionStep
{
    public string PluginId { get; set; } = string.Empty;

    public string ActionId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int DelayMs { get; set; }

    public Dictionary<string, string>? Parameters { get; set; }

    public ScenePluginActionStep Clone() =>
        new()
        {
            PluginId = PluginId,
            ActionId = ActionId,
            DisplayName = DisplayName,
            DelayMs = DelayMs,
            Parameters = Parameters is null ? null : new Dictionary<string, string>(Parameters),
        };
}

public sealed class AutomationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "New Automation";

    public bool IsEnabled { get; set; }

    public AutomationTriggerType TriggerType { get; set; } = AutomationTriggerType.AppLaunch;

    /// <summary>
    /// Comma-separated Windows process names without .exe (e.g. "vlc, mpv, Plex").
    /// </summary>
    public string ProcessNames { get; set; } = string.Empty;

    /// <summary>
    /// 24-hour local schedule time in HH:mm format (e.g. "22:00").
    /// </summary>
    public string ScheduleTime { get; set; } = "22:00";

    /// <summary>
    /// Schedule cadence: "Daily", "Weekdays", or "Weekends".
    /// </summary>
    public string DaysFilter { get; set; } = "Daily";

    /// <summary>
    /// Optional Smart Scene ID to activate when triggered.
    /// </summary>
    public string? TargetSceneId { get; set; }

    /// <summary>
    /// Optional extra plugin actions to execute when triggered.
    /// </summary>
    public List<ScenePluginActionStep> PluginActions { get; set; } = [];

    /// <summary>
    /// Minimum seconds between repeated AppLaunch triggers for this rule.
    /// </summary>
    public int CooldownSeconds { get; set; } = 120;

    public AutomationRule Clone() =>
        new()
        {
            Id = Id,
            Name = Name,
            IsEnabled = IsEnabled,
            TriggerType = TriggerType,
            ProcessNames = ProcessNames,
            ScheduleTime = ScheduleTime,
            DaysFilter = DaysFilter,
            TargetSceneId = TargetSceneId,
            PluginActions = PluginActions.Select(static s => s.Clone()).ToList(),
            CooldownSeconds = CooldownSeconds,
        };

    public static List<AutomationRule> CreateDefaults() =>
    [
        new AutomationRule
        {
            Id = "auto-cinema-app",
            Name = "Media Player → Movie Night",
            IsEnabled = false,
            TriggerType = AutomationTriggerType.AppLaunch,
            ProcessNames = "vlc, mpv, Plex, Kodi",
            TargetSceneId = "movie-night",
            PluginActions =
            [
                new ScenePluginActionStep
                {
                    PluginId = "philips-hue",
                    ActionId = "hue.movie_dim",
                    DisplayName = "Philips Hue: Cinema Dim (15%)",
                    DelayMs = 0,
                },
            ],
            CooldownSeconds = 180,
        },
        new AutomationRule
        {
            Id = "auto-night-guard",
            Name = "Night Guard (22:00)",
            IsEnabled = false,
            TriggerType = AutomationTriggerType.TimeOfDay,
            ScheduleTime = "22:00",
            DaysFilter = "Daily",
            TargetSceneId = "late-night",
            PluginActions =
            [
                new ScenePluginActionStep
                {
                    PluginId = "philips-hue",
                    ActionId = "hue.candlelight",
                    DisplayName = "Philips Hue: Late Night Candlelight",
                    DelayMs = 0,
                },
            ],
            CooldownSeconds = 300,
        },
        new AutomationRule
        {
            Id = "auto-gaming-app",
            Name = "Gaming Launch → Hi-Fi Cyan",
            IsEnabled = false,
            TriggerType = AutomationTriggerType.AppLaunch,
            ProcessNames = "Cyberpunk2077, cs2, valorant, eldenring",
            TargetSceneId = "pure-audiophile",
            PluginActions =
            [
                new ScenePluginActionStep
                {
                    PluginId = "philips-hue",
                    ActionId = "hue.hifi_cyan",
                    DisplayName = "Philips Hue: Hi-Fi Cyan Glow",
                    DelayMs = 0,
                },
            ],
            CooldownSeconds = 300,
        },
    ];
}
