using Denon.App.Desktop.ViewModels;

namespace Denon.App.Desktop.CommandPalette;

/// <summary>
/// Reads the palette rows from the main view model when the palette opens. It only reads: the rows run
/// through the commands the pages already use.
/// </summary>
public static class CommandPaletteIndex
{
    /// <summary>All rows, in the order the palette shows them before the user types.</summary>
    public static IReadOnlyList<CommandPaletteEntry> Build(MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        var entries = new List<CommandPaletteEntry>(CommandPalettePages.Entries());

        entries.Add(new CommandPaletteEntry(
            "tool:receiver-setup-check",
            CommandPaletteCategory.Tool,
            "Receiver setup check",
            "Compare the receiver with recommended settings (reads only until you apply)",
            ["setup", "audyssey", "reference level", "dynamic eq", "recommended", "profile", "best settings", "check"],
            CommandPaletteTool.ReceiverSetupCheck));

        foreach (var scene in main.ScenePresets)
        {
            if (string.IsNullOrWhiteSpace(scene.Name))
            {
                continue;
            }

            entries.Add(new CommandPaletteEntry(
                $"scene:{scene.Id}",
                CommandPaletteCategory.Scene,
                scene.Name,
                string.IsNullOrWhiteSpace(scene.Description) ? "Activate this Smart Scene" : scene.Description,
                [scene.Badge],
                scene));
        }

        foreach (var station in main.StationPresets)
        {
            var title = string.IsNullOrWhiteSpace(station.DisplayName) ? station.Alias : station.DisplayName;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            entries.Add(new CommandPaletteEntry(
                $"station:{station.Alias}:{station.Url}",
                CommandPaletteCategory.Station,
                title,
                "Play this saved station",
                [station.Alias],
                station));
        }

        foreach (var input in main.InputSources)
        {
            entries.Add(new CommandPaletteEntry(
                $"input:{input.Code}",
                CommandPaletteCategory.Input,
                input.DisplayName,
                "Switch the receiver to this input",
                [input.Code, input.ShortLabel],
                input));
        }

        foreach (var mode in main.SoundModes)
        {
            entries.Add(new CommandPaletteEntry(
                $"soundmode:{mode.Code}",
                CommandPaletteCategory.SoundMode,
                mode.DisplayName,
                "Set this sound mode",
                [mode.Code],
                mode));
        }

        foreach (var plugin in main.InstalledPlugins)
        {
            entries.Add(new CommandPaletteEntry(
                $"plugin:{plugin.Id}",
                CommandPaletteCategory.Plugin,
                plugin.Name,
                "Open this plugin",
                [plugin.ShortName, plugin.Category],
                plugin));
        }

        foreach (var plugin in main.InstalledPlugins.Where(p => p.IsEnabled))
        {
            // An action that needs values (a scene or an automation step gives them) cannot run from here.
            foreach (var action in plugin.Actions.Where(a => !a.Parameters.Any(p => p.Required)))
            {
                entries.Add(new CommandPaletteEntry(
                    $"plugin:{plugin.Id}:{action.ActionId}",
                    CommandPaletteCategory.PluginAction,
                    action.DisplayName,
                    plugin.Name,
                    [action.Category, plugin.ShortName],
                    new CommandPalettePluginAction(plugin, action)));
            }
        }

        return entries;
    }
}

/// <summary>The payload of a plugin action row.</summary>
public sealed record CommandPalettePluginAction(PluginCardViewModel Plugin, Denon.App.PluginSdk.PluginActionDescriptor Action);

/// <summary>The payload of a tool row: a page area of its own that the palette only opens.</summary>
public sealed record CommandPaletteTool(string Name, Action<MainViewModel> Open)
{
    /// <summary>Scenes &amp; Acoustics → Setup check. Opening it reads nothing from the receiver.</summary>
    public static CommandPaletteTool ReceiverSetupCheck { get; } = new("receiver-setup-check", static main => main.OpenReceiverSetupCheck());
}
