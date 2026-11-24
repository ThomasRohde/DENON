using System.Net;
using Denon.Plugin.AppleTv.Discovery;
using Denon.Plugin.AppleTv.Protocol.Mrp;
using Xunit;

namespace Denon.Plugin.HomePod.Tests;

public sealed class HomePodGroupTests
{
    private const string PairGid = "AC6297B5-12FE-5C0D-AE2A-D17B43B34162+0+6A1DA44F-771E-4360-8BDD-15A8023B3B1A";

    private static AppleDevice Pod(string name, string ip, string deviceId, params (string Key, string Value)[] txt)
    {
        var records = txt.ToDictionary(t => t.Key, t => t.Value);
        records["model"] = "AudioAccessory5,1";
        var airplay = new MdnsService(AppleServiceTypes.AirPlay, $"{name}.{AppleServiceTypes.AirPlay}", null, 7000, records, IPAddress.Parse(ip));
        return new AppleDevice(name, IPAddress.Parse(ip), "AudioAccessory5,1", "HomePod mini", AppleDeviceKind.HomePod, "27.2.1", deviceId, [airplay]);
    }

    [Fact]
    public void A_stereo_pair_is_one_speaker_led_by_the_group_leader()
    {
        // TXT records as the user's HomePods announce them (2026-11-24).
        var left = Pod("HomePod Left", "192.168.1.8", "82:84:80:69:A8:87", ("gid", PairGid), ("igl", "1"), ("gcgl", "1"));
        var right = Pod("HomePod Right", "192.168.1.9", "F6:1A:B2:98:68:6B", ("gid", PairGid), ("igl", "0"), ("pgid", PairGid), ("pgcgl", "1"));
        var kitchen = Pod("Køkken", "192.168.1.7", "CE:2E:F4:52:71:DE", ("gid", "9F59B428-563A-4488-AE84-AA64BAB90BA1+F14EF702"), ("igl", "1"));

        var speakers = HomePodGroups.Build([right, kitchen, left]);

        Assert.Equal(2, speakers.Count);
        var pair = speakers.Single(s => s.Members.Count == 2);
        Assert.Equal("HomePod Left + Right", pair.Name);
        Assert.Equal("192.168.1.8", pair.Host); // The leader (igl=1).
        Assert.Equal("82:84:80:69:A8:87", pair.Id);
        Assert.Equal("HomePod mini stereo pair", pair.ModelName);
        Assert.Equal(["HomePod Left", "HomePod Right"], pair.Members);

        var single = speakers.Single(s => s.Members.Count == 1);
        Assert.Equal("Køkken", single.Name);
        Assert.Equal("192.168.1.7", single.Host);
    }

    [Fact]
    public void A_short_multi_room_group_does_not_merge_speakers()
    {
        // Køkken joins the living room for a song: same gid, but no pgid (not a stereo pair).
        var left = Pod("HomePod Left", "192.168.1.8", "L", ("gid", PairGid), ("igl", "1"));
        var right = Pod("HomePod Right", "192.168.1.9", "R", ("gid", PairGid), ("igl", "0"), ("pgid", PairGid));
        var kitchen = Pod("Køkken", "192.168.1.7", "K", ("gid", "ANOTHER"), ("igl", "0"));

        var speakers = HomePodGroups.Build([left, right, kitchen]);

        Assert.Contains(speakers, s => s.Name == "Køkken" && s.Members.Count == 1);
        Assert.Contains(speakers, s => s.Members.Count == 2);
    }

    [Fact]
    public void Apple_tvs_and_other_devices_are_not_speakers()
    {
        var tv = new AppleDevice("Stuen (2)", IPAddress.Parse("192.168.1.2"), "AppleTV6,2", "Apple TV 4K", AppleDeviceKind.AppleTv, "26.6.1", "8A", []);
        Assert.Empty(HomePodGroups.Build([tv]));
    }

    [Theory]
    [InlineData("HomePod Left|HomePod Right", "HomePod Left + Right")]
    [InlineData("Stue venstre|Stue højre", "Stue venstre + højre")]
    [InlineData("Left|Right", "Left + Right")]
    [InlineData("Køkken", "Køkken")]
    public void Pair_names_share_their_first_words(string names, string expected)
    {
        Assert.Equal(expected, HomePodGroups.PairName(names.Split('|')));
    }
}

public sealed class HomePodStateTests
{
    private static HomePodState Playing(string bundle, string? name = null, string? title = "Song") => new HomePodState("id")
    {
        Link = HomePodLink.Connected,
        NowPlaying = NowPlayingSnapshot.Empty with { AppBundleId = bundle, AppName = name, Title = title, State = MrpPlaybackState.Playing },
    };

    [Fact]
    public void Airplay_streams_name_the_sending_app()
    {
        Assert.Equal("AirMusic (AirPlay)", Playing(HomePodApps.AirPlayBundle, "AirMusic", "DR P4 København").AppName);
        Assert.Equal("AirPlay", Playing(HomePodApps.AirPlayBundle).AppName);
        Assert.Equal("Apple Music", Playing("com.apple.Music", "Music").AppName);
        Assert.Equal("Podcasts", Playing("com.apple.podcasts").AppName);
    }

