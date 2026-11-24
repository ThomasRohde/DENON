using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.PluginSdk;
using Xunit;

namespace Denon.App.Core.Tests;

public sealed class CastNowPlayingTests
{
    private static readonly ReceiverPlayerInfo Player = new("Denon AVR-X2400H", 101, "Denon AVR-X2400H", "3.1", "192.168.1.3", "wired", 1, "SER");
    private static readonly NowPlayingMedia Stale = new("song", "Url Stream", "Url Stream", "Url Stream", "", "", "", null, 1029);

    private static ReceiverStatus OnTvAudio() => new(
        Player, "stop", 44, false, Stale, DateTimeOffset.Now,
        IsPoweredOn: true, IsMainZoneOn: true, InputSource: "TV", SoundMode: "DOLBY SURROUND");

    private static PluginRecordSnapshot Plugin(string id, string status) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        Author = "test",
        Description = "",
        Category = "",
        AccentHex = "#000000",
        IconGlyph = "X",
        Capabilities = PluginCapabilities.Actions,
        Origin = PluginOrigin.BuiltIn,
        AssemblyPath = null,
        IsEnabled = true,
        IsStarted = true,
        HasDashboardView = false,
        Health = PluginHealthState.Connected,
        StatusText = status,
        LastError = null,
        Actions = [],
    };

    [Fact]
    public void Cast_program_replaces_the_tv_hdmi_input_in_the_hero()
    {
        var resolved = NowPlayingResolver.Resolve(OnTvAudio(), null,
        [
            Plugin("smart-tv", "LIVE • 55UR73006LA • DVD Player (HDMI 1)"),
            Plugin("chromecast", "CASTING • Stuen 2 • DR TV • Spionhvalen: Undercover i Arktis"),
        ]);

        Assert.Equal("Spionhvalen: Undercover i Arktis", resolved.Title);
        Assert.Contains("DR TV", resolved.Detail);
        Assert.Contains("Stuen 2", resolved.Detail);
        Assert.Contains("TV DVD Player (HDMI 1)", resolved.Detail);
        Assert.Equal("Casting", resolved.PlayStateLabel);

        // The hero knows it shows a cast, so play/pause and artwork go to the Chromecast.
        Assert.NotNull(resolved.Cast);
        Assert.Equal("Stuen 2", resolved.Cast!.DeviceName);
        Assert.True(resolved.Cast.IsPlaying);
    }

    [Fact]
    public void Live_tv_and_heos_results_carry_no_cast()
    {
        var liveTv = NowPlayingResolver.Resolve(OnTvAudio(), null,
        [
            Plugin("smart-tv", "LIVE • 55UR73006LA • Live TV (Ch 1 • DR1)"),
            Plugin("chromecast", "CASTING • Stuen 2 • DR TV • Smagen af Italien"),
        ]);
        Assert.Null(liveTv.Cast);
    }

    [Theory]
    [InlineData("{\"name\":\"Stuen 2\",\"imageUrl\":\"https://asset.dr.dk/images/art.jpg\"}", "https://asset.dr.dk/images/art.jpg")]
    [InlineData("{\"imageUrl\":null}", null)]
    [InlineData("{\"imageUrl\":\"file:///C:/secret.jpg\"}", null)]
    [InlineData("{\"imageUrl\":\"/relative.jpg\"}", null)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public void Cast_artwork_url_is_read_from_now_playing_data(string? json, string? expected)
    {
        Assert.Equal(expected, NowPlayingResolver.TryReadCastImageUrl(json));
    }

    [Theory]
    [InlineData("file:///C:/Data/plugins/data/appletv/artwork/tv1/abc.jpg", "file:///C:/Data/plugins/data/appletv/artwork/tv1/abc.jpg")]
    [InlineData("file:///C:/Data/plugins/data/appletv/../secret.jpg", null)]
    [InlineData("file:///C:/Data/plugins/data/appletv-other/x.jpg", null)]
    [InlineData("file://server/share/x.jpg", null)]
    public void Local_artwork_is_read_only_from_the_plugin_data_folder(string url, string? expected)
    {
        var json = $"{{\"imageUrl\":\"{url}\"}}";
        Assert.Equal(expected, NowPlayingResolver.TryReadCastImageUrl(json, @"C:\Data\plugins\data\appletv"));
    }

    [Fact]
    public void Netflix_without_title_shows_the_app_name()
    {
        var resolved = NowPlayingResolver.Resolve(OnTvAudio(), null,
        [
            Plugin("smart-tv", "LIVE • 55UR73006LA • DVD Player (HDMI 1)"),
            Plugin("chromecast", "CAST PAUSED • Stuen 2 • Netflix"),
        ]);

        Assert.Equal("Netflix", resolved.Title);
        Assert.Equal("Paused", resolved.PlayStateLabel);
    }

    [Fact]
    public void Live_tv_wins_over_a_background_cast()
    {
        var resolved = NowPlayingResolver.Resolve(OnTvAudio(), null,
        [
            Plugin("smart-tv", "LIVE • 55UR73006LA • Live TV (Ch 1 • DR1)"),
            Plugin("chromecast", "CAST PAUSED • Stuen 2 • DR TV • Smagen af Italien"),
        ]);

        Assert.Equal("Live TV (Ch 1 • DR1)", resolved.Title);
    }

    [Fact]
    public void Without_a_tv_plugin_only_a_playing_cast_is_shown()
    {
        Assert.Equal("Show", NowPlayingResolver.Resolve(OnTvAudio(), null, [Plugin("chromecast", "CASTING • Stuen 2 • YouTube • Show")]).Title);
        Assert.Equal("TV Audio", NowPlayingResolver.Resolve(OnTvAudio(), null, [Plugin("chromecast", "CAST PAUSED • Stuen 2 • YouTube • Show")]).Title);
    }

    [Fact]
    public void Relay_stream_reported_as_local_music_is_not_another_service()
    {
        // HEOS reports the app's own relayed FLAC as sid 1024 "Local Music" with "Url Stream" placeholders.
        var relayFlac = new NowPlayingMedia("song", "Url Stream", "Url Stream", "Url Stream", "", "1", "1", 1, 1024);
        var dlnaTrack = new NowPlayingMedia("song", "Real Song", "Album", "Artist", "", "1", "1", 1, 1024);

        Assert.False(relayFlac.IsNamedServiceSource);
        Assert.True(dlnaTrack.IsNamedServiceSource);

        var status = new ReceiverStatus(
            Player, "play", 44, false, relayFlac, DateTimeOffset.Now,
            IsPoweredOn: true, IsMainZoneOn: true, InputSource: "NET", SoundMode: "STEREO");
        var track = new LocalAudioTrackItem("t1", @"C:\Music\Metronome.flac", "Metronome.flac", "Metronome", "Denon Sync Test", ".flac", 1000);

        Assert.Equal("Metronome", NowPlayingResolver.Resolve(status, null, [], activeLocalTrack: track).Title);
    }

    [Theory]
    [InlineData("1 Cast device(s) connected.")]
    [InlineData("Cannot reach 1 Cast device(s).")]
    [InlineData("CASTING • only-two")]
    public void Other_status_texts_are_not_a_cast(string status)
    {
        Assert.Null(NowPlayingResolver.TryExtractCastNowPlaying([Plugin("chromecast", status)]));
    }
    [Fact]
    public void Apple_tv_program_replaces_the_tv_hdmi_input_in_the_hero()
    {
        var resolved = NowPlayingResolver.Resolve(OnTvAudio(), null,
        [
            Plugin("smart-tv", "LIVE • 55UR73006LA • Apple TV (HDMI 2)"),
            Plugin("apple-tv", "APPLE TV • Stue • DRTV • Min ven, Putin"),
        ]);

        Assert.Equal("Min ven, Putin", resolved.Title);
        Assert.StartsWith("Apple TV • DRTV", resolved.Detail);
        Assert.Contains("Stue", resolved.Detail);
        Assert.Equal("Playing", resolved.PlayStateLabel);

        // Play/pause and artwork go to the Apple TV plugin.
        Assert.NotNull(resolved.Cast);
        Assert.True(resolved.Cast!.IsAppleTv);
        Assert.Equal("apple-tv", resolved.Cast.PluginId);
    }

    [Fact]
    public void Apple_tv_on_a_direct_receiver_input_is_shown_in_the_hero()
    {
        var onMediaPlayer = OnTvAudio() with { InputSource = "MPLAY", SoundMode = "STEREO" };
        var resolved = NowPlayingResolver.Resolve(onMediaPlayer, null,
            [Plugin("apple-tv", "APPLE TV PAUSED • Stue • DRTV • TVA")]);

        Assert.Equal("TVA", resolved.Title);
        Assert.StartsWith("Apple TV • DRTV • Stue", resolved.Detail);
        Assert.Equal("Paused", resolved.PlayStateLabel);
        Assert.True(resolved.Cast!.IsAppleTv);

        // Without an Apple TV program the input shows as before.
        Assert.Null(NowPlayingResolver.Resolve(onMediaPlayer, null, [Plugin("apple-tv", "1 Apple TV connected (awake).")]).Cast);
    }

    [Fact]
    public void A_playing_streamer_wins_over_a_paused_one()
    {
        IReadOnlyList<PluginRecordSnapshot> pausedCast =
        [
            Plugin("chromecast", "CAST PAUSED • Stuen 2 • DR TV • Smagen af Italien"),
            Plugin("apple-tv", "APPLE TV • Stue • Netflix • Wednesday"),
        ];
        Assert.Equal("apple-tv", NowPlayingResolver.TryExtractStreamerNowPlaying(pausedCast)!.PluginId);

        IReadOnlyList<PluginRecordSnapshot> bothPaused =
        [
            Plugin("chromecast", "CAST PAUSED • Stuen 2 • DR TV"),
            Plugin("apple-tv", "APPLE TV PAUSED • Stue • Netflix"),
        ];
        Assert.Equal("chromecast", NowPlayingResolver.TryExtractStreamerNowPlaying(bothPaused)!.PluginId);

        var pausedAppleTv = NowPlayingResolver.TryExtractAppleTvNowPlaying([Plugin("apple-tv", "APPLE TV PAUSED • Stue • Netflix")]);
        Assert.False(pausedAppleTv!.IsPlaying);
        Assert.Null(pausedAppleTv.Title);
    }

    [Theory]
    [InlineData("1 Apple TV connected (asleep).")]
    [InlineData("Cannot reach 1 Apple TV(s).")]
    [InlineData("CASTING • Stue • Netflix")]
    public void Other_status_texts_are_not_an_apple_tv_program(string status)
    {
        Assert.Null(NowPlayingResolver.TryExtractAppleTvNowPlaying([Plugin("apple-tv", status)]));
    }

    [Fact]
    public void Apple_tv_live_program_marks_pause_unsupported_in_hero_and_cast()
    {
        var onMediaPlayer = OnTvAudio() with { InputSource = "MPLAY", SoundMode = "STEREO" };
        var live = NowPlayingResolver.Resolve(onMediaPlayer, null,
            [Plugin("apple-tv", "APPLE TV LIVE • Stue • DRTV • DR1")]);

        Assert.Equal("DR1", live.Title);
        Assert.Equal("Playing", live.PlayStateLabel);
        Assert.False(live.CanPause);
        Assert.NotNull(live.Cast);
        Assert.True(live.Cast!.IsPlaying);
        Assert.False(live.Cast.CanPause);

        var onTvHdmi = NowPlayingResolver.Resolve(OnTvAudio(), null,
        [
            Plugin("smart-tv", "LIVE • 55UR73006LA • Apple TV (HDMI 2)"),
            Plugin("apple-tv", "APPLE TV LIVE • Stue • DRTV • DR1"),
        ]);
        Assert.False(onTvHdmi.CanPause);
        Assert.False(onTvHdmi.Cast!.CanPause);

        // On-demand Apple TV and Chromecast default to CanPause = true.
        var onDemand = NowPlayingResolver.Resolve(onMediaPlayer, null,
            [Plugin("apple-tv", "APPLE TV • Stue • DRTV • Min ven, Putin")]);
        Assert.True(onDemand.CanPause);
        Assert.True(onDemand.Cast!.CanPause);
    }

    [Theory]
    [InlineData("{\"canPause\":false}", false)]
    [InlineData("{\"canPause\":true}", true)]
    [InlineData("{\"title\":\"Show\"}", null)]
    [InlineData("{\"canPause\":\"false\"}", null)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public void Can_pause_is_read_from_now_playing_json(string? json, bool? expected)
    {
        Assert.Equal(expected, NowPlayingResolver.TryReadCastCanPause(json));
    }
}
