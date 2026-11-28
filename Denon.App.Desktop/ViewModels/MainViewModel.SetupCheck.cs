using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Setup;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

/// <summary>One row of the receiver setup check: what the receiver has, what the profile recommends, and why.</summary>
public sealed class SetupRowItem : ObservableObject
{
    private bool _isSelected;

    public SetupRowItem(SetupRow row)
    {
        Row = row;
        // The safe rows start ticked; a row that changes how the sound feels waits for the user's tick.
        _isSelected = row.CanApply && row.Setting.Tier == SettingTier.Apply;
    }

    public SetupRow Row { get; }

    public string Id => Row.Id;

    public string Label => Row.Setting.Label;

    public string Group => Row.Setting.Group.ToUpperInvariant();

    public string CurrentText => Row.CurrentText;

    public string RecommendedText => Row.RecommendedText;

    public string Reason => Row.Reason;

    public string ScopeText => Row.Setting.Scope switch
    {
        SettingScope.PerInput => "this input",
        SettingScope.PerSoundMode => "this sound mode",
        _ => string.Empty,
    };

    public bool HasScope => ScopeText.Length > 0;

    /// <summary>"Differs", "OK" and so on, in the words of the badge.</summary>
    public string StateText => Row.State switch
    {
        SetupRowState.Ok => "OK",
        SetupRowState.Differs => Row.Setting.Tier == SettingTier.Confirm ? "DIFFERS · ASK" : "DIFFERS",
        SetupRowState.Warning => "WARNING",
        SetupRowState.CannotRead => "CANNOT READ",
        SetupRowState.BlockedByMode => "BLOCKED BY MODE",
        SetupRowState.BlockedByRequirement => "NEEDS ANOTHER",
        SetupRowState.NoRecommendation => "NO ADVICE",
        _ => "ADVICE",
    };

    /// <summary>ok, differs, warn, error, blocked or info: the badge picks its colour from it.</summary>
    public string StateKind => Row.State switch
    {
        SetupRowState.Ok => "ok",
        SetupRowState.Differs => "differs",
        SetupRowState.Warning => "warn",
        SetupRowState.CannotRead => "error",
        SetupRowState.BlockedByMode or SetupRowState.BlockedByRequirement => "blocked",
        _ => "info",
    };

    /// <summary>Only a row the app may set shows a tick box.</summary>
    public bool CanApply => Row.CanApply;

    public bool IsAdvice => Row.Setting.Tier == SettingTier.Advice;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, Row.CanApply && value);
    }

    public string AccessibleName =>
        $"{Label}, {StateText}, now {CurrentText}" + (Row.CanApply ? $", recommended {RecommendedText}" : string.Empty);
}

/// <summary>A listening use chip (Films, Music, Late night, TV).</summary>
public sealed class SetupUseItem(ListeningUse use) : ObservableObject
{
    private bool _isActive;

    public ListeningUse Use { get; } = use;

    public string Label { get; } = ListeningUses.Label(use);

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

/// <summary>
/// Scenes &amp; Acoustics → Setup check: reads the receiver against a profile of recommended settings, lists the
/// differences, and applies the ones the user ticks (with the old values saved, and an Undo).
/// </summary>
public sealed partial class MainViewModel
{
    private ReceiverSetupService? _setup;
    private SetupCheckResult? _setupCheck;
    private ListeningUse _setupUse = ListeningUse.Films;
    private bool _isSetupBusy;
    private string _setupStatusText = "Choose what you listen to, then check the receiver. A check only reads.";
    private string _setupNoticeText = string.Empty;
    private string _setupProfileText = string.Empty;
    private string _setupProblemsText = string.Empty;
    private string _setupUndoText = string.Empty;
    private bool _canUndoSetup;
    private ReceiverProfile? _setupProfile;

    public ObservableCollection<SetupRowItem> SetupRows { get; } = [];

    public ObservableCollection<SetupUseItem> SetupUses { get; } = [];

