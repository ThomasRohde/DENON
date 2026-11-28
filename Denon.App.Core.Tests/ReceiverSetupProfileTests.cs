using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Denon.App.Core.Heos;
using Denon.App.Core.Models;
using Denon.App.Core.Setup;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>Receiver setup check: profile loading and validation, and the whitelist-only command path.</summary>
public sealed class ReceiverSetupProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "denon-setup-" + Guid.NewGuid().ToString("N"));

    public ReceiverSetupProfileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- Loading and validation -------------------------------------------------------------------------------

    [Fact]
    public void The_bundled_profile_loads_without_problems_and_covers_the_fy17_family()
    {
        var library = ReceiverProfileLibrary.Load(null);

        Assert.Empty(library.Problems);
        var profile = Assert.Single(library.Profiles);
        Assert.True(profile.IsBundled);
        foreach (var model in new[] { "Denon AVR-X2400H", "AVR-X1400H", "Denon AVR-X3400H", "AVR-S930H", "Denon AVR-S730H", "Denon AVR-X2400HBKE2" })
        {
            Assert.Same(profile, library.FindForModel(model));
        }

        Assert.Null(library.FindForModel("Denon AVR-X2700H"));
        Assert.Null(library.FindForModel("Marantz NR1609"));
        Assert.Null(library.FindForModel(""));
        Assert.Null(library.FindForModel(null));
    }

    [Fact]
    public void The_bundled_profile_has_the_settings_of_the_research_and_the_advice_rows()
    {
        var profile = SetupTestProfiles.X2400H();
        var ids = profile.Settings.Select(static s => s.Id).ToList();

        Assert.Contains("refLevel", ids);
        Assert.Contains("dynamicEq", ids);
        Assert.Contains("cinemaEq", ids);
        Assert.Contains("drc", ids);
        Assert.Contains("loudnessManagement", ids);
        Assert.Contains("lfeLevel", ids);
        Assert.True(ids.IndexOf("dynamicEq") < ids.IndexOf("refLevel"), "A setting comes after the ones it requires.");

        foreach (var advice in new[] { "speakerSize", "crossover", "lpfForLfe", "subwooferMode", "distances", "hdmiControl", "signalFormat", "volumeLimit" })
        {
            var setting = profile.Settings.Single(s => s.Id == advice);
            Assert.Equal(SettingTier.Advice, setting.Tier);
            Assert.Null(setting.Read);
            Assert.Null(setting.Write);
            Assert.False(string.IsNullOrWhiteSpace(setting.Advice));
        }

        var refLevel = profile.Settings.Single(s => s.Id == "refLevel");
        Assert.Equal("ON", refLevel.Requires!["dynamicEq"]);
        Assert.Equal(["0", "5", "10", "15"], refLevel.Values!.Select(static v => v.Token));
        Assert.Equal("0", refLevel.RecommendationFor(ListeningUse.Films)!.Preferred);
        Assert.Equal("10", refLevel.RecommendationFor(ListeningUse.Music)!.Preferred);
    }

    [Fact]
    public void Every_command_a_bundled_profile_can_build_passes_the_gate_and_none_is_a_setup_command()
    {
        var profile = SetupTestProfiles.X2400H();
        var commands = new List<AvrSetupCommand>();
        foreach (var setting in profile.Settings)
        {
            if (setting.IsReadable)
            {
                commands.Add(AvrCommandGate.BuildRead(setting));
            }

            if (setting.IsWritable)
            {
                commands.AddRange(setting.Values!.Select(v => AvrCommandGate.BuildWrite(setting, v.Token)));
            }
        }

        Assert.NotEmpty(commands);
        foreach (var command in commands)
        {
            Assert.DoesNotContain(AvrCommandGate.BlockedPrefixes, p => command.Text.StartsWith(p, StringComparison.Ordinal));
            Assert.False(command.Text.StartsWith("SS", StringComparison.Ordinal), $"'{command.Text}' is an undocumented setup command.");
            Assert.True(Encoding.ASCII.GetByteCount(command.Text) + 1 <= AvrCommandGate.MaxCommandBytes);
        }

        Assert.Contains(commands, c => c.Text == "PSREFLEV 10");
        Assert.Contains(commands, c => c.Text == "PSCINEMA EQ.OFF");
        Assert.Contains(commands, c => c.Text == "PSDRC MID");
        Assert.Contains(commands, c => c.Text == "PSLFE 00");
        Assert.Contains(commands, c => c.Text == "PSLOM ON");
        Assert.Contains(commands, c => c.Text == "PSDELAY ?");
    }

    [Fact]
    public void A_valid_minimal_profile_is_accepted()
    {
        var result = ProfileValidator.Parse(SetupTestProfiles.MinimalProfileJson(), "test.json");

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("test.json", result.Profile!.Origin);
        Assert.False(result.Profile.IsBundled);
    }

    [Theory]
    [InlineData("""{ "id": "x", "name": "X", "models": ["AVR-TEST1"] }""", "settings")]
    [InlineData("""{ "id": "X Y", "name": "X", "models": ["AVR-TEST1"], "settings": [] }""", "id")]
    [InlineData("not json at all", "JSON")]
    public void A_profile_that_does_not_fit_the_schema_is_refused(string json, string errorContains)
    {
        var result = ProfileValidator.Parse(json, "x");

        Assert.False(result.IsValid);
        Assert.Null(result.Profile);
        Assert.Contains(result.Errors, e => e.Contains(errorContains, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("\"scope\": \"perInput\"", "\"scope\": \"everywhere\"")]
    [InlineData("\"tier\": \"apply\"", "\"tier\": \"always\"")]
    [InlineData("\"write\": \"PSDYNEQ {value}\"", "\"write\": \"PSDYNEQ ON\"")]
    [InlineData("\"write\": \"PSDYNEQ {value}\"", "\"write\": \"psdyneq {value}\"")]
    [InlineData("\"read\": \"PSDYNEQ ?\"", "\"read\": \"PSDYNEQ\"")]
    [InlineData("{ \"token\": \"ON\" }", "{ \"token\": \"ON\\rPWSTANDBY\" }")]
    [InlineData("{ \"token\": \"ON\" }", "{ \"token\": \"ON PWON\" }")]
    [InlineData("\"recommend\": { \"films\": \"ON\" }", "\"recommend\": { \"opera\": \"ON\" }")]
    [InlineData("\"why\": \"Because.\"", "\"extra\": 1, \"why\": \"Because.\"")]
    public void A_setting_the_schema_does_not_allow_is_refused(string from, string to)
    {
        var result = ProfileValidator.Parse(SetupTestProfiles.MinimalProfileJson().Replace(from, to, StringComparison.Ordinal), "x");

        Assert.False(result.IsValid, "The change should have been refused: " + to);
    }

    [Fact]
    public void Advice_rows_have_no_write_and_other_rows_must_have_one()
    {
        var advice = """
            { "id": "sizes", "label": "Sizes", "group": "Speakers", "scope": "global", "tier": "advice",
              "write": "PSDYNEQ {value}", "advice": "Small.", "why": "Because." }
            """;
        var noWrite = """
            { "id": "tone", "label": "Tone", "group": "Sound", "scope": "global", "tier": "confirm",
              "read": "PSTONE CTRL ?", "readParse": "^PSTONE CTRL (ON|OFF)$", "why": "Because." }
            """;

        Assert.False(ProfileValidator.Parse(SetupTestProfiles.MinimalProfileJson(advice), "x").IsValid);
        Assert.False(ProfileValidator.Parse(SetupTestProfiles.MinimalProfileJson(noWrite), "x").IsValid);
    }

    [Theory]
    [InlineData("PWON{value}", "PW")]
    [InlineData("PWSTANDBY{value}", "PW")]
    [InlineData("SYRST{value}", "SYRST")]
    [InlineData("SYPANEL{value}", "SYPANEL")]
    [InlineData("RCKK{value}", "RC")]
    public void A_profile_cannot_reach_a_blocked_command(string write, string blockedPrefix)
    {
        var setting = SetupTestProfiles.DefaultSetting.Replace("PSDYNEQ {value}", write, StringComparison.Ordinal);

        var result = ProfileValidator.Parse(SetupTestProfiles.MinimalProfileJson(setting), "x");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(blockedPrefix, StringComparison.Ordinal) && e.Contains("blocked", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("MV{value}")]
    [InlineData("SI{value}")]
    [InlineData("Z2{value}")]
    public void A_profile_cannot_use_a_command_family_outside_the_list(string write)
    {
        var setting = SetupTestProfiles.DefaultSetting.Replace("PSDYNEQ {value}", write, StringComparison.Ordinal);

        var result = ProfileValidator.Parse(SetupTestProfiles.MinimalProfileJson(setting), "x");

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("\"requires\": { \"later\": \"ON\" }")]
    [InlineData("\"requires\": { \"missing\": \"ON\" }")]
    [InlineData("\"requires\": { \"dynamicEq\": \"ON\" }")]
    public void A_requirement_must_name_an_earlier_setting_with_an_allowed_value(string requires)
    {
        var first = SetupTestProfiles.DefaultSetting;
        var second = """
            {
              "id": "refLevel", "label": "Ref", "group": "Audyssey", "scope": "perInput", "tier": "confirm",
              REQUIRES,
              "read": "PSREFLEV ?", "readParse": "^PSREFLEV (\\d+)$", "write": "PSREFLEV {value}",
              "values": [ { "token": "0" }, { "token": "5" } ], "recommend": { "films": "0" }, "why": "Because."
            }
            """.Replace("REQUIRES", requires, StringComparison.Ordinal);

        // "dynamicEq" is the first setting here, so that requirement is fine: it is the control case.
        var result = ProfileValidator.Parse(SetupTestProfiles.MinimalProfileJson(first + "," + second), "x");

        Assert.Equal(requires.Contains("dynamicEq", StringComparison.Ordinal), result.IsValid);
    }

    [Fact]
    public void A_requirement_on_a_value_the_other_setting_does_not_allow_is_refused()
    {
        var second = """
            {
              "id": "refLevel", "label": "Ref", "group": "Audyssey", "scope": "perInput", "tier": "confirm",
              "requires": { "dynamicEq": "MAYBE" },
              "read": "PSREFLEV ?", "readParse": "^PSREFLEV (\\d+)$", "write": "PSREFLEV {value}",
              "values": [ { "token": "0" } ], "recommend": { "films": "0" }, "why": "Because."
            }
            """;

        var result = ProfileValidator.Parse(SetupTestProfiles.MinimalProfileJson(SetupTestProfiles.DefaultSetting + "," + second), "x");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("MAYBE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_recommended_value_must_be_an_allowed_value()
    {
        var json = SetupTestProfiles.MinimalProfileJson().Replace("\"films\": \"ON\"", "\"films\": \"BRIGHT\"", StringComparison.Ordinal);

        var result = ProfileValidator.Parse(json, "x");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("BRIGHT", StringComparison.Ordinal));
    }

    [Fact]
    public void A_read_pattern_needs_a_capture_group_and_must_be_a_valid_regular_expression()
    {
        var noGroup = SetupTestProfiles.MinimalProfileJson().Replace("^PSDYNEQ (ON|OFF)$", "^PSDYNEQ ON$", StringComparison.Ordinal);
        var broken = SetupTestProfiles.MinimalProfileJson().Replace("^PSDYNEQ (ON|OFF)$", "^PSDYNEQ (ON|OFF$", StringComparison.Ordinal);

        Assert.False(ProfileValidator.Parse(noGroup, "x").IsValid);
        Assert.False(ProfileValidator.Parse(broken, "x").IsValid);
    }

    [Fact]
    public void Two_settings_with_one_id_are_refused()
    {
        var json = SetupTestProfiles.MinimalProfileJson(SetupTestProfiles.DefaultSetting + "," + SetupTestProfiles.DefaultSetting);

        var result = ProfileValidator.Parse(json, "x");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("twice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_warning_needs_a_read_command()
    {
        var setting = """
            { "id": "subTrim", "label": "Trim", "group": "Speakers", "scope": "perInput", "tier": "advice",
              "warnWhen": { "atLimits": { "min": -12, "max": 12 }, "message": "At the limit." }, "why": "Because." }
            """;

        Assert.False(ProfileValidator.Parse(SetupTestProfiles.MinimalProfileJson(setting), "x").IsValid);
    }

    // ---- Library: user profiles override bundled ones ----------------------------------------------------------

    [Fact]
    public void A_user_profile_with_the_id_of_a_bundled_one_replaces_it()
    {
        var bundledId = SetupTestProfiles.X2400H().Id;
        File.WriteAllText(
            Path.Combine(_dir, "mine.json"),
            SetupTestProfiles.MinimalProfileJson().Replace("\"test-profile\"", $"\"{bundledId}\"", StringComparison.Ordinal)
                .Replace("AVR-TEST1", "AVR-X2400H", StringComparison.Ordinal));

        var library = ReceiverProfileLibrary.Load(_dir);

        var profile = Assert.Single(library.Profiles);
        Assert.False(profile.IsBundled);
        Assert.Single(profile.Settings);
        Assert.Empty(library.Problems);
    }

    [Fact]
    public void A_user_profile_for_the_same_model_wins_over_the_bundled_one()
    {
        File.WriteAllText(
            Path.Combine(_dir, "x2400h.json"),
            SetupTestProfiles.MinimalProfileJson().Replace("AVR-TEST1", "AVR-X2400H", StringComparison.Ordinal));

        var library = ReceiverProfileLibrary.Load(_dir);

        Assert.Equal(2, library.Profiles.Count);
        Assert.Equal("test-profile", library.FindForModel("Denon AVR-X2400H")!.Id);
        Assert.Equal(SetupTestProfiles.X2400H().Id, library.FindForModel("Denon AVR-X1400H")!.Id);
    }

    [Fact]
    public void A_broken_user_file_is_reported_and_not_used()
    {
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ \"id\": \"broken\" }");
        File.WriteAllText(Path.Combine(_dir, "worse.json"), "][");

        var library = ReceiverProfileLibrary.Load(_dir);

        Assert.Single(library.Profiles);
        Assert.Equal(2, library.Problems.Count);
        Assert.All(library.Problems, p => Assert.NotEmpty(p.Errors));
        Assert.Contains(library.Problems, p => p.Origin.EndsWith("broken.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_user_files_with_one_id_keep_the_first_and_report_the_second()
    {
        File.WriteAllText(Path.Combine(_dir, "a.json"), SetupTestProfiles.MinimalProfileJson());
        File.WriteAllText(Path.Combine(_dir, "b.json"), SetupTestProfiles.MinimalProfileJson());

        var library = ReceiverProfileLibrary.Load(_dir);

        Assert.Equal(2, library.Profiles.Count); // One user profile and the bundled one.
        Assert.Single(library.Problems);
        Assert.EndsWith("b.json", library.Problems[0].Origin, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_profiles_folder_is_fine()
    {
        var library = ReceiverProfileLibrary.Load(Path.Combine(_dir, "nope"));

        Assert.Empty(library.Problems);
        Assert.Single(library.Profiles);
    }

    // ---- Per-model overrides -----------------------------------------------------------------------------------

    private const string TwoSettingsWithOverrides = """
        {
          "id": "family", "name": "Family", "models": ["AVR-A100", "AVR-A200"],
          "modelOverrides": {
            "AVR-A100": {
              "exclude": ["cinemaEq"],
              "recommend": { "dynamicEq": { "films": "OFF" } }
            }
          },
          "settings": [
            {
              "id": "dynamicEq", "label": "Dynamic EQ", "group": "Audyssey", "scope": "perInput", "tier": "apply",
              "read": "PSDYNEQ ?", "readParse": "^PSDYNEQ (ON|OFF)$", "write": "PSDYNEQ {value}",
              "values": [ { "token": "ON" }, { "token": "OFF" } ],
              "recommend": { "films": "ON", "music": "ON" }, "why": "Because."
            },
            {
              "id": "cinemaEq", "label": "Cinema EQ", "group": "Audyssey", "scope": "perInput", "tier": "apply",
              "read": "PSCINEMA EQ. ?", "readParse": "^PSCINEMA EQ\\.(ON|OFF)$", "write": "PSCINEMA EQ.{value}",
              "values": [ { "token": "ON" }, { "token": "OFF" } ],
              "recommend": { "films": "OFF" }, "why": "Because."
            }
          ]
        }
        """;

    [Fact]
    public void A_model_override_drops_settings_and_replaces_recommendations_for_that_model_only()
    {
        var parsed = ProfileValidator.Parse(TwoSettingsWithOverrides, "x");
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
        var family = parsed.Profile!;

        var a100 = ReceiverProfileLibrary.Resolve(family, "Denon AVR-A100");
        var a200 = ReceiverProfileLibrary.Resolve(family, "Denon AVR-A200");

        Assert.Equal(["dynamicEq"], a100.Settings.Select(static s => s.Id));
        Assert.Equal("OFF", a100.Settings[0].RecommendationFor(ListeningUse.Films)!.Preferred);
        Assert.Equal("ON", a100.Settings[0].RecommendationFor(ListeningUse.Music)!.Preferred); // Untouched uses stay.
        Assert.Equal(["dynamicEq", "cinemaEq"], a200.Settings.Select(static s => s.Id));
        Assert.Equal("ON", a200.Settings[0].RecommendationFor(ListeningUse.Films)!.Preferred);
        Assert.Same(family, ReceiverProfileLibrary.Resolve(family, null));
    }

    [Fact]
    public void A_model_override_that_names_nothing_real_is_refused()
    {
        var badExclude = TwoSettingsWithOverrides.Replace("\"exclude\": [\"cinemaEq\"]", "\"exclude\": [\"ghost\"]", StringComparison.Ordinal);
        var badToken = TwoSettingsWithOverrides.Replace("\"films\": \"OFF\" } }", "\"films\": \"SIDEWAYS\" } }", StringComparison.Ordinal);
        var badModel = TwoSettingsWithOverrides.Replace("\"AVR-A100\": {", "\"AVR-B999\": {", StringComparison.Ordinal);

        Assert.False(ProfileValidator.Parse(badExclude, "x").IsValid);
        Assert.False(ProfileValidator.Parse(badToken, "x").IsValid);
        Assert.False(ProfileValidator.Parse(badModel, "x").IsValid);
    }

    [Theory]
    [InlineData("Denon AVR-X2400H", "X2400H")]
    [InlineData("AVR-X2400H", "X2400H")]
    [InlineData("X2400H", "X2400H")]
    [InlineData("Marantz NR1609", "NR1609")]
    public void Model_names_are_matched_without_brand_and_dashes(string name, string normalized)
    {
        Assert.Equal(normalized, ModelNames.Normalize(name));
        Assert.True(ModelNames.Matches("AVR-" + normalized, name));
        Assert.False(ModelNames.Matches("AVR-X24000H", name));
    }

    // ---- The whitelist ------------------------------------------------------------------------------------------

    [Fact]
    public void A_write_carries_only_a_value_from_the_settings_own_list()
    {
        var setting = SetupTestProfiles.X2400H().Settings.Single(s => s.Id == "refLevel");

        Assert.Equal("PSREFLEV 15", AvrCommandGate.BuildWrite(setting, "15").Text);
        Assert.Equal("PSREFLEV 5", AvrCommandGate.BuildWrite(setting, "5").Text);
        Assert.Throws<SetupCommandRefusedException>(() => AvrCommandGate.BuildWrite(setting, "20"));
        Assert.Throws<SetupCommandRefusedException>(() => AvrCommandGate.BuildWrite(setting, "5\rPWSTANDBY"));
        Assert.Throws<SetupCommandRefusedException>(() => AvrCommandGate.BuildWrite(setting, string.Empty));
        Assert.Throws<SetupCommandRefusedException>(() => AvrCommandGate.BuildWrite(setting, "0 "));
    }

    [Fact]
    public void An_advice_row_and_a_row_without_a_write_command_cannot_be_written()
    {
        var profile = SetupTestProfiles.X2400H();
        var trim = profile.Settings.Single(s => s.Id == "subTrim");
        var size = profile.Settings.Single(s => s.Id == "speakerSize");

        Assert.Throws<SetupCommandRefusedException>(() => AvrCommandGate.BuildWrite(trim, "0"));
        Assert.Throws<SetupCommandRefusedException>(() => AvrCommandGate.BuildWrite(size, "SMALL"));
        Assert.Throws<SetupCommandRefusedException>(() => AvrCommandGate.BuildRead(size));
    }

    [Theory]
    [InlineData("SYRST", false)]
    [InlineData("SYRST?", true)]
    [InlineData("PWSTANDBY", false)]
    [InlineData("PWON", false)]
    [InlineData("PW?", true)]
    [InlineData("SYPANELLOCK", false)]
    [InlineData("RCKK01", false)]
    [InlineData("MV50", false)]
    [InlineData("SITV", false)]
    [InlineData("SSSPC ?", true)]
    [InlineData("SSSPCFRO ?", true)]
    [InlineData("psdyneq on", false)]
    [InlineData(" PSDYNEQ ON", false)]
    [InlineData("PSDYNEQ ON\rPWSTANDBY", false)]
    [InlineData("PSDYNEQ ON\nPWSTANDBY", false)]
    [InlineData("PSDYNEQ ?", false)]
    [InlineData("PSDYNEQ ON?", false)]
    [InlineData("PSDYNEQ ON", true)]
    [InlineData("", false)]
    public void The_gate_refuses_blocked_unknown_and_malformed_commands(string command, bool isRead)
    {
        Assert.Throws<SetupCommandRefusedException>(() => AvrCommandGate.EnsureAllowed(command, isRead));
    }

    [Theory]
    [InlineData("PSDYNEQ ON", false)]
    [InlineData("PSDYNEQ ?", true)]
    [InlineData("ECO?", true)]
    [InlineData("ECOOFF", false)]
    [InlineData("DIM ?", true)]
    [InlineData("MS?", true)]
    [InlineData("PSCINEMA EQ. ?", true)]
    public void The_gate_lets_the_allowed_families_through(string command, bool isRead)
    {
        AvrCommandGate.EnsureAllowed(command, isRead);
    }

    [Fact]
    public void A_command_is_at_most_135_bytes_with_its_carriage_return()
    {
        var fits = "PS" + new string('A', 132); // 134 bytes + CR = 135.
        var tooLong = "PS" + new string('A', 133);

        AvrCommandGate.EnsureAllowed(fits, isRead: false);
        Assert.Throws<SetupCommandRefusedException>(() => AvrCommandGate.EnsureAllowed(tooLong, isRead: false));
        DenonAvrClient.EnsureSingleCommand(fits);
        Assert.Throws<ArgumentException>(() => DenonAvrClient.EnsureSingleCommand(tooLong));
    }

    [Fact]
    public void Only_the_gate_can_make_a_command_for_the_client()
    {
        Assert.Empty(typeof(AvrSetupCommand).GetConstructors());
        Assert.True(typeof(AvrSetupCommand).IsSealed);
    }

    // ---- The client path, on loopback ---------------------------------------------------------------------------

    [Fact]
    public async Task The_client_sends_a_profile_command_as_one_line_and_keeps_50_ms_between_commands()
    {
        using var avr = new LoopbackAvr();
        var client = new DenonAvrClient(avr.Port);
        var settings = new ReceiverSettings { Host = "127.0.0.1" };
        var refLevel = SetupTestProfiles.X2400H().Settings.Single(s => s.Id == "refLevel");

        var lines = await client.QuerySetupAsync(settings, AvrCommandGate.BuildRead(refLevel), static l => l.StartsWith("PSREFLEV", StringComparison.Ordinal), CancellationToken.None);
        await client.SendSetupAsync(settings, AvrCommandGate.BuildWrite(refLevel, "10"), CancellationToken.None);
        await client.SendSetupAsync(settings, AvrCommandGate.BuildWrite(refLevel, "5"), CancellationToken.None);

        Assert.Contains("PSREFLEV 0", lines);
        Assert.Equal(["PSREFLEV ?", "PSREFLEV 10", "PSREFLEV 5"], avr.Commands);
        for (var i = 1; i < avr.Times.Count; i++)
        {
            Assert.True((avr.Times[i] - avr.Times[i - 1]).TotalMilliseconds >= 45, "Commands came less than 50 ms apart.");
        }
    }

    [Fact]
    public async Task The_client_methods_refuse_the_wrong_kind_of_command()
    {
        using var avr = new LoopbackAvr();
        var client = new DenonAvrClient(avr.Port);
        var settings = new ReceiverSettings { Host = "127.0.0.1" };
        var refLevel = SetupTestProfiles.X2400H().Settings.Single(s => s.Id == "refLevel");

        await Assert.ThrowsAsync<SetupCommandRefusedException>(() =>
            client.SendSetupAsync(settings, AvrCommandGate.BuildRead(refLevel), CancellationToken.None));
        await Assert.ThrowsAsync<SetupCommandRefusedException>(() =>
            client.QuerySetupAsync(settings, AvrCommandGate.BuildWrite(refLevel, "0"), static _ => true, CancellationToken.None));
        Assert.Empty(avr.Commands);
    }

    /// <summary>A receiver on loopback that answers PSREFLEV ? and records every command with its time.</summary>
    private sealed class LoopbackAvr : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<(string Command, DateTimeOffset At)> _log = new();

        public LoopbackAvr()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyList<string> Commands => _log.Select(static e => e.Command).ToList();

        public IReadOnlyList<DateTimeOffset> Times => _log.Select(static e => e.At).ToList();

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch
            {
                // Stopped.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[1024];
                var pending = new StringBuilder();
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read <= 0)
                        {
                            return;
                        }

                        pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        var text = pending.ToString();
                        var end = text.LastIndexOf('\r');
                        if (end < 0)
                        {
                            continue;
                        }

                        pending.Clear().Append(text[(end + 1)..]);
                        foreach (var command in text[..end].Split('\r', StringSplitOptions.RemoveEmptyEntries))
                        {
                            _log.Enqueue((command, DateTimeOffset.UtcNow));
                            var reply = command == "PSREFLEV ?" ? "PSREFLEV 0\r" : command + "\r";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), _stop.Token);
                        }
                    }
                }
                catch
                {
                    // Closed.
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
