using System.Text.Json;
using Denon.App.Core.Heos;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// #75: Zone 2 volume, source and mute (the Z2 lines and commands), and HEOS multi-room groups (get_groups, set_group,
/// the group volume, the group events and the automation commands).
/// </summary>
public sealed class Zone2AndHeosGroupTests
{
    // ---- Z2 lines ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Z240", 40)]
    [InlineData("Z205", 5)]
    [InlineData("Z200", 0)]
    [InlineData("Z298", 98)]
    [InlineData("Z2405", 40)] // a half step: the whole number shows
    [InlineData(" z235 ", 35)]
    public void A_Z2_number_line_is_the_zone_2_volume(string line, int volume) =>
        Assert.Equal(volume, AvrLiveState.Empty.Apply(line).Zone2Volume);

    [Theory]
    [InlineData("Z2TV", "TV")]
    [InlineData("Z2NET", "NET")]
    [InlineData("Z2SOURCE", "SOURCE")]
    [InlineData("Z2SAT/CBL", "SAT/CBL")]
    [InlineData("Z2bt", "BT")]
    [InlineData("Z2AUX2", "AUX2")]
    [InlineData("Z2USB/IPOD", "USB/IPOD")]
    public void A_Z2_source_line_is_the_zone_2_source(string line, string source) =>
        Assert.Equal(source, AvrLiveState.Empty.Apply(line).Zone2Source);

    [Fact]
    public void Power_and_mute_lines_set_their_own_fields()
    {
        var state = AvrLiveState.Empty.Apply("Z2ON").Apply("Z2MUON");
        Assert.True(state.IsZone2On);
        Assert.True(state.IsZone2Muted);
        Assert.Null(state.Zone2Source); // "MUON" is not a source
        Assert.Null(state.Zone2Volume);

        state = state.Apply("Z2MUOFF").Apply("Z2OFF");
        Assert.False(state.IsZone2On);
        Assert.False(state.IsZone2Muted);
    }

    [Theory]
    [InlineData("Z2CS ST")]
    [InlineData("Z2CVFL 50")]
    [InlineData("Z2PSBAS 50")]
    [InlineData("Z2HPFOFF")]
    [InlineData("Z2SLPOFF")]
    [InlineData("Z2QUICK1")]
    [InlineData("Z2SMART2")]
    [InlineData("Z2HDA THR")]
    [InlineData("Z2")]
    [InlineData("Z2--")]
    [InlineData("Z21")]
    [InlineData("Z21234")]
    public void Other_Z2_lines_change_nothing(string line) =>
        Assert.Equal(AvrLiveState.Empty, AvrLiveState.Empty.Apply(line));

    [Fact]
    public void The_Z2_reply_in_any_order_gives_the_whole_zone()
    {
        var zone = DenonAvrClient.ParseZone2Lines(["Z235", "MV45", "Z2NET", "Z2ON", "Z2MUON"]);

        Assert.True(zone.IsZone2On);
        Assert.Equal(35, zone.Zone2Volume);
        Assert.Equal("NET", zone.Zone2Source);
        Assert.True(zone.IsZone2Muted);
        Assert.Null(zone.Volume); // MV45 is the main zone: not read here
    }

    [Fact]
    public void The_live_zone_2_state_goes_into_the_status()
    {
        var status = new ReceiverStatus(
            new ReceiverPlayerInfo("AVR", 1, "AVR-X", "1", "10.0.0.2", "wired", 0, ""),
            "stop", 40, false, new NowPlayingMedia("", "", "", "", "", "", "", null, null), DateTimeOffset.Now,
            IsZone2On: false, Zone2Volume: 20, Zone2Source: "TV");

        var live = AvrLiveState.Empty.Apply("Z2ON").Apply("Z230").Apply("Z2MUON").ApplyTo(status);

        Assert.True(live.IsZone2On);
        Assert.Equal(30, live.Zone2Volume);
        Assert.Equal("TV", live.Zone2Source); // not reported on this link: the read's value stays
        Assert.True(live.IsZone2Muted);
    }

    // ---- Z2 commands ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(40, "Z240")]
    [InlineData(5, "Z205")]
    [InlineData(0, "Z200")]
    [InlineData(98, "Z298")]
    [InlineData(150, "Z298")]
    public void The_zone_2_volume_command_is_two_digits(int level, string command) =>
        Assert.Equal(command, DenonAvrClient.Zone2VolumeCommand(level));