    /// <summary>Asks the user before an apply. A test or a headless run can replace it.</summary>
    public Func<string, bool> ConfirmSetupApply { get; set; } = static message =>
        System.Windows.MessageBox.Show(message, "Apply recommended settings", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public AsyncRelayCommand RunSetupCheckCommand { get; private set; } = null!;

    public AsyncRelayCommand ApplySetupCommand { get; private set; } = null!;

    public AsyncRelayCommand UndoSetupCommand { get; private set; } = null!;

    public RelayCommand SelectSetupUseCommand { get; private set; } = null!;

    public RelayCommand ReloadSetupProfilesCommand { get; private set; } = null!;

    public RelayCommand OpenSetupProfilesFolderCommand { get; private set; } = null!;

    public bool IsSetupBusy
    {
        get => _isSetupBusy;
        private set
        {
            if (SetProperty(ref _isSetupBusy, value))
            {
                NotifySetupCommands();
            }
        }
    }

    public string SetupStatusText
    {
        get => _setupStatusText;
        private set => SetProperty(ref _setupStatusText, value);
    }

    /// <summary>A warning about the whole check (standby, the input changed). Empty when there is none.</summary>
    public string SetupNoticeText
    {
        get => _setupNoticeText;
        private set
        {
            if (SetProperty(ref _setupNoticeText, value))
            {
                OnPropertyChanged(nameof(HasSetupNotice));
            }
        }
    }

    public bool HasSetupNotice => SetupNoticeText.Length > 0;

    /// <summary>"Denon AVR-X2400H · profile Denon FY17 X-series (bundled)", or why there is none.</summary>
    public string SetupProfileText
    {
        get => _setupProfileText;
        private set => SetProperty(ref _setupProfileText, value);
    }

    public string SetupProblemsText
    {
        get => _setupProblemsText;
        private set
        {
            if (SetProperty(ref _setupProblemsText, value))
            {
                OnPropertyChanged(nameof(HasSetupProblems));
            }
        }
    }

    public bool HasSetupProblems => SetupProblemsText.Length > 0;

    public bool CanUndoSetup
    {
        get => _canUndoSetup;
        private set
        {
            if (SetProperty(ref _canUndoSetup, value))
            {
                UndoSetupCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    public string SetupUndoText
    {
        get => _setupUndoText;
        private set => SetProperty(ref _setupUndoText, value);
    }

    public bool HasSetupRows => SetupRows.Count > 0;

    public string SetupProfilesFolder => AppDataPaths.ProfilesDirectory;

    /// <summary>Re-reads whether the setup commands may run. Called when the receiver's busy state changes too.</summary>
    private void NotifySetupCommands()
    {
        RunSetupCheckCommand?.NotifyCanExecuteChanged();
        SelectSetupUseCommand?.NotifyCanExecuteChanged();
        ApplySetupCommand?.NotifyCanExecuteChanged();
        UndoSetupCommand?.NotifyCanExecuteChanged();
    }

    private void InitializeSetupCheck(ReceiverSetupService setup)
    {
        _setup = setup;
        foreach (var use in Enum.GetValues<ListeningUse>())
        {
            SetupUses.Add(new SetupUseItem(use) { IsActive = use == _setupUse });
        }

        RunSetupCheckCommand = new AsyncRelayCommand(_ => RunSetupCheckAsync(), _ => !IsSetupBusy && !IsBusy);
        ApplySetupCommand = new AsyncRelayCommand(_ => ApplySetupAsync(), _ => !IsSetupBusy && !IsBusy && SetupRows.Any(static r => r.IsSelected));
        UndoSetupCommand = new AsyncRelayCommand(_ => UndoSetupAsync(), _ => !IsSetupBusy && !IsBusy && CanUndoSetup);
        SelectSetupUseCommand = new RelayCommand(param =>
        {
            if (param is ListeningUse use)
            {
                SelectSetupUse(use);
            }
        }, _ => !IsSetupBusy);
        ReloadSetupProfilesCommand = new RelayCommand(_ => ReloadSetupProfiles());
        OpenSetupProfilesFolderCommand = new RelayCommand(_ => OpenFolder(_setup!.ProfilesDirectory));

        ReloadSetupProfiles();
        RefreshSetupUndo();
    }

    /// <summary>Opens Scenes &amp; Acoustics on the Setup check tab. Reads nothing from the receiver.</summary>
    public void OpenReceiverSetupCheck()
    {
        ActiveSection = "Acoustics";
        AcousticsSubTab = "Setup";
        RefreshSetupProfileText();
    }

    private void SelectSetupUse(ListeningUse use)
    {
        _setupUse = use;
        foreach (var item in SetupUses)
        {
            item.IsActive = item.Use == use;
        }

        // The rows belong to the use they were checked for: drop them, so nothing stale can be applied. A new check only reads.
        if (_setupCheck is not null && _setupCheck.Use != use)
        {
            _setupCheck = null;
            SetupRows.Clear();
            OnPropertyChanged(nameof(HasSetupRows));
            SetupNoticeText = string.Empty;
            ApplySetupCommand.NotifyCanExecuteChanged();
            SetupStatusText = $"Use changed to {ListeningUses.Label(use).ToLowerInvariant()}. Press Check receiver.";
        }
    }

    private void ReloadSetupProfiles()
    {
        if (_setup is null)
        {
            return;
        }

        var library = _setup.Reload();
        SetupProblemsText = library.Problems.Count == 0
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                library.Problems.Select(static p => $"{Path.GetFileName(p.Origin)}: {string.Join(" ", p.Errors.Take(3))}"));
        RefreshSetupProfileText();
    }

    private void RefreshSetupProfileText()
    {
        if (_setup is null)
        {
            return;
        }

        var model = _setup.CurrentModel;
        _setupProfile = _setup.FindProfileForCurrentModel();
        SetupProfileText = string.IsNullOrWhiteSpace(model)
            ? "The receiver's model is not known yet. Connect to the receiver first."
            : _setupProfile is null
                ? $"{model}: no profile covers this model. Add a profile file to the profiles folder."
                : $"{model} · profile {_setupProfile.Name} ({(_setupProfile.IsBundled ? "bundled" : "your file")})";
    }

    private async Task RunSetupCheckAsync()
    {
        if (_setup is null)
        {
            return;
        }

        RefreshSetupProfileText();
        if (_setupProfile is null)
        {
            SetupStatusText = SetupProfileText;
            return;
        }

        IsSetupBusy = true;
        SetupStatusText = "Reading the receiver. Nothing is changed...";
        try
        {
            var check = await _setup.CheckAsync(_setupProfile, _setupUse, CancellationToken.None);
            ShowSetupCheck(check);
        }
        catch (Exception ex)
        {
            SetupStatusText = $"The check could not read the receiver: {ex.Message}";
        }
        finally
        {
            IsSetupBusy = false;
        }
    }

    private void ShowSetupCheck(SetupCheckResult check)
    {
        // A check that finished after the use was changed belongs to the old use: never show or apply it.
        if (check.Use != _setupUse)
        {
            return;
        }

        _setupCheck = check;
        SetupRows.Clear();
        foreach (var row in check.Rows)
        {
            var item = new SetupRowItem(row);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SetupRowItem.IsSelected))
                {
                    ApplySetupCommand.NotifyCanExecuteChanged();
                }
            };
            SetupRows.Add(item);
        }

        OnPropertyChanged(nameof(HasSetupRows));
        SetupNoticeText = check.Notice ?? string.Empty;
        var input = string.IsNullOrWhiteSpace(check.Context.InputCode) ? "the current input" : $"input {check.Context.InputCode}";
        SetupStatusText = check.DifferenceCount == 0
            ? $"Checked {check.CheckedAt:HH:mm:ss} for {ListeningUses.Label(check.Use).ToLowerInvariant()} on {input}: nothing differs."
            : $"Checked {check.CheckedAt:HH:mm:ss} for {ListeningUses.Label(check.Use).ToLowerInvariant()} on {input}: " +
              $"{check.DifferenceCount} differ" + (check.WarningCount > 0 ? $", {check.WarningCount} to look at." : ".");
        ApplySetupCommand.NotifyCanExecuteChanged();
    }

