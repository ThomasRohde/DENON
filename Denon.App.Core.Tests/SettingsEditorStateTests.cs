using Denon.App.Core.Models;
using Denon.App.Desktop.ViewModels;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// Unsaved changes in Settings, Scenes and Stations (#76): a settings change from somewhere else (an automation,
/// Volume Memory, the MCP server) loads again only the areas it changed that have no unsaved edits.
/// </summary>
public sealed class SettingsEditorStateTests
{
    private static SettingsEditorValues Values(AppSettings settings) => SettingsEditorValues.FromSettings(settings);

    private static (SettingsEditorState State, AppSettings Saved) Loaded()
    {
        var saved = AppSettings.CreateDefault();
        saved.Receiver.Stations = [new StationPreset("p3", "DR P3", "https://example.test/p3")];
        saved.Receiver.Scenes = [new ScenePreset { Id = "movie", Name = "Movie Night" }];
        var state = new SettingsEditorState();
        state.Loaded(SettingsEditorAreas.All, Values(saved), Values(saved));
        return (state, saved);
    }

    [Fact]
    public void Loaded_values_are_not_dirty()
    {
        var (state, saved) = Loaded();

        Assert.Equal(SettingsEditorAreas.None, state.DirtyAreas(Values(saved)));
    }

    [Fact]
    public void An_edit_makes_only_its_own_area_dirty()
    {
        var (state, saved) = Loaded();
        var editor = Values(saved).WithField(SettingsEditorValues.Host, "192.168.1.50");
        var withStation = editor with { Stations = [.. editor.Stations, new StationPreset("jazz", "Jazz", "https://example.test/jazz")] };

        Assert.Equal(SettingsEditorAreas.Settings, state.DirtyAreas(editor));
        Assert.Equal(SettingsEditorAreas.Settings | SettingsEditorAreas.Stations, state.DirtyAreas(withStation));
    }

    [Fact]
    public void An_automation_or_volume_memory_save_reloads_nothing_and_keeps_the_edits()
    {
        var (state, saved) = Loaded();
        var editor = Values(saved).WithField(SettingsEditorValues.Host, "192.168.1.50");
        var elsewhere = saved.Clone();
        elsewhere.AutomationGraphs = [];
        elsewhere.VolumeMemory.MinDifference = 6;

        var plan = state.Plan(Values(elsewhere), editor);

        Assert.Equal(new SettingsReloadPlan(SettingsEditorAreas.None, SettingsEditorAreas.None), plan);
        Assert.Equal(SettingsEditorAreas.Settings, state.DirtyAreas(editor));
    }

    [Fact]
    public void A_station_added_by_the_mcp_server_reloads_stations_but_not_the_settings_being_edited()
    {
        var (state, saved) = Loaded();
        var editor = Values(saved).WithField(SettingsEditorValues.PollIntervalSeconds, "9");
        var elsewhere = saved.Clone();
        elsewhere.Receiver.Stations.Add(new StationPreset("jazz", "Jazz", "https://example.test/jazz"));

        var plan = state.Plan(Values(elsewhere), editor);

        Assert.Equal(SettingsEditorAreas.Stations, plan.Reload);
        Assert.Equal(SettingsEditorAreas.None, plan.Kept);
    }

    [Fact]
    public void A_change_to_an_area_with_edits_keeps_the_edits_and_says_so_once()
    {
        var (state, saved) = Loaded();
        var editor = Values(saved) with { Scenes = [new ScenePreset { Id = "movie", Name = "Film Night" }] };
        var elsewhere = saved.Clone();
        elsewhere.Receiver.Scenes.Add(new ScenePreset { Id = "radio", Name = "Morning Radio" });

        var first = state.Plan(Values(elsewhere), editor);
        var again = state.Plan(Values(elsewhere), editor);

        Assert.Equal(new SettingsReloadPlan(SettingsEditorAreas.None, SettingsEditorAreas.Scenes), first);
        Assert.Equal(new SettingsReloadPlan(SettingsEditorAreas.None, SettingsEditorAreas.None), again);
        Assert.Equal(SettingsEditorAreas.Scenes, state.ChangedElsewhere);
        Assert.Equal(SettingsEditorAreas.Scenes, state.DirtyAreas(editor));
    }

    [Fact]
    public void Loading_an_area_again_clears_its_changed_elsewhere_mark()
    {
        var (state, saved) = Loaded();
        var editor = Values(saved) with { Scenes = [] };
        var elsewhere = saved.Clone();
        elsewhere.Receiver.Scenes.Add(new ScenePreset { Id = "radio", Name = "Morning Radio" });
        state.Plan(Values(elsewhere), editor);

        state.Loaded(SettingsEditorAreas.Scenes, Values(elsewhere), Values(elsewhere));

        Assert.Equal(SettingsEditorAreas.None, state.ChangedElsewhere);
        Assert.Equal(SettingsEditorAreas.None, state.DirtyAreas(Values(elsewhere)));
    }

    [Fact]
    public void A_field_that_saves_at_once_is_not_an_edit_or_a_change_elsewhere()
    {
        var (state, saved) = Loaded();
        var editor = Values(saved).WithField(SettingsEditorValues.CinemaSyncOffsetMs, "320");
        state.FieldSaved(SettingsEditorValues.CinemaSyncOffsetMs, "320");
        var after = saved.Clone();
        after.Media.CinemaSyncOffsetMs = 320;

        Assert.Equal(SettingsEditorAreas.None, state.DirtyAreas(editor));
        Assert.Equal(new SettingsReloadPlan(SettingsEditorAreas.None, SettingsEditorAreas.None), state.Plan(Values(after), editor));
    }

    [Fact]
    public void The_order_of_plugin_step_parameters_does_not_count()
    {
        static ScenePreset Scene(params (string Key, string Value)[] parameters) => new()
        {
            Id = "movie",
            PluginActions =
            [
                new ScenePluginActionStep
                {
                    PluginId = "hue",
                    ActionId = "scene",
                    Parameters = parameters.ToDictionary(p => p.Key, p => p.Value),
                },
            ],
        };

        var (state, saved) = Loaded();
        var loaded = Values(saved) with { Scenes = [Scene(("room", "Living"), ("brightness", "40"))] };
        state.Loaded(SettingsEditorAreas.Scenes, loaded, loaded);

        Assert.Equal(SettingsEditorAreas.None, state.DirtyAreas(loaded with { Scenes = [Scene(("brightness", "40"), ("room", "Living"))] }));
        Assert.Equal(SettingsEditorAreas.Scenes, state.DirtyAreas(loaded with { Scenes = [Scene(("brightness", "60"), ("room", "Living"))] }));
    }
}
