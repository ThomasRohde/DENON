using Denon.App.PluginSdk;
using Denon.Plugin.Chromecast.Cast;
using Xunit;

namespace Denon.Plugin.Chromecast.Tests;

public sealed class CastWatchLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 28, 20, 0, 0, TimeSpan.Zero);

    private static readonly PluginReceiverSnapshot Receiver = PluginReceiverSnapshot.Empty with
    {
        IsConnected = true, IsPoweredOn = true, Volume = 40, InputSource = "MPLAY", SoundMode = "MOVIE",
    };

    private readonly List<(string Name, Dictionary<string, object?> Data)> _events = [];
    private readonly CastWatchLog _log;
    private readonly CastDeviceConfig _device = new() { Id = "Stue-ABC", Name = "Stuen 2", Model = "Chromecast" };

    public CastWatchLogTests()
    {
        _log = new CastWatchLog((name, data) => _events.Add((name, data)));
    }

    private static CastDeviceState Playing(string? title = "Planet Earth", string playerState = "PLAYING") =>
        new("Stue-ABC", CastConnectionState.Connected, AppId: "233637DE", AppName: "YouTube", SessionId: "s1",
            PlayerState: playerState, Title: title);

    private void Update(CastDeviceState state, int second) => _log.Update(_device, state, Receiver, T0.AddSeconds(second));

    private void TickUntil(int lastSecond, int fromSecond = 5)
    {
        for (var s = fromSecond; s <= lastSecond; s += 5)
        {
            _log.Tick(_ => Receiver, T0.AddSeconds(s));
        }
    }

    private IEnumerable<string> Names => _events.Select(e => e.Name);

    [Fact]
    public void A_program_that_plays_for_the_dwell_starts_a_session()
    {
        Update(Playing(), 0);
        TickUntil(10);
        Assert.DoesNotContain("session.started", Names);

        TickUntil(15, fromSecond: 15);

        var started = Assert.Single(_events, e => e.Name == "session.started").Data;
        Assert.Equal("Planet Earth", started["title"]);
        Assert.Equal("YouTube", started["app"]);
        Assert.Equal("MPLAY", started["receiverInput"]);
        Assert.Equal(15, started["dwellConfirmedSec"]);
    }

    [Fact]
    public void A_program_paused_after_one_second_is_not_logged_while_it_stays_paused()
    {
        Update(Playing(), 0);
        Update(Playing(playerState: "PAUSED"), 1);

        TickUntil(300);

        Assert.DoesNotContain("session.started", Names);
    }

    [Fact]
    public void A_program_paused_early_starts_its_session_when_it_plays_again()
    {
        Update(Playing(), 0);
        Update(Playing(playerState: "PAUSED"), 1);
        TickUntil(60);

        Update(Playing(), 62);
        TickUntil(65, fromSecond: 65);

        Assert.Single(_events, e => e.Name == "session.started");
    }

    [Fact]
    public void A_program_that_played_the_dwell_and_then_paused_is_logged()
    {
        Update(Playing(), 0);
        Update(Playing(playerState: "PAUSED"), 16);
        TickUntil(20, fromSecond: 20);

        Assert.Single(_events, e => e.Name == "session.started");
    }

    [Fact]
    public void A_program_that_never_played_does_not_start_a_session()
    {
        Update(Playing(playerState: "IDLE"), 0);

        TickUntil(120);

        Assert.DoesNotContain("session.started", Names);
    }

    [Fact]
    public void A_late_title_goes_to_the_open_session()
    {
        Update(Playing(title: null), 0);
        TickUntil(15);
        Update(Playing(title: "Planet Earth"), 20);
        Update(Playing(title: null) with { AppId = null, AppName = null, SessionId = null, PlayerState = "" }, 40);

        Assert.Equal(["session.started", "session.ended", "cast.app_ended"], Names);
        var ended = _events[1].Data;
        Assert.Equal("Planet Earth", ended["title"]);
        Assert.Equal("app_closed", ended["reason"]);
        Assert.Equal(40, ended["durationSec"]);
        Assert.Equal(40, ended["watchedSec"]);
        Assert.Equal(40.0, ended["avgVolume"]);
    }

    [Fact]
    public void A_new_title_ends_the_session_as_switched()
    {
        Update(Playing(), 0);
        TickUntil(15);
        Update(Playing(title: "Blue Planet"), 30);

        var ended = Assert.Single(_events, e => e.Name == "session.ended").Data;
        Assert.Equal("Planet Earth", ended["title"]);
        Assert.Equal("switched", ended["reason"]);
    }

    [Fact]
    public void EndAll_for_one_device_matches_its_id_without_case()
    {
        Update(Playing(), 0);
        TickUntil(20);

        _log.EndAll(T0.AddSeconds(30), "device_disabled", "stue-abc");

        var ended = Assert.Single(_events, e => e.Name == "session.ended").Data;
        Assert.Equal("device_disabled", ended["reason"]);
        Assert.Contains("cast.app_ended", Names);
    }

    [Fact]
    public void EndAll_for_another_device_leaves_the_session_open()
    {
        Update(Playing(), 0);
        TickUntil(20);

        _log.EndAll(T0.AddSeconds(30), "device_disabled", "ip-192.168.1.20");

        Assert.DoesNotContain("session.ended", Names);
    }

    [Fact]
    public void A_long_pause_ends_the_session()
    {
        Update(Playing(), 0);
        TickUntil(20);
        Update(Playing(playerState: "PAUSED"), 21);

        TickUntil(25 + (int)CastWatchLog.PauseTimeout.TotalSeconds, fromSecond: 25);

        var ended = Assert.Single(_events, e => e.Name == "session.ended").Data;
        Assert.Equal("paused", ended["reason"]);
    }
}