    private async Task ApplySetupAsync()
    {
        if (_setup is null || _setupCheck is null)
        {
            return;
        }

        var selected = SetupRows.Where(static r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var scopeNote = _setupCheck.Context.InputCode.Length > 0
            ? $"Settings for an input change for {_setupCheck.Context.InputCode} only; the app does not switch input."
            : string.Empty;
        var lines = selected.Select(static r => $"  {r.Label}: {r.CurrentText}  →  {r.Row.Setting.FindValue(r.Row.RecommendedToken)?.Display ?? r.RecommendedText}");
        var message = $"Change {selected.Count} setting{(selected.Count == 1 ? string.Empty : "s")} on the receiver?\n\n" +
                      string.Join('\n', lines) + "\n\n" +
                      "The old values are saved first, and Undo puts them back. " + scopeNote;
        if (!ConfirmSetupApply(message))
        {
            SetupStatusText = "Nothing was changed.";
            return;
        }

        IsSetupBusy = true;
        SetupStatusText = "Applying...";
        try
        {
            var result = await _setup.ApplyAsync(_setupCheck, selected.Select(static r => r.Id).ToList(), CancellationToken.None);
            SetupStatusText = result.Summarize("Applied");
            RefreshSetupUndo();

            // Read again, so the list shows what the receiver has now.
            if (_setupProfile is not null)
            {
                ShowSetupCheck(await _setup.CheckAsync(_setupProfile, _setupUse, CancellationToken.None));
                SetupStatusText = result.Summarize("Applied") + " " + SetupStatusText;
            }
        }
        catch (Exception ex)
        {
            SetupStatusText = $"The apply stopped: {ex.Message}";
            RefreshSetupUndo();
        }
        finally
        {
            IsSetupBusy = false;
        }
    }

    private async Task UndoSetupAsync()
    {
        if (_setup is null)
        {
            return;
        }

        IsSetupBusy = true;
        SetupStatusText = "Putting the old values back...";
        try
        {
            var result = await _setup.UndoAsync(CancellationToken.None);
            SetupStatusText = result.Summarize("Restored");
            RefreshSetupUndo();
            if (_setupProfile is not null && _setupCheck is not null)
            {
                ShowSetupCheck(await _setup.CheckAsync(_setupProfile, _setupUse, CancellationToken.None));
                SetupStatusText = result.Summarize("Restored") + " " + SetupStatusText;
            }
        }
        catch (Exception ex)
        {
            SetupStatusText = $"Undo stopped: {ex.Message}";
        }
        finally
        {
            IsSetupBusy = false;
        }
    }

    private void RefreshSetupUndo()
    {
        var snapshot = _setup?.LatestSnapshot;
        CanUndoSetup = snapshot is not null;
        SetupUndoText = snapshot is null
            ? string.Empty
            : $"Last apply {snapshot.CreatedAt:dd MMM HH:mm}: {snapshot.Entries.Count(static e => e.Written)} setting(s) can be put back.";
    }
}

public sealed partial class MainViewModel
{
    /// <summary>
    /// Fills the Setup check tab with a made-up check for the README screenshots: a made-up receiver answers, nothing
    /// is read from the network. Called only in screenshot mode.
    /// </summary>
    internal void LoadShowcaseSetupCheck()
    {
        if (_setup?.Library.FindForModel("Denon AVR-X2400H") is not { } profile)
        {
            return;
        }

        _setupProfile = profile;
        SetupProfileText = $"Denon AVR-X2400H · profile {profile.Name} (bundled)";
        var link = new ShowcaseSetupLink(profile);
        var check = SetupCheckEngine.CheckAsync(profile, ListeningUse.Films, link, CancellationToken.None).GetAwaiter().GetResult();
        ShowSetupCheck(check);
    }

