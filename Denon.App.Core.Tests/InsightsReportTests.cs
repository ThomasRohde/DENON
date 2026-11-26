using System.Globalization;
using System.Text.Json;
using Denon.App.Core.Insights;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>The Insights page report and activity feed, built from habit events.</summary>
public sealed class InsightsReportTests
{
    // Monday 2026-11-23, 22:00 local time.
    private static readonly DateTime Now = new(2026, 11, 23, 22, 0, 0, DateTimeKind.Local);

    private static DateTimeOffset At(DateTime local) => new(local, TimeZoneInfo.Local.GetUtcOffset(local));

    private static JsonElement Ev(string stream, string device, string name, string origin, DateTime local, object data)
    {
        var ts = At(local).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        return JsonSerializer.SerializeToElement(new { ts, block = InsightsReport.BlockOf(local.Hour), stream, device, @event = name, origin, data });
    }

    private static JsonElement Ended(DateTime end, string kind, string title, int duration, int exclusive, string? app = null, string? input = null) =>
        Ev("media", kind, "session.ended", "external", end, new
        {
            kind,
            title,
            app,
            input,
            soundMode = kind == "avr_input" ? "STEREO" : null,
            durationSec = duration,
            exclusiveSec = exclusive,
        });

    [Fact]
    public void Watch_time_counts_exclusive_seconds_and_compares_with_the_period_before()
    {
        var events = new[]
        {
            // This week: 1 h of Apple TV and 30 min of cast; the receiver input under them counts 0 (covered).
            Ended(Now.AddHours(-1), "appletv", "Night Train: Episode 1", 3600, 3600, app: "DRTV"),
            Ended(Now.AddDays(-2), "cast", "YouTube", 1800, 1800, app: "YouTube"),
            Ended(Now.AddHours(-1), "avr_input", "Media Player", 3700, 0, input: "MPLAY"),
            // The week before: 1 h.
            Ended(Now.AddDays(-9), "appletv", "Blue Hour", 3600, 3600, app: "Apple TV app"),
        };

        var report = InsightsReport.Build(events, At(Now), 7);

        Assert.Equal(5400, report.Watch.TotalSeconds);
        Assert.Equal("1 h 30 min", report.Watch.TotalText);
        Assert.Equal(2, report.Watch.Sessions);
        Assert.Equal(3600, report.Watch.PreviousSeconds);
        Assert.Equal(50, report.Watch.ChangePercent);
        Assert.Equal(7, report.Watch.ByDay.Count);
        Assert.Equal("appletv", report.Watch.ByKind[0].Key);
        Assert.Equal("Apple TV", report.Watch.ByKind[0].Label);
        Assert.Equal("DRTV", report.Watch.TopApps[0].Key);
        Assert.Equal("Night Train: Episode 1", report.Watch.TopTitles[0].Label);
        Assert.Equal("DRTV", report.Watch.TopTitles[0].Detail);
        Assert.Equal(5400, report.Watch.VideoSeconds);

        // The receiver time uses the input sessions' whole duration.
        Assert.Equal(3700, report.Receiver.InUseSeconds);
        Assert.Equal("Media Player", report.Receiver.Inputs[0].Label);
        Assert.Equal("STEREO", report.Receiver.SoundModes[0].Key);
    }

    [Fact]
    public void Rhythm_spreads_a_session_over_the_hours_it_ran()
    {
        // Monday 20:30–21:30.
        var events = new[] { Ended(new DateTime(2026, 11, 23, 21, 30, 0, DateTimeKind.Local), "appletv", "Show", 3600, 3600, app: "DRTV") };

        var report = InsightsReport.Build(events, At(Now), 7);

        Assert.Equal(1800, report.Rhythm.Cells[0][20]);
        Assert.Equal(1800, report.Rhythm.Cells[0][21]);
        Assert.Equal(0, report.Rhythm.PeakDay);
        Assert.Equal(20, report.Rhythm.PeakHour);
        Assert.Equal("Mondays around 20:00", report.Rhythm.PeakText);
        Assert.Equal(1.0, report.Rhythm.ByBlock.Single(b => b.Block == "Evening").Share);
    }

