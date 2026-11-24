using Denon.App.PluginSdk;
using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Mrp;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

public sealed class AppleTvWatchLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 20, 0, 0, TimeSpan.FromHours(1));

    // Made-up device: no real identifiers.
    private static readonly AppleTvDeviceConfig Device = new() { Id = "AA:BB:CC:DD:EE:FF", Name = "Test Room", Model = "Apple TV 4K" };

    private static readonly PluginReceiverSnapshot Receiver = PluginReceiverSnapshot.Empty with
    {
        IsConnected = true,
        IsPoweredOn = true,
        Volume = 40,
        InputSource = "TV",
        SoundMode = "DOLBY SURROUND",
    };

    private readonly List<(string Name, Dictionary<string, object?> Data)> _events = [];

    private AppleTvWatchLog NewLog() => new((name, data) => _events.Add((name, data)));

    private static AppleTvDeviceState State(
        MrpPlaybackState playback,
        string? title,
        string bundle = "dk.dr.tv",
        string app = "DRTV",
        AttentionState power = AttentionState.Awake,
        AppleTvLink link = AppleTvLink.Connected) =>
        new AppleTvDeviceState(Device.Id)
        {
            Link = link,
            Power = power,
            NowPlayingLinked = true,
            AppName = app,
            NowPlaying = NowPlayingSnapshot.Empty with
            {
                AppBundleId = bundle,
                AppName = app,
                State = playback,
                Title = title,
                UpdatedUtc = T0,
            },
        };

    private static AppleTvDeviceState Asleep() => new(Device.Id) { Link = AppleTvLink.Connected, Power = AttentionState.Asleep };

    private void TickFor(AppleTvWatchLog log, DateTimeOffset from, int seconds)
    {
        for (var s = 0; s <= seconds; s += 5)
        {
            log.Tick(() => Receiver, from.AddSeconds(s));
        }
    }

    private List<Dictionary<string, object?>> Named(string name) => _events.Where(e => e.Name == name).Select(e => e.Data).ToList();

    [Fact]
    public void Program_that_plays_past_the_dwell_logs_one_session_ending_on_sleep()
    {
        var log = NewLog();
        log.Update(Device, State(MrpPlaybackState.Playing, "Min ven, Putin"), Receiver, T0);
        TickFor(log, T0, 600);

        var started = Assert.Single(Named("session.started"));
        Assert.Equal("appletv", started["kind"]);
        Assert.Equal("Min ven, Putin", started["title"]);
        Assert.Equal("DRTV", started["app"]);
        Assert.Equal("Test Room", started["appleTvDevice"]);
        Assert.Equal("TV", started["receiverInput"]);

        log.Update(Device, Asleep(), Receiver, T0.AddSeconds(600));

        var ended = Assert.Single(Named("session.ended"));
        Assert.Equal("standby", ended["reason"]);
        Assert.Equal(600, ended["durationSec"]);
        Assert.InRange(Convert.ToInt32(ended["watchedSec"]), 595, 600);
        Assert.Equal(40.0, ended["avgVolume"]);

        var run = Assert.Single(Named("appletv.app_ended"));
        Assert.Equal("dk.dr.tv", run["appId"]);
        Assert.Contains("Min ven, Putin", (IEnumerable<string>)run["titles"]!);
    }

    [Fact]
    public void Program_that_never_plays_is_not_logged()
    {
        var log = NewLog();
        log.Update(Device, State(MrpPlaybackState.Paused, "Paused show"), Receiver, T0);
        TickFor(log, T0, 120);
        log.Update(Device, State(MrpPlaybackState.Unknown, null), Receiver, T0.AddSeconds(120));

        Assert.Empty(Named("session.started"));
        Assert.Empty(Named("session.ended"));
    }

    [Fact]
    public void A_new_title_closes_the_old_session_as_switched()
    {
        var log = NewLog();
        log.Update(Device, State(MrpPlaybackState.Playing, "Episode 1"), Receiver, T0);
        TickFor(log, T0, 60);
        log.Update(Device, State(MrpPlaybackState.Playing, "Episode 2"), Receiver, T0.AddSeconds(60));
        TickFor(log, T0.AddSeconds(60), 60);

        var ended = Assert.Single(Named("session.ended"));
        Assert.Equal("Episode 1", ended["title"]);
        Assert.Equal("switched", ended["reason"]);
        Assert.Equal(2, Named("session.started").Count);
    }

    [Fact]
    public void Title_that_is_the_app_name_is_not_a_program_title()
    {
        var log = NewLog();
        log.Update(Device, State(MrpPlaybackState.Playing, "Netflix", bundle: "com.netflix.Netflix", app: "Netflix"), Receiver, T0);
        TickFor(log, T0, 30);

        var started = Assert.Single(Named("session.started"));
        Assert.Equal(false, started["hasTitle"]);
        Assert.Equal("Netflix", started["title"]);
    }

    [Fact]
    public void Long_pause_ends_the_session_and_play_starts_a_new_one()
    {
        var log = NewLog();
        log.Update(Device, State(MrpPlaybackState.Playing, "Film"), Receiver, T0);
        TickFor(log, T0, 60);
        log.Update(Device, State(MrpPlaybackState.Paused, "Film"), Receiver, T0.AddSeconds(60));
        TickFor(log, T0.AddSeconds(60), (int)AppleTvWatchLog.PauseTimeout.TotalSeconds + 5);

        var ended = Assert.Single(Named("session.ended"));
        Assert.Equal("paused", ended["reason"]);

        var resume = T0.AddMinutes(20);
        log.Update(Device, State(MrpPlaybackState.Playing, "Film"), Receiver, resume);
        TickFor(log, resume, 30);
        Assert.Equal(2, Named("session.started").Count);
    }

    [Fact]
    public void End_reasons_follow_link_and_power()
    {
        Assert.Equal("disconnected", AppleTvWatchLog.EndReason(State(MrpPlaybackState.Playing, "x", link: AppleTvLink.Disconnected)));
        Assert.Equal("standby", AppleTvWatchLog.EndReason(Asleep()));
        Assert.Equal("app_closed", AppleTvWatchLog.EndReason(State(MrpPlaybackState.Unknown, null)));
    }

    [Fact]
    public void EndAll_closes_open_sessions_with_the_given_reason()
    {
        var log = NewLog();
        log.Update(Device, State(MrpPlaybackState.Playing, "Show"), Receiver, T0);
        TickFor(log, T0, 30);
        log.EndAll(T0.AddSeconds(40), "app_shutdown");

        Assert.Equal("app_shutdown", Assert.Single(Named("session.ended"))["reason"]);
        Assert.Equal("app_shutdown", Assert.Single(Named("appletv.app_ended"))["reason"]);
    }

    [Fact]
    public void Sleep_within_grace_period_after_program_ends_logs_standby_with_original_duration()
    {
        var log = NewLog();
        log.Update(Device, State(MrpPlaybackState.Playing, "Min ven, Putin"), Receiver, T0);
        TickFor(log, T0, 600);

        // Remote power button stops playback ~30 s before tvOS reports Asleep.
        log.Update(Device, State(MrpPlaybackState.Unknown, null), Receiver, T0.AddSeconds(600));
        Assert.Empty(Named("session.ended"));
        Assert.Empty(Named("appletv.app_ended"));

        // Ticks inside the 35 s window keep waiting.
        log.Tick(() => Receiver, T0.AddSeconds(615));
        log.Tick(() => Receiver, T0.AddSeconds(625));
        Assert.Empty(Named("session.ended"));

        // tvOS reports Asleep 30 s after playback stopped.
        log.Update(Device, Asleep(), Receiver, T0.AddSeconds(630));

        var ended = Assert.Single(Named("session.ended"));
        Assert.Equal("standby", ended["reason"]);
        Assert.Equal(600, ended["durationSec"]);
        Assert.InRange(Convert.ToInt32(ended["watchedSec"]), 595, 600);
        Assert.Equal(T0.AddSeconds(600), DateTimeOffset.Parse((string)ended["endedAt"]!, System.Globalization.CultureInfo.InvariantCulture));

        var run = Assert.Single(Named("appletv.app_ended"));
        Assert.Equal("standby", run["reason"]);
        Assert.Equal(600, run["durationSec"]);
    }

    [Fact]
    public void Staying_awake_past_grace_period_logs_app_closed()
    {
        var log = NewLog();
        log.Update(Device, State(MrpPlaybackState.Playing, "Show"), Receiver, T0);
        TickFor(log, T0, 120);

        log.Update(Device, State(MrpPlaybackState.Unknown, null), Receiver, T0.AddSeconds(120));
        Assert.Empty(Named("session.ended"));

        log.Tick(() => Receiver, T0.AddSeconds(150));
        Assert.Empty(Named("session.ended"));

        log.Tick(() => Receiver, T0.AddSeconds(155)); // 35 s grace elapsed
        var ended = Assert.Single(Named("session.ended"));
        Assert.Equal("app_closed", ended["reason"]);
        Assert.Equal(120, ended["durationSec"]);

        var run = Assert.Single(Named("appletv.app_ended"));
        Assert.Equal("app_closed", run["reason"]);
        Assert.Equal(120, run["durationSec"]);
    }

    [Fact]
    public void Sleep_after_grace_period_expired_does_not_reclassify_as_standby()
    {
        var log = NewLog();
        log.Update(Device, State(MrpPlaybackState.Playing, "Show"), Receiver, T0);
        TickFor(log, T0, 60);

        log.Update(Device, State(MrpPlaybackState.Unknown, null), Receiver, T0.AddSeconds(60));
        // Even without an intervening Tick, Asleep arriving after the 35 s grace logs app_closed.
        log.Update(Device, Asleep(), Receiver, T0.AddSeconds(120));

        Assert.Equal("app_closed", Assert.Single(Named("session.ended"))["reason"]);
        Assert.Equal("app_closed", Assert.Single(Named("appletv.app_ended"))["reason"]);
    }

    [Fact]
    public void Starting_a_new_program_during_grace_period_flushes_the_previous_session_first()
    {
        var log = NewLog();
        log.Update(Device, State(MrpPlaybackState.Playing, "Show A"), Receiver, T0);
        TickFor(log, T0, 60);

        log.Update(Device, State(MrpPlaybackState.Unknown, null), Receiver, T0.AddSeconds(60));
        Assert.Empty(Named("session.ended"));

        log.Update(Device, State(MrpPlaybackState.Playing, "Show B"), Receiver, T0.AddSeconds(70));
        var ended = Assert.Single(Named("session.ended"));
        Assert.Equal("Show A", ended["title"]);
        Assert.Equal("app_closed", ended["reason"]);
        Assert.Equal(60, ended["durationSec"]);

        TickFor(log, T0.AddSeconds(70), 30);
        Assert.Equal(2, Named("session.started").Count);
    }

    [Fact]
    public void App_shutdown_during_grace_period_does_not_lose_the_deferred_session()
    {
        var log = NewLog();
        log.Update(Device, State(MrpPlaybackState.Playing, "Show"), Receiver, T0);
        TickFor(log, T0, 90);

        log.Update(Device, State(MrpPlaybackState.Unknown, null), Receiver, T0.AddSeconds(90));
        Assert.Empty(Named("session.ended"));

        log.EndAll(T0.AddSeconds(100), "app_shutdown");

        var ended = Assert.Single(Named("session.ended"));
        Assert.Equal("app_closed", ended["reason"]);
        Assert.Equal(90, ended["durationSec"]);

        var run = Assert.Single(Named("appletv.app_ended"));
        Assert.Equal("app_closed", run["reason"]);
        Assert.Equal(90, run["durationSec"]);
    }
}

