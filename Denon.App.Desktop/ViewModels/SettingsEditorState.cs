using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Denon.App.Core.Models;

namespace Denon.App.Desktop.ViewModels;

/// <summary>The parts of the settings that Save &amp; Sync writes, one per page that edits them.</summary>
[Flags]
public enum SettingsEditorAreas
{
    None = 0,

    /// <summary>The Settings page: receiver, behaviour, relay, telemetry and shortcut fields.</summary>
    Settings = 1,

    /// <summary>The Smart Scenes list on the Scenes &amp; Acoustics page.</summary>
    Scenes = 2,

    /// <summary>The preset stations on the Stations &amp; Streams page.</summary>
    Stations = 4,

    All = Settings | Scenes | Stations,
}

/// <summary>
/// The values of the three editor areas at one moment. Plain data, no WPF: the tracker and its tests compare it.
/// </summary>
/// <param name="Fields">The Settings page fields as the editor shows them (text and on/off), by name.</param>
/// <param name="Shortcuts">The shortcut rows (part of the Settings area).</param>
/// <param name="Scenes">The Smart Scenes.</param>
/// <param name="Stations">The preset stations.</param>
public sealed record SettingsEditorValues(
    ImmutableSortedDictionary<string, string> Fields,
    IReadOnlyList<ShortcutBinding> Shortcuts,
    IReadOnlyList<ScenePreset> Scenes,
    IReadOnlyList<StationPreset> Stations)
{
    // The keys of Fields. The view model uses the same keys for the editor fields.
    public const string Host = nameof(Host);
    public const string Port = nameof(Port);
    public const string PlayerName = nameof(PlayerName);
    public const string PollIntervalSeconds = nameof(PollIntervalSeconds);
    public const string LowVolume = nameof(LowVolume);
    public const string VolumePresets = nameof(VolumePresets);
    public const string StartMinimizedToTray = nameof(StartMinimizedToTray);
    public const string MinimizeToTray = nameof(MinimizeToTray);
    public const string CloseToTray = nameof(CloseToTray);
    public const string ShowNotifications = nameof(ShowNotifications);
    public const string DisplayVolumeInDb = nameof(DisplayVolumeInDb);
    public const string LaunchAtWindowsStartup = nameof(LaunchAtWindowsStartup);
    public const string CheckForUpdates = nameof(CheckForUpdates);
    public const string TelemetryEnabled = nameof(TelemetryEnabled);
    public const string TelemetryRetentionDays = nameof(TelemetryRetentionDays);
    public const string TelemetryMediaDwellSeconds = nameof(TelemetryMediaDwellSeconds);
    public const string RelayPort = nameof(RelayPort);
    public const string FfmpegPath = nameof(FfmpegPath);
    public const string PreferFfmpegMp3 = nameof(PreferFfmpegMp3);
    public const string CinemaSyncOffsetMs = nameof(CinemaSyncOffsetMs);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// The saved values of the editor areas, the way Settings shows them. The room microphone switches are not in
    /// it: they save at once and are never unsaved edits.
    /// </summary>
    public static SettingsEditorValues FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var receiver = settings.Receiver;
        var fields = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        fields[Host] = receiver.Host;
        fields[Port] = Text(receiver.Port);
        fields[PlayerName] = receiver.PlayerName;
        fields[PollIntervalSeconds] = Text(receiver.PollIntervalSeconds);
        fields[LowVolume] = Text(receiver.DefaultLowVolume);
        fields[VolumePresets] = string.Join(", ", receiver.VolumePresets);
        fields[StartMinimizedToTray] = Text(settings.Ui.StartMinimizedToTray);
        fields[MinimizeToTray] = Text(settings.Ui.MinimizeToTray);
        fields[CloseToTray] = Text(settings.Ui.CloseToTray);
        fields[ShowNotifications] = Text(settings.Ui.ShowNotifications);
        fields[DisplayVolumeInDb] = Text(settings.Ui.DisplayVolumeInDb);
        fields[LaunchAtWindowsStartup] = Text(settings.Ui.LaunchAtWindowsStartup);
        fields[CheckForUpdates] = Text(settings.Ui.CheckForUpdates);
        fields[TelemetryEnabled] = Text(settings.Telemetry.Enabled);
        fields[TelemetryRetentionDays] = Text(settings.Telemetry.RetentionDays);
        fields[TelemetryMediaDwellSeconds] = Text(settings.Telemetry.MediaDwellSeconds);
        fields[RelayPort] = Text(settings.Media.RelayPort);
        fields[FfmpegPath] = settings.Media.FfmpegPath ?? string.Empty;
        fields[PreferFfmpegMp3] = Text(settings.Media.PreferFfmpegMp3Transcode);
        fields[CinemaSyncOffsetMs] = Text(settings.Media.CinemaSyncOffsetMs);

        return new SettingsEditorValues(
            fields.ToImmutable(),
            [.. settings.Shortcuts.Bindings],
            [.. receiver.Scenes],
            [.. receiver.Stations]);
    }

    public static string Text(bool value) => value ? "true" : "false";

    public static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A text form of one area. Two values with the same text for an area are equal in that area. Object keys are
    /// sorted, so the order of plugin step parameters (which a plugin can change when it loads) does not count.
    /// </summary>
    public string Fingerprint(SettingsEditorAreas area) => Canonical(area switch
    {
        SettingsEditorAreas.Settings => JsonSerializer.SerializeToNode(new { Fields, Shortcuts }, JsonOptions),
        SettingsEditorAreas.Scenes => JsonSerializer.SerializeToNode(Scenes, JsonOptions),
        SettingsEditorAreas.Stations => JsonSerializer.SerializeToNode(Stations, JsonOptions),
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "Give one area."),
    })?.ToJsonString(JsonOptions) ?? "null";

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => KeyValuePair.Create(pair.Key, Canonical(pair.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Canonical)]),
        _ => node?.DeepClone(),
    };

    /// <summary>The areas in which this and <paramref name="other"/> are different.</summary>
    public SettingsEditorAreas DifferentAreas(SettingsEditorValues other, SettingsEditorAreas within = SettingsEditorAreas.All)
    {
        ArgumentNullException.ThrowIfNull(other);
        var different = SettingsEditorAreas.None;
        foreach (var area in SettingsEditorState.Each(within))
        {
            if (!string.Equals(Fingerprint(area), other.Fingerprint(area), StringComparison.Ordinal))
            {
                different |= area;
            }
        }

        return different;
    }

    /// <summary>A copy with one Settings field changed.</summary>
    public SettingsEditorValues WithField(string key, string value) => this with { Fields = Fields.SetItem(key, value) };

    /// <summary>A copy that takes the given areas from <paramref name="source"/>.</summary>
    public SettingsEditorValues WithAreas(SettingsEditorValues source, SettingsEditorAreas areas)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SettingsEditorValues(
            areas.HasFlag(SettingsEditorAreas.Settings) ? source.Fields : Fields,
            areas.HasFlag(SettingsEditorAreas.Settings) ? source.Shortcuts : Shortcuts,
            areas.HasFlag(SettingsEditorAreas.Scenes) ? source.Scenes : Scenes,
            areas.HasFlag(SettingsEditorAreas.Stations) ? source.Stations : Stations);
    }
}