    [Fact]
    public void Control_counts_changes_by_origin_but_not_bookkeeping()
    {
        var t = Now.AddHours(-3);
        var events = new[]
        {
            Ev("avr", "Denon", "volume.settled", "external", t, new { from = 50, to = 52 }),
            Ev("avr", "Denon", "volume.settled", "phone_remote", t.AddMinutes(1), new { from = 52, to = 48 }),
            Ev("avr", "Denon", "input.changed", "mcp_agent", t.AddMinutes(2), new { from = "MPLAY", to = "NET" }),
            Ev("avr", "avr", "connection.changed", "system", t, new { connected = true }),
            // The first read after a start (empty "from") is not a change someone made.
            Ev("avr", "Denon", "sound_mode.changed", "external", t, new { from = "", to = "STEREO" }),
        };

        var report = InsightsReport.Build(events, At(Now), 1);

        Assert.Equal(3, report.Control.Total);
        Assert.Equal(1, report.Control.FromOutside);
        Assert.Equal(2, report.Control.FromApp);
        Assert.Contains(report.Control.Origins, o => o.Key == "mcp_agent" && o.Label == "AI agent (MCP)");
        Assert.Equal(2, report.Receiver.Volume.Changes);
        Assert.Null(report.Receiver.Volume.Average); // weighted by listening time, and there are no sessions
    }

    [Fact]
    public void Reading_back_across_a_summer_time_change_uses_that_days_offset()
    {
        // 180 days before late November is in May (summer time in most of Europe).
        var since = WatchTimeReport.ReadFrom(At(Now), 180);

        var firstDay = Now.Date.AddDays(-179);
        Assert.Equal(firstDay.AddHours(-12), since.LocalDateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(firstDay.AddHours(-12)), since.Offset);
    }

    [Fact]
    public void Volume_reads_in_receiver_decibels()
    {
        Assert.Equal("−26 dB", InsightsReport.Db(54));
        Assert.Equal("−30.5 dB", InsightsReport.Db(49.5));
        Assert.Equal("+2 dB", InsightsReport.Db(82));
    }

    [Fact]
    public void An_empty_log_gives_a_report_with_a_hint()
    {
        var report = InsightsReport.Build([], At(Now), 30);

        Assert.Equal(0, report.Watch.TotalSeconds);
        Assert.Null(report.Watch.ChangePercent);
        Assert.Equal(30, report.Watch.ByDay.Count);
        Assert.Null(report.Rhythm.PeakDay);
        Assert.Contains(report.Insights, i => i.Title == "Nothing watched yet");
    }

    [Fact]
    public void Activity_reads_as_sentences_and_marks_bookkeeping_quiet()
    {
        var t = Now.AddMinutes(-5);
        var items = ActivityFeed.Describe(new[]
        {
            Ev("avr", "Denon AVR-X2400H", "volume.settled", "phone_remote", t, new { from = 56.5, to = 54 }),
            Ev("avr", "avr", "connection.changed", "system", t, new { connected = true, model = "Denon AVR-X2400H" }),
            Ended(t, "appletv", "Night Train", 2700, 2700, app: "DRTV"),
            Ev("tv", "smart-tv", "app.changed", "external", t, new { appTitle = "HDMI 2" }),
        });

        Assert.Equal("Volume 56.5 → 54 (−26 dB)", items[0].Text);
        Assert.Equal("Phone remote", items[0].OriginLabel);
        Assert.False(items[0].Quiet);
        Assert.True(items[1].Quiet);
        Assert.Equal("Night Train · DRTV: 45 min", items[2].Text);
        Assert.Equal("TV: HDMI 2", items[3].Text);
        Assert.Equal("TV", items[3].Device);
    }

