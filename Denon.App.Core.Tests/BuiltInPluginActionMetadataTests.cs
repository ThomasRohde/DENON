using Denon.App.Core.Mcp;
using Denon.App.PluginSdk;
using Denon.Plugin.HomeAssistant;
using Denon.Plugin.PhilipsHue;
using Denon.Plugin.SmartTv;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>Built-in plugins declare action metadata, so MCP clients get typed tools.</summary>
public sealed class BuiltInPluginActionMetadataTests
{
    private static IReadOnlyList<PluginActionDescriptor> Actions(string id) => id switch
    {
        "hue" => new PhilipsHuePlugin().GetActions(),
        "tv" => new SmartTvPlugin().GetActions(),
        _ => new HomeAssistantPlugin().GetActions(),
    };

    [Fact]
    public void Key_parameters_and_hints_are_declared()
    {
        var hue = Actions("hue").ToDictionary(a => a.ActionId);
        var tv = Actions("tv").ToDictionary(a => a.ActionId);
        var ha = Actions("ha").ToDictionary(a => a.ActionId);

        Assert.Contains(hue["hue.movie_dim"].Parameters, p => p.Name == "roomId");
        Assert.Contains(tv["tv.youtube"].Parameters, p => p.Name == "contentId");
        Assert.Contains(tv["tv.toast"].Parameters, p => p.Name == "message");
        Assert.Contains(ha["ha.trigger_entity"].Parameters, p => p.Name == "entityId" && p.Required);

        Assert.True(McpPluginToolBridge.ResolveHints(hue["hue.refresh"]).ReadOnly);
        Assert.True(McpPluginToolBridge.ResolveHints(ha["ha.refresh"]).ReadOnly);
        Assert.True(McpPluginToolBridge.ResolveHints(tv["tv.power_off"]).Destructive);
        Assert.True(McpPluginToolBridge.ResolveHints(tv["tv.close_app"]).Destructive);
        Assert.True(McpPluginToolBridge.ResolveHints(hue["hue.all_off"]).Destructive);
    }

    [Fact]
    public void Permission_gates_do_not_change_for_built_ins()
    {
        // No built-in declares IsDestructive = true, so hue.all_off / tv.close_app stay allowed in SafeControl;
        // only power_off needs FullOrchestration.
        var declaredDestructive = new[] { "hue", "tv", "ha" }
            .SelectMany(Actions)
            .Where(a => a.IsDestructive == true)
            .Select(a => a.ActionId)
            .ToList();

        Assert.Empty(declaredDestructive);
    }
}