    [Fact]
    public void A_negative_zone_2_volume_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DenonAvrClient.Zone2VolumeCommand(-1));

    [Theory]
    [InlineData("tv", "Z2TV")]
    [InlineData(" SOURCE ", "Z2SOURCE")]
    [InlineData("SAT/CBL", "Z2SAT/CBL")]
    public void The_zone_2_source_command_takes_a_known_source(string code, string command) =>
        Assert.Equal(command, DenonAvrClient.Zone2SourceCommand(code));

    [Theory]
    [InlineData("50")]        // would be "Z250", a volume past the safe limit
    [InlineData("MUON")]      // a mute command
    [InlineData("UP")]
    [InlineData("TV\rMV98")]  // a second command
    [InlineData("")]
    [InlineData("KITCHEN")]
    public void Anything_else_as_a_zone_2_source_is_refused(string code) =>
        Assert.Throws<ArgumentException>(() => DenonAvrClient.Zone2SourceCommand(code));

    [Fact]
    public void The_picker_sources_are_all_zone_2_sources()
    {
        Assert.Equal("SOURCE", DenonCatalog.Zone2Sources[0].Code);
        Assert.All(DenonCatalog.Zone2Sources, s => Assert.True(DenonCatalog.IsZone2SourceCode(s.Code)));
        Assert.Equal("Main zone source", DenonCatalog.GetFriendlyZone2SourceName("source"));
        Assert.Equal("TV Audio", DenonCatalog.GetFriendlyZone2SourceName("TV"));
    }

    // ---- HEOS groups: JSON and commands ------------------------------------------------------------------------

    [Fact]
    public void Get_groups_reads_number_and_string_ids_and_puts_the_leader_first()
    {
        using var doc = JsonDocument.Parse("""
            [
              { "name": "Living Room + Kitchen", "gid": -1574958592,
                "players": [ { "name": "Kitchen", "pid": 1205427563, "role": "member" },
                             { "name": "Living Room", "pid": -1574958592, "role": "leader" } ] },
              { "name": "Patio", "gid": "42",
                "players": [ { "name": "Patio", "pid": "42", "role": "leader" }, { "name": "bad", "pid": "x" } ] },
              { "name": "no id", "players": [] },
              "not an object"
            ]
            """);

        var groups = HeosClient.ParseGroups(doc.RootElement);

        Assert.Equal(2, groups.Count);
        var first = groups[0];
        Assert.Equal(-1574958592, first.GroupId);
        Assert.Equal("Living Room", first.Leader?.Name);
        Assert.Equal(["Living Room", "Kitchen"], first.Members.Select(static m => m.Name));
        Assert.True(first.Contains(1205427563));
        Assert.Equal(42, groups[1].GroupId);
        Assert.Equal([42], groups[1].Members.Select(static m => m.PlayerId));
    }

    [Fact]
    public void Get_groups_with_no_payload_array_is_no_groups()
    {
        using var doc = JsonDocument.Parse("{}");
        Assert.Empty(HeosClient.ParseGroups(doc.RootElement));
    }

    [Fact]
    public void Set_group_lists_the_leader_first()
    {
        Assert.Equal("heos://group/set_group?pid=-5,7,9", HeosClient.SetGroupCommand([-5, 7, 9]));
        Assert.Equal("heos://group/set_group?pid=7", HeosClient.SetGroupCommand([7]));
        Assert.Throws<ArgumentException>(() => HeosClient.SetGroupCommand([]));
        Assert.Throws<ArgumentException>(() => HeosClient.SetGroupCommand([7, 7]));
    }

    [Fact]
    public void The_group_volume_command_takes_0_to_100()
    {
        Assert.Equal("heos://group/set_volume?gid=12&level=30", HeosClient.GroupVolumeCommand(12, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeosClient.GroupVolumeCommand(12, 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeosClient.GroupVolumeCommand(12, -1));
    }

    // ---- HEOS groups: events and the store ---------------------------------------------------------------------

    private static HeosGroupsState TwoRooms(int? volume = 20) => new(
        [Player(1, "Living Room"), Player(2, "Kitchen"), Player(3, "Patio")],
        [new HeosGroup(1, "Living Room + Kitchen", [new(1, "Living Room", true), new(2, "Kitchen", false)], volume, false)],
        DateTimeOffset.Now);

    private static ReceiverPlayerInfo Player(int pid, string name) => new(name, pid, "HEOS", "1", "10.0.0." + pid, "wired", 0, "");

    private static IReadOnlyDictionary<string, string> Fields(string line)
    {
        Assert.True(HeosMessageParser.TryParseLine(line, string.Empty, out var parsed));
        return parsed!.MessageFields;
    }

    [Fact]
    public void A_group_volume_event_patches_that_group()
    {
        var store = new ReceiverStateStore();
        store.UpdateHeosGroups(TwoRooms());
        var raised = 0;
        store.StateChanged += _ => raised++;

        var line = """{"heos": {"command": "event/group_volume_changed", "message": "gid=1&level=35&mute=on"}}""";
        Assert.True(HeosEventListenerService.ApplyGroupVolumeEvent(store, "event/group_volume_changed", Fields(line)));

        var group = Assert.Single(store.Snapshot.HeosGroups!.Groups);
        Assert.Equal(35, group.Volume);
        Assert.True(group.IsMuted);
        Assert.Equal(1, raised);

        // The same values again: nothing changed, nothing raised.
        HeosEventListenerService.ApplyGroupVolumeEvent(store, "event/group_volume_changed", Fields(line));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_group_volume_event_for_an_unknown_group_changes_nothing()
    {
        var store = new ReceiverStateStore();
        store.UpdateHeosGroups(TwoRooms());
        var before = store.Snapshot;

        var line = """{"heos": {"command": "event/group_volume_changed", "message": "gid=99&level=80&mute=off"}}""";
        Assert.True(HeosEventListenerService.ApplyGroupVolumeEvent(store, "event/group_volume_changed", Fields(line)));

        Assert.Same(before, store.Snapshot);
    }

    [Fact]
    public void Other_events_are_not_group_volume_events()
    {
        var store = new ReceiverStateStore();
        Assert.False(HeosEventListenerService.ApplyGroupVolumeEvent(store, "event/player_volume_changed", new Dictionary<string, string> { ["level"] = "5" }));
        Assert.True(HeosEventListenerService.IsGroupsChangedEvent("event/groups_changed"));
        Assert.True(HeosEventListenerService.IsGroupsChangedEvent("event/players_changed"));
        Assert.False(HeosEventListenerService.IsGroupsChangedEvent("event/group_volume_changed"));
    }

    [Fact]
    public void A_group_volume_event_during_a_read_wins_over_the_read()
    {
        var store = new ReceiverStateStore();
        store.UpdateHeosGroups(TwoRooms(volume: 20));

        var before = store.BeginHeosGroupsRead();
        store.PatchGroupVolume(1, 10, null);          // came in before the read began: the read has it
        var readStart = store.BeginHeosGroupsRead();
        store.PatchGroupVolume(1, 35, true);          // came in while the read ran
        var kept = store.UpdateHeosGroups(TwoRooms(volume: 22), readStart);

        Assert.Equal(35, kept.Groups[0].Volume);
        Assert.True(kept.Groups[0].IsMuted);
        Assert.Equal(kept, store.Snapshot.HeosGroups);

        // A later read that began after the event: the read wins.
        var later = store.UpdateHeosGroups(TwoRooms(volume: 22), store.BeginHeosGroupsRead());
        Assert.Equal(22, later.Groups[0].Volume);
        Assert.True(before < readStart);
    }

    [Fact]
    public void A_group_volume_event_for_a_group_the_read_brings_is_kept()
    {
        var store = new ReceiverStateStore();
        var readStart = store.BeginHeosGroupsRead();
        store.PatchGroupVolume(1, 40, false);         // no groups known yet

        var kept = store.UpdateHeosGroups(TwoRooms(volume: 20), readStart);

        Assert.Equal(40, kept.Groups[0].Volume);
    }

    [Fact]
    public void The_same_groups_read_again_raises_nothing()
    {
        var store = new ReceiverStateStore();
        store.UpdateHeosGroups(TwoRooms());
        var raised = 0;
        store.StateChanged += _ => raised++;

        store.UpdateHeosGroups(TwoRooms()); // new lists, same content
        Assert.Equal(0, raised);

        store.UpdateHeosGroups(TwoRooms(volume: 21));
        Assert.Equal(1, raised);
    }

    // ---- HEOS groups: who is in the group after a tap ----------------------------------------------------------

    [Fact]
    public void A_player_outside_the_group_joins_it()
    {
        Assert.Equal([1, 2, 3], ReceiverController.GroupPlayersAfterToggle(TwoRooms(), ownPlayerId: 1, playerId: 3));
    }

    [Fact]
    public void A_member_leaves_and_the_group_stays()
    {
        var state = TwoRooms() with
        {
            Groups = [new HeosGroup(1, "All", [new(1, "Living Room", true), new(2, "Kitchen", false), new(3, "Patio", false)])],
        };
        Assert.Equal([1, 3], ReceiverController.GroupPlayersAfterToggle(state, ownPlayerId: 1, playerId: 2));
    }

    [Fact]
    public void Without_a_group_the_receiver_leads_a_new_one()
    {
        var alone = TwoRooms() with { Groups = [] };
        Assert.Equal([1, 3], ReceiverController.GroupPlayersAfterToggle(alone, ownPlayerId: 1, playerId: 3));
    }

    [Fact]
    public void When_the_leader_leaves_the_group_ends()
    {
        // This receiver (2) is a member; a tap on the leader (1) ends the group.
        Assert.Equal([1], ReceiverController.GroupPlayersAfterToggle(TwoRooms(), ownPlayerId: 2, playerId: 1));
        Assert.Equal([1], ReceiverController.PlayersAfterLeave(TwoRooms().Groups[0], 1));
        Assert.Equal([1], ReceiverController.PlayersAfterLeave(TwoRooms().Groups[0], 2));
    }

    [Fact]
    public void Players_are_found_by_name_or_id()
    {
        var players = TwoRooms().Players;
        Assert.Equal([2, 3], ReceiverController.ResolveHeosPlayerIds(players, ["kitchen", " 3 ", "", "Kitchen"]));

        var error = Assert.Throws<InvalidOperationException>(() => ReceiverController.ResolveHeosPlayerIds(players, ["Garage"]));
        Assert.Contains("Living Room, Kitchen, Patio", error.Message);
    }

    // ---- Smart Scenes ------------------------------------------------------------------------------------------

    [Fact]
    public void The_scene_editor_keeps_the_zone_2_and_group_steps()
    {
        var scene = new ScenePreset
        {
            Name = "Party Multi-Room",
            Zone2On = true,
            Zone2Source = "SOURCE",
            Zone2Volume = 30,
            Zone2Muted = false,
            HeosGroupMembers = ["Kitchen", "Patio"],
        };

        var back = Desktop.ViewModels.ScenePresetEditor.FromModel(scene).ToModel();

        Assert.True(back.Zone2On);
        Assert.Equal("SOURCE", back.Zone2Source);
        Assert.Equal(30, back.Zone2Volume);
        Assert.False(back.Zone2Muted);
        Assert.Equal(["Kitchen", "Patio"], back.HeosGroupMembers);
        Assert.False(back.HeosUngroup);

        var clone = scene.Clone();
        Assert.NotSame(scene.HeosGroupMembers, clone.HeosGroupMembers);
        Assert.Equal(scene.HeosGroupMembers, clone.HeosGroupMembers);
    }

    // ---- Automations -------------------------------------------------------------------------------------------

    private static IReadOnlyList<AutomationProblem> Check(string command, string value)
    {
        var graph = new AutomationGraph
        {
            Id = "g", Name = "g",
            Nodes =
            [
                new AutomationNode { Id = "t", Type = AutomationNodeTypes.TriggerWebhook, Config = new() { ["hook"] = "party" } },
                new AutomationNode { Id = "a", Type = AutomationNodeTypes.ActionReceiver, Config = new() { ["command"] = command, ["value"] = value } },
            ],
            Edges = [new AutomationEdge { From = "t", Port = AutomationNodeTypes.PortOut, To = "a" }],
        };
        return AutomationGraphValidator.Validate(graph).Where(static p => p.NodeId == "a").ToList();
    }

    [Theory]
    [InlineData("zone2_on", "")]
    [InlineData("zone2_off", "")]
    [InlineData("zone2_volume", "30")]
    [InlineData("zone2_volume", "98")]
    [InlineData("zone2_source", "TV")]
    [InlineData("zone2_source", "SOURCE")]
    [InlineData("zone2_mute", "")]
    [InlineData("zone2_unmute", "")]
    [InlineData("group_with", "Kitchen, Patio")]
    [InlineData("ungroup", "")]
    [InlineData("group_volume", "25")]
    public void The_new_receiver_commands_are_valid(string command, string value) =>
        Assert.Empty(Check(command, value));

    [Theory]
    [InlineData("zone2_volume", "99")]
    [InlineData("zone2_volume", "-1")]
    [InlineData("zone2_volume", "loud")]
    [InlineData("group_volume", "120")]
    [InlineData("zone2_source", "")]
    [InlineData("zone2_source", "50")]
    [InlineData("zone2_source", "MUON")]
    [InlineData("group_with", " , ")]
    [InlineData("zone3_on", "")]
    public void Bad_values_are_refused(string command, string value) =>
        Assert.NotEmpty(Check(command, value));

    [Fact]
    public void Every_receiver_command_option_is_a_command_the_validator_takes()
    {
        var field = AutomationNodeTypes.Find(AutomationNodeTypes.ActionReceiver)!.Fields.Single(static f => f.Key == "command");
        Assert.All(field.Options!, o => Assert.DoesNotContain(Check(o.Value, "1"), p => p.Message.Contains("unknown command")));
    }

    [Theory]
    [InlineData("zone2_volume", "30", "Zone 2 volume 30")]
    [InlineData("group_with", "Kitchen", "HEOS group with Kitchen")]
    [InlineData("ungroup", "", "Leave HEOS group")]
    [InlineData("power_on", "", "Receiver power on")]
    public void A_receiver_step_is_described_in_plain_words(string command, string value, string text) =>
        Assert.Equal(text, AutomationGraphRunner.DescribeReceiverCommand(command, value));
}
