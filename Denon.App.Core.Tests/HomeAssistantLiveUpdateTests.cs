using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Denon.App.PluginSdk;
using Denon.Plugin.HomeAssistant;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// Home Assistant: the Input Booleans filter pill and live state over the WebSocket API (#79). There is no real Home
/// Assistant server here: the socket tests use a small loopback server that speaks the documented protocol.
/// </summary>
public sealed class HomeAssistantLiveUpdateTests
{
    private const string TestToken = "test-token-for-loopback";

    private static readonly HaEntityItem[] MixedEntities =
    [
        new("scene.movie_time", "Movie Time", "scene", "scening"),
        new("script.close_curtains", "Close Curtains", "script", "off"),
        new("switch.sub_plug", "Sub Plug", "switch", "on"),
        new("light.desk", "Desk Light", "light", "off"),
        new("input_boolean.guest_mode", "Guest Mode", "input_boolean", "off"),
        new("input_boolean.vacation_mode", "Vacation Mode", "input_boolean", "on"),
    ];

    // ---------------------------------------------------------------- Input Booleans filter

    [Theory]
    [InlineData("input_boolean", new[] { "input_boolean.guest_mode", "input_boolean.vacation_mode" })]
    [InlineData("scene", new[] { "scene.movie_time" })]
    [InlineData("script", new[] { "script.close_curtains" })]
    [InlineData("switch", new[] { "switch.sub_plug" })]
    [InlineData("light", new[] { "light.desk" })]
    public void Each_filter_pill_shows_only_its_domain(string filter, string[] expected)
    {
        var vm = new HomeAssistantViewModel(new HomeAssistantPlugin());
        vm.ApplyEntities(MixedEntities);

        vm.SelectedDomainFilter = filter;

        Assert.Equal(expected, vm.Entities.Select(e => e.EntityId).OrderBy(id => id));
    }

    [Fact]
    public void The_all_pill_shows_every_entity_and_search_still_works_with_a_pill()
    {
        var vm = new HomeAssistantViewModel(new HomeAssistantPlugin());
        vm.ApplyEntities(MixedEntities);

        vm.SetDomainFilterCommand.Execute("input_boolean");
        vm.SetDomainFilterCommand.Execute("all");
        Assert.Equal(MixedEntities.Length, vm.Entities.Count);

        vm.SelectedDomainFilter = "input_boolean";
        vm.SearchQuery = "vacation";
        Assert.Equal("input_boolean.vacation_mode", Assert.Single(vm.Entities).EntityId);
    }

    [Fact]
    public async Task Demo_mode_has_input_booleans_that_toggle_and_never_opens_a_socket()
    {
        var context = new FakeContext(new HomeAssistantConfig { DemoMode = true });
        var plugin = new HomeAssistantPlugin();
        await plugin.InitializeAsync(context);
        await plugin.StartAsync();
        var vm = plugin.ViewModel!;
        await WaitUntil(() => vm.Entities.Count > 0);

        vm.SelectedDomainFilter = "input_boolean";
        Assert.Equal(["Guest Mode", "Vacation Mode"], vm.Entities.Select(e => e.FriendlyName));
        Assert.False(vm.GetEntity("input_boolean.guest_mode")!.IsOn);

        await plugin.TriggerEntityAsync("input_boolean.guest_mode");
        Assert.True(vm.GetEntity("input_boolean.guest_mode")!.IsOn);

        Assert.Equal(HaLiveState.Off, plugin.LiveState);
        Assert.Contains("Demo Mode", vm.LiveStatusText);
        await plugin.StopAsync();
        await plugin.DisposeAsync();
    }

    // ---------------------------------------------------------------- WebSocket URL

