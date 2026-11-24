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
        Assert.Equal(50, report.Receiver.Volume.Average);
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
}
