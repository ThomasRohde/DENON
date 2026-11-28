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
    public void Hero_details_name_the_speaker_app_title_and_only_matching_artwork()
    {
        var speaker = new HomePodSpeakerConfig { Id = "L", Name = "HomePod Left + Right", Members = ["HomePod Left", "HomePod Right"] };
        var state = Playing("com.apple.Music", "Music", "DR P4 København") with { ArtworkKey = "old|key", ArtworkPath = @"C:\art\old.jpg" };

        var details = HomePodPlugin.HeroDetails((speaker, state));
        Assert.Equal("homepod", details["kind"]);
        Assert.Equal("HomePod Left + Right", details["speaker"]);
        Assert.Equal("Apple Music", details["app"]);
        Assert.Equal("Playing", details["playbackState"]);
        Assert.Equal("DR P4 København", details["title"]);
        Assert.False(details.ContainsKey("artworkPath")); // The artwork belongs to an older item.

        Assert.Empty(HomePodPlugin.HeroDetails((speaker, new HomePodState("L"))));
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

    private static void TickEvery5s(HomePodListenLog log, DateTimeOffset from, DateTimeOffset to)
    {
        for (var t = from; t <= to; t = t.AddSeconds(5))
        {
            log.Tick(t);
        }
    }

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

        TickEvery5s(log, T0.AddSeconds(15), T0.AddMinutes(30)); // the plugin ticks every few seconds
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

    [Fact]
    public void A_pc_sleep_while_the_speaker_plays_does_not_add_hours()
    {
        var (log, events) = NewLog();
        log.Update(Pair, State(MrpPlaybackState.Playing, "DR P4"), T0);
        TickEvery5s(log, T0.AddSeconds(5), T0.AddSeconds(60));

        // The PC sleeps for three hours; the next tick comes after it wakes.
        var wake = T0.AddSeconds(60).AddHours(3);
        log.Tick(wake);
        log.Update(Pair, State(MrpPlaybackState.Unknown, null) with { NowPlaying = NowPlayingSnapshot.Empty }, wake.AddSeconds(5));

        var ended = events[^1];
        Assert.Equal("session.ended", ended.Name);
        Assert.Equal(60 + 120 + 5, ended.Data["watchedSec"]); // each step counts at most 120 s
    }

    [Fact]
    public void A_second_of_play_then_a_pause_is_not_a_session()
    {
        var (log, events) = NewLog();
        log.Update(Pair, State(MrpPlaybackState.Playing, "Song"), T0);
        log.Update(Pair, State(MrpPlaybackState.Paused, "Song"), T0.AddSeconds(1));
        TickEvery5s(log, T0.AddSeconds(5), T0.AddSeconds(120));
        Assert.Empty(events);

        log.Update(Pair, State(MrpPlaybackState.Playing, "Song"), T0.AddSeconds(125));
        log.Tick(T0.AddSeconds(130));
        Assert.Equal("session.started", Assert.Single(events).Name);
    }
}

