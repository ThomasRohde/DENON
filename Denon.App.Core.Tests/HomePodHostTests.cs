using System.Globalization;
using System.Text.Json;
using Denon.App.Core.Insights;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.PluginSdk;
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

    private static PluginRecordSnapshot HomePodPlugin(IReadOnlyDictionary<string, string> details) => new()
    {
        Id = "homepod",
        Name = "HomePod",
        Version = "0.2.0",
        Author = "test",
        Description = "",
        Category = "",
        AccentHex = "#000000",
        IconGlyph = "POD",
        Capabilities = PluginCapabilities.Actions,
        Origin = PluginOrigin.ExternalDll,
        IsEnabled = true,
        IsStarted = true,
        Health = PluginHealthState.Connected,
        StatusText = "HOMEPOD • HomePod Left + Right • Apple Music • DR P4 København",
        StatusDetails = details,
    };

    private static readonly ReceiverPlayerInfo Player = new("Denon AVR-X2400H", 101, "Denon AVR-X2400H", "3.1", "192.168.1.3", "wired", 1, "SER");

    private static ReceiverStatus Receiver(bool on) => new(
        Player, "stop", 32, false, new NowPlayingMedia("song", "", "", "", "", "", "", null, null), DateTimeOffset.Now,
        IsPoweredOn: on, IsMainZoneOn: on, InputSource: "TV", SoundMode: "MCH STEREO");

    private static readonly Dictionary<string, string> PlayingRadio = new()
    {
        ["kind"] = "homepod",
        ["speaker"] = "HomePod Left + Right",
        ["app"] = "Apple Music",
        ["playbackState"] = "Playing",
        ["title"] = "DR P4 København",
    };

    [Fact]
    public void While_the_receiver_sleeps_the_hero_shows_the_homepod_that_plays()
    {
        var resolved = NowPlayingResolver.Resolve(Receiver(on: false), null, [HomePodPlugin(PlayingRadio)]);

        Assert.Equal("DR P4 København", resolved.Title);
        Assert.Equal("Apple Music • HomePod Left + Right", resolved.Detail);
        Assert.Equal("Playing", resolved.PlayStateLabel);
        Assert.Equal("homepod", resolved.Cast?.PluginId);
        Assert.Equal("HomePod Left + Right", resolved.Cast?.DeviceName); // play/pause and artwork go to this speaker
        Assert.True(resolved.Cast?.IsPlaying);
    }

    [Fact]
    public void An_awake_receiver_or_a_silent_homepod_keeps_the_receiver_hero()
    {
        Assert.NotEqual("DR P4 København", NowPlayingResolver.Resolve(Receiver(on: true), null, [HomePodPlugin(PlayingRadio)]).Title);
        Assert.Equal("Receiver in Standby", NowPlayingResolver.Resolve(Receiver(on: false), null, [HomePodPlugin(new Dictionary<string, string>())]).Title);
    }

    [Fact]
    public void A_homepod_paused_long_ago_leaves_the_hero()
    {
        // Its own speaker name: the resolver remembers when each stream last played.
        var playing = new Dictionary<string, string>(PlayingRadio) { ["speaker"] = "Stue test" };
        var paused = new Dictionary<string, string>(playing) { ["playbackState"] = "Paused" };
        var t = new DateTimeOffset(2026, 11, 26, 4, 0, 0, TimeSpan.Zero);

        Assert.Equal("Playing", NowPlayingResolver.TryResolveHomePod([HomePodPlugin(playing)], now: t)?.PlayStateLabel);
        Assert.Equal("Paused", NowPlayingResolver.TryResolveHomePod([HomePodPlugin(paused)], now: t.AddMinutes(5))?.PlayStateLabel);
        Assert.Null(NowPlayingResolver.TryResolveHomePod([HomePodPlugin(paused)], now: t.AddMinutes(11)));

        // Never seen playing in this run (paused since before the app started): not shown.
        var neverPlayed = new Dictionary<string, string>(paused) { ["speaker"] = "Køkken test" };
        Assert.Null(NowPlayingResolver.TryResolveHomePod([HomePodPlugin(neverPlayed)], now: t));
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