    [Fact]
    public void Activity_marks_covered_receiver_input_sessions_quiet_once_they_end()
    {
        var start = Now.AddHours(-2);
        var end = start.AddMinutes(45);

        // 1. Fully covered Media Player session under Apple TV (exclusiveSec = 0) -> both start and end become quiet.
        //    Pass newest-first (as QueryRecentEvents returns) to verify order-independence.
        var newestFirst = new[]
        {
            Ev("media", "avr", "session.ended", "external", end, new
            {
                kind = "avr_input",
                title = "Media Player",
                input = "MPLAY",
                durationSec = 2700,
                coveredSec = 2700,
                exclusiveSec = 0,
                coveredBy = new[] { "appletv:Stuen" },
            }),
            Ended(end, "appletv", "Night Train", 2700, 2700, app: "DRTV"),
            Ev("media", "appletv", "session.started", "external", start.AddSeconds(15), new
            {
                kind = "appletv",
                title = "Night Train",
                app = "DRTV",
                dwellConfirmedSec = 15,
            }),
            Ev("media", "avr", "session.started", "external", start.AddSeconds(15), new
            {
                kind = "avr_input",
                title = "Media Player",
                input = "MPLAY",
                dwellConfirmedSec = 15,
            }),
        };

        var items = ActivityFeed.Describe(newestFirst);
        Assert.True(items[0].Quiet); // Media Player session.ended
        Assert.False(items[1].Quiet); // Apple TV session.ended
        Assert.False(items[2].Quiet); // Apple TV session.started
        Assert.True(items[3].Quiet); // Media Player session.started (covered once ended!)

        // 2. Receiver TV Audio session predominantly covered by Chromecast (brief menu tail) -> both start & end quiet.
        var castStart = Now.AddMinutes(-50);
        var castEnd = castStart.AddMinutes(30);
        var castCovered = ActivityFeed.Describe(new[]
        {
            Ev("media", "avr", "session.started", "external", castStart.AddSeconds(15), new
            {
                kind = "avr_input",
                title = "TV Audio",
                input = "TV",
                dwellConfirmedSec = 15,
            }),
            Ev("media", "avr", "session.ended", "external", castEnd, new
            {
                kind = "avr_input",
                title = "TV Audio",
                input = "TV",
                durationSec = 1830,
                coveredSec = 1800,
                exclusiveSec = 30,
                coveredBy = new[] { "cast:Stuen 2" },
            }),
        });
        Assert.True(castCovered[0].Quiet);
        Assert.True(castCovered[1].Quiet);

        // 3. Uncovered receiver session (Blu-ray) and a still-running receiver session stay visible (Quiet = false).
        var activeAndUncovered = ActivityFeed.Describe(new[]
        {
            Ev("media", "avr", "session.started", "external", start.AddSeconds(15), new
            {
                kind = "avr_input",
                title = "Blu-ray",
                input = "BD",
                dwellConfirmedSec = 15,
            }),
            Ev("media", "avr", "session.ended", "external", end, new
            {
                kind = "avr_input",
                title = "Blu-ray",
                input = "BD",
                durationSec = 2700,
                coveredSec = 0,
                exclusiveSec = 2700,
            }),
            Ev("media", "avr", "session.started", "external", Now.AddMinutes(-5), new
            {
                kind = "avr_input",
                title = "Media Player",
                input = "MPLAY",
                dwellConfirmedSec = 15,
            }),
        });
        Assert.All(activeAndUncovered, item => Assert.False(item.Quiet));
    }

    private static JsonElement Session(DateTime end, string device, string kind, string title, int seconds, string? reason = null, double? avgVolume = null, string[]? coveredBy = null) =>
        Ev("media", device, "session.ended", "external", end, new
        {
            kind,
            title,
            durationSec = seconds,
            exclusiveSec = seconds,
            reason,
            avgVolume,
            coveredBy,
        });