    [Fact]
    public void System_sounds_and_a_lost_link_are_not_media()
    {
        Assert.False(Playing("com.apple.SoundBoard").HasMedia);
        Assert.True(Playing("com.apple.Music").IsPlaying);
        Assert.False((Playing("com.apple.Music") with { Link = HomePodLink.Disconnected }).HasMedia);
    }
}

public sealed class HomePodListenLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 20, 0, 0, TimeSpan.Zero);

    private static readonly HomePodSpeakerConfig Pair = new()
    {
        Id = "L",
        Name = "HomePod Left + Right",
        ModelName = "HomePod mini stereo pair",
        Members = ["HomePod Left", "HomePod Right"],
    };

    private static HomePodState State(MrpPlaybackState playback, string? title, string bundle = HomePodApps.AirPlayBundle, string? artist = null) => new HomePodState("L")
    {
        Link = HomePodLink.Connected,
        NowPlaying = NowPlayingSnapshot.Empty with { AppBundleId = bundle, AppName = "AirMusic", Title = title, Artist = artist, State = playback },
    };

    private static (HomePodListenLog Log, List<(string Name, Dictionary<string, object?> Data)> Events) NewLog()
    {
        var events = new List<(string, Dictionary<string, object?>)>();
        return (new HomePodListenLog((name, data) => events.Add((name, data))), events);
    }

    [Fact]
    public void A_stream_that_plays_past_the_dwell_is_one_session()
    {
        var (log, events) = NewLog();
        log.Update(Pair, State(MrpPlaybackState.Playing, "DR P4 København"), T0);
        log.Tick(T0.AddSeconds(10));
        Assert.Empty(events); // Still in the dwell.

        log.Tick(T0.AddSeconds(15));
        var started = Assert.Single(events);
        Assert.Equal("session.started", started.Name);
        Assert.Equal("homepod", started.Data["kind"]);
        Assert.Equal("DR P4 København", started.Data["title"]);
        Assert.Equal("AirMusic (AirPlay)", started.Data["app"]);
        Assert.Equal("HomePod Left + Right", started.Data["homepodDevice"]);
        Assert.Equal("HomePod Left, HomePod Right", started.Data["homepodSpeakers"]);

        log.Tick(T0.AddMinutes(30));
        log.Update(Pair, State(MrpPlaybackState.Unknown, null) with { NowPlaying = NowPlayingSnapshot.Empty }, T0.AddMinutes(30));
        var ended = events[^1];
        Assert.Equal("session.ended", ended.Name);
        Assert.Equal(1800, ended.Data["durationSec"]);
        Assert.Equal(1800, ended.Data["watchedSec"]);
        Assert.Equal("stopped", ended.Data["reason"]);
    }

    [Fact]
    public void A_short_track_is_not_logged_and_a_new_track_switches()
    {
        var (log, events) = NewLog();
        log.Update(Pair, State(MrpPlaybackState.Playing, "Intro", "com.apple.Music", "Band"), T0);
        log.Update(Pair, State(MrpPlaybackState.Playing, "Song A", "com.apple.Music", "Band"), T0.AddSeconds(8));
        log.Tick(T0.AddSeconds(24));
        Assert.Equal("Song A", Assert.Single(events).Data["title"]);
        Assert.Equal("Band", events[0].Data["artist"]);

        log.Update(Pair, State(MrpPlaybackState.Playing, "Song B", "com.apple.Music", "Band"), T0.AddSeconds(200));
        Assert.Equal("session.ended", events[^1].Name);
        Assert.Equal("switched", events[^1].Data["reason"]);
        Assert.Equal("Song A", events[^1].Data["title"]);
    }

    [Fact]
    public void A_long_pause_ends_the_session_and_paused_time_does_not_count()
    {
        var (log, events) = NewLog();
        log.Update(Pair, State(MrpPlaybackState.Playing, "Podcast", "com.apple.podcasts"), T0);
        log.Tick(T0.AddSeconds(60));
        log.Update(Pair, State(MrpPlaybackState.Paused, "Podcast", "com.apple.podcasts"), T0.AddSeconds(60));
        log.Tick(T0.AddSeconds(60) + HomePodListenLog.PauseTimeout);

        var ended = events[^1];
        Assert.Equal("paused", ended.Data["reason"]);
        Assert.Equal(60, ended.Data["watchedSec"]);
    }

    [Fact]
    public void A_title_that_arrives_late_keeps_the_session_and_stop_ends_all()
    {
        var (log, events) = NewLog();
        log.Update(Pair, State(MrpPlaybackState.Playing, null), T0);
        log.Update(Pair, State(MrpPlaybackState.Playing, "DR P1"), T0.AddSeconds(3));
        log.Tick(T0.AddSeconds(20));
        Assert.Equal("DR P1", Assert.Single(events).Data["title"]);

        log.EndAll(T0.AddSeconds(80), "app_shutdown");
        Assert.Equal("app_shutdown", events[^1].Data["reason"]);
        Assert.Equal(2, events.Count);
    }
}
