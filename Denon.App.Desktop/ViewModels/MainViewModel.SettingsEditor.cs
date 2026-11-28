using System.Collections;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

/// <summary>
/// Unsaved changes in the Settings, Scenes and Stations editors (#76): a dirty flag and a banner per page, and a
/// settings change from somewhere else (an automation, Volume Memory, the MCP server, a plugin) loads again only the
/// areas it changed that have no unsaved edits. <see cref="SettingsEditorState"/> holds the logic.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly SettingsEditorState _editorState = new();
    private RelayCommand? _discardEditorChangesCommand;
    private SettingsEditorAreas _dirtyEditorAreas;
    private SettingsEditorAreas _editorAreasChangedElsewhere;
    private bool _isLoadingEditor;
    private bool _isEditorDirtyCheckQueued;

    // More than 0 while the user's own Save & Sync or an import runs: its settings change loads all areas.
    private int _fullEditorReloadDepth;

    public bool IsSettingsDirty => _dirtyEditorAreas.HasFlag(SettingsEditorAreas.Settings);

    public bool IsScenesDirty => _dirtyEditorAreas.HasFlag(SettingsEditorAreas.Scenes);

    public bool IsStationsDirty => _dirtyEditorAreas.HasFlag(SettingsEditorAreas.Stations);

    public string SettingsDirtyText => DirtyText(SettingsEditorAreas.Settings, "settings", "Save & Sync");

    public string ScenesDirtyText => DirtyText(SettingsEditorAreas.Scenes, "scenes", "Save Scenes");

    public string StationsDirtyText => DirtyText(SettingsEditorAreas.Stations, "stations", "Save Changes");

    /// <summary>Loads the saved values of one area again. The parameter is "Settings", "Scenes" or "Stations".</summary>
    public RelayCommand DiscardEditorChangesCommand => _discardEditorChangesCommand ??= new RelayCommand(DiscardEditorChanges);

    private void InitializeSettingsEditor()
    {
        PropertyChanged += OnEditorPropertyChanged;
        WatchEditorCollection(StationPresets);
        WatchEditorCollection(ScenePresets);
        WatchEditorCollection(ShortcutEditors);
    }

    /// <summary>The editor values now, in the form <see cref="SettingsEditorValues.FromSettings"/> gives.</summary>
    private SettingsEditorValues CaptureEditorValues()
    {
        var fields = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        fields[SettingsEditorValues.Host] = SettingsHost ?? string.Empty;
        fields[SettingsEditorValues.Port] = SettingsPort ?? string.Empty;
        fields[SettingsEditorValues.PlayerName] = SettingsPlayerName ?? string.Empty;
        fields[SettingsEditorValues.PollIntervalSeconds] = SettingsPollIntervalSeconds ?? string.Empty;
        fields[SettingsEditorValues.LowVolume] = SettingsLowVolume ?? string.Empty;
        fields[SettingsEditorValues.VolumePresets] = SettingsVolumePresetsText ?? string.Empty;
        fields[SettingsEditorValues.StartMinimizedToTray] = SettingsEditorValues.Text(SettingsStartMinimizedToTray);
        fields[SettingsEditorValues.MinimizeToTray] = SettingsEditorValues.Text(SettingsMinimizeToTray);
        fields[SettingsEditorValues.CloseToTray] = SettingsEditorValues.Text(SettingsCloseToTray);
        fields[SettingsEditorValues.ShowNotifications] = SettingsEditorValues.Text(SettingsShowNotifications);
        fields[SettingsEditorValues.DisplayVolumeInDb] = SettingsEditorValues.Text(SettingsDisplayVolumeInDb);
        fields[SettingsEditorValues.LaunchAtWindowsStartup] = SettingsEditorValues.Text(SettingsLaunchAtWindowsStartup);
        fields[SettingsEditorValues.CheckForUpdates] = SettingsEditorValues.Text(SettingsCheckForUpdates);
        fields[SettingsEditorValues.TelemetryEnabled] = SettingsEditorValues.Text(SettingsTelemetryEnabled);
        fields[SettingsEditorValues.TelemetryRetentionDays] = SettingsTelemetryRetentionDays ?? string.Empty;
        fields[SettingsEditorValues.TelemetryMediaDwellSeconds] = SettingsTelemetryMediaDwellSeconds ?? string.Empty;
        fields[SettingsEditorValues.RelayPort] = SettingsRelayPort ?? string.Empty;
        fields[SettingsEditorValues.FfmpegPath] = SettingsFfmpegPath ?? string.Empty;
        fields[SettingsEditorValues.PreferFfmpegMp3] = SettingsEditorValues.Text(SettingsPreferFfmpegMp3);
        fields[SettingsEditorValues.CinemaSyncOffsetMs] = SettingsCinemaSyncOffsetMs ?? string.Empty;

        return new SettingsEditorValues(
            fields.ToImmutable(),
            [.. ShortcutEditors.Select(static shortcut => shortcut.ToModel())],
            [.. ScenePresets.Select(static scene => scene.ToModel())],
            [.. StationPresets.Select(static station => station.ToModel())]);
    }

    /// <summary>
    /// Raised by the settings store, on the thread that saved. Whether the user's own save is running is read here,
    /// before the hop to the UI thread, because that save is still in progress at this moment.
    /// </summary>
    private void OnStoreSettingsChanged(AppSettings settings)
    {
        var fullReload = Volatile.Read(ref _fullEditorReloadDepth) > 0;
        _ = _dispatcher.InvokeAsync(() => ApplyStoreSettingsChange(settings, fullReload));
    }

    private void ApplyStoreSettingsChange(AppSettings settings, bool fullReload)
    {
        if (fullReload)
        {
            LoadSettings(settings);
            return;
        }

        var plan = _editorState.Plan(SettingsEditorValues.FromSettings(settings), CaptureEditorValues());
        LoadSettings(settings, plan.Reload);
        if (plan.Kept != SettingsEditorAreas.None)
        {
            var names = string.Join(" and ", SettingsEditorState.Each(plan.Kept).Select(AreaName));
            StatusMessage = $"The saved {names} changed somewhere else. Your unsaved edits are kept.";
            _logStore.Info($"Settings changed outside the editor ({names}); unsaved edits there are kept.");
        }
    }

    /// <summary>Runs the user's own save or an import: its settings change loads every area.</summary>
    private async Task SaveWithFullEditorReloadAsync(Func<Task> save)
    {
        Interlocked.Increment(ref _fullEditorReloadDepth);
        try
        {
            await save();
        }
        finally
        {
            Interlocked.Decrement(ref _fullEditorReloadDepth);
        }
    }

    /// <summary>A field that saves at once now shows its saved value: not an unsaved edit.</summary>
    private void EditorFieldSaved(string key, string value)
    {
        _editorState.FieldSaved(key, value);
        UpdateEditorDirtyFlags();
    }

    private void DiscardEditorChanges(object? parameter)
    {
        if (parameter is not string name || !Enum.TryParse<SettingsEditorAreas>(name, out var area) ||
            area is SettingsEditorAreas.None)
        {
            return;
        }

        LoadSettings(_settingsStore.Current, area);
        StatusMessage = $"Unsaved {AreaName(area)} changes discarded.";
    }

    private void UpdateEditorDirtyFlags()
    {
        if (_isLoadingEditor)
        {
            return;
        }

        var dirty = _editorState.DirtyAreas(CaptureEditorValues());

        // An area that changed somewhere else and has no edits left (the user undid them) shows the saved values.
        var stale = _editorState.ChangedElsewhere & ~dirty;
        if (stale != SettingsEditorAreas.None)
        {
            LoadSettings(_settingsStore.Current, stale);
            return;
        }

        var changedElsewhere = _editorState.ChangedElsewhere;
        if (dirty == _dirtyEditorAreas && changedElsewhere == _editorAreasChangedElsewhere)
        {
            return;
        }

        _dirtyEditorAreas = dirty;
        _editorAreasChangedElsewhere = changedElsewhere;
        OnPropertyChanged(nameof(IsSettingsDirty));
        OnPropertyChanged(nameof(IsScenesDirty));
        OnPropertyChanged(nameof(IsStationsDirty));
        OnPropertyChanged(nameof(SettingsDirtyText));
        OnPropertyChanged(nameof(ScenesDirtyText));
        OnPropertyChanged(nameof(StationsDirtyText));
    }

    private string DirtyText(SettingsEditorAreas area, string noun, string saveLabel)
    {
        if (!_dirtyEditorAreas.HasFlag(area))
        {
            return string.Empty;
        }

        return _editorAreasChangedElsewhere.HasFlag(area)
            ? $"Unsaved changes. The saved {noun} also changed somewhere else (for example an automation or the MCP server). Your edits are kept: {saveLabel} replaces that change, Discard loads it."
            : $"Unsaved changes. Click {saveLabel} to keep them, or Discard to load the saved {noun}.";
    }

    private static string AreaName(SettingsEditorAreas area) => area switch
    {
        SettingsEditorAreas.Scenes => "scenes",
        SettingsEditorAreas.Stations => "stations",
        _ => "settings",
    };

    // Change tracking: any edit queues one check at background priority, so typing stays fast.
    private void QueueEditorDirtyCheck()
    {
        if (_isLoadingEditor || _isEditorDirtyCheckQueued)
        {
            return;
        }

        _isEditorDirtyCheckQueued = true;
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _isEditorDirtyCheckQueued = false;
            UpdateEditorDirtyFlags();
        });
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name &&
            name.StartsWith("Settings", StringComparison.Ordinal) &&
            name is not (nameof(SettingsSaveProblem) or nameof(SettingsDirtyText)))
        {
            QueueEditorDirtyCheck();
        }
    }

    private void WatchEditorCollection(INotifyCollectionChanged collection)
    {
        collection.CollectionChanged -= OnEditorCollectionChanged;
        collection.CollectionChanged += OnEditorCollectionChanged;
        foreach (var item in (IEnumerable)collection)
        {
            WatchEditorItem(item);
        }
    }

    private void OnEditorCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                WatchEditorItem(item);
            }
        }

        QueueEditorDirtyCheck();
    }

    // Removed rows are not unhooked: they are dropped, and an extra check from one is harmless.
    private void WatchEditorItem(object? item)
    {
        if (item is INotifyPropertyChanged observable)
        {
            observable.PropertyChanged -= OnEditorItemPropertyChanged;
            observable.PropertyChanged += OnEditorItemPropertyChanged;
        }

        switch (item)
        {
            case ScenePresetEditor scene:
                WatchEditorCollection(scene.PluginActions);
                break;
            case ScenePluginActionStepEditor step:
                WatchEditorCollection(step.Parameters);
                break;
        }
    }

    private void OnEditorItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => QueueEditorDirtyCheck();
}
