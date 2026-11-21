using System.Collections.ObjectModel;
using System.Windows.Input;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Services;

namespace Denon.App.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private GlobalHotkeyService _globalHotkeyService = null!;
    private ShortcutItemEditor? _recordingShortcut;
    private string _shortcutSummaryText = "Click 'Record' on any shortcut, press your key combo, then Save Settings.";
    private bool _hasShortcutCollisions;

    public ObservableCollection<ShortcutItemEditor> ShortcutEditors { get; } = [];

    public RelayCommand ToggleRecordShortcutCommand { get; private set; } = null!;
    public RelayCommand ClearShortcutCommand { get; private set; } = null!;
    public RelayCommand CheckShortcutCollisionsCommand { get; private set; } = null!;
    public RelayCommand ResetShortcutsToDefaultCommand { get; private set; } = null!;

    private void InitializeShortcuts(GlobalHotkeyService globalHotkeyService)
    {
        _globalHotkeyService = globalHotkeyService;

        ToggleRecordShortcutCommand = new RelayCommand(ToggleRecordShortcut);
        ClearShortcutCommand = new RelayCommand(ClearShortcut);
        CheckShortcutCollisionsCommand = new RelayCommand(_ => ValidateShortcutCollisions(reportClean: true));
        ResetShortcutsToDefaultCommand = new RelayCommand(_ => ResetShortcutsToDefaults());
    }

    public bool IsRecordingAnyShortcut => _recordingShortcut is not null;

    public string ShortcutSummaryText
    {
        get => _shortcutSummaryText;
        private set => SetProperty(ref _shortcutSummaryText, value);
    }

    public bool HasShortcutCollisions
    {
        get => _hasShortcutCollisions;
        private set => SetProperty(ref _hasShortcutCollisions, value);
    }

    public bool TryCaptureRecordedShortcut(Key key, ModifierKeys modifiers)
    {
        if (_recordingShortcut is null)
        {
            return false;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            CancelRecordingShortcut();
            ShortcutSummaryText = "Shortcut recording canceled.";
            return true;
        }

        if (key == Key.Back && modifiers == ModifierKeys.None)
        {
            _recordingShortcut.Gesture = string.Empty;
            CancelRecordingShortcut();
            ValidateShortcutCollisions(reportClean: true);
            return true;
        }

        if (!GlobalHotkeyService.TryFormatCapturedKey(key, modifiers, out var canonical))
        {
            // Still waiting for the non-modifier key (e.g., user pressed Ctrl alone)
            return true;
        }

        var target = _recordingShortcut;
        target.Gesture = canonical;
        CancelRecordingShortcut();
        ValidateShortcutCollisions(reportClean: true);
        return true;
    }

    public bool TryExecuteInAppShortcut(Key key, ModifierKeys modifiers)
    {
        if (IsRecordingAnyShortcut)
        {
            return false;
        }

        var bindings = _settingsStore.Current.Shortcuts.Bindings;
        foreach (var binding in bindings.Where(static b => !b.IsGlobal && !string.IsNullOrWhiteSpace(b.Gesture)))
        {
            if (!GlobalHotkeyService.TryParseGesture(binding.Gesture, out var parsed) || parsed is null)
            {
                continue;
            }

            if (parsed.Key == key && parsed.Modifiers == modifiers)
            {
                ExecuteShortcutAction(binding.ActionId);
                return true;
            }
        }

        return false;
    }

    public void ExecuteShortcutAction(string actionId)
    {
        switch (actionId)
        {
            case ShortcutActionIds.AppPlayPause or ShortcutActionIds.GlobalPlayPause:
                if (PlayPauseCommand.CanExecute(null)) PlayPauseCommand.Execute(null);
                break;
            case ShortcutActionIds.AppMute or ShortcutActionIds.GlobalMute:
                if (ToggleMuteCommand.CanExecute(null)) ToggleMuteCommand.Execute(null);
                break;
            case ShortcutActionIds.AppVolumeUp or ShortcutActionIds.GlobalVolumeUp:
                AdjustVolumeByWheel(2);
                break;
            case ShortcutActionIds.AppVolumeDown or ShortcutActionIds.GlobalVolumeDown:
                AdjustVolumeByWheel(-2);
                break;
            case ShortcutActionIds.GlobalNextTrack:
                if (NextTrackCommand.CanExecute(null)) NextTrackCommand.Execute(null);
                break;
            case ShortcutActionIds.GlobalPreviousTrack:
                if (PreviousTrackCommand.CanExecute(null)) PreviousTrackCommand.Execute(null);
                break;
            case ShortcutActionIds.AppRefresh:
                if (RefreshCommand.CanExecute(null)) RefreshCommand.Execute(null);
                break;
        }
    }

    private void LoadShortcutsFromSettings(AppSettings settings)
    {
        CancelRecordingShortcut();
        ShortcutEditors.Clear();
        foreach (var binding in settings.Shortcuts.Bindings)
        {
            ShortcutEditors.Add(ShortcutItemEditor.FromModel(binding));
        }

        var regStates = _globalHotkeyService.ApplyGlobalBindings(settings.Shortcuts.Bindings);
        ValidateShortcutCollisions(reportClean: false, registrationStates: regStates);
    }

    private void ToggleRecordShortcut(object? parameter)
    {
        if (parameter is not ShortcutItemEditor editor)
        {
            return;
        }

        if (ReferenceEquals(_recordingShortcut, editor))
        {
            CancelRecordingShortcut();
            ValidateShortcutCollisions(reportClean: false);
            return;
        }

        CancelRecordingShortcut();
        _recordingShortcut = editor;
        editor.IsRecording = true;
        ShortcutSummaryText = $"Listening for '{editor.Label}' — press your desired key combination (Esc to cancel, Backspace to clear)...";
    }

    private void CancelRecordingShortcut()
    {
        if (_recordingShortcut is not null)
        {
            _recordingShortcut.IsRecording = false;
            _recordingShortcut = null;
        }
    }

    private void ClearShortcut(object? parameter)
    {
        if (parameter is not ShortcutItemEditor editor)
        {
            return;
        }

        if (ReferenceEquals(_recordingShortcut, editor))
        {
            CancelRecordingShortcut();
        }

        editor.Gesture = string.Empty;
        ValidateShortcutCollisions(reportClean: true);
    }

    private void ResetShortcutsToDefaults()
    {
        CancelRecordingShortcut();
        ShortcutEditors.Clear();
        foreach (var def in ShortcutSettings.CreateDefaults())
        {
            ShortcutEditors.Add(ShortcutItemEditor.FromModel(def));
        }

        ValidateShortcutCollisions(reportClean: true);
        StatusMessage = "Default shortcuts loaded into editor. Click 'Save & Sync' to apply.";
    }

    private bool ValidateShortcutCollisions(
        bool reportClean,
        IReadOnlyDictionary<string, HotkeyRegistrationState>? registrationStates = null)
    {
        // Map normalized gesture -> list of editors using it
        var globalGroups = new Dictionary<string, List<ShortcutItemEditor>>(StringComparer.OrdinalIgnoreCase);
        var appGroups = new Dictionary<string, List<ShortcutItemEditor>>(StringComparer.OrdinalIgnoreCase);
        var parsedByEditor = new Dictionary<ShortcutItemEditor, ParsedShortcutGesture?>();

        foreach (var editor in ShortcutEditors)
        {
            if (string.IsNullOrWhiteSpace(editor.Gesture))
            {
                parsedByEditor[editor] = null;
                continue;
            }

            if (GlobalHotkeyService.TryParseGesture(editor.Gesture, out var parsed) && parsed is not null)
            {
                editor.Gesture = parsed.CanonicalText;
                parsedByEditor[editor] = parsed;

                var map = editor.IsGlobal ? globalGroups : appGroups;
                if (!map.TryGetValue(parsed.CanonicalText, out var list))
                {
                    list = [];
                    map[parsed.CanonicalText] = list;
                }

                list.Add(editor);
            }
            else
            {
                parsedByEditor[editor] = null;
            }
        }

        var errorCount = 0;
        var warningCount = 0;

        foreach (var editor in ShortcutEditors)
        {
            if (string.IsNullOrWhiteSpace(editor.Gesture))
            {
                editor.SetValidationResult(false, false, null, "OFF");
                continue;
            }

            var parsed = parsedByEditor[editor];
            if (parsed is null)
            {
                editor.SetValidationResult(true, false, "Unrecognized shortcut format.", "INVALID");
                errorCount++;
                continue;
            }

            // 1. Check exact duplicate in same scope (Global vs Global, or In-App vs In-App)
            var sameScopeMap = editor.IsGlobal ? globalGroups : appGroups;
            var sameScopePeers = sameScopeMap[parsed.CanonicalText].Where(other => !ReferenceEquals(other, editor)).ToList();
            if (sameScopePeers.Count > 0)
            {
                var names = string.Join(", ", sameScopePeers.Select(static p => $"\"{p.Label}\""));
                editor.SetValidationResult(true, false, $"Collision: already assigned to {names}.", "CONFLICT");
                errorCount++;
                continue;
            }

            // 2. Check reserved Windows / navigation shortcuts
            var reserved = GlobalHotkeyService.GetReservedWarning(parsed.CanonicalText, editor.IsGlobal);
            if (!string.IsNullOrWhiteSpace(reserved))
            {
                editor.SetValidationResult(true, false, reserved, "RESERVED");
                errorCount++;
                continue;
            }

            // 3. Check live Win32 OS hotkey collision for Global shortcuts
            if (editor.IsGlobal)
            {
                var osTaken = registrationStates is not null &&
                              registrationStates.TryGetValue(editor.ActionId, out var regState)
                    ? regState == HotkeyRegistrationState.OsCollision
                    : _globalHotkeyService.IsTakenByAnotherApp(parsed.CanonicalText, editor.ActionId);

                if (osTaken)
                {
                    editor.SetValidationResult(
                        true,
                        false,
                        $"Collision: '{parsed.CanonicalText}' is already claimed by Windows or another running app.",
                        "OS TAKEN");
                    errorCount++;
                    continue;
                }
            }

            // 4. Cross-scope overlap warning (Global hotkey shadows In-App shortcut)
            var otherScopeMap = editor.IsGlobal ? appGroups : globalGroups;
            if (otherScopeMap.TryGetValue(parsed.CanonicalText, out var crossPeers) && crossPeers.Count > 0)
            {
                var names = string.Join(", ", crossPeers.Select(static p => $"\"{p.Label}\""));
                editor.SetValidationResult(false, true, $"Overlaps with {names} in the other scope (Global takes precedence).", "OVERLAP");
                warningCount++;
                continue;
            }

            editor.SetValidationResult(false, false, null, "OK");
        }

        HasShortcutCollisions = errorCount > 0;
        if (errorCount > 0)
        {
            ShortcutSummaryText = $"Found {errorCount} shortcut collision(s). Resolve highlighted conflicts before saving.";
        }
        else if (warningCount > 0)
        {
            ShortcutSummaryText = $"No blocking collisions ({warningCount} cross-scope overlap note). Click 'Save & Sync' to apply.";
        }
        else if (reportClean)
        {
            ShortcutSummaryText = "All shortcuts checked — zero internal or Windows OS collisions detected!";
        }

        return errorCount == 0;
    }
}