    /// <summary>A receiver that answers from a table: reads echo the write template with the stored value.</summary>
    private sealed class ShowcaseSetupLink(ReceiverProfile profile) : IReceiverSetupLink
    {
        private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
        {
            ["multEq"] = "AUDYSSEY", ["dynamicEq"] = "ON", ["refLevel"] = "10", ["dynamicVolume"] = "OFF", ["cinemaEq"] = "ON",
            ["restorer"] = "OFF", ["tone"] = "ON", ["drc"] = "AUTO", ["loudnessManagement"] = "ON", ["lfeLevel"] = "00", ["eco"] = "AUTO",
        };

        public Task<SetupContext> GetContextAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SetupContext("Denon AVR-X2400H", "TV", "STEREO", true, true));

        public Task<IReadOnlyList<string>> QueryAsync(AvrSetupCommand command, Func<string, bool> answerLine, CancellationToken cancellationToken)
        {
            var setting = profile.Settings.First(s => s.Id == command.SettingId);
            IReadOnlyList<string> lines = setting.Id switch
            {
                "subTrim" => ["PSSWL ON", "PSSWL 62"],
                "soundMode" => ["MSSTEREO"],
                "audioDelay" => ["PSDELAY 000"],
                _ when Values.TryGetValue(setting.Id, out var token) && setting.Write is { } write =>
                    [write.Replace(AvrCommandGate.ValuePlaceholder, token, StringComparison.Ordinal)],
                _ => [],
            };
            return Task.FromResult(lines);
        }

        public Task SendAsync(AvrSetupCommand command, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