/// <summary>What to do with a settings change that came from somewhere else.</summary>
/// <param name="Reload">The areas to load again: they changed and have no unsaved edits.</param>
/// <param name="Kept">The areas that changed but have unsaved edits: the edits stay, and the user is told.</param>
public sealed record SettingsReloadPlan(SettingsEditorAreas Reload, SettingsEditorAreas Kept);

/// <summary>
/// Knows which editor areas have unsaved edits, and which areas a settings change from somewhere else (an automation,
/// Volume Memory, the MCP server, a plugin) may load again without losing edits (#76). No WPF.
/// </summary>
/// <remarks>
/// It keeps two baselines per area: the saved values when the area was last loaded, and the editor values just
/// after that load. An area is dirty when the editor values are not the same as the editor baseline. A change from
/// somewhere else touches an area when its saved values are not the same as the saved baseline.
/// </remarks>
public sealed class SettingsEditorState
{
    private SettingsEditorValues? _savedBaseline;
    private SettingsEditorValues? _editorBaseline;

    /// <summary>Areas with unsaved edits that also changed somewhere else since they were loaded.</summary>
    public SettingsEditorAreas ChangedElsewhere { get; private set; }

    /// <summary>The areas in the order the pages show them.</summary>
    public static IEnumerable<SettingsEditorAreas> Each(SettingsEditorAreas areas)
    {
        foreach (var area in (SettingsEditorAreas[])[SettingsEditorAreas.Settings, SettingsEditorAreas.Scenes, SettingsEditorAreas.Stations])
        {
            if (areas.HasFlag(area))
            {
                yield return area;
            }
        }
    }

    /// <summary>
    /// The given areas were just loaded from <paramref name="saved"/> and now show <paramref name="editor"/>.
    /// </summary>
    public void Loaded(SettingsEditorAreas areas, SettingsEditorValues saved, SettingsEditorValues editor)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(editor);
        _savedBaseline = _savedBaseline is null ? saved : _savedBaseline.WithAreas(saved, areas);
        _editorBaseline = _editorBaseline is null ? editor : _editorBaseline.WithAreas(editor, areas);
        ChangedElsewhere &= ~areas;
    }

    /// <summary>The areas whose editor values are not the loaded ones.</summary>
    public SettingsEditorAreas DirtyAreas(SettingsEditorValues editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return _editorBaseline is null ? SettingsEditorAreas.None : editor.DifferentAreas(_editorBaseline);
    }

    /// <summary>
    /// Plans a settings change from somewhere else: load again the areas that changed and are clean, and keep the
    /// edits in the areas that changed and are dirty. The saved baseline of a kept area moves on, so the same change
    /// is not reported twice.
    /// </summary>
    public SettingsReloadPlan Plan(SettingsEditorValues saved, SettingsEditorValues editor)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(editor);
        if (_savedBaseline is null)
        {
            return new SettingsReloadPlan(SettingsEditorAreas.All, SettingsEditorAreas.None);
        }

        var changed = saved.DifferentAreas(_savedBaseline);
        var dirty = DirtyAreas(editor);
        var kept = changed & dirty;
        if (kept != SettingsEditorAreas.None)
        {
            _savedBaseline = _savedBaseline.WithAreas(saved, kept);
            ChangedElsewhere |= kept;
        }

        return new SettingsReloadPlan(changed & ~dirty, kept);
    }

    /// <summary>
    /// A field that saves at once (the dB / number switch, a Cinema offset) was saved with <paramref name="value"/>,
    /// and the editor shows it: it is not an unsaved edit, and it is not a change from somewhere else.
    /// </summary>
    public void FieldSaved(string key, string value)
    {
        _savedBaseline = _savedBaseline?.WithField(key, value);
        _editorBaseline = _editorBaseline?.WithField(key, value);
    }
}
