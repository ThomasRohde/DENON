namespace Denon.App.Core.Models;

/// <summary>One plugin action in a Smart Scene's action chain.</summary>
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
