using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.ViewModels;

namespace Denon.App.Desktop.CommandPalette;

/// <summary>One row in the palette list.</summary>
public sealed class CommandPaletteResultItem
{
    public CommandPaletteResultItem(CommandPaletteEntry entry, string? badge)
    {
        Entry = entry;
        Badge = badge ?? string.Empty;
        Icon = System.Windows.Application.Current?.TryFindResource(IconKey(entry)) as Geometry;
    }

    public CommandPaletteEntry Entry { get; }

    public string Title => Entry.Title;

    public string Subtitle => Entry.Subtitle;

    public string CategoryLabel => CommandPaletteViewModel.CategoryLabel(Entry.Category);

    /// <summary>"Active" for the current input or sound mode, "Current" for the open page. Empty otherwise.</summary>
    public string Badge { get; }

    public bool HasBadge => Badge.Length > 0;

    public Geometry? Icon { get; }

    /// <summary>The name a screen reader says, for example "Movie Night, Scene, Activate this Smart Scene".</summary>
    public string AccessibleName => HasBadge
        ? $"{Title}, {CategoryLabel}, {Badge}, {Subtitle}"
        : $"{Title}, {CategoryLabel}, {Subtitle}";

    private static string IconKey(CommandPaletteEntry entry) => entry.Category switch
    {
        CommandPaletteCategory.Page when entry.Payload is CommandPalettePage page => page.IconKey,
        CommandPaletteCategory.Scene => "IconNavScenes",
        CommandPaletteCategory.Station => "IconRadio",
        CommandPaletteCategory.Input => "IconNavControlDeck",
        CommandPaletteCategory.SoundMode => "IconMusicNote",
        CommandPaletteCategory.Plugin => "IconNavPlugins",
        CommandPaletteCategory.PluginAction => "IconPlay",
        _ => "IconChevronRight",
    };
}

/// <summary>
/// The Ctrl+K command palette: a search box over pages, Smart Scenes, saved stations, inputs, sound modes
/// and plugin actions. Choosing a row runs it through the command the page itself uses.
/// </summary>
public sealed class CommandPaletteViewModel : ObservableObject
{
    private const int MaxResults = 200;

    private readonly MainViewModel _main;
    private readonly IReadOnlyList<CommandPaletteEntry> _entries;
    private string _query = string.Empty;
    private CommandPaletteResultItem? _selectedResult;
    private string _statusText = string.Empty;
    private int _totalMatches;

    public CommandPaletteViewModel(MainViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _entries = CommandPaletteIndex.Build(main);
        RunSelectedCommand = new RelayCommand(_ => RunSelected());
        Refresh();
    }

    /// <summary>Asks the window to close. The palette closes before a row runs, so focus goes back to the page.</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<CommandPaletteResultItem> Results { get; } = [];

    public ICommand RunSelectedCommand { get; }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
            {
                Refresh();
            }
        }
    }

    public CommandPaletteResultItem? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (SetProperty(ref _selectedResult, value))
            {
                UpdateStatusText();
            }
        }
    }

    /// <summary>The footer line, also read out by screen readers: the chosen row and how many rows match.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasResults => Results.Count > 0;

    public static string CategoryLabel(CommandPaletteCategory category) => category switch
    {
        CommandPaletteCategory.Page => "Page",
        CommandPaletteCategory.Scene => "Scene",
        CommandPaletteCategory.Station => "Station",
        CommandPaletteCategory.Input => "Input",
        CommandPaletteCategory.SoundMode => "Sound mode",
        CommandPaletteCategory.Plugin => "Plugin",
        CommandPaletteCategory.PluginAction => "Plugin action",
        _ => category.ToString(),
    };

    /// <summary>Moves the chosen row up (negative) or down (positive). Up from the top goes to the bottom.</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = SelectedResult is null ? -1 : Results.IndexOf(SelectedResult);
        if (index < 0)
        {
            index = delta > 0 ? 0 : Results.Count - 1;
        }
        else if (Math.Abs(delta) == 1)
        {
            index = (index + delta + Results.Count) % Results.Count;
        }
        else
        {
            index = Math.Clamp(index + delta, 0, Results.Count - 1);
        }

        SelectedResult = Results[index];
    }

    public void SelectFirst() => SelectedResult = Results.FirstOrDefault();

    public void SelectLast() => SelectedResult = Results.LastOrDefault();

    /// <summary>Runs the chosen row. Returns false and keeps the palette open when the row cannot run now.</summary>
    public bool RunSelected()
    {
        if (SelectedResult is not { } item)
        {
            return false;
        }

        var run = Prepare(item.Entry);
        if (run is null)
        {
            StatusText = "The receiver is busy. Try again in a moment.";
            return false;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        run();
        return true;
    }

    /// <summary>The action for a row, or null when its command cannot run now (for example while the receiver is busy).</summary>
    private Action? Prepare(CommandPaletteEntry entry)
    {
        switch (entry.Payload)
        {
            case CommandPalettePage { Section: CommandPalettePages.HelpSection }:
                return () => _main.OpenContextHelpCommand.Execute(null);
            case CommandPalettePage page:
                return () => _main.NavigateCommand.Execute(page.Section);
            case ScenePresetEditor scene:
                return Guard(_main.ActivateSceneCommand, scene);
            case StationPresetEditor station:
                return Guard(_main.PlayPresetCommand, station);
            case InputSourceItem input:
                return Guard(_main.SelectInputCommand, input);
            case SoundModeItem mode:
                return Guard(_main.SelectSoundModeCommand, mode);
            case PluginCardViewModel plugin:
                return () => _main.OpenPluginCommand.Execute(plugin);
            case CommandPalettePluginAction pluginAction:
                // The same path a plugin-action shortcut takes: it runs the action and shows the result in the status bar.
                return () => _main.ExecuteShortcutAction($"plugin:{pluginAction.Plugin.Id}:{pluginAction.Action.ActionId}");
            default:
                return null;
        }
    }

    private static Action? Guard(ICommand command, object parameter) =>
        command.CanExecute(parameter) ? () => command.Execute(parameter) : null;

    private void Refresh()
    {
        var matches = CommandPaletteSearch.Search(_entries, Query, MaxResults);
        _totalMatches = matches.Count;

        Results.Clear();
        foreach (var match in matches)
        {
            Results.Add(new CommandPaletteResultItem(match.Entry, BadgeFor(match.Entry)));
        }

        OnPropertyChanged(nameof(HasResults));
        _selectedResult = null;
        SelectedResult = Results.FirstOrDefault();
        UpdateStatusText();
    }

    private string? BadgeFor(CommandPaletteEntry entry) => entry.Payload switch
    {
        CommandPalettePage page when string.Equals(page.Section, _main.ActiveSection, StringComparison.OrdinalIgnoreCase) => "Current",
        InputSourceItem { IsActive: true } => "Active",
        SoundModeItem { IsActive: true } => "Active",
        PluginCardViewModel { IsEnabled: false } => "Off",
        _ => null,
    };

    private void UpdateStatusText()
    {
        if (Results.Count == 0)
        {
            StatusText = $"No match for \"{Query.Trim()}\".";
            return;
        }

        var count = _totalMatches == 1 ? "1 result" : $"{_totalMatches} results";
        StatusText = SelectedResult is { } selected
            ? $"{selected.Title} ({selected.CategoryLabel}), {Results.IndexOf(selected) + 1} of {count}"
            : count;
    }
}