public sealed class HomePodMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    // Made-up ids and addresses. Each HomePod's device id is its first letter ("HomePod Left" is "L"), as in the
    // speaker ids below.
    private static string Key(string name) => name.StartsWith("HomePod ", StringComparison.Ordinal) ? name[8..9] : name[..1];

    private static DiscoveredSpeaker Found(string id, string host, params string[] members) =>
        new(id, HomePodGroups.PairName(members), host, 7000, "HomePod mini", "27.2", members, members.Select(Key).ToList());

    private static HomePodSpeakerConfig Saved(string id, string host, params string[] members) =>
        new() { Id = id, Name = HomePodGroups.PairName(members), Host = host, Members = [.. members], MemberIds = [.. members.Select(Key)] };

    [Fact]
    public void A_pair_saved_without_member_ids_still_follows_a_leader_switch()
    {
        // Saved by the earlier plugin: no member ids, and the pair is off.
        var pair = new HomePodSpeakerConfig { Id = "L", Name = "HomePod Left + Right", Host = "192.168.1.8", Members = ["HomePod Left", "HomePod Right"], Enabled = false };

        var merge = HomePodGroups.Merge([pair], [Found("R", "192.168.1.9", "HomePod Left", "HomePod Right")], Now);

        var speaker = Assert.Single(merge.Speakers);
        Assert.Same(pair, speaker);
        Assert.Equal("R", speaker.Id);
        Assert.False(speaker.Enabled); // the saved setting stays
        Assert.Equal(["L", "R"], speaker.MemberIds);
        Assert.Equal("L", Assert.Single(merge.Restart).OldId);
    }

    [Fact]
    public void A_single_saved_without_member_ids_goes_when_it_joins_a_pair()
    {
        var left = Saved("L", "192.168.1.8", "HomePod Left");
        var right = new HomePodSpeakerConfig { Id = "R", Name = "HomePod Right", Host = "192.168.1.9", Members = ["HomePod Right"] };

        var merge = HomePodGroups.Merge([left, right], [Found("L", "192.168.1.8", "HomePod Left", "HomePod Right")], Now);

        Assert.Same(left, Assert.Single(merge.Speakers));
        Assert.Same(right, Assert.Single(merge.Removed));
    }

    [Fact]
    public void Two_speakers_with_the_same_name_stay_two_speakers()
    {
        // Two single HomePods both named "Kitchen": the same name, different devices.
        var a = new HomePodSpeakerConfig { Id = "A", Name = "Kitchen", Host = "192.168.1.7", Members = ["Kitchen"], MemberIds = ["A"], Enabled = false };
        var b = new HomePodSpeakerConfig { Id = "B", Name = "Kitchen", Host = "192.168.1.8", Members = ["Kitchen"], MemberIds = ["B"] };

        var merge = HomePodGroups.Merge(
            [a, b],
            [new("A", "Kitchen", "192.168.1.7", 7000, "HomePod mini", "27.2", ["Kitchen"], ["A"]),
             new("B", "Kitchen", "192.168.1.8", 7000, "HomePod mini", "27.2", ["Kitchen"], ["B"])],
            Now);

        Assert.Equal(2, merge.Speakers.Count);
        Assert.Empty(merge.Removed);
        Assert.Empty(merge.Restart); // nothing moved, so no client restarts
        Assert.False(merge.Speakers.Single(s => s.Id == "A").Enabled); // its saved setting stays
    }

    [Fact]
    public void A_leader_switch_restarts_the_speaker_under_its_old_id()
    {
        var pair = Saved("L", "192.168.1.8", "HomePod Left", "HomePod Right");

        var merge = HomePodGroups.Merge([pair], [Found("R", "192.168.1.9", "HomePod Left", "HomePod Right")], Now);

        var speaker = Assert.Single(merge.Speakers);
        Assert.Equal("R", speaker.Id);
        Assert.Equal("192.168.1.9", speaker.Host);
        var (oldId, restarted) = Assert.Single(merge.Restart);
        Assert.Equal("L", oldId); // the running client and the listen-log slot are keyed by this one
        Assert.Same(speaker, restarted);
        Assert.Empty(merge.Removed);
        Assert.Equal(0, merge.Added);
    }

    [Fact]
    public void Two_single_speakers_that_become_a_pair_are_one_speaker()
    {
        var left = Saved("L", "192.168.1.8", "HomePod Left");
        var right = Saved("R", "192.168.1.9", "HomePod Right");

        var merge = HomePodGroups.Merge([left, right], [Found("L", "192.168.1.8", "HomePod Left", "HomePod Right")], Now);

        var speaker = Assert.Single(merge.Speakers);
        Assert.Equal("L", speaker.Id);
        Assert.Equal(["HomePod Left", "HomePod Right"], speaker.Members);
        Assert.Same(right, Assert.Single(merge.Removed)); // its client stops and its session ends under "R"
        Assert.Empty(merge.Restart); // same leader, same address: the link stays
    }

    [Fact]
    public void A_pair_led_by_the_other_speaker_keeps_that_speaker_and_drops_the_old_pair()
    {
        // A stale single "R" and the pair saved under "L"; the pair now answers with "R" as leader.
        var pair = Saved("L", "192.168.1.8", "HomePod Left", "HomePod Right");
        var right = Saved("R", "192.168.1.9", "HomePod Right");

        var merge = HomePodGroups.Merge([pair, right], [Found("R", "192.168.1.9", "HomePod Left", "HomePod Right")], Now);

        var speaker = Assert.Single(merge.Speakers);
        Assert.Same(right, speaker);
        Assert.Equal(2, speaker.Members.Count);
        Assert.Same(pair, Assert.Single(merge.Removed));
    }

    [Fact]
    public void New_moved_and_unchanged_speakers()
    {
        var kitchen = Saved("K", "192.168.1.7", "Køkken");
        var bath = Saved("B", "192.168.1.5", "Bad");

        var merge = HomePodGroups.Merge(
            [kitchen, bath],
            [Found("K", "192.168.1.7", "Køkken"), Found("B", "192.168.1.6", "Bad"), Found("N", "192.168.1.4", "Ny")],
            Now);

        Assert.Equal(1, merge.Added);
        Assert.Equal(3, merge.Speakers.Count);
        Assert.Equal(["B", "N"], merge.Restart.Select(r => r.OldId).Order());
        Assert.Empty(merge.Removed);
        Assert.Equal(Now, kitchen.LastSeenUtc);
    }

    [Fact]
    public void A_pair_stays_a_pair_when_the_other_homepod_misses_a_reply()
    {
        // The leader carries no pgid, so on its own it looks like a single HomePod.
        var pair = Saved("L", "192.168.1.8", "HomePod Left", "HomePod Right");

        var merge = HomePodGroups.Merge([pair], [Found("L", "192.168.1.8", "HomePod Left")], Now);

        var speaker = Assert.Single(merge.Speakers);
        Assert.Same(pair, speaker);
        Assert.Equal("HomePod Left + Right", speaker.Name);
        Assert.Equal(["HomePod Left", "HomePod Right"], speaker.Members);
        Assert.Equal(["L", "R"], speaker.MemberIds);
        Assert.Empty(merge.Restart); // the link stays
        Assert.Empty(merge.Removed);
        Assert.Equal(Now, speaker.LastSeenUtc);
    }

    [Fact]
    public void A_pair_keeps_its_leader_when_only_the_other_homepod_answers()
    {
        var pair = Saved("L", "192.168.1.8", "HomePod Left", "HomePod Right");

        var merge = HomePodGroups.Merge([pair], [Found("R", "192.168.1.9", "HomePod Right")], Now);

        var speaker = Assert.Single(merge.Speakers);
        Assert.Equal("L", speaker.Id);
        Assert.Equal("192.168.1.8", speaker.Host);
        Assert.Equal(2, speaker.Members.Count);
        Assert.Empty(merge.Restart);
        Assert.Equal(0, merge.Added);
    }

    [Fact]
    public void A_pair_whose_leader_answers_alone_at_a_new_address_restarts_there()
    {
        var pair = Saved("L", "192.168.1.8", "HomePod Left", "HomePod Right");

        var merge = HomePodGroups.Merge([pair], [Found("L", "192.168.1.18", "HomePod Left")], Now);

        var (oldId, speaker) = Assert.Single(merge.Restart);
        Assert.Equal("L", oldId);
        Assert.Equal("192.168.1.18", speaker.Host);
        Assert.Equal(2, speaker.Members.Count);
    }

    [Fact]
    public void A_pair_that_answers_in_part_too_long_follows_discovery()
    {
        var pair = Saved("L", "192.168.1.8", "HomePod Left", "HomePod Right");

        for (var pass = 1; pass < HomePodGroups.MaxPartialPasses; pass++)
        {
            HomePodGroups.Merge([pair], [Found("L", "192.168.1.8", "HomePod Left")], Now);
            Assert.Equal(2, pair.Members.Count);
        }

        var merge = HomePodGroups.Merge([pair], [Found("L", "192.168.1.8", "HomePod Left")], Now);

        Assert.Equal(["HomePod Left"], Assert.Single(merge.Speakers).Members);
    }

    [Fact]
    public void A_full_reply_resets_the_count_of_partial_replies()
    {
        var pair = Saved("L", "192.168.1.8", "HomePod Left", "HomePod Right");

        for (var pass = 0; pass < HomePodGroups.MaxPartialPasses * 2; pass++)
        {
            var found = pass % 2 == 0
                ? Found("L", "192.168.1.8", "HomePod Left")
                : Found("L", "192.168.1.8", "HomePod Left", "HomePod Right");
            HomePodGroups.Merge([pair], [found], Now);
        }

        Assert.Equal(2, pair.Members.Count);
        Assert.Equal(0, pair.PartialPasses);
    }

    [Fact]
    public void A_pair_split_in_the_home_app_splits_at_once()
    {
        // Both HomePods answer, each as its own speaker: the pair is gone.
        var pair = Saved("L", "192.168.1.8", "HomePod Left", "HomePod Right");

        var merge = HomePodGroups.Merge([pair], [Found("L", "192.168.1.8", "HomePod Left"), Found("R", "192.168.1.9", "HomePod Right")], Now);

        Assert.Equal(2, merge.Speakers.Count);
        Assert.Equal(["HomePod Left"], merge.Speakers.Single(s => s.Id == "L").Members);
        Assert.Equal(1, merge.Added);
    }

    [Fact]
    public void Ids_match_in_any_case()
    {
        var kitchen = Saved("ce:2e", "192.168.1.7", "Køkken");
        var merge = HomePodGroups.Merge([kitchen], [Found("CE:2E", "192.168.1.7", "Køkken")], Now);

        Assert.Same(kitchen, Assert.Single(merge.Speakers));
        Assert.Equal("ce:2e", Assert.Single(merge.Restart).OldId); // the id text changed, so the client re-keys
    }
}