    [Fact]
    public void A_session_the_app_restart_cut_in_two_counts_as_one()
    {
        // 20:00–20:40, the app restarts (2 min gap), 20:42–21:30: one sitting of 1 h 30 min.
        var day = Now.Date.AddHours(20);
        var events = new[]
        {
            Session(day.AddMinutes(40), "lg-tv", "tv_app", "Netflix", 40 * 60, reason: "app_shutdown"),
            Session(day.AddMinutes(90), "lg-tv", "tv_app", "Netflix", 48 * 60, reason: "standby"),
            // A real second session later the same evening stays apart.
            Session(day.AddMinutes(150), "lg-tv", "tv_app", "Netflix", 20 * 60, reason: "standby"),
        };

        var report = InsightsReport.Build(events, At(Now), 1);

        Assert.Equal(2, report.Watch.Sessions);
        Assert.Equal(90 * 60, report.Watch.Longest!.Seconds);
        Assert.Equal((90 + 20) * 60, report.Watch.TotalSeconds);
    }

    [Fact]
    public void Something_else_on_the_device_after_the_restart_keeps_the_sessions_apart()
    {
        // Netflix until the restart, one minute of Live TV, then Netflix again: two Netflix sessions, not one with the
        // Live TV minute counted in it.
        var day = Now.Date.AddHours(20);
        var events = new[]
        {
            Session(day.AddMinutes(30), "lg-tv", "tv_app", "Netflix", 30 * 60, reason: "app_shutdown"),
            Session(day.AddMinutes(32), "lg-tv", "tv_channel", "DR1", 60, reason: "switched"),
            Session(day.AddMinutes(63), "lg-tv", "tv_app", "Netflix", 30 * 60, reason: "standby"),
        };

        var report = InsightsReport.Build(events, At(Now), 1);

        Assert.Equal(3, report.Watch.Sessions);
        Assert.Equal(61 * 60, report.Watch.TotalSeconds);
    }

    [Fact]
    public void A_restart_gap_is_counted_once_when_the_tv_covers_the_receiver()
    {
        // Receiver 20:00–20:40 and 20:42–21:30 (10 min of its own in each part); TV 20:10–20:40 and 20:42–21:20.
        // 90 minutes of viewing, not 92.
        var day = Now.Date.AddHours(20);
        JsonElement Part(DateTime end, string device, string kind, string title, int duration, int exclusive, string reason) =>
            Ev("media", device, "session.ended", "external", end, new { kind, title, durationSec = duration, exclusiveSec = exclusive, reason });
        var events = new[]
        {
            Part(day.AddMinutes(40), "avr", "avr_input", "TV Audio", 40 * 60, 10 * 60, "app_shutdown"),
            Part(day.AddMinutes(40), "lg-tv", "tv_channel", "DR1", 30 * 60, 30 * 60, "app_shutdown"),
            Part(day.AddMinutes(90), "avr", "avr_input", "TV Audio", 48 * 60, 10 * 60, "standby"),
            Part(day.AddMinutes(80), "lg-tv", "tv_channel", "DR1", 38 * 60, 38 * 60, "standby"),
        };

        var report = InsightsReport.Build(events, At(Now), 1);

        Assert.Equal(90 * 60, report.Watch.TotalSeconds);
    }

    [Fact]
    public void Two_players_with_the_same_title_are_not_joined()
    {
        var day = Now.Date.AddHours(20);
        JsonElement Cast(DateTime end, string player, int seconds, string reason) =>
            Ev("media", "chromecast", "session.ended", "external", end, new { kind = "cast", title = "YouTube", castDevice = player, durationSec = seconds, exclusiveSec = seconds, reason });
        var events = new[]
        {
            Cast(day.AddMinutes(30), "Stuen 2", 30 * 60, "app_shutdown"),
            Cast(day.AddMinutes(62), "Køkken", 30 * 60, "standby"),
        };

        var report = InsightsReport.Build(events, At(Now), 1);

        Assert.Equal(2, report.Watch.Sessions);
        Assert.Equal(60 * 60, report.Watch.TotalSeconds);
    }