public sealed class AppleTvStatusTests
{
    private static AppleTvDeviceState Playing(MrpPlaybackState state, string? title, string? app = "DRTV", bool canPause = true) =>
        new("id")
        {
            Link = AppleTvLink.Connected,
            Power = AttentionState.Awake,
            AppName = app,
            NowPlaying = NowPlayingSnapshot.Empty with { AppBundleId = "dk.dr.tv", State = state, Title = title, CanPause = canPause },
        };

    [Fact]
    public void Status_line_matches_the_host_hero_format()
    {
        Assert.Equal("APPLE TV • Test Room • DRTV • Min ven, Putin",
            AppleTvPlugin.FormatPlayingStatus("Test Room", Playing(MrpPlaybackState.Playing, "Min ven, Putin")));
        Assert.Equal("APPLE TV LIVE • Test Room • DRTV • DR1",
            AppleTvPlugin.FormatPlayingStatus("Test Room", Playing(MrpPlaybackState.Playing, "DR1", canPause: false)));
        Assert.Equal("APPLE TV PAUSED • Test Room • DRTV • Min ven, Putin",
            AppleTvPlugin.FormatPlayingStatus("Test Room", Playing(MrpPlaybackState.Paused, "Min ven, Putin")));
    }

    [Fact]
    public void Status_line_drops_a_title_equal_to_the_app_and_escapes_the_separator()
    {
        Assert.Equal("APPLE TV • Test Room • DRTV", AppleTvPlugin.FormatPlayingStatus("Test Room", Playing(MrpPlaybackState.Playing, "DRTV")));
        Assert.Equal("APPLE TV • Test Room • DRTV • A - B",
            AppleTvPlugin.FormatPlayingStatus("Test Room", Playing(MrpPlaybackState.Playing, "A • B")));
        Assert.Equal("APPLE TV • Test Room • dk.dr.tv • Show",
            AppleTvPlugin.FormatPlayingStatus("Test Room", Playing(MrpPlaybackState.Playing, "Show", app: null)));
    }
}
