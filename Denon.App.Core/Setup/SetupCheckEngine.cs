using System.Globalization;
using System.Text.RegularExpressions;

namespace Denon.App.Core.Setup;

/// <summary>
/// What the engine needs from the receiver. The real one sends only commands that came out of
/// <see cref="AvrCommandGate"/>; tests give it a fake, so no test ever touches a device.
/// </summary>
public interface IReceiverSetupLink
{
    /// <summary>The model, the selected input, the sound mode and the power state.</summary>
    Task<SetupContext> GetContextAsync(CancellationToken cancellationToken);

    /// <summary>Sends a read and returns the lines that came back. Ends as soon as a line satisfies <paramref name="answerLine"/>.</summary>
    Task<IReadOnlyList<string>> QueryAsync(AvrSetupCommand command, Func<string, bool> answerLine, CancellationToken cancellationToken);

    /// <summary>Sends a write.</summary>
    Task SendAsync(AvrSetupCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the receiver against a profile, and applies or undoes the settings the user picks.
/// Design points: a check only reads; per-input and per-sound-mode settings are read and written for the input and
/// sound mode that are active now (switching input would interrupt what plays), and an apply or undo stops for
/// a row when the input or mode has changed since; the old values are saved before the first write.
/// </summary>
public static class SetupCheckEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static async Task<SetupCheckResult> CheckAsync(
        ReceiverProfile profile,
        ListeningUse use,
        IReceiverSetupLink link,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(link);

        var context = await link.GetContextAsync(cancellationToken);
        var rows = new List<SetupRow>();
        string? notice = null;

        if (!context.IsAwake)
        {
            notice = "The receiver is in standby. Turn it on, then check again: it does not answer sound settings in standby.";
        }

        foreach (var setting in profile.Settings)
        {
            var row = context.IsAwake
                ? await CheckSettingAsync(setting, use, context, rows, link, cancellationToken)
                : StandbyRow(setting, use);
            rows.Add(row);
        }

        // The input or the sound mode may have changed while the rows were read; then the rows do not belong together.
        if (context.IsAwake)
        {
            var after = await TryGetContextAsync(link, cancellationToken);
            if (after is not null &&
                (!SameText(after.InputCode, context.InputCode) || !SameText(after.SoundMode, context.SoundMode)))
            {
                notice = "The input or the sound mode changed while the receiver was read. Check again before you apply anything.";
                rows = rows.Select(static r => r with { CanApply = false }).ToList();
            }
        }

        return new SetupCheckResult(profile, use, context, rows, DateTimeOffset.Now, notice);
    }

    private static SetupRow StandbyRow(ProfileSetting setting, ListeningUse use)
    {
        if (setting.Tier == SettingTier.Advice && !setting.IsReadable)
        {
            return AdviceRow(setting);
        }

        return Row(setting, SetupRowState.CannotRead, null, use, "The receiver is in standby.");
    }

    private static async Task<SetupRow> CheckSettingAsync(
        ProfileSetting setting,
        ListeningUse use,
        SetupContext context,
        IReadOnlyList<SetupRow> earlier,
        IReceiverSetupLink link,
        CancellationToken cancellationToken)
    {
        if (setting.Tier == SettingTier.Advice && !setting.IsReadable)
        {
            return AdviceRow(setting);
        }

        if (IsBlockedByMode(setting, context.SoundMode))
        {
            return Row(setting, SetupRowState.BlockedByMode, null, use,
                $"Not used in {FriendlyMode(context.SoundMode)}. Switch to Stereo or Auto to use it.");
        }

        var requirement = CheckRequirements(setting, earlier, use);
        if (requirement.Blocked is { } blockedBy)
        {
            return Row(setting, SetupRowState.BlockedByRequirement, null, use, blockedBy);
        }

        var token = await ReadTokenAsync(setting, link, cancellationToken);
        if (token is null)
        {
            // Not an answer. With a requirement that is only planned, the receiver may not answer until it is met.
            return requirement.Pending is { } pending
                ? Row(setting, SetupRowState.BlockedByRequirement, null, use, $"{pending} Check again after that.")
                : Row(setting, SetupRowState.CannotRead, null, use,
                    "The receiver gave no answer. This model may not have the setting, or it is not available now.");
        }

        return Evaluate(setting, token, use, requirement.Pending);
    }

    /// <summary>The row for a value that was read. Pure: no receiver involved.</summary>
    public static SetupRow Evaluate(ProfileSetting setting, string token, ListeningUse use, string? pendingRequirement = null)
    {
        var currentText = SetupValueText.Format(setting, token);

        if (setting.Tier == SettingTier.Advice)
        {
            return EvaluateAdvice(setting, token, currentText);
        }

        if (setting.FindValue(token) is null)
        {
            return Row(setting, SetupRowState.CannotRead, token, use,
                $"The receiver answered '{token}', which this profile does not list, so it is left alone.", currentText);
        }

        var recommendation = setting.RecommendationFor(use);
        if (recommendation is null)
        {
            return new SetupRow(setting, SetupRowState.NoRecommendation, token, currentText, null, "-",
                $"No recommendation for {ListeningUses.Label(use).ToLowerInvariant()}.", false);
        }

        var recommendedText = SetupValueText.FormatRecommendation(setting, recommendation);
        if (recommendation.Accepts(token))
        {
            return new SetupRow(setting, SetupRowState.Ok, token, currentText, recommendation.Preferred, recommendedText, string.Empty, false);
        }

        var note = pendingRequirement is null ? string.Empty : $"{pendingRequirement} {setting.Why}";
        return new SetupRow(setting, SetupRowState.Differs, token, currentText, recommendation.Preferred, recommendedText, note, setting.IsWritable);
    }

    private static SetupRow EvaluateAdvice(ProfileSetting setting, string token, string currentText)
    {
        if (setting.WarnWhen is { } warn && IsWarning(setting, warn, token))
        {
            return new SetupRow(setting, SetupRowState.Warning, token, currentText, null, "-", warn.Message, false);
        }

        return new SetupRow(setting, SetupRowState.AdviceOnly, token, currentText, null, "-",
            string.IsNullOrWhiteSpace(setting.Advice) ? setting.Why : setting.Advice, false);
    }

    private static bool IsWarning(ProfileSetting setting, WarnWhen warn, string token)
    {
        if (warn.AtLimits is { } limits &&
            int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
            (number <= limits.Min || number >= limits.Max))
        {
            return true;
        }

        return warn.Values?.Any(v => string.Equals(v, token, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static SetupRow AdviceRow(ProfileSetting setting) =>
        new(setting, SetupRowState.AdviceOnly, null, "-", null, "-",
            string.IsNullOrWhiteSpace(setting.Advice) ? setting.Why : setting.Advice, false);

    private static SetupRow Row(ProfileSetting setting, SetupRowState state, string? token, ListeningUse use, string note, string? currentText = null)
    {
        var recommendation = setting.Tier == SettingTier.Advice ? null : setting.RecommendationFor(use);
        return new SetupRow(
            setting,
            state,
            token,
            currentText ?? SetupValueText.Format(setting, token),
            recommendation?.Preferred,
            recommendation is null ? "-" : SetupValueText.FormatRecommendation(setting, recommendation),
            note,
            CanApply: false);
    }

    private static bool IsBlockedByMode(ProfileSetting setting, string soundMode) =>
        setting.BlockedWhen?.SoundModes?.Any(m => SameText(m, soundMode)) == true;

    private static string FriendlyMode(string soundMode) =>
        string.IsNullOrWhiteSpace(soundMode) ? "this sound mode" : $"the {soundMode.Trim()} sound mode";

    /// <summary>
    /// Settings this one needs. <c>Blocked</c> is set when one cannot be met; <c>Pending</c> when it is not met now but
    /// would be after the recommended change to that setting.
    /// </summary>
    private static (string? Blocked, string? Pending) CheckRequirements(ProfileSetting setting, IReadOnlyList<SetupRow> earlier, ListeningUse use)
    {
        string? pending = null;
        foreach (var (requiredId, requiredToken) in setting.Requires ?? new Dictionary<string, string>())
        {
            var row = earlier.FirstOrDefault(r => r.Id == requiredId);
            var label = row?.Setting.Label ?? requiredId;
            var wanted = row?.Setting.FindValue(requiredToken)?.Display ?? requiredToken;

            if (row is null)
            {
                return ($"Needs {label} {wanted}, which this receiver does not have.", null);
            }

            if (SameText(row.CurrentToken, requiredToken))
            {
                continue;
            }

            if (row.State == SetupRowState.Differs && SameText(row.RecommendedToken, requiredToken) && row.CanApply)
            {
                pending = $"Needs {label} {wanted}: apply that first.";
                continue;
            }

            return ($"Needs {label} {wanted}, and the check does not set it.", null);
        }

        return (null, pending);
    }

    private static async Task<string?> ReadTokenAsync(ProfileSetting setting, IReceiverSetupLink link, CancellationToken cancellationToken)
    {
        var command = AvrCommandGate.BuildRead(setting);
        var regex = new Regex(setting.ReadParse!, RegexOptions.CultureInvariant, RegexTimeout);
        try
        {
            var lines = await link.QueryAsync(command, line => regex.IsMatch(line.Trim()), cancellationToken);
            return ParseReading(setting, lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The setting's token in the first reply line that matches its pattern, or null when none does.</summary>
    public static string? ParseReading(ProfileSetting setting, IEnumerable<string> lines)
    {
        if (!setting.IsReadable)
        {
            return null;
        }

        var regex = new Regex(setting.ReadParse!, RegexOptions.CultureInvariant, RegexTimeout);
        foreach (var line in lines)
        {
            var match = regex.Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var raw = (match.Groups["value"].Success ? match.Groups["value"].Value : match.Groups[1].Value).Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            if (setting.Decode == "offset50")
            {
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var encoded))
                {
                    continue;
                }

                return (encoded - 50).ToString(CultureInfo.InvariantCulture);
            }

            return setting.FindValue(raw)?.Token ?? raw;
        }

        return null;
    }

    public static async Task<SetupApplyResult> ApplyAsync(
        SetupCheckResult check,
        IReadOnlyCollection<string> settingIds,
        IReceiverSetupLink link,
        SetupSnapshotStore? snapshots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(settingIds);
        ArgumentNullException.ThrowIfNull(link);

        var outcomes = new List<SetupOutcome>();
        var entries = new List<SnapshotEntry>();
        var requested = settingIds.ToHashSet(StringComparer.Ordinal);
        var rows = check.Profile.Settings
            .Where(s => requested.Contains(s.Id))
            .Select(s => check.Find(s.Id))
            .OfType<SetupRow>()
            .ToList();

        foreach (var id in requested.Where(id => rows.All(r => r.Id != id)))
        {
            outcomes.Add(new SetupOutcome(id, id, SetupOutcomeKind.Skipped, "This setting is not in the check."));
        }

        SetupContext context;
        try
        {
            context = await link.GetContextAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Stopped(rows, outcomes, entries, check, $"The receiver did not answer: {ex.Message}");
        }

        if (!context.IsAwake)
        {
            return Stopped(rows, outcomes, entries, check, "The receiver is in standby.");
        }

        // What each setting holds now, as far as this apply knows. A write updates it, so a later row can rely on it.
        var live = check.Rows.ToDictionary(static r => r.Id, static r => r.CurrentToken, StringComparer.Ordinal);
        var stop = false;

        foreach (var row in rows)
        {
            var setting = row.Setting;
            if (stop)
            {
                outcomes.Add(new SetupOutcome(row.Id, setting.Label, SetupOutcomeKind.Skipped, "Not written: an earlier write failed."));
                continue;
            }

            if (!row.CanApply || row.RecommendedToken is null)
            {
                outcomes.Add(new SetupOutcome(row.Id, setting.Label, SetupOutcomeKind.Skipped, "The app cannot set this one."));
                continue;
            }

            if (ScopeChanged(setting, check.Context, context) is { } scopeMessage)
            {
                outcomes.Add(new SetupOutcome(row.Id, setting.Label, SetupOutcomeKind.Skipped, scopeMessage));
                continue;
            }

            if (IsBlockedByMode(setting, context.SoundMode))
            {
                outcomes.Add(new SetupOutcome(row.Id, setting.Label, SetupOutcomeKind.Skipped,
                    $"Not used in {FriendlyMode(context.SoundMode)}."));
                continue;
            }

            if (UnmetRequirement(setting, check.Profile, live) is { } unmet)
            {
                outcomes.Add(new SetupOutcome(row.Id, setting.Label, SetupOutcomeKind.Skipped, unmet));
                continue;
            }

            var entry = new SnapshotEntry(
                setting.Id, setting.Label, setting.Scope, context.InputCode, context.SoundMode,
                row.CurrentToken ?? string.Empty, row.RecommendedToken, Written: true);

            try
            {
                var write = AvrCommandGate.BuildWrite(setting, row.RecommendedToken);

                // The old value is on disk before the receiver is touched.
                entries.Add(entry);
                SaveSnapshot(snapshots, check, context, entries);

                await link.SendAsync(write, cancellationToken);
                var after = await ReadTokenAsync(setting, link, cancellationToken);
                var kind = ClassifyReadBack(after, row.RecommendedToken);
                live[setting.Id] = kind == SetupOutcomeKind.NotTaken ? row.CurrentToken : row.RecommendedToken;

                if (kind == SetupOutcomeKind.NotTaken)
                {
                    entries[^1] = entry with { Written = false };
                    SaveSnapshot(snapshots, check, context, entries);
                }

                outcomes.Add(new SetupOutcome(row.Id, setting.Label, kind, kind switch
                {
                    SetupOutcomeKind.Applied => $"Set to {SetupValueText.Format(setting, row.RecommendedToken)}.",
                    SetupOutcomeKind.Unconfirmed => "Sent, but the receiver did not confirm the new value.",
                    _ => $"The receiver kept {SetupValueText.Format(setting, row.CurrentToken)}.",
                }));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new SetupOutcome(row.Id, setting.Label, SetupOutcomeKind.Failed, ex.Message));
                stop = true;
            }
        }

        return new SetupApplyResult(outcomes, entries.Count == 0 ? null : MakeSnapshot(check, context, entries));
    }

    public static async Task<SetupApplyResult> UndoAsync(
        SetupSnapshot snapshot,
        ReceiverProfile profile,
        IReceiverSetupLink link,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(link);

        var outcomes = new List<SetupOutcome>();
        var remaining = snapshot.Entries.ToList();

        SetupContext context;
        try
        {
            context = await link.GetContextAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SetupApplyResult(
                [new SetupOutcome("undo", "Undo", SetupOutcomeKind.Failed, $"The receiver did not answer: {ex.Message}")],
                snapshot);
        }

        if (!context.IsAwake)
        {
            return new SetupApplyResult(
                [new SetupOutcome("undo", "Undo", SetupOutcomeKind.Skipped, "The receiver is in standby.")],
                snapshot);
        }

        var stop = false;
        for (var i = snapshot.Entries.Count - 1; i >= 0; i--)
        {
            var entry = snapshot.Entries[i];
            if (!entry.Written)
            {
                continue;
            }

            var setting = profile.Settings.FirstOrDefault(s => s.Id == entry.SettingId);
            if (setting is null)
            {
                outcomes.Add(new SetupOutcome(entry.SettingId, entry.Label, SetupOutcomeKind.Skipped, "The profile no longer has this setting."));
                continue;
            }

            if (stop)
            {
                outcomes.Add(new SetupOutcome(entry.SettingId, entry.Label, SetupOutcomeKind.Skipped, "Not written: an earlier write failed."));
                continue;
            }

            var scopeMessage = entry.Scope switch
            {
                SettingScope.PerInput when !SameText(entry.InputCode, context.InputCode) =>
                    $"Switch back to input {entry.InputCode} to undo this one (it is on {context.InputCode} now).",
                SettingScope.PerSoundMode when !SameText(entry.SoundMode, context.SoundMode) =>
                    $"Switch back to the {entry.SoundMode} sound mode to undo this one.",
                _ => null,
            };
            if (scopeMessage is not null)
            {
                outcomes.Add(new SetupOutcome(entry.SettingId, entry.Label, SetupOutcomeKind.Skipped, scopeMessage));
                continue;
            }

            try
            {
                var write = AvrCommandGate.BuildWrite(setting, entry.OldToken);
                await link.SendAsync(write, cancellationToken);
                var after = await ReadTokenAsync(setting, link, cancellationToken);
                var kind = ClassifyReadBack(after, entry.OldToken);
                if (kind != SetupOutcomeKind.NotTaken)
                {
                    remaining[i] = entry with { Written = false };
                }

                outcomes.Add(new SetupOutcome(entry.SettingId, entry.Label, kind, kind switch
                {
                    SetupOutcomeKind.Applied => $"Back to {SetupValueText.Format(setting, entry.OldToken)}.",
                    SetupOutcomeKind.Unconfirmed => "Sent, but the receiver did not confirm the old value.",
                    _ => $"The receiver kept {SetupValueText.Format(setting, entry.NewToken)}.",
                }));
            }
            catch (SetupCommandRefusedException ex)
            {
                outcomes.Add(new SetupOutcome(entry.SettingId, entry.Label, SetupOutcomeKind.Skipped, ex.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new SetupOutcome(entry.SettingId, entry.Label, SetupOutcomeKind.Failed, ex.Message));
                stop = true;
            }
        }

        return new SetupApplyResult(outcomes, snapshot with { Entries = remaining });
    }

    private static SetupOutcomeKind ClassifyReadBack(string? after, string wanted)
    {
        if (after is null)
        {
            return SetupOutcomeKind.Unconfirmed;
        }

        if (SameText(after, wanted))
        {
            return SetupOutcomeKind.Applied;
        }

        return SetupOutcomeKind.NotTaken;
    }

    /// <summary>Why a per-input or per-sound-mode row cannot be written now, or null. Global rows never stop here.</summary>
    private static string? ScopeChanged(ProfileSetting setting, SetupContext checkedContext, SetupContext now) => setting.Scope switch
    {
        SettingScope.PerInput when !SameText(checkedContext.InputCode, now.InputCode) =>
            $"The input changed from {checkedContext.InputCode} to {now.InputCode} since the check. Check again.",
        SettingScope.PerSoundMode when !SameText(checkedContext.SoundMode, now.SoundMode) =>
            $"The sound mode changed from {checkedContext.SoundMode} to {now.SoundMode} since the check. Check again.",
        _ => null,
    };

    private static string? UnmetRequirement(ProfileSetting setting, ReceiverProfile profile, IReadOnlyDictionary<string, string?> live)
    {
        foreach (var (requiredId, requiredToken) in setting.Requires ?? new Dictionary<string, string>())
        {
            if (live.TryGetValue(requiredId, out var current) && SameText(current, requiredToken))
            {
                continue;
            }

            var required = profile.Settings.FirstOrDefault(s => s.Id == requiredId);
            var wanted = required?.FindValue(requiredToken)?.Display ?? requiredToken;
            return $"Needs {required?.Label ?? requiredId} {wanted}, which it is not.";
        }

        return null;
    }

    private static SetupApplyResult Stopped(
        IEnumerable<SetupRow> rows,
        List<SetupOutcome> outcomes,
        List<SnapshotEntry> entries,
        SetupCheckResult check,
        string reason)
    {
        outcomes.AddRange(rows.Select(r => new SetupOutcome(r.Id, r.Setting.Label, SetupOutcomeKind.Skipped, reason)));
        return new SetupApplyResult(outcomes, entries.Count == 0 ? null : MakeSnapshot(check, check.Context, entries));
    }

    private static SetupSnapshot MakeSnapshot(SetupCheckResult check, SetupContext context, IReadOnlyList<SnapshotEntry> entries) =>
        new(check.Profile.Id, context.Model, check.Use, DateTimeOffset.Now, entries.ToList());

    private static void SaveSnapshot(SetupSnapshotStore? store, SetupCheckResult check, SetupContext context, IReadOnlyList<SnapshotEntry> entries) =>
        store?.Save(MakeSnapshot(check, context, entries));

    private static async Task<SetupContext?> TryGetContextAsync(IReceiverSetupLink link, CancellationToken cancellationToken)
    {
        try
        {
            return await link.GetContextAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool SameText(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
