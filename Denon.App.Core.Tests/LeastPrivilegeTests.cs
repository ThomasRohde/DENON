using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Denon.App.Core.Heos;
using Denon.App.Core.Mcp;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.PluginSdk;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// Least privilege (#77): the MCP audit log (line shape, no values, rotation), the per-tool and per-plugin switches,
/// the LAN-only address ranges, and the settings of the webhook token. The webhook token's scope on the Phone Remote
/// API is tested with the webhook endpoint in <see cref="AutomationEventTests"/>.
/// </summary>
public sealed class LeastPrivilegeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-least-priv-" + Guid.NewGuid().ToString("N")[..8]);

    public LeastPrivilegeTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ---- Audit log ----------------------------------------------------------------------------------------

    [Fact]
    public void An_audit_line_has_the_fields_and_the_argument_names_but_no_values()
    {
        var at = new DateTimeOffset(2026, 11, 28, 20, 15, 7, 123, TimeSpan.FromHours(1));
        var line = McpAuditLog.FormatLine(new McpAuditEntry(at, "192.168.1.20", "claude-code", "avr_set_volume", ["level", "token"], McpAuditOutcome.Ok, 42));

        Assert.DoesNotContain('\n', line);
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("2026-11-28T19:15:07.123Z", root.GetProperty("ts").GetString());
        Assert.Equal("192.168.1.20", root.GetProperty("ip").GetString());
        Assert.Equal("claude-code", root.GetProperty("client").GetString());
        Assert.Equal("avr_set_volume", root.GetProperty("tool").GetString());
        Assert.Equal(["level", "token"], root.GetProperty("argKeys").EnumerateArray().Select(static k => k.GetString()!).ToArray());
        Assert.Equal("ok", root.GetProperty("outcome").GetString());
        Assert.Equal(42, root.GetProperty("ms").GetInt64());
        Assert.Equal(["ts", "ip", "client", "tool", "argKeys", "outcome", "ms"], root.EnumerateObject().Select(static p => p.Name).ToArray());
    }

    [Fact]
    public void Argument_names_are_capped_and_a_missing_address_is_null()
    {
        var many = Enumerable.Range(0, 50).Select(static i => "k" + i).Append(new string('x', 200)).Append("k1").ToList();
        var keys = McpAuditLog.ArgumentKeysOf(many);
        Assert.Equal(McpAuditLog.MaxKeys, keys.Count);
        Assert.Equal(keys.Distinct().Count(), keys.Count);
        Assert.All(keys, static k => Assert.True(k.Length <= McpAuditLog.MaxKeyLength));
        Assert.Equal(64, McpAuditLog.ArgumentKeysOf([new string('x', 200)]).Single().Length);
        Assert.Empty(McpAuditLog.ArgumentKeysOf(null));

        using var doc = JsonDocument.Parse(McpAuditLog.FormatLine(new McpAuditEntry(DateTimeOffset.UtcNow, null, null, "t", [], McpAuditOutcome.Refused, -5)));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("ip").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("client").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("ms").GetInt64());
    }

    [Fact]
    public void The_audit_log_starts_a_new_file_each_day_and_at_the_size_cap_and_keeps_the_newest()
    {
        var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
        var dir = Path.Combine(_tempDir, "audit");
        var log = new McpAuditLog(dir, () => now, maxFileBytes: 1024, maxFiles: 3);
        McpAuditEntry Entry() => new(now, "127.0.0.1", "test", "get_room_state", ["a"], McpAuditOutcome.Ok, 1);

        Assert.True(log.Write(Entry()));
        var first = log.CurrentFile!;
        Assert.Equal("mcp-audit-20261120-080000-00.jsonl", Path.GetFileName(first));
        Assert.True(log.Write(Entry()));
        Assert.Equal(first, log.CurrentFile);
        Assert.Equal(2, File.ReadAllLines(first).Length);

        // The size cap: a new file in the same second sorts after the full one.
        while (new FileInfo(first).Length < 1024)
        {
            log.Write(Entry());
        }

        log.Write(Entry());
        Assert.Equal("mcp-audit-20261120-080000-01.jsonl", Path.GetFileName(log.CurrentFile));

        // A new day: a new file. Only the newest 3 files are kept.
        now = now.AddDays(1);
        log.Write(Entry());
        now = now.AddDays(1);
        log.Write(Entry());
        var files = log.GetFiles().Select(static f => Path.GetFileName(f)).ToArray();
        Assert.Equal(["mcp-audit-20261120-080000-01.jsonl", "mcp-audit-20261121-080000-00.jsonl", "mcp-audit-20261122-080000-00.jsonl"], files);
        Assert.All(log.GetFiles().SelectMany(File.ReadAllLines), static l => JsonDocument.Parse(l).Dispose());
    }

    [Fact]
    public void After_a_restart_the_audit_log_goes_on_with_todays_file()
    {
        var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
        var dir = Path.Combine(_tempDir, "audit-restart");
        var first = new McpAuditLog(dir, () => now);
        first.Write(new McpAuditEntry(now, null, null, "a", [], McpAuditOutcome.Ok, 0));

        now = now.AddHours(3);
        var second = new McpAuditLog(dir, () => now);
        second.Write(new McpAuditEntry(now, null, null, "b", [], McpAuditOutcome.Ok, 0));
        Assert.Equal(first.CurrentFile, second.CurrentFile);
        Assert.Equal(2, File.ReadAllLines(second.CurrentFile!).Length);

        now = now.AddDays(1);
        var third = new McpAuditLog(dir, () => now);
        third.Write(new McpAuditEntry(now, null, null, "c", [], McpAuditOutcome.Ok, 0));
        Assert.NotEqual(first.CurrentFile, third.CurrentFile);
    }

    // ---- Tool access -------------------------------------------------------------------------------------

    [Fact]
    public void A_tool_is_denied_by_name_or_through_its_plugin()
    {
        var cfg = new McpServerSettings { DeniedTools = ["avr_set_power"], DeniedPluginIds = ["philips-hue"] };

        Assert.True(McpToolAccess.IsDenied(cfg, "avr_set_power"));
        Assert.True(McpToolAccess.IsDenied(cfg, "AVR_SET_POWER"));
        Assert.False(McpToolAccess.IsDenied(cfg, "avr_set_volume"));
        Assert.True(McpToolAccess.IsDenied(cfg, "plugin_philips_hue_hue_all_on", "philips-hue"));
        Assert.False(McpToolAccess.IsDenied(cfg, "plugin_smart_tv_tv_power_off", "smart-tv"));
        Assert.True(McpToolAccess.IsDenied(cfg, "hue_set_color")); // the plugin's named tool
        Assert.False(McpToolAccess.IsDenied(cfg, "tv_computer"));
        Assert.Equal("smart-tv", McpToolAccess.NamedToolPlugin("tv_computer"));
        Assert.Null(McpToolAccess.NamedToolPlugin("get_room_state"));
        Assert.Contains("turned off", McpToolAccess.RefusalMessage("avr_set_power"));
    }

    [Fact]
    public async Task Settings_keep_the_deny_lists_tidy_and_make_a_webhook_token_that_an_export_hides()
    {
        var log = new AppLogStore(Path.Combine(_tempDir, "app.log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, "settings.json"));
        await store.InitializeAsync();

        var hook = store.Current.Remote.WebhookToken;
        Assert.StartsWith("hook_", hook);
        Assert.NotEqual(store.Current.Remote.OwnerToken, hook);
        Assert.True(store.Current.Mcp.AuditLogEnabled);
        Assert.False(store.Current.Security.LanOnly);

        await store.UpdateAsync(s =>
        {
            s.Mcp.DeniedTools = [" avr_set_power ", "AVR_SET_POWER", "", "get_room_state"];
            s.Mcp.DeniedPluginIds = ["smart-tv", "Smart-TV"];
            s.Security.LanOnly = true;
        });
        Assert.Equal(["avr_set_power", "get_room_state"], store.Current.Mcp.DeniedTools);
        Assert.Single(store.Current.Mcp.DeniedPluginIds);

        var export = store.ExportJson();
        Assert.DoesNotContain(hook, export);
        var imported = await store.ImportJsonAsync(export);
        Assert.Equal(hook, imported.Remote.WebhookToken);
        Assert.True(imported.Security.LanOnly);

        // A file without the webhook token gets one, and it is saved (the same one after the next start).
        var reopened = new AppSettingsStore(log, Path.Combine(_tempDir, "settings.json"));
        await reopened.InitializeAsync();
        Assert.Equal(hook, reopened.Current.Remote.WebhookToken);

        var json = File.ReadAllText(Path.Combine(_tempDir, "settings.json")).Replace(hook, string.Empty, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), json);
        var fresh = new AppSettingsStore(log, Path.Combine(_tempDir, "settings.json"));
        await fresh.InitializeAsync();
        Assert.StartsWith("hook_", fresh.Current.Remote.WebhookToken);
        Assert.NotEqual(hook, fresh.Current.Remote.WebhookToken);
    }

    // ---- LAN only ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.10.20.30", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.31", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1c2d:3e4f", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("::ffff:10.1.2.3", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("100.64.1.2", false)]   // CGNAT / Tailscale
    [InlineData("8.8.8.8", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("fec0::1", false)]      // old site-local, not allowed
    [InlineData("::", false)]
    public void The_lan_only_rule_allows_loopback_private_and_link_local_addresses(string address, bool local)
    {
        var ip = IPAddress.Parse(address);
        Assert.Equal(local, LanAddressGuard.IsLocalNetwork(ip));
        Assert.Equal(!local, LanAddressGuard.Refuses(lanOnly: true, ip));
        Assert.False(LanAddressGuard.Refuses(lanOnly: false, ip));
    }

    [Fact]
    public void The_lan_only_rule_refuses_an_unknown_address()
    {
        Assert.False(LanAddressGuard.IsLocalNetwork(null));
        Assert.True(LanAddressGuard.Refuses(lanOnly: true, null));
    }

    // ---- The MCP server end to end ------------------------------------------------------------------------

    [Fact]
    public async Task A_denied_tool_is_not_listed_a_call_is_refused_and_the_audit_log_says_so()
    {
        await using var rig = await McpRig.StartAsync(_tempDir, s =>
        {
            s.Mcp.DeniedTools = ["get_listening_habits"];
            s.Mcp.DeniedPluginIds = ["fake-lights"];
            s.Security.LanOnly = true; // this test's client is on 127.0.0.1, which LAN only allows
        });
        await using var client = await rig.ConnectAsync();

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, static t => t.Name == "get_room_state");
        Assert.DoesNotContain(tools, static t => t.Name == "get_listening_habits");
        Assert.DoesNotContain(tools, static t => t.Name.StartsWith("plugin_fake_lights", StringComparison.Ordinal));
        Assert.Contains(tools, static t => t.Name == "plugin_fake_switch_fake_toggle");

        var refused = await client.CallToolAsync("get_listening_habits", new Dictionary<string, object?> { ["secret_value_key"] = "hunter2" });
        Assert.True(refused.IsError);
        Assert.Contains("turned off", Assert.IsType<TextContentBlock>(Assert.Single(refused.Content)).Text);

        var pluginRefused = await client.CallToolAsync("plugin_fake_lights_fake_on");
        Assert.True(pluginRefused.IsError);
        Assert.Equal(0, rig.Lights.ActionsRun);

        var unknown = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("no_such_tool").AsTask());
        Assert.Equal(McpErrorCode.InvalidParams, unknown.ErrorCode);

        var ok = await client.CallToolAsync("plugin_fake_switch_fake_toggle", new Dictionary<string, object?> { ["room"] = "living-room-9876" });
        Assert.NotEqual(true, ok.IsError);
        Assert.Equal(1, rig.Switch.ActionsRun);

        // The catalog for the Settings switches still has the denied tools.
        var catalog = rig.Server.GetToolCatalog();
        Assert.Contains(catalog, static t => t.Name == "get_listening_habits" && t.PluginId is null);
        Assert.Contains(catalog, static t => t.Name == "plugin_fake_lights_fake_on" && t.PluginId == "fake-lights" && t.PluginName == "Fake Lights");
        Assert.Contains(catalog, static t => t.Name == "hue_set_color" && t.PluginId == "philips-hue");

        var lines = rig.AuditLines();
        Assert.Contains(lines, static l => l.Tool == "get_listening_habits" && l.Outcome == "refused" && l.ArgKeys.SequenceEqual(["secret_value_key"]));
        Assert.Contains(lines, static l => l.Tool == "plugin_fake_lights_fake_on" && l.Outcome == "refused");
        Assert.Contains(lines, static l => l.Tool == "no_such_tool" && l.Outcome == "error");
        var good = Assert.Single(lines, static l => l.Tool == "plugin_fake_switch_fake_toggle");
        Assert.Equal("ok", good.Outcome);
        Assert.Equal("127.0.0.1", good.Ip);
        Assert.False(string.IsNullOrWhiteSpace(good.Client));
        Assert.Equal(["room"], good.ArgKeys);

        var raw = string.Join("\n", rig.Audit.GetFiles().Select(File.ReadAllText));
        Assert.DoesNotContain("hunter2", raw);
        Assert.DoesNotContain("living-room-9876", raw);
        Assert.DoesNotContain(rig.Token, raw);
    }

    [Fact]
    public async Task Turning_a_tool_on_again_lists_it_and_the_audit_log_can_be_off()
    {
        await using var rig = await McpRig.StartAsync(_tempDir, s =>
        {
            s.Mcp.DeniedTools = ["get_listening_habits"];
            s.Mcp.AuditLogEnabled = false;
        });
        await using var client = await rig.ConnectAsync();
        Assert.DoesNotContain(await client.ListToolsAsync(), static t => t.Name == "get_listening_habits");

        await rig.Store.UpdateAsync(static s => s.Mcp.DeniedTools = []);
        Assert.Contains(await client.ListToolsAsync(), static t => t.Name == "get_listening_habits");
        Assert.NotEqual(true, (await client.CallToolAsync("get_listening_habits")).IsError);
        Assert.Empty(rig.Audit.GetFiles());
    }

    [Fact]
    public async Task Apply_atmosphere_is_refused_when_it_would_use_a_plugin_that_is_off()
    {
        await using var rig = await McpRig.StartAsync(_tempDir, static s => s.Mcp.DeniedPluginIds = ["philips-hue"]);
        await using var client = await rig.ConnectAsync();

        var result = await client.CallToolAsync("apply_atmosphere", new Dictionary<string, object?> { ["hue_action"] = "hue.all_on" });
        Assert.True(result.IsError);
        Assert.Contains("philips-hue", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Contains(rig.AuditLines(), static l => l.Tool == "apply_atmosphere" && l.Outcome == "refused");
    }

    [Theory]
    [InlineData("oled_screen_off", true)]
    [InlineData("tv_toast", "Hello")]
    public async Task Apply_atmosphere_is_refused_when_its_tv_steps_would_use_the_tv_plugin_that_is_off(string argument, object value)
    {
        await using var rig = await McpRig.StartAsync(_tempDir, static s => s.Mcp.DeniedPluginIds = ["smart-tv"]);
        await using var client = await rig.ConnectAsync();

        var result = await client.CallToolAsync("apply_atmosphere", new Dictionary<string, object?> { [argument] = value, ["volume"] = 30 });
        Assert.True(result.IsError);
        Assert.Contains("smart-tv", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Null(rig.Server.GetRecentActivity().FirstOrDefault(static a => a.Outcome == "OK" && a.Detail == "apply_atmosphere"));
    }

    private sealed record AuditLine(string Tool, string Outcome, string? Ip, string? Client, string[] ArgKeys);

    private sealed class McpRig : IAsyncDisposable
    {
        private readonly List<Func<ValueTask>> _cleanup = [];

        public required AppSettingsStore Store { get; init; }

        public required DenonMcpServer Server { get; init; }

        public required McpAuditLog Audit { get; init; }

        public required string Token { get; init; }

        public required FakeActionPlugin Lights { get; init; }

        public required FakeActionPlugin Switch { get; init; }

        public static async Task<McpRig> StartAsync(string tempDir, Action<AppSettings> configure)
        {
            var dir = Path.Combine(tempDir, "rig-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var log = new AppLogStore(Path.Combine(dir, "app.log"));
            var store = new AppSettingsStore(log, Path.Combine(dir, "settings.json"));
            await store.InitializeAsync();
            var token = "ddn_test_" + Guid.NewGuid().ToString("N");
            await store.UpdateAsync(s =>
            {
                s.Mcp.Enabled = true;
                s.Mcp.Port = FreePort();
                s.Mcp.RequireBearerAuth = true;
                s.Mcp.BearerToken = token;
                s.Mcp.PermissionTier = McpPermissionTier.FullOrchestration;
                configure(s);
            });

            var bus = new RoomEventBus(log);
            var telemetry = new EventTelemetryLog(store, log, bus, Path.Combine(dir, "events"));
            var state = new ReceiverStateStore();
            var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(), store, state, telemetry, log);
            var ytMedia = new YouTubeMediaService(store, log);
            var relay = new LocalAudioRelayServer(ytMedia, store, log);
            var ytQueue = new YouTubeQueueService(relay, controller, state, log);
            var lights = new FakeActionPlugin("fake-lights", "Fake Lights", "fake.on");
            var plugSwitch = new FakeActionPlugin("fake-switch", "Fake Switch", "fake.toggle");
            var plugins = new PluginHostService([lights, plugSwitch], store, state, controller, telemetry, log);
            await plugins.InitializeAndStartAsync();
            var heos = new HeosEventListenerService(store, state, controller, log);
            var intelligence = new RoomIntelligenceService(state, controller, store, plugins, ytQueue, heos, log, Path.Combine(dir, "room-habits.json"));
            var audit = new McpAuditLog(Path.Combine(dir, "audit"));
            var server = new DenonMcpServer(store, controller, state, plugins, intelligence, new RadioBrowserService(log), ytMedia, relay, ytQueue, new LyricsService(), telemetry, new VolumeMemoryStore(log, Path.Combine(dir, "volume-memory.json")), log, audit);
            await server.ApplySettingsAsync();
            Assert.True(server.IsRunning, server.LastError);

            var rig = new McpRig { Store = store, Server = server, Audit = audit, Token = token, Lights = lights, Switch = plugSwitch };
            rig._cleanup.Add(() => server.DisposeAsync());
            rig._cleanup.Add(() => plugins.DisposeAsync());
            rig._cleanup.Add(() => telemetry.DisposeAsync());
            rig._cleanup.Add(() => { ytQueue.Dispose(); relay.Dispose(); bus.Dispose(); return ValueTask.CompletedTask; });
            return rig;
        }

        public Task<McpClient> ConnectAsync() =>
            McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(Server.ListeningEndpoint),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
            }));

        public List<AuditLine> AuditLines() =>
            Audit.GetFiles().SelectMany(File.ReadAllLines).Select(static line =>
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                return new AuditLine(
                    r.GetProperty("tool").GetString()!,
                    r.GetProperty("outcome").GetString()!,
                    r.GetProperty("ip").GetString(),
                    r.GetProperty("client").GetString(),
                    r.GetProperty("argKeys").EnumerateArray().Select(static k => k.GetString()!).ToArray());
            }).ToList();

        public async ValueTask DisposeAsync()
        {
            foreach (var step in _cleanup)
            {
                try { await step(); } catch { }
            }
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class FakeActionPlugin(string id, string name, string actionId) : IDenonPlugin
    {
        public PluginManifest Manifest { get; } = new(id, name, "1.0", "test", "", "Test", "#000000", "*", PluginCapabilities.Actions);

        public int ActionsRun;

        public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<PluginActionDescriptor> GetActions() =>
            [new(actionId, "Do", "Does it.", "Test") { Parameters = [new PluginActionParameter("room", "string", "A room")] }];

        public Task<PluginActionResult> ExecuteActionAsync(string actionId, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ActionsRun);
            return Task.FromResult(PluginActionResult.Ok("done"));
        }

        public Task OnReceiverEventAsync(PluginReceiverEvent receiverEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