    [Theory]
    [InlineData("http://homeassistant.local:8123", "ws://homeassistant.local:8123/api/websocket")]
    [InlineData("https://ha.example.org", "wss://ha.example.org/api/websocket")]
    [InlineData("https://example.org:8443/ha/", "wss://example.org:8443/ha/api/websocket")]
    [InlineData("http://example.org/proxy/ha", "ws://example.org/proxy/ha/api/websocket")]
    [InlineData("  http://10.0.0.5:8123/?x=1#top ", "ws://10.0.0.5:8123/api/websocket")]
    [InlineData("http://example.org:80", "ws://example.org/api/websocket")]
    [InlineData("https://example.org:443/", "wss://example.org/api/websocket")]
    [InlineData("http://example.org:443", "ws://example.org:443/api/websocket")]
    [InlineData("HTTP://[fd00::5]:8123", "ws://[fd00::5]:8123/api/websocket")]
    public void The_websocket_url_comes_from_the_rest_url(string baseUrl, string expected) =>
        Assert.Equal(expected, HomeAssistantWebSocketProtocol.TryBuildWebSocketUri(baseUrl)!.AbsoluteUri);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("homeassistant.local:8123")]
    [InlineData("ftp://example.org")]
    [InlineData("not a url")]
    [InlineData("/api/states")]
    public void A_url_that_is_not_http_or_https_gives_no_websocket_url(string? baseUrl) =>
        Assert.Null(HomeAssistantWebSocketProtocol.TryBuildWebSocketUri(baseUrl));

    // ---------------------------------------------------------------- Messages the client sends

    [Fact]
    public void The_auth_message_has_only_the_type_and_the_trimmed_token()
    {
        using var doc = JsonDocument.Parse(HomeAssistantWebSocketProtocol.BuildAuth("  abc.def-123  "));
        var props = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(new Dictionary<string, string?> { ["type"] = "auth", ["access_token"] = "abc.def-123" }, props);
    }

    [Fact]
    public void The_subscribe_and_ping_messages_carry_their_id()
    {
        using var sub = JsonDocument.Parse(HomeAssistantWebSocketProtocol.BuildSubscribeStateChanged(7));
        Assert.Equal(7, sub.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("subscribe_events", sub.RootElement.GetProperty("type").GetString());
        Assert.Equal("state_changed", sub.RootElement.GetProperty("event_type").GetString());

        using var ping = JsonDocument.Parse(HomeAssistantWebSocketProtocol.BuildPing(12));
        Assert.Equal(12, ping.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("ping", ping.RootElement.GetProperty("type").GetString());
    }

    // ---------------------------------------------------------------- Messages the server sends

    [Fact]
    public void Auth_messages_are_read()
    {
        Assert.IsType<HaWsMessage.AuthRequired>(Parse("""{"type":"auth_required","ha_version":"2026.11.0"}"""));
        Assert.IsType<HaWsMessage.AuthOk>(Parse("""{"type":"auth_ok","ha_version":"2026.11.0"}"""));
        var invalid = Assert.IsType<HaWsMessage.AuthInvalid>(Parse("""{"type":"auth_invalid","message":"Invalid access token or password"}"""));
        Assert.Equal("Invalid access token or password", invalid.Message);
        Assert.Equal(string.Empty, Assert.IsType<HaWsMessage.AuthInvalid>(Parse("""{"type":"auth_invalid"}""")).Message);
    }

    [Fact]
    public void Result_messages_are_read_with_their_error()
    {
        Assert.Equal(new HaWsMessage.Result(1, true, null), Parse("""{"id":1,"type":"result","success":true,"result":null}"""));
        Assert.Equal(
            new HaWsMessage.Result(2, false, "Unknown command."),
            Parse("""{"id":2,"type":"result","success":false,"error":{"code":"unknown_command","message":"Unknown command."}}"""));
        Assert.Equal(
            new HaWsMessage.Result(3, false, "invalid_format"),
            Parse("""{"id":3,"type":"result","success":false,"error":{"code":"invalid_format"}}"""));
    }

    [Fact]
    public void A_state_changed_event_gives_the_new_state()
    {
        var msg = Parse("""
            {"id":1,"type":"event","event":{"event_type":"state_changed","data":{
              "entity_id":"input_boolean.guest_mode",
              "old_state":{"entity_id":"input_boolean.guest_mode","state":"off","attributes":{"friendly_name":"Guest Mode"}},
              "new_state":{"entity_id":"input_boolean.guest_mode","state":"on","attributes":{"friendly_name":"Guest Mode","icon":"mdi:account"}}},
              "origin":"LOCAL","time_fired":"2026-11-28T10:00:00+00:00"}}
            """);

        var change = Assert.IsType<HaWsMessage.StateChanged>(msg);
        Assert.Equal(1, change.Id);
        Assert.Equal("input_boolean.guest_mode", change.EntityId);
        Assert.Equal(new HaEntityItem("input_boolean.guest_mode", "Guest Mode", "input_boolean", "on"), change.NewState);
    }

    [Fact]
    public void A_state_changed_event_with_null_new_state_means_removed()
    {
        var change = Assert.IsType<HaWsMessage.StateChanged>(Parse("""
            {"id":4,"type":"event","event":{"event_type":"state_changed","data":{"entity_id":"light.old_lamp","new_state":null,
              "old_state":{"entity_id":"light.old_lamp","state":"off","attributes":{}}}}}
            """));
        Assert.Equal("light.old_lamp", change.EntityId);
        Assert.Null(change.NewState);
    }

    [Fact]
    public void A_state_without_a_friendly_name_uses_the_entity_id()
    {
        var change = Assert.IsType<HaWsMessage.StateChanged>(Parse("""
            {"id":1,"type":"event","event":{"event_type":"state_changed","data":{"entity_id":"switch.x","new_state":{"entity_id":"switch.x","state":"on"}}}}
            """));
        Assert.Equal("switch.x", change.NewState!.FriendlyName);
    }

    [Theory]
    [InlineData("""{"id":5,"type":"pong"}""", 5)]
    [InlineData("""{"type":"pong","id":99}""", 99)]
    public void Pong_messages_are_read(string json, int id) =>
        Assert.Equal(new HaWsMessage.Pong(id), Parse(json));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"type\":")]
    [InlineData("[{\"type\":\"auth_ok\"}]")]
    [InlineData("42")]
    [InlineData("""{"type":"event","event":{"event_type":"call_service","data":{}}}""")]
    [InlineData("""{"type":"event","event":{"event_type":"state_changed","data":{"new_state":null}}}""")]
    [InlineData("""{"type":"event","event":{"event_type":"state_changed","data":{"entity_id":"a.b","new_state":{"entity_id":"c.d","state":"on"}}}}""")]
    [InlineData("""{"type":"event","event":"x"}""")]
    [InlineData("""{"type":"something_new"}""")]
    [InlineData("""{"id":"one","type":"pong"}""")]
    public void Garbage_and_other_messages_are_unknown_and_never_throw(string json)
    {
        var msg = Parse(json);
        if (msg is HaWsMessage.Pong pong)
        {
            Assert.Equal(0, pong.Id); // An id that is not a number never matches a sent ping.
        }
        else
        {
            Assert.IsType<HaWsMessage.Unknown>(msg);
        }
    }

    // ---------------------------------------------------------------- Applying a state change

    [Fact]
    public void A_state_change_updates_the_tile_in_place()
    {
        var vm = new HomeAssistantViewModel(new HomeAssistantPlugin());
        vm.ApplyEntities(MixedEntities);
        vm.SelectedDomainFilter = "input_boolean";
        var before = vm.Entities.ToList();

        vm.ApplyStateChange("input_boolean.guest_mode", new("input_boolean.guest_mode", "Guest Mode", "input_boolean", "on"));

        Assert.True(vm.GetEntity("input_boolean.guest_mode")!.IsOn);
        Assert.True(vm.Entities[0].IsOn);
        Assert.Same(before[1], vm.Entities[1]); // The other tile is not rebuilt.
        Assert.Equal("6 synced", vm.EntitiesCountLabel);
    }

    [Fact]
    public void A_state_change_adds_a_new_listed_entity_and_ignores_other_domains()
    {
        var vm = new HomeAssistantViewModel(new HomeAssistantPlugin());
        vm.ApplyEntities(MixedEntities);

        vm.ApplyStateChange("input_boolean.away", new("input_boolean.away", "Away", "input_boolean", "off"));
        vm.ApplyStateChange("cover.garage_door", new("cover.garage_door", "Garage", "cover", "open"));
        vm.ApplyStateChange("sensor.temp", new("sensor.temp", "Temperature", "sensor", "21"));

        Assert.NotNull(vm.GetEntity("input_boolean.away"));
        Assert.Null(vm.GetEntity("cover.garage_door"));
        Assert.Null(vm.GetEntity("sensor.temp"));
        Assert.Equal(MixedEntities.Length + 1, vm.Entities.Count);
        Assert.Equal("scene.movie_time", vm.Entities[0].EntityId); // Scenes stay first.
    }

    [Fact]
    public void A_removed_entity_leaves_the_dashboard_and_a_renamed_one_is_shown_with_its_new_name()
    {
        var vm = new HomeAssistantViewModel(new HomeAssistantPlugin());
        vm.ApplyEntities(MixedEntities);

        vm.ApplyStateChange("light.desk", null);
        vm.ApplyStateChange("light.not_known", null);
        vm.ApplyStateChange("switch.sub_plug", new("switch.sub_plug", "Subwoofer", "switch", "on"));

        Assert.Null(vm.GetEntity("light.desk"));
        Assert.Equal(MixedEntities.Length - 1, vm.Entities.Count);
        Assert.Contains(vm.Entities, e => e.FriendlyName == "Subwoofer");
    }

    // ---------------------------------------------------------------- REST reads and live changes together

    [Fact]
    public void A_slow_rest_read_does_not_turn_back_a_newer_live_change()
    {
        var vm = new HomeAssistantViewModel(new HomeAssistantPlugin());
        vm.ApplyEntities(MixedEntities);

        var ticket = vm.BeginSnapshot(); // The REST read starts: guest mode is off.
        vm.ApplyStateChange("input_boolean.guest_mode", new("input_boolean.guest_mode", "Guest Mode", "input_boolean", "on"));
        vm.ApplyStateChange("light.desk", null);
        vm.ApplySnapshot(MixedEntities, ticket); // The read ends with the old values.

        Assert.True(vm.GetEntity("input_boolean.guest_mode")!.IsOn);
        Assert.Null(vm.GetEntity("light.desk"));
        Assert.True(vm.GetEntity("switch.sub_plug")!.IsOn); // Other entities come from the read.
    }

    [Fact]
    public void A_rest_read_that_started_after_a_live_change_is_newer_and_wins()
    {
        var vm = new HomeAssistantViewModel(new HomeAssistantPlugin());
        vm.ApplyEntities(MixedEntities);

        vm.ApplyStateChange("input_boolean.guest_mode", new("input_boolean.guest_mode", "Guest Mode", "input_boolean", "on"));
        var ticket = vm.BeginSnapshot();
        vm.ApplySnapshot(MixedEntities, ticket); // Guest mode was turned off again before this read.

        Assert.False(vm.GetEntity("input_boolean.guest_mode")!.IsOn);
    }

    [Fact]
    public void A_rest_read_older_than_one_already_applied_is_dropped()
    {
        var vm = new HomeAssistantViewModel(new HomeAssistantPlugin());
        var older = vm.BeginSnapshot();
        var newer = vm.BeginSnapshot();

        vm.ApplySnapshot([new("switch.sub_plug", "Sub Plug", "switch", "on")], newer);
        vm.ApplySnapshot([new("switch.sub_plug", "Sub Plug", "switch", "off"), new("scene.old", "Old", "scene", "scening")], older);

        Assert.True(Assert.Single(vm.Entities).IsOn);
    }

    [Fact]
    public async Task In_the_plugin_a_slow_refresh_does_not_undo_a_live_event()
    {
        const string offJson = """[{"entity_id":"input_boolean.guest_mode","state":"off","attributes":{"friendly_name":"Guest Mode"}}]""";
        await using var server = new FakeHaServer { HoldOpen = TimeSpan.FromSeconds(30), StatesJson = offJson };
        var socketReady = new TaskCompletionSource<(WebSocket Ws, int SubId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AfterSubscribe = (ws, subId) => { socketReady.TrySetResult((ws, subId)); return Task.CompletedTask; };
        server.Start();

        var context = new FakeContext(new HomeAssistantConfig { BaseUrl = server.BaseUrl, AccessToken = TestToken });
        var plugin = new HomeAssistantPlugin { LiveOptions = FastOptions() };
        await plugin.InitializeAsync(context);
        await plugin.StartAsync();
        var vm = plugin.ViewModel!;
        var (ws, subId) = await socketReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => plugin.IsLive && vm.GetEntity("input_boolean.guest_mode") is not null);
        await WaitUntil(() => server.StatesRequests >= 2); // The start read and the read when the socket went live.
        await Task.Delay(100);

        // A Refresh starts and Home Assistant is slow to answer it.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeStatesReply = () => release.Task;
        var requestsBefore = server.StatesRequests;
        var refresh = plugin.FetchEntitiesAsync();
        await WaitUntil(() => server.StatesRequests > requestsBefore);

        // Meanwhile the live event turns guest mode on.
        await ws.SendAsync(Encoding.UTF8.GetBytes(EventJson(subId, "input_boolean.guest_mode", "Guest Mode", "on")).AsMemory(),
            WebSocketMessageType.Text, true, CancellationToken.None);
        await WaitUntil(() => vm.GetEntity("input_boolean.guest_mode")!.IsOn);

        // The slow read ends with the old "off".
        release.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.GetEntity("input_boolean.guest_mode")!.IsOn);
        await plugin.StopAsync();
        await plugin.DisposeAsync();
    }

    // ---------------------------------------------------------------- The entity cap

    [Fact]
    public void At_the_cap_a_live_scene_pushes_out_the_lowest_ranked_entity()
    {
        var vm = new HomeAssistantViewModel(new HomeAssistantPlugin());
        var full = Enumerable.Range(0, HomeAssistantPlugin.MaxEntities)
            .Select(i => new HaEntityItem($"input_boolean.b{i:D3}", $"B {i:D3}", "input_boolean", "off"))
            .ToList();
        vm.ApplyEntities(full);

        vm.ApplyStateChange("scene.new_scene", new("scene.new_scene", "New Scene", "scene", "scening"));

        Assert.Equal(HomeAssistantPlugin.MaxEntities, vm.Entities.Count);
        Assert.Equal("scene.new_scene", vm.Entities[0].EntityId);
        Assert.Null(vm.GetEntity($"input_boolean.b{HomeAssistantPlugin.MaxEntities - 1:D3}")); // Last by name goes.
        Assert.NotNull(vm.GetEntity("input_boolean.b000"));
    }

    [Fact]
    public void At_the_cap_a_live_entity_that_ranks_last_is_not_added_and_one_that_ranks_higher_is()
    {
        var vm = new HomeAssistantViewModel(new HomeAssistantPlugin());
        var full = Enumerable.Range(0, HomeAssistantPlugin.MaxEntities)
            .Select(i => new HaEntityItem($"input_boolean.b{i:D3}", $"B {i:D3}", "input_boolean", "off"))
            .ToList();
        vm.ApplyEntities(full);

        vm.ApplyStateChange("input_boolean.zzz", new("input_boolean.zzz", "Zzz", "input_boolean", "on"));
        Assert.Null(vm.GetEntity("input_boolean.zzz"));
        Assert.Equal(HomeAssistantPlugin.MaxEntities, vm.Entities.Count);

        vm.ApplyStateChange("input_boolean.abc", new("input_boolean.abc", "Abc", "input_boolean", "on")); // Not "Aaa": in Danish "aa" sorts as "å", after "z".
        Assert.Equal("input_boolean.abc", vm.Entities[0].EntityId);
        Assert.Equal(HomeAssistantPlugin.MaxEntities, vm.Entities.Count);
        Assert.Null(vm.GetEntity($"input_boolean.b{HomeAssistantPlugin.MaxEntities - 1:D3}"));
    }

    // ---------------------------------------------------------------- Loopback WebSocket

    [Fact]
    public async Task The_socket_authenticates_subscribes_reads_a_multi_frame_event_and_answers_ping()
    {
        // The server holds the socket long enough for five pings on a slow CI runner, then closes it.
        await using var server = new FakeHaServer { HoldOpen = TimeSpan.FromSeconds(5) };
        server.AfterSubscribe = async (ws, subId) =>
        {
            var json = EventJson(subId, "input_boolean.guest_mode", "Guest Mode", "on");
            var bytes = Encoding.UTF8.GetBytes(json);
            // Three frames for one message.
            await ws.SendAsync(bytes.AsMemory(0, 10), WebSocketMessageType.Text, false, CancellationToken.None);
            await ws.SendAsync(bytes.AsMemory(10, 30), WebSocketMessageType.Text, false, CancellationToken.None);
            await ws.SendAsync(bytes.AsMemory(40), WebSocketMessageType.Text, true, CancellationToken.None);
        };
        server.Start();

        // Like the plugin: an HttpClient with a short timeout and pool lifetime. Neither may end a live socket.
        using var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMilliseconds(300) })
        {
            Timeout = TimeSpan.FromSeconds(1),
        };
        var changes = new ConcurrentQueue<(string Id, HaEntityItem? State)>();
        var states = new ConcurrentQueue<(HaLiveState State, string? Reason)>();
        var subscribed = 0;
        var socket = new HomeAssistantLiveSocket(
            server.WebSocketUri,
            TestToken,
            http,
            _ => { Interlocked.Increment(ref subscribed); return Task.CompletedTask; },
            (id, s) => changes.Enqueue((id, s)),
            (s, r) => states.Enqueue((s, r)),
            FastOptions(pingInterval: TimeSpan.FromMilliseconds(250)));
        socket.Start();

        await WaitUntil(() => !changes.IsEmpty);
        var (entityId, state) = Assert.Single(changes);
        Assert.Equal("input_boolean.guest_mode", entityId);
        Assert.Equal("on", state!.State);
        Assert.Equal(1, Volatile.Read(ref subscribed));

        // Pings go out and get a pong; the session lives longer than the HttpClient timeout.
        await WaitUntil(() => server.PingCount >= 5, 4000);
        Assert.Equal(1, server.SocketCount);
        Assert.Contains(states, s => s.State == HaLiveState.Live);
        Assert.DoesNotContain(states, s => s.State == HaLiveState.Disconnected);

        // The first message after auth_required is the auth message with the token.
        using (var auth = JsonDocument.Parse(server.Received.First()))
        {
            Assert.Equal("auth", auth.RootElement.GetProperty("type").GetString());
            Assert.Equal(TestToken, auth.RootElement.GetProperty("access_token").GetString());
        }

        // The server closes: the client connects again.
        await WaitUntil(() => server.SocketCount >= 2, 10000);
        Assert.Contains(states, s => s.State == HaLiveState.Disconnected);

        await socket.DisposeAsync();
        Assert.True(socket.Completion.IsCompleted);
        Assert.Equal(HaLiveState.Off, socket.State);
    }

    [Fact]
    public async Task A_rejected_token_stops_the_socket_without_a_retry_loop()
    {
        await using var server = new FakeHaServer { AcceptToken = false };
        server.Start();
        var states = new ConcurrentQueue<HaLiveState>();
        var socket = new HomeAssistantLiveSocket(
            server.WebSocketUri, TestToken, null, _ => Task.CompletedTask, (_, _) => { }, (s, _) => states.Enqueue(s), FastOptions());
        socket.Start();

        await socket.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300); // MinBackoff is 50 ms: a retry would show up here.

        Assert.Equal(HaLiveState.AuthRejected, socket.State);
        Assert.Equal(1, server.SocketCount);
        Assert.DoesNotContain(HaLiveState.Live, states);
        await socket.DisposeAsync();
    }

    [Fact]
    public async Task No_pong_ends_the_session_and_the_socket_connects_again()
    {
        await using var server = new FakeHaServer { AnswerPings = false, HoldOpen = TimeSpan.FromSeconds(10) };
        server.Start();
        var reasons = new ConcurrentQueue<string?>();
        var socket = new HomeAssistantLiveSocket(
            server.WebSocketUri, TestToken, null, _ => Task.CompletedTask, (_, _) => { },
            (s, r) => { if (s == HaLiveState.Disconnected) reasons.Enqueue(r); },
            FastOptions(pingInterval: TimeSpan.FromMilliseconds(100), pongTimeout: TimeSpan.FromMilliseconds(200)));
        socket.Start();

        await WaitUntil(() => server.SocketCount >= 2, 5000);
        Assert.Contains("no answer to ping", reasons);
        await socket.DisposeAsync();
    }

    [Fact]
    public async Task A_message_over_the_size_cap_ends_the_session()
    {
        await using var server = new FakeHaServer { HoldOpen = TimeSpan.FromSeconds(10) };
        server.AfterSubscribe = (ws, _) =>
            ws.SendAsync(Encoding.UTF8.GetBytes(new string(' ', 4096)).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None).AsTask();
        server.Start();
        var reasons = new ConcurrentQueue<string?>();
        var socket = new HomeAssistantLiveSocket(
            server.WebSocketUri, TestToken, null, _ => Task.CompletedTask, (_, _) => { },
            (s, r) => { if (s == HaLiveState.Disconnected) reasons.Enqueue(r); },
            new HomeAssistantLiveSocketOptions { MinBackoff = TimeSpan.FromSeconds(5), MaxMessageBytes = 1024, HandshakeTimeout = TimeSpan.FromSeconds(3) });
        socket.Start();

        await WaitUntil(() => !reasons.IsEmpty);
        Assert.Equal("message too large", Assert.Single(reasons));
        await socket.DisposeAsync();
    }

    [Fact]
    public async Task The_plugin_applies_live_events_to_tiles_calls_input_boolean_toggle_and_keeps_the_token_out_of_the_log()
    {
        await using var server = new FakeHaServer
        {
            HoldOpen = TimeSpan.FromSeconds(30),
            StatesJson = """[{"entity_id":"input_boolean.guest_mode","state":"off","attributes":{"friendly_name":"Guest Mode"}},{"entity_id":"sensor.temp","state":"20"}]""",
        };
        var sendEvent = new TaskCompletionSource<(WebSocket Ws, int SubId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AfterSubscribe = (ws, subId) => { sendEvent.TrySetResult((ws, subId)); return Task.CompletedTask; };
        server.Start();

        var context = new FakeContext(new HomeAssistantConfig { BaseUrl = server.BaseUrl, AccessToken = TestToken });
        var plugin = new HomeAssistantPlugin { LiveOptions = FastOptions() };
        await plugin.InitializeAsync(context);
        await plugin.StartAsync();
        var vm = plugin.ViewModel!;

        await WaitUntil(() => plugin.IsLive && vm.GetEntity("input_boolean.guest_mode") is not null);
        Assert.Equal("Live updates: on", vm.LiveStatusText);
        Assert.Null(vm.GetEntity("sensor.temp"));

        var (ws, subId) = await sendEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var json = EventJson(subId, "input_boolean.guest_mode", "Guest Mode", "on");
        await ws.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
        await WaitUntil(() => vm.GetEntity("input_boolean.guest_mode")!.IsOn);

        // A tile click on an input_boolean calls input_boolean.toggle.
        await plugin.TriggerEntityAsync("input_boolean.guest_mode");
        Assert.Contains(server.ServiceCalls, c => c.Path == "/api/services/input_boolean/toggle" && c.Body.Contains("input_boolean.guest_mode"));

        await plugin.StopAsync();
        Assert.Equal(HaLiveState.Off, plugin.LiveState);
        Assert.Equal("Live updates: off", vm.LiveStatusText);
        Assert.Contains(context.Log, l => l.Contains("live updates: connected"));
        Assert.DoesNotContain(context.Log, l => l.Contains(TestToken));
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task The_plugin_shows_a_rejected_token_and_does_not_try_it_again()
    {
        await using var server = new FakeHaServer { AcceptToken = false };
        server.Start();
        var context = new FakeContext(new HomeAssistantConfig { BaseUrl = server.BaseUrl, AccessToken = TestToken });
        var plugin = new HomeAssistantPlugin { LiveOptions = FastOptions() };
        await plugin.InitializeAsync(context);
        await plugin.StartAsync();
        var vm = plugin.ViewModel!;

        await WaitUntil(() => plugin.LiveState == HaLiveState.AuthRejected);
        await WaitUntil(() => vm.LiveStatusText.Contains("rejected the token"));
        Assert.Contains(context.Statuses, s => s == "Home Assistant rejected the token");

        // Saving again with the same token does not open a new socket.
        await plugin.SaveConfigAsync(new HomeAssistantConfig { BaseUrl = server.BaseUrl, AccessToken = TestToken, PushReceiverEventsToHa = false });
        await Task.Delay(300);
        Assert.Equal(1, server.SocketCount);
        Assert.Contains("rejected the token", vm.LiveStatusText);

        // A new token is tried.
        await plugin.SaveConfigAsync(new HomeAssistantConfig { BaseUrl = server.BaseUrl, AccessToken = TestToken + "-new" });
        await WaitUntil(() => server.SocketCount == 2);

        // Live updates off: no socket.
        await plugin.SaveConfigAsync(new HomeAssistantConfig { BaseUrl = server.BaseUrl, AccessToken = TestToken + "-other", LiveUpdates = false });
        await Task.Delay(300);
        Assert.Equal(2, server.SocketCount);
        Assert.Equal("Live updates: off", vm.LiveStatusText);

        Assert.DoesNotContain(context.Log, l => l.Contains(TestToken));
        await plugin.StopAsync();
        await plugin.DisposeAsync();
    }

    // ---------------------------------------------------------------- Helpers

    private static HaWsMessage Parse(string json) => HomeAssistantWebSocketProtocol.Parse(json);

    private static string EventJson(int subId, string entityId, string friendlyName, string state) =>
        JsonSerializer.Serialize(new
        {
            id = subId,
            type = "event",
            @event = new
            {
                event_type = "state_changed",
                data = new
                {
                    entity_id = entityId,
                    new_state = new { entity_id = entityId, state, attributes = new { friendly_name = friendlyName } },
                },
            },
        });

    private static HomeAssistantLiveSocketOptions FastOptions(TimeSpan? pingInterval = null, TimeSpan? pongTimeout = null) => new()
    {
        PingInterval = pingInterval ?? TimeSpan.FromSeconds(30),
        PongTimeout = pongTimeout ?? TimeSpan.FromSeconds(2),
        HandshakeTimeout = TimeSpan.FromSeconds(3),
        MinBackoff = TimeSpan.FromMilliseconds(50),
        MaxBackoff = TimeSpan.FromMilliseconds(200),
    };

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition did not become true in time.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>A tiny Home Assistant stand-in on localhost: REST /api/states and services, and /api/websocket.</summary>
    private sealed class FakeHaServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private int _socketCount;
        private int _pingCount;
        private int _statesRequests;

        public FakeHaServer()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
        }

        public int Port { get; }

        public string BaseUrl => $"http://localhost:{Port}";

        public Uri WebSocketUri => HomeAssistantWebSocketProtocol.TryBuildWebSocketUri(BaseUrl)!;

        public bool AcceptToken { get; init; } = true;

        public bool AnswerPings { get; init; } = true;

        public TimeSpan HoldOpen { get; init; } = TimeSpan.FromMilliseconds(500);

        public string StatesJson { get; set; } = "[]";

        /// <summary>Runs before /api/states answers, so a test can make the read slow.</summary>
        public Func<Task>? BeforeStatesReply { get; set; }

        public int StatesRequests => Volatile.Read(ref _statesRequests);

        public Func<WebSocket, int, Task>? AfterSubscribe { get; set; }

        public int SocketCount => Volatile.Read(ref _socketCount);

        public int PingCount => Volatile.Read(ref _pingCount);

        public ConcurrentQueue<string> Received { get; } = new();

        public ConcurrentQueue<(string Path, string Body)> ServiceCalls { get; } = new();

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                _listener.Close();
            }
            catch
            {
            }

            return ValueTask.CompletedTask;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url!.AbsolutePath;
                if (path == "/api/websocket" && ctx.Request.IsWebSocketRequest)
                {
                    Interlocked.Increment(ref _socketCount);
                    var wsContext = await ctx.AcceptWebSocketAsync(null);
                    await RunSocketAsync(wsContext.WebSocket);
                    return;
                }

                var authOk = ctx.Request.Headers["Authorization"]?.StartsWith("Bearer ", StringComparison.Ordinal) == true;
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                string reply;
                if (!authOk)
                {
                    ctx.Response.StatusCode = 401;
                    reply = "{}";
                }
                else if (path == "/api/states" && ctx.Request.HttpMethod == "GET")
                {
                    var json = StatesJson;
                    Interlocked.Increment(ref _statesRequests);
                    if (BeforeStatesReply is { } wait)
                    {
                        await wait();
                    }

                    reply = json;
                }
                else if (path.StartsWith("/api/services/", StringComparison.Ordinal))
                {
                    ServiceCalls.Enqueue((path, body));
                    reply = "[]";
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    reply = "{}";
                }

                var bytes = Encoding.UTF8.GetBytes(reply);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch
            {
                // The test ended or the client went away.
            }
        }

        private async Task RunSocketAsync(WebSocket ws)
        {
            await SendAsync(ws, """{"type":"auth_required","ha_version":"2026.11.0"}""");
            var auth = await ReceiveAsync(ws);
            if (auth is null)
            {
                return;
            }

            Received.Enqueue(auth);
            if (!AcceptToken)
            {
                await SendAsync(ws, """{"type":"auth_invalid","message":"Invalid access token or password"}""");
                await CloseAsync(ws);
                return;
            }

            await SendAsync(ws, """{"type":"auth_ok","ha_version":"2026.11.0"}""");
            var subscribe = await ReceiveAsync(ws);
            if (subscribe is null)
            {
                return;
            }

            Received.Enqueue(subscribe);
            using var subDoc = JsonDocument.Parse(subscribe);
            var subId = subDoc.RootElement.GetProperty("id").GetInt32();
            await SendAsync(ws, $$"""{"id":{{subId}},"type":"result","success":true,"result":null}""");
            if (AfterSubscribe is not null)
            {
                await AfterSubscribe(ws, subId);
            }

            var until = DateTime.UtcNow + HoldOpen;
            while (!_stop.IsCancellationRequested)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    break;
                }

                var receive = ReceiveAsync(ws);
                if (await Task.WhenAny(receive, Task.Delay(left)) != receive)
                {
                    break;
                }

                var text = await receive;
                if (text is null)
                {
                    return;
                }

                Received.Enqueue(text);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.GetProperty("type").GetString() == "ping")
                {
                    Interlocked.Increment(ref _pingCount);
                    if (AnswerPings)
                    {
                        await SendAsync(ws, $$"""{"id":{{doc.RootElement.GetProperty("id").GetInt32()}},"type":"pong"}""");
                    }
                }
            }

            await CloseAsync(ws);
        }

        private static Task SendAsync(WebSocket ws, string text) =>
            ws.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None).AsTask();

        private static async Task<string?> ReceiveAsync(WebSocket ws)
        {
            try
            {
                return await HomeAssistantLiveSocket.ReceiveTextAsync(ws, 1024 * 1024, CancellationToken.None);
            }
            catch
            {
                return null;
            }
        }

        private static async Task CloseAsync(WebSocket ws)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            }
            catch
            {
            }
        }
    }

    private sealed class FakeContext(HomeAssistantConfig config) : IPluginContext, IPluginLogger, IPluginStorage, IPluginEventLogger, IPluginReceiverBridge
    {
        private object _config = config;

        public ConcurrentQueue<string> Log { get; } = new();

        public ConcurrentQueue<string> Statuses { get; } = new();

        public string PluginId => "home-assistant";

        public IPluginLogger Logger => this;

        public IPluginEventLogger Events => this;

        public IPluginStorage Storage => this;

        public IPluginReceiverBridge Receiver => this;

        public IPluginReceiverControl? ReceiverControl => null;

        public bool IsAppShuttingDown => false;

        public PluginReceiverSnapshot CurrentSnapshot => PluginReceiverSnapshot.Empty;

        public string PluginDataDirectory => Path.GetTempPath();

        public HttpClient CreateHttpClient(bool allowLocalSelfSignedTls = false, TimeSpan? timeout = null) =>
            new(new SocketsHttpHandler()) { Timeout = timeout ?? TimeSpan.FromSeconds(8) };

        public void ReportStatus(string statusText, PluginHealthState health) => Statuses.Enqueue(statusText);

        public void ReportStatus(string statusText, PluginHealthState health, IReadOnlyDictionary<string, string>? details) => Statuses.Enqueue(statusText);

        public Task<PluginActionResult> RunPluginActionAsync(string pluginId, string actionId, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(PluginActionResult.Fail("not in tests"));

        public void Info(string message) => Log.Enqueue(message);

        public void Warning(string message) => Log.Enqueue(message);

        public void Error(string message, Exception? exception = null) => Log.Enqueue(message);

        public void RecordEvent(string eventName, IReadOnlyDictionary<string, object?>? data = null, string? stream = null, string origin = "plugin")
        {
        }

        public Task<T> LoadConfigAsync<T>(Func<T> defaultFactory, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_config as T ?? defaultFactory());

        public Task SaveConfigAsync<T>(T config, CancellationToken cancellationToken = default) where T : class
        {
            _config = config;
            return Task.CompletedTask;
        }
    }
}
