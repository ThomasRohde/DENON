using System.Collections.ObjectModel;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private ScenePresetEditor? _selectedScene;
    private bool _isLoadingAcoustics;
    private bool _hasLoadedAcousticsOnce;
    private string _acousticsStatusText = "Click 'Sync from AVR' to read live Audyssey, dimmer, and trim levels from your receiver.";
    private bool _dynamicEqEnabled;
    private bool _subwooferTrimEnabled;
    private int _subwooferTrimDb;
    private bool _dialogTrimEnabled;
    private int _dialogTrimDb;
    private bool _toneControlEnabled;
    private int _bassDb;
    private int _trebleDb;

    public ObservableCollection<ScenePresetEditor> ScenePresets { get; } = [];
    public ObservableCollection<AcousticChipItem> MultEqModes { get; } = [];
    public ObservableCollection<AcousticChipItem> DynamicVolumeModes { get; } = [];
    public ObservableCollection<AcousticChipItem> RestorerModes { get; } = [];
    public ObservableCollection<AcousticChipItem> DimmerModes { get; } = [];

    public AsyncRelayCommand ActivateSceneCommand { get; private set; } = null!;
    public RelayCommand SelectSceneCommand { get; private set; } = null!;
    public RelayCommand AddSceneCommand { get; private set; } = null!;
    public RelayCommand DuplicateSceneCommand { get; private set; } = null!;
    public RelayCommand RemoveSceneCommand { get; private set; } = null!;
    public RelayCommand CaptureCurrentStateToSceneCommand { get; private set; } = null!;

    public AsyncRelayCommand RefreshAcousticsCommand { get; private set; } = null!;
    public AsyncRelayCommand SetMultEqCommand { get; private set; } = null!;
    public AsyncRelayCommand ToggleDynamicEqCommand { get; private set; } = null!;
    public AsyncRelayCommand SetDynamicVolumeCommand { get; private set; } = null!;
    public AsyncRelayCommand SetRestorerCommand { get; private set; } = null!;
    public AsyncRelayCommand SetDimmerCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplySubwooferTrimCommand { get; private set; } = null!;
    public AsyncRelayCommand StepSubwooferTrimCommand { get; private set; } = null!;
    public AsyncRelayCommand ToggleSubwooferTrimCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplyDialogTrimCommand { get; private set; } = null!;
    public AsyncRelayCommand StepDialogTrimCommand { get; private set; } = null!;
    public AsyncRelayCommand ToggleDialogTrimCommand { get; private set; } = null!;
    public AsyncRelayCommand ToggleToneControlCommand { get; private set; } = null!;
    public AsyncRelayCommand StepBassCommand { get; private set; } = null!;
    public AsyncRelayCommand StepTrebleCommand { get; private set; } = null!;
    public AsyncRelayCommand ResetAcousticTrimsCommand { get; private set; } = null!;

    private void InitializeScenesAcoustics()
    {
        foreach (var opt in DenonAcousticCatalog.MultEqModes)
        {
            MultEqModes.Add(new AcousticChipItem(opt.Code, opt.DisplayName));
        }

        foreach (var opt in DenonAcousticCatalog.DynamicVolumeModes)
        {
            DynamicVolumeModes.Add(new AcousticChipItem(opt.Code, opt.DisplayName));
        }

        foreach (var opt in DenonAcousticCatalog.RestorerModes)
        {
            RestorerModes.Add(new AcousticChipItem(opt.Code, opt.DisplayName));
        }

        foreach (var opt in DenonAcousticCatalog.DimmerModes)
        {
            DimmerModes.Add(new AcousticChipItem(opt.Code, opt.DisplayName));
        }

        ActivateSceneCommand = new AsyncRelayCommand(ActivateSceneAsync, CanControlReceiver);
        SelectSceneCommand = new RelayCommand(param =>
        {
            if (param is ScenePresetEditor scene)
            {
                SelectedScene = scene;
            }
        });
        AddSceneCommand = new RelayCommand(_ => AddScene());
        DuplicateSceneCommand = new RelayCommand(_ => DuplicateSelectedScene(), _ => SelectedScene is not null);
        RemoveSceneCommand = new RelayCommand(RemoveScene);
        CaptureCurrentStateToSceneCommand = new RelayCommand(_ => CaptureCurrentReceiverStateToNewScene());

        RefreshAcousticsCommand = new AsyncRelayCommand(_ => RefreshAcousticsAsync(), _ => !IsLoadingAcoustics && !IsBusy);
        SetMultEqCommand = new AsyncRelayCommand(SetMultEqAsync, CanControlReceiver);
        ToggleDynamicEqCommand = new AsyncRelayCommand(_ => SetDynamicEqAsync(!DynamicEqEnabled), CanControlReceiver);
        SetDynamicVolumeCommand = new AsyncRelayCommand(SetDynamicVolumeAsync, CanControlReceiver);
        SetRestorerCommand = new AsyncRelayCommand(SetRestorerAsync, CanControlReceiver);
        SetDimmerCommand = new AsyncRelayCommand(SetDimmerAsync, CanControlReceiver);

        ApplySubwooferTrimCommand = new AsyncRelayCommand(_ => SetSubwooferLevelAsync(true, SubwooferTrimDb), CanControlReceiver);
        StepSubwooferTrimCommand = new AsyncRelayCommand(StepSubwooferTrimAsync, CanControlReceiver);
        ToggleSubwooferTrimCommand = new AsyncRelayCommand(_ => SetSubwooferLevelAsync(!SubwooferTrimEnabled, SubwooferTrimDb), CanControlReceiver);

        ApplyDialogTrimCommand = new AsyncRelayCommand(_ => SetDialogLevelAsync(true, DialogTrimDb), CanControlReceiver);
        StepDialogTrimCommand = new AsyncRelayCommand(StepDialogTrimAsync, CanControlReceiver);
        ToggleDialogTrimCommand = new AsyncRelayCommand(_ => SetDialogLevelAsync(!DialogTrimEnabled, DialogTrimDb), CanControlReceiver);

        ToggleToneControlCommand = new AsyncRelayCommand(_ => SetToneControlAsync(!ToneControlEnabled, BassDb, TrebleDb), CanControlReceiver);
        StepBassCommand = new AsyncRelayCommand(StepBassAsync, CanControlReceiver);
        StepTrebleCommand = new AsyncRelayCommand(StepTrebleAsync, CanControlReceiver);
        ResetAcousticTrimsCommand = new AsyncRelayCommand(_ => ResetAcousticTrimsAsync(), CanControlReceiver);
    }

    public ScenePresetEditor? SelectedScene
    {
        get => _selectedScene;
        set
        {
            if (SetProperty(ref _selectedScene, value))
            {
                DuplicateSceneCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsLoadingAcoustics
    {
        get => _isLoadingAcoustics;
        private set
        {
            if (SetProperty(ref _isLoadingAcoustics, value))
            {
                RefreshAcousticsCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    public string AcousticsStatusText
    {
        get => _acousticsStatusText;
        private set => SetProperty(ref _acousticsStatusText, value);
    }

    public bool DynamicEqEnabled
    {
        get => _dynamicEqEnabled;
        private set
        {
            if (SetProperty(ref _dynamicEqEnabled, value))
            {
                OnPropertyChanged(nameof(DynamicEqLabel));
            }
        }
    }

    public string DynamicEqLabel => DynamicEqEnabled ? "Dynamic EQ: ON" : "Dynamic EQ: OFF";

    public bool SubwooferTrimEnabled
    {
        get => _subwooferTrimEnabled;
        private set
        {
            if (SetProperty(ref _subwooferTrimEnabled, value))
            {
                OnPropertyChanged(nameof(SubwooferTrimToggleLabel));
            }
        }
    }

    public string SubwooferTrimToggleLabel => SubwooferTrimEnabled ? "Sub Trim: ON" : "Sub Trim: OFF";

    public int SubwooferTrimDb
    {
        get => _subwooferTrimDb;
        set
        {
            var clamped = Math.Clamp(value, -12, 12);
            if (SetProperty(ref _subwooferTrimDb, clamped))
            {
                OnPropertyChanged(nameof(SubwooferTrimDisplay));
            }
        }
    }

    public string SubwooferTrimDisplay => DenonAcousticCatalog.FormatDbOffset(SubwooferTrimDb);

    public bool DialogTrimEnabled
    {
        get => _dialogTrimEnabled;
        private set
        {
            if (SetProperty(ref _dialogTrimEnabled, value))
            {
                OnPropertyChanged(nameof(DialogTrimToggleLabel));
            }
        }
    }

    public string DialogTrimToggleLabel => DialogTrimEnabled ? "Dialog Boost: ON" : "Dialog Boost: OFF";

    public int DialogTrimDb
    {
        get => _dialogTrimDb;
        set
        {
            var clamped = Math.Clamp(value, -12, 12);
            if (SetProperty(ref _dialogTrimDb, clamped))
            {
                OnPropertyChanged(nameof(DialogTrimDisplay));
            }
        }
    }

    public string DialogTrimDisplay => DenonAcousticCatalog.FormatDbOffset(DialogTrimDb);

    public bool ToneControlEnabled
    {
        get => _toneControlEnabled;
        private set
        {
            if (SetProperty(ref _toneControlEnabled, value))
            {
                OnPropertyChanged(nameof(ToneControlToggleLabel));
            }
        }
    }

    public string ToneControlToggleLabel => ToneControlEnabled ? "Tone Control: ON" : "Tone Control: OFF";

    public int BassDb
    {
        get => _bassDb;
        set
        {
            var clamped = Math.Clamp(value, -6, 6);
            if (SetProperty(ref _bassDb, clamped))
            {
                OnPropertyChanged(nameof(BassDisplay));
            }
        }
    }

    public string BassDisplay => DenonAcousticCatalog.FormatDbOffset(BassDb);

    public int TrebleDb
    {
        get => _trebleDb;
        set
        {
            var clamped = Math.Clamp(value, -6, 6);
            if (SetProperty(ref _trebleDb, clamped))
            {
                OnPropertyChanged(nameof(TrebleDisplay));
            }
        }
    }

    public string TrebleDisplay => DenonAcousticCatalog.FormatDbOffset(TrebleDb);

    private void LoadScenesFromSettings(AppSettings settings)
    {
        var previousSelectedId = SelectedScene?.Id;
        ScenePresets.Clear();
        foreach (var scene in settings.Receiver.Scenes)
        {
            ScenePresets.Add(ScenePresetEditor.FromModel(scene));
        }

        SelectedScene = ScenePresets.FirstOrDefault(s => string.Equals(s.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase))
                        ?? ScenePresets.FirstOrDefault();
    }

    private async Task SyncAcousticsIfNeededAsync()
    {
        if (_hasLoadedAcousticsOnce || IsLoadingAcoustics || !IsConnected)
        {
            return;
        }

        await RefreshAcousticsAsync();
    }

    private async Task RefreshAcousticsAsync()
    {
        IsLoadingAcoustics = true;
        AcousticsStatusText = "Querying Audyssey, Dimmer, and Trim levels from receiver...";

        try
        {
            var status = await _receiverController.GetAcousticStatusAsync();
            _hasLoadedAcousticsOnce = true;
            ApplyAcousticStatus(status);
            AcousticsStatusText = $"Synced from AVR at {status.RetrievedAt.LocalDateTime:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            AcousticsStatusText = $"Could not read acoustics: {ex.Message}";
        }
        finally
        {
            IsLoadingAcoustics = false;
        }
    }

    private void ApplyAcousticStatus(AcousticStatus status)
    {
        foreach (var item in MultEqModes)
        {
            item.IsActive = string.Equals(item.Code, status.MultEq, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var item in DynamicVolumeModes)
        {
            item.IsActive = string.Equals(item.Code, status.DynamicVolume, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var item in RestorerModes)
        {
            item.IsActive = string.Equals(item.Code, status.Restorer, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var item in DimmerModes)
        {
            item.IsActive = string.Equals(item.Code, status.Dimmer, StringComparison.OrdinalIgnoreCase);
        }

        DynamicEqEnabled = status.DynamicEq == true;
        SubwooferTrimEnabled = status.SubwooferAdjustEnabled == true;
        SubwooferTrimDb = status.SubwooferTrimDb;
        DialogTrimEnabled = status.DialogAdjustEnabled == true;
        DialogTrimDb = status.DialogTrimDb;
        ToneControlEnabled = status.ToneControlEnabled == true;
        BassDb = status.BassDb;
        TrebleDb = status.TrebleDb;
    }

    private async Task ActivateSceneAsync(object? parameter)
    {
        if (parameter is not ScenePresetEditor scene)
        {
            return;
        }

        scene.IsActivating = true;
        try
        {
            StatusMessage = $"Activating scene '{scene.Name}'...";
            await _receiverController.ApplySceneAsync(scene.ToModel());
            StatusMessage = $"Scene '{scene.Name}' activated.";

            if (string.Equals(ActiveSection, "Acoustics", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshAcousticsAsync();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = $"Scene '{scene.Name}' failed: {ex.Message}";
        }
        finally
        {
            scene.IsActivating = false;
        }
    }

    private void AddScene()
    {
        var editor = new ScenePresetEditor
        {
            Id = $"scene-{ScenePresets.Count + 1}",
            Name = "New Scene",
            Description = "Custom Denon AVR macro",
            Badge = "NEW",
            AccentHex = "#00D4FF",
            PowerOn = true,
            Unmute = true,
            SmoothVolumeRamp = true,
        };

        ScenePresets.Add(editor);
        SelectedScene = editor;
        StatusMessage = "Added new scene. Customize fields and click Save Scenes.";
    }

    private void DuplicateSelectedScene()
    {
        if (SelectedScene is null)
        {
            return;
        }

        var model = SelectedScene.ToModel();
        model.Id = $"{model.Id}-copy";
        model.Name = $"{model.Name} Copy";
        var copy = ScenePresetEditor.FromModel(model);
        ScenePresets.Add(copy);
        SelectedScene = copy;
        StatusMessage = $"Duplicated '{SelectedScene.Name}'. Click Save Scenes to persist.";
    }

    private void RemoveScene(object? parameter)
    {
        var target = parameter as ScenePresetEditor ?? SelectedScene;
        if (target is null)
        {
            return;
        }

        var name = target.Name;
        ScenePresets.Remove(target);
        SelectedScene = ScenePresets.FirstOrDefault();
        StatusMessage = $"Removed scene '{name}'. Click Save Scenes to persist.";
    }

    private void CaptureCurrentReceiverStateToNewScene()
    {
        var captured = new ScenePresetEditor
        {
            Id = $"snapshot-{DateTime.Now:HHmmss}",
            Name = $"Snapshot ({InputSourceDisplay})",
            Description = $"Captured live from {ReceiverTitle} at {DateTime.Now:HH:mm}.",
            Badge = "LIVE",
            AccentHex = "#38BDF8",
            PowerOn = true,
            Unmute = !IsMuted,
            InputSource = InputSourceCode,
            SoundMode = SoundMode == "—" ? string.Empty : SoundMode,
            TargetVolumeText = LiveVolume.ToString(),
            SmoothVolumeRamp = true,
            EcoMode = EcoMode == "—" ? string.Empty : EcoMode,
            DynamicEqChoice = DynamicEqEnabled ? "ON" : string.Empty,
            DynamicVolume = DynamicVolumeModes.FirstOrDefault(static m => m.IsActive)?.Code ?? string.Empty,
            MultEq = MultEqModes.FirstOrDefault(static m => m.IsActive)?.Code ?? string.Empty,
            Restorer = RestorerModes.FirstOrDefault(static m => m.IsActive)?.Code ?? string.Empty,
            Dimmer = DimmerModes.FirstOrDefault(static m => m.IsActive)?.Code ?? string.Empty,
            SubwooferTrimText = SubwooferTrimEnabled ? SubwooferTrimDb.ToString() : string.Empty,
            DialogTrimText = DialogTrimEnabled ? DialogTrimDb.ToString() : string.Empty,
        };

        ScenePresets.Add(captured);
        SelectedScene = captured;
        StatusMessage = "Captured current AVR settings as a new Scene! Rename and click Save Scenes.";
    }

    private async Task SetMultEqAsync(object? parameter)
    {
        var code = parameter is AcousticChipItem item ? item.Code : parameter as string;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        await ExecuteAcousticControlAsync(() => _receiverController.SetMultEqAsync(code), $"Audyssey MultEQ set to {code}.");
    }

    private Task SetDynamicEqAsync(bool enabled) =>
        ExecuteAcousticControlAsync(() => _receiverController.SetDynamicEqAsync(enabled), enabled ? "Audyssey Dynamic EQ enabled." : "Audyssey Dynamic EQ disabled.");

    private async Task SetDynamicVolumeAsync(object? parameter)
    {
        var code = parameter is AcousticChipItem item ? item.Code : parameter as string;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        await ExecuteAcousticControlAsync(() => _receiverController.SetDynamicVolumeAsync(code), $"Audyssey Dynamic Volume set to {code}.");
    }

    private async Task SetRestorerAsync(object? parameter)
    {
        var code = parameter is AcousticChipItem item ? item.Code : parameter as string;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        await ExecuteAcousticControlAsync(() => _receiverController.SetRestorerAsync(code), $"Audio Restorer set to {code}.");
    }

    private async Task SetDimmerAsync(object? parameter)
    {
        var code = parameter is AcousticChipItem item ? item.Code : parameter as string;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        await ExecuteAcousticControlAsync(() => _receiverController.SetDimmerAsync(code), $"Front panel dimmer set to {code}.");
    }

    private Task SetSubwooferLevelAsync(bool enabled, int db) =>
        ExecuteAcousticControlAsync(
            () => _receiverController.SetSubwooferLevelAsync(enabled, db),
            enabled ? $"Subwoofer trim set to {DenonAcousticCatalog.FormatDbOffset(db)}." : "Subwoofer trim disabled.");

    private Task StepSubwooferTrimAsync(object? parameter)
    {
        if (!int.TryParse(parameter?.ToString(), out var delta))
        {
            return Task.CompletedTask;
        }

        SubwooferTrimDb = Math.Clamp(SubwooferTrimDb + delta, -12, 12);
        return SetSubwooferLevelAsync(true, SubwooferTrimDb);
    }

    private Task SetDialogLevelAsync(bool enabled, int db) =>
        ExecuteAcousticControlAsync(
            () => _receiverController.SetDialogLevelAsync(enabled, db),
            enabled ? $"Dialog enhancer set to {DenonAcousticCatalog.FormatDbOffset(db)}." : "Dialog enhancer disabled.");

    private Task StepDialogTrimAsync(object? parameter)
    {
        if (!int.TryParse(parameter?.ToString(), out var delta))
        {
            return Task.CompletedTask;
        }

        DialogTrimDb = Math.Clamp(DialogTrimDb + delta, -12, 12);
        return SetDialogLevelAsync(true, DialogTrimDb);
    }

    private Task SetToneControlAsync(bool enabled, int bassDb, int trebleDb) =>
        ExecuteAcousticControlAsync(
            () => _receiverController.SetToneControlAsync(enabled, bassDb, trebleDb),
            enabled ? $"Tone control updated (Bass {DenonAcousticCatalog.FormatDbOffset(bassDb)}, Treble {DenonAcousticCatalog.FormatDbOffset(trebleDb)})." : "Tone control disabled.");

    private Task StepBassAsync(object? parameter)
    {
        if (!int.TryParse(parameter?.ToString(), out var delta))
        {
            return Task.CompletedTask;
        }

        BassDb = Math.Clamp(BassDb + delta, -6, 6);
        return SetToneControlAsync(true, BassDb, TrebleDb);
    }

    private Task StepTrebleAsync(object? parameter)
    {
        if (!int.TryParse(parameter?.ToString(), out var delta))
        {
            return Task.CompletedTask;
        }

        TrebleDb = Math.Clamp(TrebleDb + delta, -6, 6);
        return SetToneControlAsync(true, BassDb, TrebleDb);
    }

    private async Task ResetAcousticTrimsAsync()
    {
        SubwooferTrimDb = 0;
        DialogTrimDb = 0;
        BassDb = 0;
        TrebleDb = 0;
        await ExecuteAcousticControlAsync(async () =>
        {
            await _receiverController.SetSubwooferLevelAsync(true, 0);
            await _receiverController.SetDialogLevelAsync(true, 0);
            return await _receiverController.SetToneControlAsync(true, 0, 0);
        }, "Reset Sub, Dialog, Bass & Treble trims to 0 dB.");
    }

    private async Task ExecuteAcousticControlAsync(Func<Task<AcousticStatus>> action, string successMessage)
    {
        try
        {
            var status = await action();
            _hasLoadedAcousticsOnce = true;
            ApplyAcousticStatus(status);
            AcousticsStatusText = successMessage;
            StatusMessage = successMessage;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AcousticsStatusText = $"Acoustic command failed: {ex.Message}";
            StatusMessage = ex.Message;
        }
    }
}
