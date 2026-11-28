using Denon.App.Core.Services;

namespace Denon.App.Core.Setup;

/// <summary>
/// The receiver setup check as the app uses it: the profile library, the model → profile choice, and check, apply
/// and undo run against the real receiver through <see cref="ReceiverController"/>.
/// </summary>
public sealed class ReceiverSetupService
{
    private readonly ReceiverController _controller;
    private readonly ReceiverStateStore _stateStore;
    private readonly string _profilesDirectory;
    private readonly SetupSnapshotStore _snapshots;
    private ReceiverProfileLibrary _library;

    public ReceiverSetupService(
        ReceiverController controller,
        ReceiverStateStore stateStore,
        string profilesDirectory,
        SetupSnapshotStore snapshots)
    {
        _controller = controller;
        _stateStore = stateStore;
        _profilesDirectory = profilesDirectory;
        _snapshots = snapshots;
        _library = ReceiverProfileLibrary.Load(profilesDirectory);
    }

    public string ProfilesDirectory => _profilesDirectory;

    public ReceiverProfileLibrary Library => _library;

    /// <summary>The model HEOS reported at the last status read, or an empty string before the first read.</summary>
    public string CurrentModel => _stateStore.Snapshot.Status?.Player.Model ?? string.Empty;

    /// <summary>Reads the profiles folder again (after the user added or edited a file).</summary>
    public ReceiverProfileLibrary Reload() => _library = ReceiverProfileLibrary.Load(_profilesDirectory);

    /// <summary>The profile for the connected model, or null when none covers it.</summary>
    public ReceiverProfile? FindProfileForCurrentModel() => _library.FindForModel(CurrentModel);

    public SetupSnapshot? LatestSnapshot => _snapshots.Load();

    public Task<SetupCheckResult> CheckAsync(ReceiverProfile profile, ListeningUse use, CancellationToken cancellationToken) =>
        _controller.RunSetupSessionAsync(
            "Receiver setup check",
            link => SetupCheckEngine.CheckAsync(ReceiverProfileLibrary.Resolve(profile, CurrentModel), use, link, cancellationToken),
            cancellationToken);

    public Task<SetupApplyResult> ApplyAsync(SetupCheckResult check, IReadOnlyCollection<string> settingIds, CancellationToken cancellationToken) =>
        _controller.RunSetupSessionAsync(
            "Receiver setup apply",
            link => SetupCheckEngine.ApplyAsync(check, settingIds, link, _snapshots, cancellationToken),
            cancellationToken);

    /// <summary>Puts the values of the last apply back. Keeps the snapshot while some rows could not be undone.</summary>
    public async Task<SetupApplyResult> UndoAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshots.Load();
        if (snapshot is null)
        {
            return new SetupApplyResult([new SetupOutcome("undo", "Undo", SetupOutcomeKind.Skipped, "There is nothing to undo.")], null);
        }

        var profile = _library.FindById(snapshot.ProfileId);
        if (profile is null)
        {
            return new SetupApplyResult(
                [new SetupOutcome("undo", "Undo", SetupOutcomeKind.Skipped, $"The profile '{snapshot.ProfileId}' is not loaded any more.")],
                snapshot);
        }

        var resolved = ReceiverProfileLibrary.Resolve(profile, snapshot.Model);
        var result = await _controller.RunSetupSessionAsync(
            "Receiver setup undo",
            link => SetupCheckEngine.UndoAsync(snapshot, resolved, link, cancellationToken),
            cancellationToken);

        if (result.Snapshot is { HasWrittenEntries: true } remaining)
        {
            _snapshots.Save(remaining);
        }
        else
        {
            _snapshots.Clear();
        }

        return result;
    }
}
