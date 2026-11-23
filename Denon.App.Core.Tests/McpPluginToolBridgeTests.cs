using System.Text.Json;
using Denon.App.Core.Mcp;
using Denon.App.PluginSdk;
using Xunit;

namespace Denon.App.Core.Tests;

public sealed class McpPluginToolBridgeTests
{
    private static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Declared_parameters_become_the_input_schema()
    {
        var action = new PluginActionDescriptor("set-volume", "Set volume", "Sets volume.")
        {
            Parameters =
            [
                new("level", "integer", "Volume 0-100.", Required: true),
                new("device", "string", "Device name."),
                new("odd", "date-time", "Unknown type falls back to string."),
            ],
        };

        var schema = ToJson(McpPluginToolBridge.BuildInputSchema(action));
        var props = schema.GetProperty("properties");

        Assert.Equal("integer", props.GetProperty("level").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("device").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("odd").GetProperty("type").GetString());
        Assert.False(props.TryGetProperty("message", out _));
        Assert.Equal(["level"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void Declared_empty_parameters_give_an_empty_schema()
    {
        var schema = ToJson(McpPluginToolBridge.BuildInputSchema(
            new PluginActionDescriptor("list-devices", "List", "Lists devices.") { Parameters = [] }));

        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public void Older_plugins_keep_the_legacy_generic_schema()
    {
        var schema = ToJson(McpPluginToolBridge.BuildInputSchema(new PluginActionDescriptor("tv.toast", "Toast", "Shows a toast.")));
        var props = schema.GetProperty("properties");

        Assert.True(props.TryGetProperty("message", out _));
        Assert.True(props.TryGetProperty("button", out _));
        Assert.False(schema.TryGetProperty("required", out _));
    }

    [Theory]
    [InlineData("now-playing", true, null, true, false)]      // declared read-only
    [InlineData("quit-app", null, true, false, true)]         // declared destructive
    [InlineData("hue.refresh", null, null, true, false)]      // legacy guess: *.refresh
    [InlineData("tv.power_off", null, null, false, true)]     // legacy guess: power_off
    [InlineData("tv.power_off", null, false, false, false)]   // declaration wins over the guess
    public void Hints_use_declarations_then_legacy_guesses(string id, bool? readOnly, bool? destructive, bool expectReadOnly, bool expectDestructive)
    {
        var action = new PluginActionDescriptor(id, id, id) { IsReadOnly = readOnly, IsDestructive = destructive };
        var hints = McpPluginToolBridge.ResolveHints(action);

        Assert.Equal(expectReadOnly, hints.ReadOnly);
        Assert.Equal(expectDestructive, hints.Destructive);
    }

    [Fact]
    public void Result_keeps_json_data_and_image()
    {
        var result = PluginActionResult.OkWithData(
            "Stuen 2: Netflix (playing)",
            """{"device":"Stuen 2","app":"Netflix","volume":40}""",
            imageBase64: "aGVsbG8=",
            imageMimeType: "image/png");

        var json = ToJson(McpPluginToolBridge.BuildResult(result));
        var content = json.GetProperty("content").EnumerateArray().ToList();

        Assert.False(json.GetProperty("isError").GetBoolean());
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("image/png", content[0].GetProperty("mimeType").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        Assert.Contains("Stuen 2: Netflix (playing)", content[1].GetProperty("text").GetString());
        Assert.Contains("\"app\": \"Netflix\"", content[1].GetProperty("text").GetString());
        Assert.Equal("Netflix", json.GetProperty("structuredContent").GetProperty("app").GetString());
    }

    [Fact]
    public void Plain_and_failed_results()
    {
        var ok = ToJson(McpPluginToolBridge.BuildResult(PluginActionResult.Ok("Paused.")));
        Assert.Equal("Paused.", ok.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(ok.TryGetProperty("structuredContent", out _));

        var array = ToJson(McpPluginToolBridge.BuildResult(PluginActionResult.OkWithData("2 devices", "[1,2]")));
        Assert.False(array.TryGetProperty("structuredContent", out _)); // structuredContent must be an object

        var failed = ToJson(McpPluginToolBridge.BuildResult(PluginActionResult.Fail("No Cast device is active.")));
        Assert.True(failed.GetProperty("isError").GetBoolean());
        Assert.Equal("No Cast device is active.", failed.GetProperty("content")[0].GetProperty("text").GetString());
    }
}
