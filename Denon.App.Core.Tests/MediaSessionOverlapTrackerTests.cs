using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

public sealed class MediaSessionOverlapTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 20, 0, 0, TimeSpan.FromHours(1));

    private static Dictionary<string, object?> Started(string kind, string title, params (string Key, object? Value)[] extra)
    {
        var data = new Dictionary<string, object?> { ["kind"] = kind, ["title"] = title, ["dwellConfirmedSec"] = 15 };
        foreach (var (k, v) in extra)
        {
            data[k] = v;
        }

        return data;
    }

    private static Dictionary<string, object?> Ended(Dictionary<string, object?> started, int durationSec)
    {
        var data = new Dictionary<string, object?>(started) { ["durationSec"] = durationSec };
        data.Remove("dwellConfirmedSec");
        return data;
    }

    private static int Int(IReadOnlyDictionary<string, object?>? data, string key) => Convert.ToInt32(data![key]);

    [Fact]
    public void Cast_through_tv_hdmi_and_denon_arc_counts_once()
    {
        var tracker = new MediaSessionOverlapTracker();
        var tv = Started("tv_app", "DVD Player (HDMI 1)");
        var avr = Started("avr_input", "TV Audio", ("input", "TV"));
        var cast = Started("cast", "Spionhvalen", ("castDevice", "Stuen 2"), ("receiverInput", "TV"));

        // TV and Denon on from 20:00, cast program 20:10-20:40, everything off at 21:00.
        tracker.Annotate("lg-tv", "session.started", tv, T0.AddSeconds(15));
        tracker.Annotate("avr", "session.started", avr, T0.AddSeconds(15));
        tracker.Annotate("chromecast", "session.started", cast, T0.AddMinutes(10).AddSeconds(15));
        var castEnd = tracker.Annotate("chromecast", "session.ended", Ended(cast, 1800), T0.AddMinutes(40));
        var tvEnd = tracker.Annotate("lg-tv", "session.ended", Ended(tv, 3600), T0.AddMinutes(60));
        var avrEnd = tracker.Annotate("avr", "session.ended", Ended(avr, 3600), T0.AddMinutes(60));

        Assert.Equal(1800, Int(castEnd, "exclusiveSec"));
        Assert.Equal(0, Int(castEnd, "coveredSec"));

        Assert.Equal(1800, Int(tvEnd, "coveredSec"));
        Assert.Equal(1800, Int(tvEnd, "exclusiveSec"));
        Assert.Contains("cast:Stuen 2", (IEnumerable<string>)tvEnd!["coveredBy"]!);

        // The Denon is covered by the TV session (ARC) for the whole hour.
        Assert.Equal(3600, Int(avrEnd, "coveredSec"));
        Assert.Equal(0, Int(avrEnd, "exclusiveSec"));

        // Sum of exclusiveSec = the real hour, not 2.5 hours.
        Assert.Equal(3600, Int(castEnd, "exclusiveSec") + Int(tvEnd, "exclusiveSec") + Int(avrEnd, "exclusiveSec"));
    }

    [Fact]
    public void Live_tv_channel_is_not_covered_by_cast()
    {
        var tracker = new MediaSessionOverlapTracker();
        var cast = Started("cast", "Show", ("castDevice", "Stuen 2"));
        var tv = Started("tv_channel", "Live TV (Ch 1 • DR1)");
        tracker.Annotate("chromecast", "session.started", cast, T0.AddSeconds(15));
        tracker.Annotate("lg-tv", "session.started", tv, T0.AddSeconds(15));
        var tvEnd = tracker.Annotate("lg-tv", "session.ended", Ended(tv, 600), T0.AddMinutes(10));

        Assert.Equal(0, Int(tvEnd, "coveredSec"));
        Assert.Equal(600, Int(tvEnd, "exclusiveSec"));
        Assert.False(tvEnd!.ContainsKey("coveredBy"));
    }

    [Fact]
    public void Paused_cast_behind_live_tv_is_not_counted_twice()
    {
        // Live log 2026-11-23: DR TV paused on the Chromecast for 10 min while the TV showed live DR1.
        var tracker = new MediaSessionOverlapTracker();
        var tv = Started("tv_channel", "Live TV (Ch 1 • DR1)");
        var cast = Started("cast", "Smagen af Italien", ("castDevice", "Stuen 2"), ("receiverInput", "TV"));
        tracker.Annotate("lg-tv", "session.started", tv, T0.AddSeconds(15));
        tracker.Annotate("chromecast", "session.started", cast, T0.AddSeconds(15));

        var castEndData = Ended(cast, 600);
        castEndData["watchedSec"] = 0;
        var castEnd = tracker.Annotate("chromecast", "session.ended", castEndData, T0.AddMinutes(10));
        var tvEnd = tracker.Annotate("lg-tv", "session.ended", Ended(tv, 600), T0.AddMinutes(10));

        Assert.Equal(0, Int(castEnd, "exclusiveSec"));
        Assert.Contains("tv_channel:lg-tv", (IEnumerable<string>)castEnd!["coveredBy"]!);
        Assert.Equal(600, Int(tvEnd, "exclusiveSec"));
    }

    [Fact]
    public void Cast_exclusive_time_is_capped_at_watched_time()
    {
        var tracker = new MediaSessionOverlapTracker();
        var cast = Started("cast", "Show", ("castDevice", "Stuen 2"));
        tracker.Annotate("chromecast", "session.started", cast, T0.AddSeconds(15));
        var data = Ended(cast, 900);
        data["watchedSec"] = 600; // 5 min paused
        var end = tracker.Annotate("chromecast", "session.ended", data, T0.AddMinutes(15));

        Assert.Equal(0, Int(end, "coveredSec"));
        Assert.Equal(600, Int(end, "exclusiveSec"));
    }

    [Fact]
    public void Heos_music_on_net_is_not_covered_by_tv()
    {
        var tracker = new MediaSessionOverlapTracker();
        var tv = Started("tv_app", "Netflix");
        var music = Started("spotify", "Song", ("input", "NET"));
        tracker.Annotate("lg-tv", "session.started", tv, T0.AddSeconds(15));
        tracker.Annotate("avr", "session.started", music, T0.AddSeconds(15));
        var musicEnd = tracker.Annotate("avr", "session.ended", Ended(music, 300), T0.AddMinutes(5));

        Assert.Equal(300, Int(musicEnd, "exclusiveSec"));
    }

    [Fact]
    public void Overlapping_coverers_are_not_subtracted_twice()
    {
        var tracker = new MediaSessionOverlapTracker();
        var avr = Started("avr_input", "TV Audio", ("input", "TV"));
        var tv = Started("tv_app", "DVD Player (HDMI 1)");
        var cast = Started("cast", "Show", ("castDevice", "Stuen 2"), ("receiverInput", "TV"));
        tracker.Annotate("avr", "session.started", avr, T0.AddSeconds(15));
        tracker.Annotate("lg-tv", "session.started", tv, T0.AddSeconds(15));
        tracker.Annotate("chromecast", "session.started", cast, T0.AddSeconds(15));
        var avrEnd = tracker.Annotate("avr", "session.ended", Ended(avr, 600), T0.AddMinutes(10));

        Assert.Equal(600, Int(avrEnd, "coveredSec"));
        Assert.Equal(0, Int(avrEnd, "exclusiveSec"));
    }

    [Fact]
    public void Other_events_pass_through_unchanged()
    {
        var tracker = new MediaSessionOverlapTracker();
        var data = new Dictionary<string, object?> { ["castDevice"] = "Stuen 2", ["playerState"] = "PLAYING" };
        Assert.Same(data, tracker.Annotate("chromecast", "cast.playback_changed", data, T0));
    }
}
