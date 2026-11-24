using System.Globalization;
using System.Text.Json;
using Denon.App.Core.Insights;
using Denon.App.Core.Services;
using Denon.App.Desktop.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>The app side of the HomePod plugin: overlap, Insights, the feed and the update check.</summary>
public sealed class HomePodHostTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 20, 0, 0, TimeSpan.FromHours(1));

    private static Dictionary<string, object?> Session(string speaker, string title, int? durationSec = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["kind"] = "homepod",
            ["title"] = title,
            ["app"] = "Apple Music",
            ["homepodDevice"] = speaker,
        };
        if (durationSec is { } d)
        {
            data["durationSec"] = d;
            data["watchedSec"] = d;
        }
        else
        {
            data["dwellConfirmedSec"] = 15;
        }

        return data;
    }

    [Fact]
    public void Two_homepods_that_play_at_once_both_count_in_full()
    {
        var tracker = new MediaSessionOverlapTracker();
        tracker.Annotate("homepod", "session.started", Session("Køkken", "Song A"), T0.AddSeconds(15));
        tracker.Annotate("homepod", "session.started", Session("HomePod Left + Right", "Radio"), T0.AddSeconds(15));
        // TV in the living room at the same time: no effect on HomePod time.
        tracker.Annotate("lg-tv", "session.started", new Dictionary<string, object?> { ["kind"] = "tv_channel", ["title"] = "TVA", ["dwellConfirmedSec"] = 15 }, T0.AddSeconds(15));

        var kitchen = tracker.Annotate("homepod", "session.ended", Session("Køkken", "Song A", 600), T0.AddMinutes(10));
        var pair = tracker.Annotate("homepod", "session.ended", Session("HomePod Left + Right", "Radio", 1200), T0.AddMinutes(20));

        Assert.Equal(600, Convert.ToInt32(kitchen!["exclusiveSec"]));
        Assert.Equal(1200, Convert.ToInt32(pair!["exclusiveSec"]));
    }

    private static readonly DateTime Now = new(2026, 11, 24, 22, 0, 0, DateTimeKind.Local);

    private static JsonElement Ev(string name, DateTime local, object data, string device = "homepod")
    {
        var ts = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        return JsonSerializer.SerializeToElement(new { ts, block = InsightsReport.BlockOf(local.Hour), stream = "media", device, @event = name, origin = "external", data });
    }

    [Fact]
    public void Insights_count_homepod_time_as_listening_by_app()
    {
        var events = new[]
        {
            Ev("session.ended", Now.AddHours(-1), new { kind = "homepod", title = "DR P4 København", app = "AirMusic (AirPlay)", homepodDevice = "HomePod Left + Right", durationSec = 1800, watchedSec = 1800, exclusiveSec = 1800 }),
        };

        var report = InsightsReport.Build(events, new DateTimeOffset(Now, TimeZoneInfo.Local.GetUtcOffset(Now)), 7);

        Assert.Equal(1800, report.Watch.AudioSeconds);
        Assert.Equal(0, report.Watch.VideoSeconds);
        Assert.Contains(report.Watch.ByKind, k => k.Key == "homepod" && k.Label == "HomePod");
        Assert.Contains(report.Watch.TopApps, a => a.Label == "AirMusic (AirPlay)");
        Assert.Contains(report.Watch.TopTitles, t => t.Label == "DR P4 København");
    }

    [Fact]
    public void The_feed_names_homepod_events()
    {
        var linked = ActivityFeed.Describe(Ev("homepod.connected", Now, new { homepodDevice = "Køkken" }));
        Assert.Equal("Køkken linked", linked?.Text);
        Assert.True(linked?.Quiet);
        Assert.Equal("HomePod", linked?.Device);

        var paused = ActivityFeed.Describe(Ev("homepod.paused_by_automation", Now, new { homepodDevice = "Køkken" }));
        Assert.Equal("Køkken paused: the receiver turned on", paused?.Text);

        var started = ActivityFeed.Describe(Ev("session.started", Now, new { kind = "homepod", title = "Song A", app = "Apple Music" }));
        Assert.Equal("Started Song A · Apple Music", started?.Text);
    }

    [Fact]
    public void Homepod_plugin_releases_are_tracked()
    {
        const string releases = """
            [
              { "tag_name": "homepod-v0.1.0", "draft": false, "prerelease": false, "body": "first", "html_url": "https://x/hp-01",
                "assets": [ { "name": "Denon.Plugin.HomePod-0.1.0.zip", "browser_download_url": "https://x/hp-0.1.0.zip" } ] }
            ]
            """;

        var latest = UpdateService.PickLatestPlugins(releases);
        Assert.Equal("homepod-v0.1.0", latest["homepod"].Tag);
        Assert.True(UpdateService.IsTrackablePlugin("homepod"));
    }
}