    [Fact]
    public void A_joined_session_keeps_the_volume_of_both_parts()
    {
        // Two equal parts at 30 and 60: the joined session plays at 45, not 30.
        var day = Now.Date.AddHours(20);
        var events = new[]
        {
            Session(day.AddMinutes(30), "lg-tv", "tv_app", "Netflix", 30 * 60, reason: "app_shutdown", avgVolume: 30),
            Session(day.AddMinutes(61), "lg-tv", "tv_app", "Netflix", 30 * 60, reason: "standby", avgVolume: 60),
        };

        var report = InsightsReport.Build(events, At(Now), 1);

        Assert.Equal(1, report.Watch.Sessions);
        Assert.Equal(45, report.Receiver.Volume.Average);
    }

    [Theory]
    [InlineData("TVA (1397)", "TVA")]
    [InlineData("Kriminalkommissær Barnaby XVII (1) (1)", "Kriminalkommissær Barnaby XVII")]
    [InlineData("Min familie og andre dyr III (7:8)", "Min familie og andre dyr III")]
    [InlineData("Blade Runner 2049", "Blade Runner 2049")]
    [InlineData("(1)", "(1)")]
    public void Episodes_group_under_their_series(string title, string series) =>
        Assert.Equal(series, InsightsReport.SeriesTitle(title));

    [Fact]
    public void Volume_is_weighted_by_listening_time_and_compared_by_app()
    {
        var t = Now.AddHours(-1);
        var events = new[]
        {
            // 2 h of Netflix at 56 and 1 h of DR1 at 41: the average is 51, not the middle of the two.
            Session(t, "lg-tv", "tv_app", "Netflix", 2 * 3600, avgVolume: 56),
            Session(t.AddMinutes(-150), "lg-tv", "tv_channel", "DR1", 3600, avgVolume: 41),
            // A settled change to 0 (the receiver switching inputs) is not the quietest level.
            Ev("avr", "Denon", "volume.settled", "external", t, new { from = 41, to = 0 }),
            Ev("avr", "Denon", "volume.settled", "external", t.AddMinutes(1), new { from = 0, to = 56 }),
        };

        var report = InsightsReport.Build(events, At(Now), 1);

        Assert.Equal(51, report.Receiver.Volume.Average);
        Assert.Equal(56, report.Receiver.Volume.Min);
        Assert.DoesNotContain(report.Receiver.Volume.Points, p => p.Volume == 0);
        Assert.Contains(report.Receiver.Volume.ByApp, a => a.Label == "Netflix" && a.Average == 56);
        Assert.Contains(report.Insights, i => i.Title == "Streaming plays louder than live TV" && i.Text.Contains("about 15 louder") && !i.Text.Contains("dB"));
    }

    [Fact]
    public void An_input_is_named_by_the_streamer_seen_on_it()
    {
        var t = Now.AddHours(-1);
        var events = new[]
        {
            Ev("media", "avr", "session.ended", "external", t, new
            {
                kind = "avr_input", title = "DVD Player (HDMI 1)", input = "DVD", durationSec = 3600, exclusiveSec = 0,
                coveredSec = 3600, coveredBy = new[] { "cast:Stuen 2" },
            }),
        };

        var report = InsightsReport.Build(events, At(Now), 1);

        Assert.Equal("Mostly Chromecast Stuen 2", report.Receiver.Inputs.Single().Detail);
    }

    [Fact]
    public void A_streamer_that_covered_a_moment_does_not_name_the_input()
    {
        var t = Now.AddHours(-1);
        var events = new[]
        {
            Ev("media", "avr", "session.ended", "external", t, new
            {
                kind = "avr_input", title = "DVD Player (HDMI 1)", input = "DVD", durationSec = 3600, exclusiveSec = 3580,
                coveredSec = 20, coveredBy = new[] { "cast:Stuen 2" }, coveredBySec = new Dictionary<string, int> { ["cast:Stuen 2"] = 20 },
            }),
        };

        var report = InsightsReport.Build(events, At(Now), 1);

        Assert.Null(report.Receiver.Inputs.Single().Detail);
    }

