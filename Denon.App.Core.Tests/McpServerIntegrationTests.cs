using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Denon.App.Core.Heos;
using Denon.App.Core.Mcp;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The MCP server on the official C# SDK, end to end: the SDK's own client connects over Streamable HTTP on
/// 127.0.0.1 with the Bearer token and lists and calls tools, reads resources and gets prompts. No receiver is
/// needed: the tools used here only read the app's state.
/// </summary>
public sealed class McpServerIntegrationTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-mcp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> _asyncDisposables = [];
    private readonly List<IDisposable> _disposables = [];
    private AppSettingsStore _settings = null!;
    private DenonMcpServer _server = null!;
    private string _token = string.Empty;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var log = new AppLogStore(Path.Combine(_tempDir, "app.log"));
        _settings = new AppSettingsStore(log, Path.Combine(_tempDir, "settings.json"));
        await _settings.InitializeAsync();
        _token = "ddn_test_" + Guid.NewGuid().ToString("N");
        await _settings.UpdateAsync(s =>
        {
            s.Mcp.Enabled = true;
            s.Mcp.Port = FreePort();
            s.Mcp.RequireBearerAuth = true;
            s.Mcp.BearerToken = _token;
            s.Mcp.PermissionTier = McpPermissionTier.FullOrchestration;
        });

        var bus = new RoomEventBus(log);
        var telemetry = new EventTelemetryLog(_settings, log, bus, Path.Combine(_tempDir, "events"));
        var state = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(), _settings, state, telemetry, log);
        var ytMedia = new YouTubeMediaService(_settings, log);
        var relay = new LocalAudioRelayServer(ytMedia, _settings, log);
        var ytQueue = new YouTubeQueueService(relay, controller, state, log);
        var plugins = new PluginHostService([], _settings, state, controller, telemetry, log);
        var heos = new HeosEventListenerService(_settings, state, controller, log);
        var intelligence = new RoomIntelligenceService(state, controller, _settings, plugins, ytQueue, heos, log, Path.Combine(_tempDir, "room-habits.json"));
        _server = new DenonMcpServer(_settings, controller, state, plugins, intelligence, new RadioBrowserService(log), ytMedia, relay, ytQueue, new LyricsService(), telemetry, new VolumeMemoryStore(log, Path.Combine(_tempDir, "volume-memory.json")), log);

        _asyncDisposables.Add(_server);
        _asyncDisposables.Add(telemetry);
        _disposables.AddRange([ytQueue, relay, bus]);

        await _server.ApplySettingsAsync();
        Assert.True(_server.IsRunning, _server.LastError);
    }

    public async Task DisposeAsync()
    {
        foreach (var d in _asyncDisposables)
        {
            await d.DisposeAsync();
        }

        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private Uri Endpoint => new(_server.ListeningEndpoint);

    private Task<McpClient> ConnectAsync() =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {_token}" },
        }));

    [Fact]
    public async Task The_sdk_client_lists_and_calls_tools_reads_resources_and_gets_prompts()
    {
        await using var client = await ConnectAsync();
        Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
        Assert.Equal(DenonMcpServer.ServerVersion, client.ServerInfo?.Version); // the app's version, from the entry assembly

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "get_room_state");
        var volume = Assert.Single(tools, t => t.Name == "avr_set_volume");
        Assert.Equal("object", volume.ProtocolTool.InputSchema.GetProperty("type").GetString());

        var habits = await client.CallToolAsync("get_listening_habits");
        Assert.NotEqual(true, habits.IsError);
        Assert.Equal(JsonValueKind.Object, habits.StructuredContent?.ValueKind);
        Assert.IsType<TextContentBlock>(Assert.Single(habits.Content));

        var resources = await client.ListResourcesAsync();
        Assert.Contains(resources, r => r.Uri == "denon://room/state");
        var catalog = await client.ReadResourceAsync("denon://catalog/capabilities");
        var text = Assert.IsType<TextResourceContents>(Assert.Single(catalog.Contents));
        using (JsonDocument.Parse(text.Text))
        {
        }

        var unknown = await Assert.ThrowsAsync<McpProtocolException>(() => client.ReadResourceAsync("denon://nothing").AsTask());
        Assert.Equal(McpErrorCode.InvalidParams, unknown.ErrorCode);

        var prompt = await client.GetPromptAsync("design_atmosphere", new Dictionary<string, object?> { ["vibe"] = "rainy jazz" });
        var message = Assert.Single(prompt.Messages);
        Assert.Contains("rainy jazz", Assert.IsType<TextContentBlock>(message.Content).Text);

        Assert.Contains(_server.GetRecentActivity(), a => a.Method == "tools/call" && a.Detail == "get_listening_habits" && a.Outcome == "OK");
    }

    [Fact]
    public async Task The_now_playing_card_is_an_mcp_app()
    {
        await using var client = await ConnectAsync();

        // The server announces MCP Apps; the tool points at its card.
        Assert.True(client.ServerCapabilities.Extensions?.ContainsKey("io.modelcontextprotocol/ui"));
        var tool = Assert.Single(await client.ListToolsAsync(), t => t.Name == "show_now_playing");
        var ui = tool.ProtocolTool.Meta?["ui"];
        Assert.Equal("ui://denon/now-playing.html", ui?["resourceUri"]?.GetValue<string>());
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);

        // The card is a ui:// resource with the MCP Apps MIME type: a whole HTML page that speaks the view protocol.
        var resource = Assert.Single(await client.ListResourcesAsync(), r => r.Uri == "ui://denon/now-playing.html");
        Assert.Equal("text/html;profile=mcp-app", resource.MimeType);
        var html = Assert.IsType<TextResourceContents>(Assert.Single((await client.ReadResourceAsync("ui://denon/now-playing.html")).Contents));
        Assert.Equal("text/html;profile=mcp-app", html.MimeType);
        Assert.StartsWith("<!DOCTYPE html>", html.Text);
        Assert.Contains("ui/initialize", html.Text);
        Assert.Contains("ui/notifications/tool-result", html.Text);

        // Without a receiver it says so, as text and as data for the card.
        var result = await client.CallToolAsync("show_now_playing");
        Assert.NotEqual(true, result.IsError);
        var data = result.StructuredContent!.Value;
        Assert.False(data.GetProperty("connected").GetBoolean());
        Assert.Equal(65, data.GetProperty("safeVolume").GetInt32());
        Assert.Contains("not connected", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task Every_card_is_an_mcp_app_with_the_shared_script_put_in()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync();
        var resources = await client.ListResourcesAsync();

        foreach (var card in DenonMcpServer.AppCards)
        {
            var tool = Assert.Single(tools, t => t.Name == card.ToolName);
            Assert.Equal(card.Uri, tool.ProtocolTool.Meta?["ui"]?["resourceUri"]?.GetValue<string>());
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint, card.ToolName);
            Assert.Contains(resources, r => r.Uri == card.Uri && r.MimeType == "text/html;profile=mcp-app");

            var page = Assert.IsType<TextResourceContents>(Assert.Single((await client.ReadResourceAsync(card.Uri)).Contents)).Text;
            Assert.StartsWith("<!DOCTYPE html>", page);
            Assert.Contains("ui/initialize", page);
            Assert.Contains("--color-background-primary", page);
            Assert.DoesNotContain("/*@shared", page);
            Assert.Contains($"tool: \"{card.ToolName}\"", page);
        }

        foreach (var helper in new[] { "media_play_station", "save_station", "volume_memory_set_level", "volume_memory_forget" })
        {
            Assert.Contains(tools, t => t.Name == helper);
        }
    }

    [Fact]
    public async Task The_cards_return_their_data_without_a_receiver()
    {
        await using var client = await ConnectAsync();

        var remote = (await client.CallToolAsync("show_room_remote")).StructuredContent!.Value;
        Assert.False(remote.GetProperty("connected").GetBoolean());
        Assert.True(remote.GetProperty("scenes").GetArrayLength() > 0);
        Assert.True(remote.GetProperty("stations").GetArrayLength() > 0);
        Assert.Contains(remote.GetProperty("inputs").EnumerateArray(), i => i.GetProperty("code").GetString() == "NET");

        var list = (await client.CallToolAsync("preview_automation")).StructuredContent!.Value;
        Assert.Equal("list", list.GetProperty("mode").GetString());

        var insights = await client.CallToolAsync("show_insights", new Dictionary<string, object?> { ["days"] = 7 });
        Assert.NotEqual(true, insights.IsError);
        Assert.Equal(7, insights.StructuredContent!.Value.GetProperty("days").GetInt32());

        var memory = (await client.CallToolAsync("show_volume_memory")).StructuredContent!.Value;
        Assert.Equal(65, memory.GetProperty("safeVolume").GetInt32());
        Assert.Equal(0, memory.GetProperty("levels").GetArrayLength());
    }

    [Fact]
    public async Task An_automation_preview_shows_steps_and_problems_and_saves_nothing()
    {
        await using var client = await ConnectAsync();
        var before = _settings.Current.AutomationGraphs.Count;
        var scene = _settings.Current.Receiver.Scenes[0];

        var good = new Dictionary<string, object?>
        {
            ["automation"] = new Dictionary<string, object?>
            {
                ["name"] = "Webhook to scene",
                ["nodes"] = new object[]
                {
                    new { id = "t", type = "trigger.webhook", config = new Dictionary<string, string> { ["hook"] = "arrive-home" } },
                    new { id = "w", type = "condition.time", config = new Dictionary<string, string> { ["from"] = "07:00", ["to"] = "22:00", ["days"] = "Daily" } },
                    new { id = "a", type = "action.scene", config = new Dictionary<string, string> { ["sceneId"] = scene.Id } },
                },
                ["edges"] = new object[] { new { from = "t", port = "out", to = "w" }, new { from = "w", port = "yes", to = "a" } },
            },
        };
        var data = (await client.CallToolAsync("preview_automation", good)).StructuredContent!.Value;
        Assert.Equal("preview", data.GetProperty("mode").GetString());
        Assert.Equal(0, data.GetProperty("problems").GetArrayLength());
        var steps = data.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["t", "w", "a"], steps.Select(s => s.GetProperty("id").GetString()));
        Assert.Equal("yes", steps[2].GetProperty("port").GetString());
        Assert.Contains(scene.Name, steps[2].GetProperty("text").GetString());
        Assert.Contains("arrive-home", steps[0].GetProperty("text").GetString());
        Assert.False(data.GetProperty("automation").GetProperty("enabled").GetBoolean());

        var bad = await client.CallToolAsync("preview_automation", new Dictionary<string, object?>
        {
            ["automation"] = new Dictionary<string, object?> { ["name"] = "No trigger", ["nodes"] = Array.Empty<object>(), ["edges"] = Array.Empty<object>() },
        });
        Assert.True(bad.StructuredContent!.Value.GetProperty("problems").GetArrayLength() > 0);
        Assert.Equal(before, _settings.Current.AutomationGraphs.Count); // a preview saves nothing

        // Both branches lead to one action: the second path shows it as a repeat. A null config gives problems, not a failure.
        var converge = (await client.CallToolAsync("preview_automation", new Dictionary<string, object?>
        {
            ["automation"] = new Dictionary<string, object?>
            {
                ["name"] = "Both ways",
                ["nodes"] = new object?[]
                {
                    new { id = "t", type = "trigger.webhook", config = new Dictionary<string, string> { ["hook"] = "x" } },
                    new { id = "c", type = "condition.receiver", config = new Dictionary<string, string> { ["power"] = "on" } },
                    new { id = "a", type = "action.receiver", config = (Dictionary<string, string>?)null },
                },
                ["edges"] = new object[]
                {
                    new { from = "t", port = "out", to = "c" }, new { from = "c", port = "yes", to = "a" }, new { from = "c", port = "no", to = "a" },
                },
            },
        })).StructuredContent!.Value;
        var paths = converge.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("id").GetString() == "a").ToList();
        Assert.Equal(2, paths.Count);
        Assert.Equal(["yes", "no"], paths.Select(s => s.GetProperty("port").GetString()));
        Assert.True(paths[1].GetProperty("repeat").GetBoolean());
        Assert.True(converge.GetProperty("problems").GetArrayLength() > 0); // the receiver action has no command

        var saved = _settings.Current.AutomationGraphs[0];
        var shown = (await client.CallToolAsync("preview_automation", new Dictionary<string, object?> { ["automation_id"] = saved.Id })).StructuredContent!.Value;
        Assert.Equal("saved", shown.GetProperty("mode").GetString());
        Assert.Equal(saved.Name, shown.GetProperty("automation").GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_card_helpers_save_a_station_and_check_their_input()
    {
        await using var client = await ConnectAsync();

        var save = await client.CallToolAsync("save_station", new Dictionary<string, object?> { ["name"] = "Test FM", ["url"] = "https://example.com/test.mp3" });
        Assert.NotEqual(true, save.IsError);
        Assert.Contains(_settings.Current.Receiver.Stations, s => s.Alias == "test-fm" && s.Url == "https://example.com/test.mp3");
        var again = await client.CallToolAsync("save_station", new Dictionary<string, object?> { ["name"] = "Test FM 2", ["url"] = "https://example.com/test.mp3" });
        Assert.Contains("saved already", Assert.IsType<TextContentBlock>(Assert.Single(again.Content)).Text);
        var notHttp = await client.CallToolAsync("save_station", new Dictionary<string, object?> { ["name"] = "X", ["url"] = "file:///c:/x.mp3" });
        Assert.True(notHttp.IsError);

        // Unknown keys and stations are invalid params.
        var unknown = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync(
            "volume_memory_set_level", new Dictionary<string, object?> { ["key"] = "input:nothing", ["level"] = 30 }).AsTask());
        Assert.Contains("No learned volume", unknown.Message);
        var missing = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync(
            "media_play_station", new Dictionary<string, object?> { ["station"] = "no-such-station" }).AsTask());
        Assert.Contains("No saved station", missing.Message);
    }

    [Fact]
    public async Task A_client_on_the_older_protocol_still_works_with_a_session()
    {
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = Endpoint,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {_token}" },
            }),
            new McpClientOptions { ProtocolVersion = "2025-11-25" });

        Assert.Equal("2025-11-25", client.NegotiatedProtocolVersion);
        Assert.Contains(await client.ListToolsAsync(), t => t.Name == "get_room_state");
        Assert.NotEqual(true, (await client.CallToolAsync("diagnose_room")).IsError);
    }

    [Fact]
    public async Task The_permission_tier_still_guards_the_tools()
    {
        await _settings.UpdateAsync(s => s.Mcp.PermissionTier = McpPermissionTier.Observer);
        await using var client = await ConnectAsync();

        var blocked = await client.CallToolAsync("avr_set_volume", new Dictionary<string, object?> { ["volume"] = 30 });

        Assert.True(blocked.IsError);
        Assert.Contains("Observer", Assert.IsType<TextContentBlock>(Assert.Single(blocked.Content)).Text);
    }

    [Fact]
    public async Task Without_the_token_or_from_another_site_the_server_says_no()
    {
        using var http = new HttpClient();
        var health = await http.GetAsync(new Uri(Endpoint, "/health"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode); // no token needed

        HttpRequestMessage Initialize(string? token, string? origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Initialize(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Initialize("ddn_wrong", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Initialize(_token, "https://evil.example"))).StatusCode);
    }

    [Fact]
    public async Task A_browser_client_on_this_pc_passes_the_preflight_and_others_do_not()
    {
        using var http = new HttpClient();

        HttpRequestMessage Preflight(string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Options, Endpoint);
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "authorization, content-type, mcp-protocol-version");
            return request;
        }

        var local = await http.SendAsync(Preflight("http://localhost:6274"));
        Assert.True(local.IsSuccessStatusCode, $"preflight: {(int)local.StatusCode}");
        Assert.Equal("http://localhost:6274", local.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var foreign = await http.SendAsync(Preflight("https://evil.example"));
        Assert.False(foreign.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Every_declared_output_field_is_in_the_tool_result()
    {
        await using var client = await ConnectAsync();

        // The tools that need no receiver and no input: their outputSchema must name fields the result really has.
        foreach (var name in new[] { "get_room_state", "diagnose_room", "get_listening_habits", "get_activity_history" })
        {
            var tool = Assert.Single(await client.ListToolsAsync(), t => t.Name == name);
            if (tool.ProtocolTool.OutputSchema is not { } schema || !schema.TryGetProperty("properties", out var declared))
            {
                continue;
            }

            var result = await client.CallToolAsync(name);
            Assert.NotEqual(true, result.IsError);
            var data = result.StructuredContent!.Value;
            foreach (var field in declared.EnumerateObject())
            {
                Assert.True(data.TryGetProperty(field.Name, out _), $"{name}: outputSchema names '{field.Name}', which the result does not have.");
            }
        }
    }

    [Fact]
    public async Task Apply_atmosphere_refuses_an_undeclared_plugin_action_before_it_changes_anything()
    {
        await _settings.UpdateAsync(s => s.Mcp.PermissionTier = McpPermissionTier.SafeControl);
        await using var client = await ConnectAsync();

        // No receiver here: a step that ran would fail to connect. The refusal must come first.
        var refused = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("apply_atmosphere", new Dictionary<string, object?>
        {
            ["reason"] = "test",
            ["volume"] = 20,
            ["tv_action"] = "tv.power_off",
        }).AsTask());
        Assert.Equal(McpErrorCode.InvalidParams, refused.ErrorCode);
        Assert.Contains("smart-tv", refused.Message);
    }

    [Fact]
    public async Task An_automation_with_repeated_node_ids_gets_the_validator_message()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("create_or_update_automation", new Dictionary<string, object?>
        {
            ["automation"] = new Dictionary<string, object?>
            {
                ["name"] = "Twice",
                ["nodes"] = new object[]
                {
                    new Dictionary<string, object?> { ["id"] = "a", ["type"] = "trigger.receiver_power" },
                    new Dictionary<string, object?> { ["id"] = "a", ["type"] = "action.set_volume" },
                },
                ["edges"] = Array.Empty<object>(),
            },
        });

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("not saved", text);
        Assert.DoesNotContain("same key", text);
    }

    [Fact]
    public async Task Health_answers_only_for_this_pcs_host_name()
    {
        using var http = new HttpClient();
        var health = new Uri(Endpoint, "/health");

        var local = await http.GetAsync(health);
        Assert.True(local.IsSuccessStatusCode);

        using var rebound = new HttpRequestMessage(HttpMethod.Get, health);
        rebound.Headers.Host = "evil.example:" + health.Port; // A DNS-rebinding page reaches 127.0.0.1 under its own name.
        var refused = await http.SendAsync(rebound);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Theory]
    [InlineData("localhost", false, true)]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("[::1]", false, true)]
    [InlineData("evil.example", false, false)]
    [InlineData("evil.example", true, false)]
    [InlineData("192.168.1.31", false, false)]
    [InlineData("192.168.1.31", true, true)]
    [InlineData("10.0.0.5", true, true)]
    [InlineData("8.8.8.8", true, false)] // Not a LAN address.
    [InlineData("[fe80::1]", true, true)]
    [InlineData("", true, false)]
    public void The_host_header_must_name_this_pc(string host, bool allowLan, bool allowed) =>
        Assert.Equal(allowed, DenonMcpServer.IsAllowedHost(host, allowLan));

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://localhost:6274", true)]
    [InlineData("http://127.0.0.1:5173", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("null", false)]
    public void Origins_from_this_pc_only(string? origin, bool allowed) =>
        Assert.Equal(allowed, DenonMcpServer.IsAllowedOrigin(origin));
}
