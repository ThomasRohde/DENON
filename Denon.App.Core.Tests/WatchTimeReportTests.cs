using System.Text.Json;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

public sealed class WatchTimeReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 21, 0, 0, TimeSpan.FromHours(1));

    private static JsonElement Ended(DateTimeOffset end, string device, object data) =>
        JsonSerializer.SerializeToElement(new { ts = end.ToString("yyyy-MM-ddTHH:mm:sszzz"), stream = "media", device, @event = "session.ended", data });

    [Fact]
    public void One_viewing_counts_once_and_groups_by_app_and_title()
    {
        var end = Now.AddHours(-1);
        var events = new[]
        {
            // Same 30 minutes seen by three sources (live 1.16 log shape).
            Ended(end, "chromecast", new { kind = "cast", title = "Spionhvalen", source = "DR TV", app = "DR TV", durationSec = 1800, watchedSec = 1700, coveredSec = 0, exclusiveSec = 1700 }),
            Ended(end, "lg-tv", new { kind = "tv_app", title = "DVD Player (HDMI 1)", durationSec = 1800, coveredSec = 1800, exclusiveSec = 0 }),
            Ended(end, "avr", new { kind = "avr_input", title = "TV Audio", input = "TV", durationSec = 1800, coveredSec = 1800, exclusiveSec = 0 }),
            // Live TV earlier the same evening.
            Ended(Now.AddHours(-3), "lg-tv", new { kind = "tv_channel", title = "Live TV (Ch 1 • DR1)", durationSec = 600, coveredSec = 0, exclusiveSec = 600 }),
            // Netflix: title is the app name, so it is not a title entry.
            Ended(Now.AddMinutes(-10), "chromecast", new { kind = "cast", title = "Netflix", app = "Netflix", hasTitle = false, durationSec = 300, watchedSec = 300, exclusiveSec = 300 }),
        };

        var report = WatchTimeReport.Build(events, Now, days: 7);

        Assert.Equal(1700 + 600 + 300, report.TotalSeconds);
        Assert.Equal("DR TV", report.ByApp[0].Key);
        Assert.Equal(1700, report.ByApp[0].Seconds);
        Assert.Contains(report.ByApp, e => e.Key == "Live TV" && e.Seconds == 600);
        Assert.DoesNotContain(report.ByApp, e => e.Key == "TV Audio"); // 0 exclusive seconds: not listed
        Assert.Single(report.TopTitles, t => t.Key == "Spionhvalen" && t.App == "DR TV");
        Assert.DoesNotContain(report.TopTitles, t => t.Key == "Netflix");
        Assert.Equal("43 min", report.TotalText);
    }

    [Fact]
    public void Sessions_without_exclusive_time_are_skipped()
    {
        var events = new[]
        {
            Ended(Now.AddHours(-2), "avr", new { kind = "spotify", title = "Song", source = "Spotify", durationSec = 240 }),
        };

        var report = WatchTimeReport.Build(events, Now, days: 1);

        Assert.Equal(0, report.TotalSeconds);
        Assert.Equal(0, report.Sessions);
    }

    [Fact]
    public void Sessions_count_on_their_start_day_inside_the_window()
    {
        var events = new[]
        {
            Ended(Now.AddDays(-8), "lg-tv", new { kind = "tv_channel", title = "Old", durationSec = 600, exclusiveSec = 600 }), // outside 7 days
            Ended(new DateTimeOffset(2026, 11, 22, 0, 30, 0, TimeSpan.FromHours(1)), "lg-tv",
                new { kind = "tv_channel", title = "Late", durationSec = 3600, exclusiveSec = 3600 }), // started 21 Nov 23:30
            Ended(Now.AddHours(-1), "other", new { note = "not a session" }),
        };

        var report = WatchTimeReport.Build(events, Now, days: 7);

        Assert.Equal(3600, report.TotalSeconds);
        Assert.Equal("2026-11-21", Assert.Single(report.ByDay).Date);
        Assert.Equal("2026-11-17", report.From);
        Assert.Equal("2026-11-23", report.To);
    }
}
