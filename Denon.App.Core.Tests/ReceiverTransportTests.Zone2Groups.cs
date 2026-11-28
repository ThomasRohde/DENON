using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>#75 against the fake receivers: Zone 2 read, pushed and set on port 23, HEOS groups, and the safe volume.</summary>
public sealed partial class ReceiverTransportTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Not within 5 s: {what}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>The Zone 2 commands the fake got, without the queries.</summary>
    private static List<string> Zone2Commands(FakeAvr avr) =>
        avr.Commands.Where(static c => c.StartsWith("Z2", StringComparison.Ordinal) && !c.EndsWith('?')).ToList();

    [Fact]
    public async Task A_full_read_has_the_zone_2_volume_source_and_mute()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        avr.Replies["Z2?"] = "Z2ON\rZ2NET\rZ233\r";
        avr.Replies["Z2MU?"] = "Z2MUON\r";
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);

        var status = await controller.RefreshAsync();

        Assert.True(status.IsZone2On);
        Assert.Equal("NET", status.Zone2Source);
        Assert.Equal(33, status.Zone2Volume);
        Assert.True(status.IsZone2Muted);
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task Zone_2_mute_is_not_asked_while_zone_2_is_off()
    {
        using var avr = new FakeAvr(); // Z2? answers Z2OFF
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);

        var status = await controller.RefreshAsync();

        Assert.False(status.IsZone2On);
        Assert.Equal(40, status.Zone2Volume);
        Assert.DoesNotContain("Z2MU?", avr.Commands);
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_pushed_zone_2_line_changes_the_state_at_once()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, state, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();
        await avr.WaitForConnectionsAsync(1);

        await avr.PushAsync("Z2ON\rZ228\rZ2MUON\rZ2BT\r"); // the second room's remote

        await WaitUntilAsync(() => state.Snapshot.Status is { IsZone2On: true, Zone2Volume: 28, IsZone2Muted: true, Zone2Source: "BT" }, "the pushed Zone 2 state");
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task Zone_2_commands_are_one_line_each_and_the_echo_shows_at_once()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();
        avr.ClearCommands();

        var status = await controller.SetZone2VolumeAsync(30);
        await controller.SetZone2SourceAsync("tv");
        await controller.SetZone2MuteAsync(true);
        await controller.StepZone2VolumeAsync(up: false);

        Assert.Equal(30, status.Zone2Volume); // the receiver's echo, without a full read
        Assert.Equal(["Z230", "Z2TV", "Z2MUON", "Z2DOWN"], Zone2Commands(avr));
        Assert.DoesNotContain("PW?", avr.Commands);
        await Assert.ThrowsAsync<ArgumentException>(() => controller.SetZone2SourceAsync("50"));
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_step_up_stops_at_the_ceiling()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        avr.Replies["Z2?"] = "Z2ON\rZ2TV\rZ265\r";
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();
        avr.ClearCommands();

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StepZone2VolumeAsync(up: true, ceiling: 65));

        Assert.Empty(Zone2Commands(avr));
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_scene_sets_zone_2_on_source_volume_then_mute()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        avr.ClearCommands();

        await controller.ApplySceneAsync(new ScenePreset
        {
            Name = "Patio",
            PowerOn = false,
            Unmute = false,
            Zone2On = true,
            Zone2Source = "TV",
            Zone2Volume = 25,
            Zone2Muted = false,
        });

        // Muted before it turns on, the volume before the source, and the mute released last.
        Assert.Equal(["Z2MUON", "Z2ON", "Z2MUON", "Z225", "Z2TV", "Z2MUOFF"], Zone2Commands(avr));
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_scene_keeps_zone_2_muted_when_its_volume_step_fails()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        avr.ClearCommands();

        avr.DropOn = static c => c == "Z230"; // the link drops on the volume write (and on its one retry)
        await controller.ApplySceneAsync(new ScenePreset
        {
            Name = "Patio",
            PowerOn = false,
            Unmute = false,
            Zone2On = true,
            Zone2Source = "TV",
            Zone2Volume = 30,
            Zone2Muted = false,
        });

        Assert.Equal(["Z2MUON", "Z2ON", "Z2MUON", "Z230", "Z230"], Zone2Commands(avr)); // no source, no unmute
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_scene_sets_zone_2_before_it_switches_the_main_input()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        avr.ClearCommands();

        await controller.ApplySceneAsync(new ScenePreset
        {
            Name = "Game",
            PowerOn = false,
            Unmute = false,
            InputSource = "GAME",
            Zone2Volume = 20,                 // Zone 2 follows the main zone: it must be down before GAME plays
        });

        var commands = avr.Commands.ToList();
        Assert.True(commands.IndexOf("Z220") < commands.IndexOf("SIGAME"), string.Join(" | ", commands));
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_zone_2_volume_mutes_zone_2_before_the_main_input_switch()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        avr.ClearCommands();

        avr.DropOn = static c => c == "Z220";
        await controller.ApplySceneAsync(new ScenePreset { Name = "Game", PowerOn = false, Unmute = false, InputSource = "GAME", Zone2Volume = 20 });

        var commands = avr.Commands.ToList();
        Assert.True(commands.IndexOf("Z2MUON") < commands.IndexOf("SIGAME"), string.Join(" | ", commands));
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task With_zone_2_neither_set_nor_muted_the_main_input_is_skipped()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        avr.ClearCommands();

        avr.DropOn = static c => c is "Z220" or "Z2MUON";
        await controller.ApplySceneAsync(new ScenePreset { Name = "Game", PowerOn = false, Unmute = false, InputSource = "GAME", Zone2Volume = 20 });

        Assert.DoesNotContain("SIGAME", avr.Commands);
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task With_zone_2_neither_set_nor_muted_a_stream_does_not_switch_the_main_input()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        avr.ClearCommands();

        avr.DropOn = static c => c is "Z220" or "Z2MUON";
        await controller.ApplySceneAsync(new ScenePreset
        {
            Name = "Radio", PowerOn = false, Unmute = false, StreamUrl = "http://127.0.0.1:9/radio.mp3", Zone2Volume = 20,
        });

        Assert.DoesNotContain("SINET", avr.Commands); // the receiver is on TV
        Assert.DoesNotContain(heos.Commands, c => c.StartsWith("browse/play_stream", StringComparison.Ordinal));
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_scene_does_not_turn_zone_2_on_when_the_first_mute_fails()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        avr.ClearCommands();

        avr.DropOn = static c => c == "Z2MUON";
        await controller.ApplySceneAsync(new ScenePreset
        {
            Name = "Patio",
            PowerOn = false,
            Unmute = false,
            Zone2On = true,
            Zone2Volume = 25,
            Zone2Muted = false,
        });

        Assert.Equal(["Z2MUON", "Z2MUON"], Zone2Commands(avr)); // the mute and its one retry; no Z2ON
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_scene_does_not_unmute_zone_2_when_its_volume_step_fails()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        avr.ClearCommands();

        avr.DropOn = static c => c == "Z225";
        await controller.ApplySceneAsync(new ScenePreset
        {
            Name = "Quiet patio",
            PowerOn = false,
            Unmute = false,
            Zone2Volume = 25,
            Zone2Muted = false,               // no power or source step: the zone is on and muted already
        });

        Assert.Equal(["Z225", "Z225"], Zone2Commands(avr)); // no Z2MUOFF
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task Before_any_read_the_guard_asks_the_receiver_for_the_zone_2_mute()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        avr.Replies["Z2MU?"] = "Z2MUON\r"; // the zone is muted; the app has not read anything yet
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);

        await controller.ApplyZone2Async(on: true, source: null, volume: 20, muted: null);

        Assert.Contains("Z2MU?", avr.Commands);
        Assert.DoesNotContain("Z2MUOFF", avr.Commands); // stays muted, as it was
        Assert.Equal("Z2MUON", Zone2Commands(avr)[^1]);
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task With_no_mute_answer_the_guard_keeps_zone_2_muted()
    {
        using var avr = new FakeAvr(); // no Z2MU? reply
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);

        await controller.ApplyZone2Async(on: true, source: null, volume: 20, muted: null);

        Assert.DoesNotContain("Z2MUOFF", avr.Commands);
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task Leaving_a_group_reads_the_members_first()
    {
        using var avr = new FakeAvr();
        // The cache knows nothing; the speaker has Fake leading Kitchen and Patio.
        const string patio = ",{\"name\":\"Patio\",\"pid\":3,\"model\":\"HEOS 1\",\"version\":\"1\",\"ip\":\"127.0.0.3\",\"network\":\"wifi\",\"lineout\":0,\"serial\":\"\"}";
        using var heos = new FakeHeos
        {
            MorePlayersJson = KitchenPlayer + patio,
            GroupsJson = "[{\"name\":\"All\",\"gid\":1,\"players\":[{\"name\":\"Fake\",\"pid\":1,\"role\":\"leader\"},{\"name\":\"Kitchen\",\"pid\":2,\"role\":\"member\"},{\"name\":\"Patio\",\"pid\":3,\"role\":\"member\"}]}]",
        };
        var (controller, state, telemetry) = await CreateControllerAsync(avr, heos);
        state.UpdateHeosGroups(HeosGroupsState.Empty with { RetrievedAt = DateTimeOffset.Now }); // a stale "alone"

        await controller.LeaveHeosGroupAsync(2);

        Assert.Contains("group/set_group?pid=1,3", heos.Commands); // Kitchen leaves, Patio stays
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_zone_2_sequence_without_a_volume_needs_no_mute_and_turns_off_last()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();
        avr.ClearCommands();

        await controller.ApplyZone2Async(on: true, source: "NET", volume: null, muted: null);
        await controller.ApplyZone2Async(on: false, source: "TV", volume: null, muted: true);

        Assert.Equal(["Z2ON", "Z2NET", "Z2TV", "Z2MUON", "Z2OFF"], Zone2Commands(avr));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.ApplyZone2Async(on: null, source: "50", volume: null, muted: null));
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_new_source_with_a_lower_volume_keeps_the_zone_muted_until_the_volume_is_set()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        avr.Replies["Z2?"] = "Z2ON\rZ2TV\rZ270\r";
        avr.Replies["Z2MU?"] = "Z2MUOFF\r";
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();
        avr.ClearCommands();

        await controller.ApplyZone2Async(on: null, source: "NET", volume: 20, muted: null);

        // Mute goes back to what it was (off) only after the lower volume.
        Assert.Equal(["Z2MUON", "Z220", "Z2NET", "Z2MUOFF"], Zone2Commands(avr));
        await telemetry.DisposeAsync();
    }

    private const string KitchenPlayer = ",{\"name\":\"Kitchen\",\"pid\":2,\"model\":\"HEOS 1\",\"version\":\"1\",\"ip\":\"127.0.0.2\",\"network\":\"wifi\",\"lineout\":0,\"serial\":\"\"}";

    private const string FakeAndKitchenGroup =
        "[{\"name\":\"Fake + Kitchen\",\"gid\":1,\"players\":[{\"name\":\"Fake\",\"pid\":1,\"role\":\"leader\"},{\"name\":\"Kitchen\",\"pid\":2,\"role\":\"member\"}]}]";

    [Fact]
    public async Task Grouping_sends_set_group_with_this_receiver_first_and_reads_the_groups()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos { MorePlayersJson = KitchenPlayer };
        var (controller, state, telemetry) = await CreateControllerAsync(avr, heos);

        heos.GroupsAfterSetGroup = FakeAndKitchenGroup; // what the speaker has after set_group
        var groups = await controller.ToggleHeosGroupMemberAsync(2);

        Assert.Contains("group/set_group?pid=1,2", heos.Commands);
        var group = Assert.Single(groups.Groups);
        Assert.Equal(20, group.Volume);
        Assert.Equal(groups, state.Snapshot.HeosGroups);
        Assert.Equal(2, groups.Players.Count);

        heos.GroupsAfterSetGroup = "[]";
        var alone = await controller.ToggleHeosGroupMemberAsync(2); // Kitchen leaves: this receiver plays alone
        Assert.Contains("group/set_group?pid=1", heos.Commands);
        Assert.Empty(alone.Groups);
        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task An_automation_caps_zone_2_and_group_volume_at_the_safe_volume()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos { MorePlayersJson = KitchenPlayer, GroupsJson = FakeAndKitchenGroup };
        var log = new AppLogStore(Path.Combine(_tempDir, "auto.log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, "auto.json"));
        await store.InitializeAsync();
        await store.UpdateAsync(s =>
        {
            s.Receiver.Host = "127.0.0.1";
            s.Receiver.Port = heos.Port;
            s.Mcp.MaxSafeVolume = 40;
        });
        var bus = new RoomEventBus(log);
        var telemetry = new EventTelemetryLog(store, log, bus, Path.Combine(_tempDir, "auto-events"));
        var state = new ReceiverStateStore();
        var controller = new ReceiverController(new Heos.HeosClient(), new Heos.DenonAvrClient(avr.Port), store, state, telemetry, log);
        var plugins = new PluginHostService([], store, state, controller, telemetry, log);
        using var engine = new AutomationEngineService(store, controller, state, plugins, telemetry, bus, log);

        var graph = new AutomationGraph
        {
            Id = "party", Name = "Party", Enabled = true,
            Nodes =
            [
                new AutomationNode { Id = "t", Type = AutomationNodeTypes.TriggerWebhook, Config = new() { ["hook"] = "party" } },
                new AutomationNode { Id = "z", Type = AutomationNodeTypes.ActionReceiver, Config = new() { ["command"] = "zone2_volume", ["value"] = "90" } },
                new AutomationNode { Id = "g", Type = AutomationNodeTypes.ActionReceiver, Config = new() { ["command"] = "group_volume", ["value"] = "90" } },
            ],
            Edges =
            [
                new AutomationEdge { From = "t", Port = AutomationNodeTypes.PortOut, To = "z" },
                new AutomationEdge { From = "z", Port = AutomationNodeTypes.PortOut, To = "g" },
            ],
        };

        var run = await engine.RunGraphNowAsync(graph, "t", null, dryRun: false);

        Assert.True(run.Success, run.Summary);
        Assert.Equal(["Z240"], Zone2Commands(avr));
        Assert.Contains("group/set_volume?gid=1&level=40", heos.Commands);
        Assert.DoesNotContain(heos.Commands, c => c.Contains("level=90", StringComparison.Ordinal));
        Assert.Contains(run.Steps, s => s.Message.Contains("capped from 90", StringComparison.Ordinal));

        await telemetry.DisposeAsync();
        bus.Dispose();
    }
}
