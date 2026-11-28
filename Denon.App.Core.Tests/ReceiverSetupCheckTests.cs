using Denon.App.Core.Setup;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>Receiver setup check: comparison logic, the check engine, apply and undo, per-input scope. Fakes only.</summary>
public sealed class ReceiverSetupCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "denon-setup-check-" + Guid.NewGuid().ToString("N"));
    private readonly ReceiverProfile _profile = SetupTestProfiles.X2400H();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ProfileSetting Setting(string id) => _profile.Settings.Single(s => s.Id == id);

    private SetupSnapshotStore Store() => new(_dir);

    // ---- Comparison logic (pure) --------------------------------------------------------------------------------

    [Theory]
    [InlineData("refLevel", "0", ListeningUse.Films, SetupRowState.Ok)]
    [InlineData("refLevel", "10", ListeningUse.Films, SetupRowState.Differs)]
    [InlineData("refLevel", "10", ListeningUse.Music, SetupRowState.Ok)]
    [InlineData("refLevel", "0", ListeningUse.Tv, SetupRowState.Differs)]
    [InlineData("dynamicEq", "OFF", ListeningUse.Films, SetupRowState.Differs)]
    [InlineData("cinemaEq", "OFF", ListeningUse.Films, SetupRowState.Ok)]
    [InlineData("eco", "ON", ListeningUse.Films, SetupRowState.Differs)]
    public void A_value_is_ok_or_differs_from_the_recommendation_for_the_use(string id, string token, ListeningUse use, SetupRowState expected)
    {
        var row = SetupCheckEngine.Evaluate(Setting(id), token, use);

        Assert.Equal(expected, row.State);
        Assert.Equal(expected == SetupRowState.Differs, row.CanApply);
    }

    [Theory]
    [InlineData("000", "0 ms")] // the X2400H answers "PSDELAY 000"
    [InlineData("040", "40 ms")]
    [InlineData("200", "200 ms")]
    public void A_zero_padded_number_reads_as_a_plain_number(string token, string expected) =>
        Assert.Equal(expected, SetupValueText.Format(Setting("audioDelay"), token));

    [Fact]
    public void A_use_with_two_accepted_values_is_ok_for_either_and_applies_the_first()
    {
        var dynamicVolume = Setting("dynamicVolume");

        Assert.Equal(SetupRowState.Ok, SetupCheckEngine.Evaluate(dynamicVolume, "LIT", ListeningUse.LateNight).State);
        Assert.Equal(SetupRowState.Ok, SetupCheckEngine.Evaluate(dynamicVolume, "MED", ListeningUse.LateNight).State);

        var heavy = SetupCheckEngine.Evaluate(dynamicVolume, "HEV", ListeningUse.LateNight);
        Assert.Equal(SetupRowState.Differs, heavy.State);
        Assert.Equal("LIT", heavy.RecommendedToken);
        Assert.Equal("Light or Medium", heavy.RecommendedText);
        Assert.Equal("Heavy", heavy.CurrentText);
    }

    [Fact]
    public void A_use_without_a_recommendation_is_left_alone()
    {
        var row = SetupCheckEngine.Evaluate(Setting("lfeLevel"), "05", ListeningUse.Music);

        Assert.Equal(SetupRowState.NoRecommendation, row.State);
        Assert.False(row.CanApply);
        Assert.Equal("-5 dB", row.CurrentText);
    }

    [Fact]
    public void An_answer_the_profile_does_not_list_is_not_compared_or_changed()
    {
        var row = SetupCheckEngine.Evaluate(Setting("restorer"), "MODE1", ListeningUse.Films);

        Assert.Equal(SetupRowState.CannotRead, row.State);
        Assert.False(row.CanApply);
        Assert.Contains("MODE1", row.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PSSWL 38", "-12", SetupRowState.Warning)]
    [InlineData("PSSWL 62", "12", SetupRowState.Warning)]
    [InlineData("PSSWL 50", "0", SetupRowState.AdviceOnly)]
    [InlineData("PSSWL 39", "-11", SetupRowState.AdviceOnly)]
    [InlineData("PSSWL 61", "11", SetupRowState.AdviceOnly)]
    [InlineData("PSSWL 505", "0", SetupRowState.AdviceOnly)]
    public void The_subwoofer_trim_only_warns_at_the_ends_of_its_range(string line, string expectedToken, SetupRowState expectedState)
    {
        var trim = Setting("subTrim");

        var token = SetupCheckEngine.ParseReading(trim, ["PSSWL ON", line]);
        var row = SetupCheckEngine.Evaluate(trim, token!, ListeningUse.Films);

        Assert.Equal(expectedToken, token);
        Assert.Equal(expectedState, row.State);
        Assert.False(row.CanApply);
        Assert.Null(row.RecommendedToken);
    }

    [Fact]
    public void The_trim_is_shown_in_decibels_with_a_sign()
    {
        Assert.Equal("-12 dB", SetupCheckEngine.Evaluate(Setting("subTrim"), "-12", ListeningUse.Films).CurrentText);
        Assert.Equal("+3 dB", SetupCheckEngine.Evaluate(Setting("subTrim"), "3", ListeningUse.Films).CurrentText);
        Assert.Equal("0 dB", SetupCheckEngine.Evaluate(Setting("subTrim"), "0", ListeningUse.Films).CurrentText);
    }

    [Theory]
    [InlineData("MSDIRECT", SetupRowState.Warning)]
    [InlineData("MSPURE DIRECT", SetupRowState.Warning)]
    [InlineData("MSSTEREO", SetupRowState.AdviceOnly)]
    [InlineData("MSDOLBY DIGITAL", SetupRowState.AdviceOnly)]
    public void Direct_and_pure_direct_sound_modes_get_a_warning(string line, SetupRowState expected)
    {
        var mode = Setting("soundMode");

        var token = SetupCheckEngine.ParseReading(mode, [line]);

        Assert.Equal(expected, SetupCheckEngine.Evaluate(mode, token!, ListeningUse.Music).State);
    }

    [Fact]
    public void Reading_a_reply_picks_the_matching_line_and_the_canonical_token()
    {
        var multEq = Setting("multEq");

        Assert.Equal("AUDYSSEY", SetupCheckEngine.ParseReading(multEq, ["PSMULTEQ:audyssey"]));
        Assert.Equal("BYP.LR", SetupCheckEngine.ParseReading(multEq, ["SITV", "PSMULTEQ:BYP.LR", "PSMULTEQ:FLAT"]));
        Assert.Null(SetupCheckEngine.ParseReading(multEq, ["SITV", "MSSTEREO"]));
        Assert.Null(SetupCheckEngine.ParseReading(multEq, []));
        Assert.Equal("OFF", SetupCheckEngine.ParseReading(Setting("eco"), ["ECOOFF"]));
        Assert.Equal("ON", SetupCheckEngine.ParseReading(Setting("cinemaEq"), ["PSCINEMA EQ.ON"]));
        Assert.Equal("00", SetupCheckEngine.ParseReading(Setting("lfeLevel"), ["PSLFE 00"]));
        Assert.Null(SetupCheckEngine.ParseReading(Setting("speakerSize"), ["anything"]));
    }

    // ---- The check -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_check_reads_every_setting_and_shows_the_differences_for_films()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);

        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        Assert.Null(check.Notice);
        Assert.Equal(_profile.Settings.Count, check.Rows.Count);
        Assert.Equal(SetupRowState.Differs, check.Find("multEq")!.State);
        Assert.Equal("AUDYSSEY", check.Find("multEq")!.RecommendedToken);
        Assert.Equal("Flat", check.Find("multEq")!.CurrentText);
        Assert.Equal("Reference", check.Find("multEq")!.RecommendedText);
        Assert.Equal(SetupRowState.Differs, check.Find("dynamicEq")!.State);
        Assert.Equal("0 dB (film reference)", check.Find("refLevel")!.RecommendedText);
        Assert.Equal(SetupRowState.Differs, check.Find("lfeLevel")!.State);
        Assert.Equal(SetupRowState.AdviceOnly, check.Find("subTrim")!.State);
        Assert.Equal(SetupRowState.AdviceOnly, check.Find("speakerSize")!.State);
        Assert.True(check.DifferenceCount >= 10);
    }

    [Fact]
    public async Task A_check_only_reads_and_never_sends_a_setup_command_or_a_blocked_one()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);

        await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        Assert.Empty(link.Writes);
        Assert.NotEmpty(link.Reads);
        Assert.All(link.Sent, c => Assert.True(c.IsRead));
        Assert.DoesNotContain(link.Sent, c => c.Text.StartsWith("SS", StringComparison.Ordinal));
        Assert.DoesNotContain(link.Sent, c => AvrCommandGate.BlockedPrefixes.Any(p => c.Text.StartsWith(p, StringComparison.Ordinal)));
        // Advice rows with no read command are never asked about.
        Assert.Equal(link.Reads.Count(), link.Reads.Distinct().Count());
    }

    [Fact]
    public async Task A_check_follows_the_chosen_use()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        link.Set("refLevel", "10");
        link.Set("drc", "OFF");

        var films = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        var music = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Music, link, CancellationToken.None);

        Assert.Equal(SetupRowState.Differs, films.Find("refLevel")!.State);
        Assert.Equal(SetupRowState.Ok, music.Find("refLevel")!.State);
        Assert.Equal(SetupRowState.Ok, films.Find("drc")!.State);
        Assert.Equal(SetupRowState.NoRecommendation, music.Find("lfeLevel")!.State);
    }

    [Theory]
    [InlineData("DIRECT")]
    [InlineData("PURE DIRECT")]
    public async Task Settings_blocked_in_direct_modes_are_not_read_and_not_offered(string mode)
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        link.SoundMode = mode;

        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        foreach (var id in new[] { "multEq", "dynamicEq", "refLevel", "dynamicVolume", "cinemaEq" })
        {
            var row = check.Find(id)!;
            Assert.Equal(SetupRowState.BlockedByMode, row.State);
            Assert.False(row.CanApply);
            Assert.Contains(mode, row.Reason, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("PSDYNEQ ?", link.Reads);
        Assert.DoesNotContain("PSREFLEV ?", link.Reads);
        Assert.Equal(SetupRowState.Differs, check.Find("eco")!.State); // Not tied to the mode.
        Assert.Equal(SetupRowState.Warning, check.Find("soundMode")!.State);
    }

    [Fact]
    public async Task A_setting_that_needs_another_one_says_so_when_the_check_will_set_it()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile); // Dynamic EQ is off.

        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        var refLevel = check.Find("refLevel")!;
        Assert.Equal(SetupRowState.Differs, refLevel.State);
        Assert.True(refLevel.CanApply);
        Assert.Contains("Dynamic EQ", refLevel.Reason, StringComparison.Ordinal);
        Assert.Contains("apply that first", refLevel.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_setting_that_needs_another_one_is_blocked_when_the_other_does_not_answer_or_cannot_be_set()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        link.Silent.Add("refLevel"); // The receiver does not answer it while Dynamic EQ is off.

        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        Assert.Equal(SetupRowState.BlockedByRequirement, check.Find("refLevel")!.State);
        Assert.False(check.Find("refLevel")!.CanApply);

        // A profile that wants Dynamic EQ off: Reference Level Offset cannot work, and it is not even read.
        var offProfile = _profile with
        {
            Settings = _profile.Settings
                .Select(s => s.Id == "dynamicEq" ? s with { Recommend = new Dictionary<string, RecommendedValue> { ["films"] = new(["OFF"]) } } : s)
                .ToList(),
        };
        var offLink = SetupTestProfiles.FakeWithWrongValues(offProfile);
        var offCheck = await SetupCheckEngine.CheckAsync(offProfile, ListeningUse.Films, offLink, CancellationToken.None);

        Assert.Equal(SetupRowState.Ok, offCheck.Find("dynamicEq")!.State);
        Assert.Equal(SetupRowState.BlockedByRequirement, offCheck.Find("refLevel")!.State);
        Assert.DoesNotContain("PSREFLEV ?", offLink.Reads);
    }

    [Fact]
    public async Task A_setting_the_receiver_does_not_answer_or_fails_to_read_cannot_be_read_and_is_not_offered()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        link.Silent.Add("cinemaEq");
        link.ThrowsOnRead.Add("drc");

        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        Assert.Equal(SetupRowState.CannotRead, check.Find("cinemaEq")!.State);
        Assert.Equal(SetupRowState.CannotRead, check.Find("drc")!.State);
        Assert.False(check.Find("cinemaEq")!.CanApply);
        Assert.False(check.Find("drc")!.CanApply);
        Assert.Equal(SetupRowState.Differs, check.Find("restorer")!.State); // The others are still checked.
    }

    [Fact]
    public async Task A_check_cancelled_by_the_user_stops_and_is_not_reported_as_unreadable()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var cancelled = new CancelOnReadLink(link);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, cancelled, cts.Token));
    }

    [Fact]
    public async Task In_standby_nothing_is_read_and_the_check_says_why()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        link.IsPoweredOn = false;

        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        Assert.Contains("standby", check.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(link.Sent);
        Assert.All(check.Rows, r => Assert.False(r.CanApply));
        Assert.Equal(SetupRowState.CannotRead, check.Find("eco")!.State);
        Assert.Equal(SetupRowState.AdviceOnly, check.Find("speakerSize")!.State);
    }

    [Fact]
    public async Task A_check_that_sees_the_input_change_while_it_reads_offers_nothing_to_apply()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        link.AfterFirstRead = () => link.Input = "GAME";

        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        Assert.Contains("changed", check.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(check.Rows, static r => r.State == SetupRowState.Differs);
        Assert.All(check.Rows, r => Assert.False(r.CanApply));
    }

    // ---- Apply ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_writes_only_the_chosen_rows_in_profile_order_and_reads_each_back()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        link.Sent.Clear();

        // Chosen out of order: Reference Level Offset first, then Dynamic EQ, which it needs.
        var result = await SetupCheckEngine.ApplyAsync(check, ["refLevel", "dynamicEq", "eco"], link, Store(), CancellationToken.None);

        Assert.Equal(["PSDYNEQ ON", "PSREFLEV 0", "ECOOFF"], link.Writes);
        Assert.Equal("ON", link.Get("dynamicEq"));
        Assert.Equal("0", link.Get("refLevel"));
        Assert.Equal("FLAT", link.Get("multEq")); // Not chosen: untouched.
        Assert.Equal("HEV", link.Get("dynamicVolume"));
        Assert.All(result.Outcomes, o => Assert.Equal(SetupOutcomeKind.Applied, o.Kind));
        Assert.Equal(3, result.AppliedCount);

        // Each write was followed by a read of that setting.
        var order = link.Sent.Select(static c => c.Text).ToList();
        Assert.Equal(["PSDYNEQ ?", "PSDYNEQ ON", "PSDYNEQ ?", "PSREFLEV ?", "PSREFLEV 0", "PSREFLEV ?", "ECO?", "ECOOFF", "ECO?"], order);
    }

    [Fact]
    public async Task Apply_saves_the_old_values_before_it_writes_anything()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        SetupSnapshot? seenAtFirstWrite = null;
        link.BeforeWrite = _ => seenAtFirstWrite ??= store.Load();

        await SetupCheckEngine.ApplyAsync(check, ["dynamicEq", "refLevel"], link, store, CancellationToken.None);

        Assert.NotNull(seenAtFirstWrite);
        var entry = Assert.Single(seenAtFirstWrite!.Entries);
        Assert.Equal("dynamicEq", entry.SettingId);
        Assert.Equal("OFF", entry.OldToken);
        Assert.Equal("ON", entry.NewToken);
        Assert.Equal("TV", entry.InputCode);

        var saved = store.Load()!;
        Assert.Equal(["dynamicEq", "refLevel"], saved.Entries.Select(static e => e.SettingId));
        Assert.Equal(["OFF", "15"], saved.Entries.Select(static e => e.OldToken));
        Assert.Equal(_profile.Id, saved.ProfileId);
    }

    [Fact]
    public async Task Apply_never_writes_advice_rows_unreadable_rows_or_rows_that_are_already_right()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        link.Silent.Add("tone");
        link.Set("cinemaEq", "OFF");
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        link.Sent.Clear();

        var result = await SetupCheckEngine.ApplyAsync(
            check, ["speakerSize", "subTrim", "soundMode", "tone", "cinemaEq", "volumeLimit", "ghost"], link, Store(), CancellationToken.None);

        Assert.Empty(link.Sent);
        Assert.All(result.Outcomes, o => Assert.Equal(SetupOutcomeKind.Skipped, o.Kind));
        Assert.Equal(7, result.Outcomes.Count);
        Assert.Null(result.Snapshot);
        Assert.Null(Store().Load());
    }

    [Fact]
    public async Task A_write_the_receiver_ignores_is_reported_and_leaves_nothing_to_undo()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        link.IgnoresWrites.Add("cinemaEq");
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        var result = await SetupCheckEngine.ApplyAsync(check, ["cinemaEq", "eco"], link, store, CancellationToken.None);

        Assert.Equal(SetupOutcomeKind.NotTaken, result.Outcomes.Single(o => o.SettingId == "cinemaEq").Kind);
        Assert.Equal(SetupOutcomeKind.Applied, result.Outcomes.Single(o => o.SettingId == "eco").Kind);
        Assert.True(result.HasProblems);
        Assert.Equal(["eco"], store.Load()!.Entries.Where(static e => e.Written).Select(static e => e.SettingId));
    }

    [Fact]
    public async Task A_write_the_receiver_does_not_confirm_is_unconfirmed_and_stays_in_the_undo_list()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        link.BeforeWrite = _ => link.Silent.Add("eco"); // After the write the receiver stops answering.

        var result = await SetupCheckEngine.ApplyAsync(check, ["eco"], link, Store(), CancellationToken.None);

        Assert.Equal(SetupOutcomeKind.Unconfirmed, Assert.Single(result.Outcomes).Kind);
        Assert.True(Store().Load()!.Entries[0].Written);
    }

    [Fact]
    public async Task Apply_stops_after_a_failed_write_and_keeps_what_was_written_for_undo()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        link.FailWrite = c => c.Text == "PSREFLEV 0";
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        var result = await SetupCheckEngine.ApplyAsync(check, ["dynamicEq", "refLevel", "eco"], link, store, CancellationToken.None);

        Assert.Equal(
            [SetupOutcomeKind.Applied, SetupOutcomeKind.Failed, SetupOutcomeKind.Skipped],
            result.Outcomes.Select(static o => o.Kind));
        Assert.Equal("ON", link.Get("eco")); // Skipped after the failure.
        Assert.DoesNotContain("ECOOFF", link.Writes);
        Assert.Contains(store.Load()!.Entries, e => e.SettingId == "dynamicEq" && e.Written);
    }

    [Fact]
    public async Task A_setting_that_needs_another_one_is_skipped_when_that_one_is_not_applied()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        link.Sent.Clear();

        var result = await SetupCheckEngine.ApplyAsync(check, ["refLevel"], link, Store(), CancellationToken.None);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(SetupOutcomeKind.Skipped, outcome.Kind);
        Assert.Contains("Dynamic EQ", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(link.Writes);
    }

    [Fact]
    public async Task In_standby_apply_writes_nothing()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        link.IsPoweredOn = false;
        link.Sent.Clear();

        var result = await SetupCheckEngine.ApplyAsync(check, ["eco", "dynamicEq"], link, Store(), CancellationToken.None);

        Assert.Empty(link.Sent);
        Assert.All(result.Outcomes, o => Assert.Equal(SetupOutcomeKind.Skipped, o.Kind));
    }

    // ---- Per-input scope -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Per_input_settings_are_checked_and_written_for_the_current_input_only()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        link.Set("dynamicEq", "OFF", "GAME");
        link.Set("cinemaEq", "ON", "GAME");
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        await SetupCheckEngine.ApplyAsync(check, ["dynamicEq", "cinemaEq"], link, Store(), CancellationToken.None);

        Assert.Equal("TV", check.Context.InputCode);
        Assert.Equal("ON", link.Get("dynamicEq", "TV"));
        Assert.Equal("OFF", link.Get("cinemaEq", "TV"));
        Assert.Equal("OFF", link.Get("dynamicEq", "GAME")); // The other input is not touched: no input switch happens.
        Assert.Equal("ON", link.Get("cinemaEq", "GAME"));
        Assert.DoesNotContain(link.Sent, c => c.Text.StartsWith("SI", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_input_changed_since_the_check_per_input_rows_are_skipped_and_global_rows_still_apply()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        link.Input = "GAME";
        link.Sent.Clear();

        var result = await SetupCheckEngine.ApplyAsync(check, ["dynamicEq", "cinemaEq", "eco"], link, Store(), CancellationToken.None);

        Assert.Equal(["ECOOFF"], link.Writes);
        Assert.Equal(SetupOutcomeKind.Skipped, result.Outcomes.Single(o => o.SettingId == "dynamicEq").Kind);
        Assert.Contains("TV", result.Outcomes.Single(o => o.SettingId == "dynamicEq").Message, StringComparison.Ordinal);
        Assert.Contains("GAME", result.Outcomes.Single(o => o.SettingId == "dynamicEq").Message, StringComparison.Ordinal);
        Assert.Equal(SetupOutcomeKind.Applied, result.Outcomes.Single(o => o.SettingId == "eco").Kind);
        Assert.Equal("OFF", link.Get("dynamicEq", "TV"));
    }

    [Fact]
    public async Task A_per_sound_mode_row_is_skipped_when_the_mode_changed()
    {
        var perMode = _profile with
        {
            Settings = _profile.Settings.Select(s => s.Id == "cinemaEq" ? s with { Scope = SettingScope.PerSoundMode } : s).ToList(),
        };
        var link = SetupTestProfiles.FakeWithWrongValues(perMode);
        var check = await SetupCheckEngine.CheckAsync(perMode, ListeningUse.Films, link, CancellationToken.None);
        link.SoundMode = "MOVIE";
        link.Sent.Clear();

        var result = await SetupCheckEngine.ApplyAsync(check, ["cinemaEq", "eco"], link, Store(), CancellationToken.None);

        Assert.Equal(["ECOOFF"], link.Writes);
        Assert.Contains("sound mode", result.Outcomes.Single(o => o.SettingId == "cinemaEq").Message, StringComparison.Ordinal);
    }

    // ---- Things that change between the check and the write ------------------------------------------------------

    [Fact]
    public async Task A_value_that_changed_since_the_check_is_not_overwritten_and_never_enters_the_undo_list()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        link.Set("refLevel", "5"); // Another controller changed it after the check.

        var result = await SetupCheckEngine.ApplyAsync(check, ["dynamicEq", "refLevel"], link, store, CancellationToken.None);

        var skipped = result.Outcomes.Single(o => o.SettingId == "refLevel");
        Assert.Equal(SetupOutcomeKind.Skipped, skipped.Kind);
        Assert.Contains("changed since the check", skipped.Message, StringComparison.Ordinal);
        Assert.Equal("5", link.Get("refLevel"));
        Assert.Equal(["PSDYNEQ ON"], link.Writes);
        Assert.Equal(["dynamicEq"], store.Load()!.Entries.Select(static e => e.SettingId));
    }

    [Fact]
    public async Task The_value_saved_for_undo_is_the_one_read_right_before_the_write()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);

        await SetupCheckEngine.ApplyAsync(check, ["eco"], link, store, CancellationToken.None);

        Assert.Equal("ON", store.Load()!.Entries.Single().OldToken);
    }

    [Fact]
    public async Task When_the_input_changes_after_the_first_write_the_next_per_input_rows_are_not_written()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        link.BeforeWrite = c =>
        {
            if (c.Text == "PSDYNEQ ON")
            {
                link.Input = "GAME"; // The remote or HDMI-CEC switches input after the first write.
            }
        };

        var result = await SetupCheckEngine.ApplyAsync(check, ["dynamicEq", "cinemaEq", "eco"], link, store, CancellationToken.None);

        Assert.Equal(["PSDYNEQ ON", "ECOOFF"], link.Writes);
        Assert.Equal(SetupOutcomeKind.Skipped, result.Outcomes.Single(o => o.SettingId == "cinemaEq").Kind);
        Assert.Equal("ON", link.Get("cinemaEq", "TV"));
    }

    [Fact]
    public async Task When_the_input_changes_during_undo_the_next_per_input_rows_are_kept_for_later()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        await SetupCheckEngine.ApplyAsync(check, ["dynamicEq", "cinemaEq", "eco"], link, store, CancellationToken.None);
        link.Sent.Clear();
        link.BeforeWrite = c =>
        {
            if (c.Text == "ECOON")
            {
                link.Input = "GAME";
            }
        };

        var undo = await SetupCheckEngine.UndoAsync(store.Load()!, _profile, link, CancellationToken.None);

        Assert.Equal(["ECOON"], link.Writes);
        Assert.Equal(["dynamicEq", "cinemaEq"], undo.Snapshot!.Entries.Where(static e => e.Written).Select(static e => e.SettingId));
    }

    [Fact]
    public async Task An_undo_the_receiver_does_not_confirm_stays_in_the_snapshot_for_another_try()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        await SetupCheckEngine.ApplyAsync(check, ["eco"], link, store, CancellationToken.None);
        link.BeforeWrite = _ => link.Silent.Add("eco");

        var undo = await SetupCheckEngine.UndoAsync(store.Load()!, _profile, link, CancellationToken.None);

        Assert.Equal(SetupOutcomeKind.Unconfirmed, Assert.Single(undo.Outcomes).Kind);
        Assert.True(undo.Snapshot!.HasWrittenEntries);
    }

    [Fact]
    public void The_summary_counts_only_confirmed_writes_and_names_the_unconfirmed_and_undone_ones()
    {
        var result = new SetupApplyResult(
            [
                new SetupOutcome("a", "Dynamic EQ", SetupOutcomeKind.Applied, "Set to On."),
                new SetupOutcome("b", "ECO mode", SetupOutcomeKind.Unconfirmed, "Sent, but the receiver did not confirm the new value."),
                new SetupOutcome("c", "Tone control", SetupOutcomeKind.Skipped, "The input changed."),
            ],
            null);

        var text = result.Summarize("Applied");

        Assert.StartsWith("Applied 1.", text, StringComparison.Ordinal);
        Assert.Contains("Not confirmed: ECO mode: Sent, but the receiver did not confirm", text, StringComparison.Ordinal);
        Assert.Contains("Not done: Tone control: The input changed.", text, StringComparison.Ordinal);
        Assert.Equal("Applied 0.", new SetupApplyResult([], null).Summarize("Applied"));
    }

    // ---- Which receiver ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_snapshot_remembers_the_receiver_and_undo_refuses_another_one()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        await SetupCheckEngine.ApplyAsync(check, ["eco"], link, store, CancellationToken.None);
        var snapshot = store.Load()!;
        Assert.Equal("192.0.2.10", snapshot.ReceiverId);

        var other = SetupTestProfiles.FakeWithWrongValues(_profile);
        other.ReceiverId = "192.0.2.99";
        var undo = await SetupCheckEngine.UndoAsync(snapshot, _profile, other, CancellationToken.None);

        Assert.Equal(SetupOutcomeKind.Skipped, Assert.Single(undo.Outcomes).Kind);
        Assert.Empty(other.Sent);
        Assert.Equal(snapshot, undo.Snapshot); // Kept for the receiver it belongs to.
    }

    [Fact]
    public async Task Apply_writes_nothing_when_the_app_is_connected_to_another_receiver_than_the_checked_one()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        link.ReceiverId = "192.0.2.99";
        link.Sent.Clear();

        var result = await SetupCheckEngine.ApplyAsync(check, ["eco"], link, Store(), CancellationToken.None);

        Assert.Empty(link.Sent);
        Assert.All(result.Outcomes, o => Assert.Equal(SetupOutcomeKind.Skipped, o.Kind));
    }

    // ---- Undo ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Undo_puts_the_old_values_back_in_reverse_order_and_clears_the_snapshot()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        await SetupCheckEngine.ApplyAsync(check, ["dynamicEq", "refLevel", "eco"], link, store, CancellationToken.None);
        link.Sent.Clear();

        var undo = await SetupCheckEngine.UndoAsync(store.Load()!, _profile, link, CancellationToken.None);

        Assert.Equal(["ECOON", "PSREFLEV 15", "PSDYNEQ OFF"], link.Writes);
        Assert.Equal("OFF", link.Get("dynamicEq"));
        Assert.Equal("15", link.Get("refLevel"));
        Assert.Equal("ON", link.Get("eco"));
        Assert.All(undo.Outcomes, o => Assert.Equal(SetupOutcomeKind.Applied, o.Kind));
        Assert.False(undo.Snapshot!.HasWrittenEntries);
    }

    [Fact]
    public async Task Undo_on_another_input_skips_the_per_input_rows_and_keeps_them_for_later()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        await SetupCheckEngine.ApplyAsync(check, ["dynamicEq", "eco"], link, store, CancellationToken.None);
        link.Input = "GAME";
        link.Sent.Clear();

        var undo = await SetupCheckEngine.UndoAsync(store.Load()!, _profile, link, CancellationToken.None);

        Assert.Equal(["ECOON"], link.Writes);
        Assert.Equal(SetupOutcomeKind.Skipped, undo.Outcomes.Single(o => o.SettingId == "dynamicEq").Kind);
        Assert.Contains("TV", undo.Outcomes.Single(o => o.SettingId == "dynamicEq").Message, StringComparison.Ordinal);
        Assert.Equal(["dynamicEq"], undo.Snapshot!.Entries.Where(static e => e.Written).Select(static e => e.SettingId));

        link.Input = "TV";
        var second = await SetupCheckEngine.UndoAsync(undo.Snapshot!, _profile, link, CancellationToken.None);
        Assert.Equal("OFF", link.Get("dynamicEq", "TV"));
        Assert.False(second.Snapshot!.HasWrittenEntries);
    }

    [Fact]
    public async Task Undo_ignores_rows_that_were_never_written()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        link.IgnoresWrites.Add("cinemaEq");
        var store = Store();
        var check = await SetupCheckEngine.CheckAsync(_profile, ListeningUse.Films, link, CancellationToken.None);
        await SetupCheckEngine.ApplyAsync(check, ["cinemaEq"], link, store, CancellationToken.None);
        link.Sent.Clear();

        Assert.Null(store.Load()); // Nothing was written, so there is nothing to undo.
        var undo = await SetupCheckEngine.UndoAsync(
            new SetupSnapshot(_profile.Id, "x", ListeningUse.Films, DateTimeOffset.Now,
                [new SnapshotEntry("cinemaEq", "Cinema EQ", SettingScope.PerInput, "TV", "STEREO", "ON", "OFF", Written: false)]),
            _profile, link, CancellationToken.None);

        Assert.Empty(undo.Outcomes);
        Assert.Empty(link.Sent);
    }

    [Fact]
    public async Task Undo_refuses_an_old_value_the_profile_no_longer_allows()
    {
        var link = SetupTestProfiles.FakeWithWrongValues(_profile);
        var snapshot = new SetupSnapshot(_profile.Id, "x", ListeningUse.Films, DateTimeOffset.Now,
            [new SnapshotEntry("eco", "ECO mode", SettingScope.Global, "TV", "STEREO", "TURBO\rPWSTANDBY", "OFF", Written: true)]);

        var undo = await SetupCheckEngine.UndoAsync(snapshot, _profile, link, CancellationToken.None);

        Assert.Equal(SetupOutcomeKind.Skipped, Assert.Single(undo.Outcomes).Kind);
        Assert.Empty(link.Writes);
    }

    // ---- Snapshot store -------------------------------------------------------------------------------------------

    [Fact]
    public void The_snapshot_survives_a_restart_and_a_snapshot_with_nothing_written_is_not_returned()
    {
        var entry = new SnapshotEntry("eco", "ECO mode", SettingScope.Global, "TV", "STEREO", "ON", "OFF", Written: true);
        var snapshot = new SetupSnapshot("p", "Denon AVR-X2400H", ListeningUse.LateNight, DateTimeOffset.Now, [entry]);
        var store = Store();

        store.Save(snapshot);
        var loaded = new SetupSnapshotStore(_dir).Load()!;

        Assert.Equal(ListeningUse.LateNight, loaded.Use);
        Assert.Equal(entry, loaded.Entries.Single());

        store.Save(snapshot with { Entries = [entry with { Written = false }] });
        Assert.Null(store.Load());
        store.Save(snapshot);
        store.Clear();
        Assert.Null(store.Load());
        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Null(store.Load());
    }

    private sealed class CancelOnReadLink(FakeSetupLink inner) : IReceiverSetupLink
    {
        public Task<SetupContext> GetContextAsync(CancellationToken cancellationToken) => inner.GetContextAsync(cancellationToken);

        public Task<IReadOnlyList<string>> QueryAsync(AvrSetupCommand command, Func<string, bool> answerLine, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.QueryAsync(command, answerLine, cancellationToken);
        }

        public Task SendAsync(AvrSetupCommand command, CancellationToken cancellationToken) => inner.SendAsync(command, cancellationToken);
    }
}