    [Fact]
    public void Only_automation_runs_that_acted_count_as_changes()
    {
        var t = Now.AddHours(-2);
        var events = new[]
        {
            Ev("control", "automation", "automation.triggered", "automation", t, new
            {
                automation = "Arrive home", trigger = "Webhook 'arrive-home'", outcome = "acted",
                summary = "Webhook 'arrive-home' → Scene 'Morning Jazz'",
            }),
            Ev("control", "automation", "automation.triggered", "automation", t.AddMinutes(5), new
            {
                automation = "Arrive home", trigger = "Webhook 'arrive-home'", outcome = "nothing",
                summary = "Webhook 'arrive-home' → nothing to do: No: the receiver is on.",
            }),
            // A read the Deck made (logged as an action before 2.9.3) is not a change.
            Ev("control", "chromecast", "plugin.action", "desktop_ui", t, new { pluginId = "chromecast", actionId = "now-playing" }),
            // An older run without an outcome is not counted at all.
            Ev("control", "automation", "automation.triggered", "automation", t, new { automation = "Old", trigger = "x" }),
            // The receiver following the TV's signal is not someone at the remote.
            Ev("avr", "Denon", "sound_mode.changed", "external", t, new { from = "MCH STEREO", to = "DOLBY D+" }),
            // The Hue plugin's status text (older entries) is not a light changing; a count is.
            Ev("lighting", "philips-hue", "lighting.state_changed", "external", t, new { from = "Connecting…", to = "Stopped" }),
            Ev("lighting", "philips-hue", "lighting.state_changed", "external", t.AddMinutes(1), new { from = 0, to = 3, total = 4 }),
        };

        var report = InsightsReport.Build(events, At(Now), 1);

        Assert.Equal(2, report.Control.Total); // the acted run and the light count
        Assert.Equal(2, report.Automations.Runs);
        Assert.Equal(1, report.Automations.Acted);
        Assert.Equal(1, report.Automations.Nothing);
        var row = Assert.Single(report.Automations.ByAutomation);
        Assert.Equal("nothing to do: No: the receiver is on.", row.LastSummary);
        Assert.Equal(1, report.Lighting.Changes);
        Assert.Equal("3 of 4 on", report.Lighting.LastState);

        var feed = ActivityFeed.Describe(events);
        Assert.True(feed.Single(i => i.Event == "plugin.action").Quiet); // an old artwork read
        var lights = feed.Where(i => i.Event == "lighting.state_changed").ToList();
        Assert.True(lights[0].Quiet); // the old status text
        Assert.Equal("Lights: 3 of 4 on", lights[1].Text);
        Assert.False(lights[1].Quiet);
        var acted = feed.First(i => i.Event == "automation.triggered");
        Assert.Equal("Automation Arrive home: Scene 'Morning Jazz'", acted.Text);
        Assert.False(acted.Quiet);
        Assert.True(feed.Where(i => i.Event == "automation.triggered").ElementAt(1).Quiet);
    }

    [Theory]
    [InlineData("192.168.1.16 • 3/4 lights on", true, 3, 4)]
    [InlineData("192.168.1.16 • 1/1 light on", true, 1, 1)]
    [InlineData("Connecting to 192.168.1.16...", false, 0, 0)]
    [InlineData("Stopped", false, 0, 0)]
    public void Hue_status_gives_the_lights_on(string status, bool ok, int on, int total)
    {
        Assert.Equal(ok, EventTelemetryMonitorService.TryReadLightsOn(status, out var readOn, out var readTotal));
        Assert.Equal(on, readOn);
        Assert.Equal(total, readTotal);
    }
}
