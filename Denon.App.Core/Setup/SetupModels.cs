using System.Globalization;

namespace Denon.App.Core.Setup;

/// <summary>What a check found for one setting.</summary>
public enum SetupRowState
{
    /// <summary>The receiver already has a recommended value.</summary>
    Ok,

    /// <summary>The receiver has another value than the recommended one.</summary>
    Differs,

    /// <summary>A read-only rule found something worth a look (for example a trim at its limit).</summary>
    Warning,

    /// <summary>The receiver gave no usable answer, so nothing can be compared or safely changed.</summary>
    CannotRead,

    /// <summary>The active sound mode does not use this setting (Direct and Pure Direct skip Audyssey).</summary>
    BlockedByMode,

    /// <summary>Another setting has to have a value first (Reference Level Offset needs Dynamic EQ on).</summary>
    BlockedByRequirement,

    /// <summary>The profile has no recommended value for the chosen use.</summary>
    NoRecommendation,

    /// <summary>The protocol cannot set this: the profile only gives advice (or shows the value it can read).</summary>
    AdviceOnly,
}

/// <summary>What the receiver is doing now. Per-input and per-sound-mode settings are read and written for exactly this.</summary>
public sealed record SetupContext(
    string Model,
    string InputCode,
    string SoundMode,
    bool IsPoweredOn,
    bool IsMainZoneOn)
{
    public bool IsAwake => IsPoweredOn && IsMainZoneOn;
}

/// <summary>One line of a check.</summary>
public sealed record SetupRow(
    ProfileSetting Setting,
    SetupRowState State,
    string? CurrentToken,
    string CurrentText,
    string? RecommendedToken,
    string RecommendedText,
    string Note,
    bool CanApply)
{
    public string Id => Setting.Id;

    /// <summary>What the row says to the user: the state-specific note, else the profile's reason.</summary>
    public string Reason => string.IsNullOrWhiteSpace(Note) ? Setting.Why : Note;

    /// <summary>True when the receiver has a value other than the recommended one and the app may set it.</summary>
    public bool IsDifference => State == SetupRowState.Differs;
}

/// <summary>The result of reading the receiver against a profile. Nothing was changed.</summary>
public sealed record SetupCheckResult(
    ReceiverProfile Profile,
    ListeningUse Use,
    SetupContext Context,
    IReadOnlyList<SetupRow> Rows,
    DateTimeOffset CheckedAt,
    string? Notice = null)
{
    public int DifferenceCount => Rows.Count(static r => r.State == SetupRowState.Differs);

    public int WarningCount => Rows.Count(static r => r.State == SetupRowState.Warning);

    public SetupRow? Find(string id) => Rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
}

public enum SetupOutcomeKind
{
    /// <summary>Written and read back with the new value.</summary>
    Applied,

    /// <summary>Written, but the receiver did not answer the read back, so it is not known to have taken it.</summary>
    Unconfirmed,

    /// <summary>Written, and the receiver still has the old value.</summary>
    NotTaken,

    /// <summary>Not written: a rule stopped it (the input changed, a requirement is not met).</summary>
    Skipped,

    /// <summary>The write failed (a connection error).</summary>
    Failed,
}

public sealed record SetupOutcome(string SettingId, string Label, SetupOutcomeKind Kind, string Message);

/// <summary>One setting an apply set (or meant to set): the old value is what an undo writes back.</summary>
public sealed record SnapshotEntry(
    string SettingId,
    string Label,
    SettingScope Scope,
    string InputCode,
    string SoundMode,
    string OldToken,
    string NewToken,
    bool Written);

/// <summary>The old values of one apply. Saved before the first write, so an undo works after a crash too.</summary>
public sealed record SetupSnapshot(
    string ProfileId,
    string Model,
    ListeningUse Use,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SnapshotEntry> Entries)
{
    public bool HasWrittenEntries => Entries.Any(static e => e.Written);
}

public sealed record SetupApplyResult(IReadOnlyList<SetupOutcome> Outcomes, SetupSnapshot? Snapshot)
{
    public int AppliedCount => Outcomes.Count(static o => o.Kind is SetupOutcomeKind.Applied or SetupOutcomeKind.Unconfirmed);

    public bool HasProblems => Outcomes.Any(static o => o.Kind is SetupOutcomeKind.NotTaken or SetupOutcomeKind.Failed or SetupOutcomeKind.Skipped);
}

/// <summary>How a value reads on screen.</summary>
public static class SetupValueText
{
    public static string Format(ProfileSetting setting, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "-";
        }

        if (setting.FindValue(token) is { } known)
        {
            return known.Display;
        }

        if (setting.Decode == "offset50" && int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            var signed = number > 0 ? $"+{number}" : number.ToString(CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(setting.Unit) ? signed : $"{signed} {setting.Unit}";
        }

        return string.IsNullOrEmpty(setting.Unit) ? token : $"{token} {setting.Unit}";
    }

    /// <summary>"Reference or Light" for a recommendation with more than one accepted value.</summary>
    public static string FormatRecommendation(ProfileSetting setting, RecommendedValue value) =>
        string.Join(" or ", value.Tokens.Select(t => Format(setting, t)));
}
