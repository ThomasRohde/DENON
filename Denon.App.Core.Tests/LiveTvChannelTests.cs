using System.Globalization;
using System.Text.Json;
using Denon.App.Core.Insights;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.PluginSdk;
using Denon.Plugin.SmartTv;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>Live TV: the LG guide, channel logos, the hero, the watch log and Insights.</summary>
public sealed class LiveTvChannelTests
{
    // ---------------------------------------------------------------- program guide

    /// <summary>A trimmed getChannelProgramInfo reply from an LG TV (UTC and local times, guide dated one day ahead).</summary>
    private const string GuideJson = """
        {
          "channel": { "channelId": "3_18_1_1_2190_101_65024", "channelName": "DR1" },
          "programList": [
            { "programName": "Kriminalkommissær Barnaby XVI (5)", "startTime": "2026,11,25,13,55,00", "endTime": "2026,11,25,15,25,00",
              "localStartTime": "2026,11,25,14,55,00", "localEndTime": "2026,11,25,16,25,00" },
            { "programName": "Min familie og andre dyr III (6:8)", "startTime": "2026,11,25,15,25,00", "endTime": "2026,11,25,16,15,00",
              "localStartTime": "2026,11,25,16,25,00", "localEndTime": "2026,11,25,17,15,00" },
            { "programName": "TVA", "startTime": "2026,11,25,16,15,00", "endTime": "2026,11,25,16,30,00",
              "localStartTime": "2026,11,25,17,15,00", "localEndTime": "2026,11,25,17,30,00" },
            { "programName": "", "startTime": "2026,11,25,16,30,00", "endTime": "2026,11,25,17,00,00" },
            { "programName": "Broken", "startTime": "2026,13,40,16,30,00", "endTime": "2026,11,25,17,00,00" }
          ]
        }
        """;

    private static readonly TimeSpan Cet = TimeSpan.FromHours(1);

    [Fact]
    public void Guide_is_parsed_with_the_tvs_offset_and_skips_unnamed_or_broken_programs()
    {
        var programs = LgTvProgramGuide.Parse(JsonDocument.Parse(GuideJson).RootElement);

        Assert.Equal(3, programs.Count);
        Assert.Equal("Kriminalkommissær Barnaby XVI (5)", programs[0].Name);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 14, 55, 0, Cet), programs[0].Start);
        Assert.Equal(Cet, programs[0].Start.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 16, 25, 0, Cet), programs[0].End);
    }

    [Fact]
    public void Current_program_is_found_and_a_guide_one_day_ahead_is_corrected()
    {
        var programs = LgTvProgramGuide.Parse(JsonDocument.Parse(GuideJson).RootElement);

        // The real date is the 25th: no shift.
        var onTime = LgTvProgramGuide.PickCurrent(programs, new DateTimeOffset(2026, 11, 25, 16, 47, 0, Cet));
        Assert.Equal("Min familie og andre dyr III (6:8)", onTime.Current?.Name);
        Assert.Equal("TVA", onTime.Next?.Name);
        Assert.Equal(0, onTime.DayShift);

        // The TV dates its guide a day ahead (seen live): it is really the 24th.
        var shifted = LgTvProgramGuide.PickCurrent(programs, new DateTimeOffset(2026, 11, 24, 16, 47, 0, Cet));
        Assert.Equal(1, shifted.DayShift);
        Assert.Equal("Min familie og andre dyr III (6:8)", shifted.Current?.Name);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 16, 25, 0, Cet), shifted.Current?.Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 17, 15, 0, Cet), shifted.Next?.Start);

        // Outside the guide: nothing.
        var none = LgTvProgramGuide.PickCurrent(programs, new DateTimeOffset(2026, 11, 25, 22, 0, 0, Cet));
        Assert.Null(none.Current);
        Assert.Null(none.Next);
    }

    // ---------------------------------------------------------------- logo matching

    private static readonly ChannelLogoMatcher.LogoFile[] Catalog =
    [
        new("countries/nordic/denmark", "dr1-dk.png"),
        new("countries/nordic/denmark", "tv2-dk.png"),
        new("countries/nordic/denmark", "tv2-charlie-dk.png"),
        new("countries/nordic/denmark", "tv2-kosmopol-dk.png"),
        new("countries/nordic/denmark", "tv2-kosmopol-hz-dk.png"),
        new("countries/nordic/denmark", "tv2-ost-dk.png"),
        new("countries/nordic/denmark", "tv3-plus-dk.png"),
        new("countries/nordic/denmark", "6eren-dk.png"),
        new("countries/nordic/denmark", "v-sport-ultrahd-dk.png"),
        new("countries/nordic/denmark", "discovery-channel-dk.png"),
        new("countries/international", "cnn-international-int.png"),
        new("countries/united-kingdom", "aljazeera-uk.png"),
        new("countries/united-kingdom", "dr1-uk.png"), // Same name in a later folder: the country's own folder wins.
        new("countries/united-kingdom", "bbc-news-icon-uk.png"),
    ];

    [Theory]
    [InlineData("DR1", "dr1-dk.png")]
    [InlineData("TV 2", "tv2-dk.png")]
    [InlineData("TV 2 Charlie", "tv2-charlie-dk.png")]
    [InlineData("TV3+", "tv3-plus-dk.png")]
    [InlineData("TV2 ØST", "tv2-ost-dk.png")]
    [InlineData("6´eren", "6eren-dk.png")]
    [InlineData("V sport ultra HD", "v-sport-ultrahd-dk.png")]
    [InlineData("DR1 Synstolkning", "dr1-dk.png")]
    [InlineData("DR1 HD", "dr1-dk.png")]
    [InlineData("Kosmopol", "tv2-kosmopol-dk.png")]
    [InlineData("Discovery", "discovery-channel-dk.png")]
    [InlineData("CNN", "cnn-international-int.png")]
    [InlineData("Al Jazeera Engelsk", "aljazeera-uk.png")]
    public void Channel_names_match_their_logo_files(string channel, string expected)
    {
        Assert.Equal(expected, ChannelLogoMatcher.Match(channel, Catalog)?.FileName);
    }

    [Theory]
    [InlineData("BBC News")] // Only a style variant ("-icon") exists.
    [InlineData("Infokanalen")]
    [InlineData("TV")]
    [InlineData("")]
    public void Unknown_channels_and_style_variants_get_no_logo(string channel)
    {
        Assert.Null(ChannelLogoMatcher.Match(channel, Catalog));
    }

    [Fact]
    public void Logo_folders_start_with_the_country_then_international()
    {
        var folders = ChannelLogoCache.FoldersForRegion("DK");
        Assert.Equal("countries/nordic/denmark", folders[0]);
        Assert.Equal("countries/international", folders[1]);
        Assert.Contains("countries/nordic/sweden", folders);
        Assert.Equal("countries/germany", ChannelLogoCache.FoldersForRegion("DE")[0]);
        Assert.Equal("countries/united-kingdom", ChannelLogoCache.FoldersForRegion("GB")[0]);
    }

    // ---------------------------------------------------------------- hero

    private static readonly ReceiverPlayerInfo Player = new("Denon AVR-X2400H", 101, "Denon AVR-X2400H", "3.1", "192.168.1.3", "wired", 1, "SER");
    private static readonly NowPlayingMedia Stale = new("song", "Url Stream", "Url Stream", "Url Stream", "", "", "", null, 1029);

    private static ReceiverStatus OnTvAudio() => new(
        Player, "stop", 44, false, Stale, DateTimeOffset.Now,
        IsPoweredOn: true, IsMainZoneOn: true, InputSource: "TV", SoundMode: "MCH STEREO");

    private static Dictionary<string, string> LiveDetails(string? logo = null)
    {
        var d = new Dictionary<string, string>
        {
            ["kind"] = "live_tv",
            ["channelName"] = "DR1",
            ["channelNumber"] = "1",
            ["channelId"] = "3_18_1_1_2190_101_65024",
            ["signalChannelId"] = "2190_101_65024",
            ["channelType"] = "Cable Digital TV",
            ["programName"] = "Min familie og andre dyr III (6:8)",
            ["programStart"] = new DateTimeOffset(2026, 11, 24, 16, 25, 0, Cet).ToString("O", CultureInfo.InvariantCulture),
            ["programEnd"] = new DateTimeOffset(2026, 11, 24, 17, 15, 0, Cet).ToString("O", CultureInfo.InvariantCulture),
            ["nextProgramName"] = "TVA",
            ["nextProgramStart"] = new DateTimeOffset(2026, 11, 24, 17, 15, 0, Cet).ToString("O", CultureInfo.InvariantCulture),
        };
        if (logo is not null)
        {
            d["channelLogo"] = logo;
        }

        return d;
    }

    private static PluginRecordSnapshot SmartTv(string status, IReadOnlyDictionary<string, string>? details) => new()
    {
        Id = "smart-tv",
        Name = "smart-tv",
        Version = "1.0.0",
        Author = "test",
        Description = "",
        Category = "",
        AccentHex = "#000000",
        IconGlyph = "X",
        Capabilities = PluginCapabilities.Actions,
        Origin = PluginOrigin.BuiltIn,
        IsEnabled = true,
        IsStarted = true,
        Health = PluginHealthState.Connected,
        StatusText = status,
        StatusDetails = details ?? new Dictionary<string, string>(),
    };

    [Fact]
    public void Hero_shows_the_program_as_title_and_the_channel_and_times_as_detail()
    {
        var resolved = NowPlayingResolver.Resolve(OnTvAudio(), null,
            [SmartTv("LIVE • 55UR73006LA • Live TV (Ch 1 • DR1)", LiveDetails())]);

        Assert.Equal("Min familie og andre dyr III (6:8)", resolved.Title);
        var start = new DateTimeOffset(2026, 11, 24, 16, 25, 0, Cet).ToLocalTime();
        var end = new DateTimeOffset(2026, 11, 24, 17, 15, 0, Cet).ToLocalTime();
        Assert.Equal($"DR1 • Ch 1 • {start:HH:mm}–{end:HH:mm} • MCH STEREO", resolved.Detail);
        Assert.Equal("Live TV", resolved.PlayStateLabel);
        Assert.Equal(string.Empty, resolved.ImageUrl); // No logo cached.
        Assert.Equal("TVA", resolved.LiveTv?.NextProgramName);
    }

    [Fact]
    public void Hero_without_details_keeps_the_old_live_tv_text()
    {
        var resolved = NowPlayingResolver.Resolve(OnTvAudio(), null, [SmartTv("LIVE • 55UR73006LA • Live TV (Ch 1 • DR1)", null)]);
        Assert.Equal("Live TV (Ch 1 • DR1)", resolved.Title);
        Assert.Null(resolved.LiveTv);
    }

    [Fact]
    public void Logo_path_counts_only_inside_the_plugin_data_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "denon-tests", Guid.NewGuid().ToString("N"));
        var logos = Directory.CreateDirectory(Path.Combine(root, "channel-logos")).FullName;
        var inside = Path.Combine(logos, "dr1-dk.png");
        var outside = Path.Combine(Path.GetTempPath(), "denon-tests", $"{Guid.NewGuid():N}-dr1.png");
        File.WriteAllBytes(inside, [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(outside, [0x89, 0x50, 0x4E, 0x47]);
        try
        {
            var live = NowPlayingResolver.TryReadLiveTv(LiveDetails(inside), root);
            Assert.Equal(inside, live?.LogoPath);
            Assert.Equal("dr1-dk.png", live?.LogoFileName);

            Assert.Null(NowPlayingResolver.TryReadLiveTv(LiveDetails(outside), root)?.LogoPath);
            Assert.Null(NowPlayingResolver.TryReadLiveTv(LiveDetails(Path.Combine(logos, "missing.png")), root)?.LogoPath);
            Assert.Null(NowPlayingResolver.TryReadLiveTv(new Dictionary<string, string> { ["kind"] = "app", ["appId"] = "netflix" }, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    // ---------------------------------------------------------------- watch log, Insights, feed

    [Theory]
    [InlineData("Live TV (Ch 1 • DR1)", "DR1")]
    [InlineData("Live TV (TV 2 Charlie)", "TV 2 Charlie")]
    [InlineData("Netflix", null)]
    [InlineData("Live TV", null)]
    public void Channel_is_read_from_the_old_live_tv_text(string showing, string? expected)
    {
        Assert.Equal(expected, WatchTimeReport.ChannelFromShowing(showing));
    }

    private static readonly DateTime Now = new(2026, 11, 23, 22, 0, 0, DateTimeKind.Local);

    private static JsonElement Ev(string name, DateTime local, object data)
    {
        var ts = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        return JsonSerializer.SerializeToElement(new { ts, block = InsightsReport.BlockOf(local.Hour), stream = "media", device = "lg-tv", @event = name, origin = "external", data });
    }

    private static JsonElement LiveTvEnded(DateTime end, string title, string? channel, int seconds, string? logo = null) =>
        Ev("session.ended", end, new { kind = "tv_channel", title, channelName = channel, channelLogo = logo, durationSec = seconds, exclusiveSec = seconds });

    [Fact]
    public void Insights_rank_channels_with_their_logos_and_list_programs_not_channel_texts()
    {
        var events = new[]
        {
            LiveTvEnded(Now.AddHours(-3), "Min familie og andre dyr III (6:8)", "DR1", 3000, "dr1-dk.png"),
            LiveTvEnded(Now.AddHours(-2), "Deadline", "DR1", 1800, "dr1-dk.png"),
            LiveTvEnded(Now.AddHours(-1), "Nyhederne", "TV 2", 1200, "tv2-dk.png"),
            // Written before the plugin sent channel details: the channel comes from the text, it is no program title.
            LiveTvEnded(Now.AddDays(-1), "Live TV (Ch 1 • DR1)", null, 600),
            LiveTvEnded(Now.AddDays(-1), "Bad logo", "DR3", 60, "../settings.json"),
        };

        var at = new DateTimeOffset(Now, TimeZoneInfo.Local.GetUtcOffset(Now));
        var report = InsightsReport.Build(events, at, 7);

        var channels = report.Watch.TopChannels;
        Assert.Equal(["DR1", "TV 2", "DR3"], channels.Select(c => c.Label));
        Assert.Equal(5400, channels[0].Seconds);
        Assert.Equal(3, channels[0].Sessions);
        Assert.Equal("dr1-dk.png", channels[0].Logo);
        Assert.Null(channels[2].Logo); // Not a plain file name: dropped.

        Assert.Contains(report.Watch.TopApps, a => a.Label == "DR1" && a.Logo == "dr1-dk.png");
        Assert.Contains(report.Watch.TopTitles, t => t.Label == "Deadline" && t.Detail == "DR1" && t.Logo == "dr1-dk.png");
        Assert.DoesNotContain(report.Watch.TopTitles, t => t.Label.StartsWith("Live TV", StringComparison.Ordinal));

        // DR1 leads all apps, so "DR1 leads" says it; no second observation about the channel.
        Assert.Contains(report.Insights, i => i.Title == "DR1 leads");
        Assert.DoesNotContain(report.Insights, i => i.Title == "DR1 is your channel");

        // With another app in front, the channel gets its own observation.
        var withAppleTv = events.Append(Ev("session.ended", Now.AddHours(-4), new { kind = "appletv", title = "Night Train", app = "DRTV", durationSec = 9000, exclusiveSec = 9000 }));
        Assert.Contains(InsightsReport.Build(withAppleTv, at, 7).Insights, i => i.Title == "DR1 is your channel");
    }

    [Fact]
    public void Older_live_tv_sessions_get_their_logo_by_channel_name_and_a_plain_longest_title()
    {
        var events = new[] { LiveTvEnded(Now.AddHours(-2), "Live TV (Ch 7 • TV 2 Charlie)", null, 3360) };
        var at = new DateTimeOffset(Now, TimeZoneInfo.Local.GetUtcOffset(Now));

        var report = InsightsReport.Build(events, at, 7, name => name == "TV 2 Charlie" ? "tv2-charlie-dk.png" : null);

        Assert.Equal("tv2-charlie-dk.png", report.Watch.TopChannels.Single().Logo);
        Assert.Equal("TV 2 Charlie", report.Watch.Longest?.Title);
        Assert.Equal("TV 2 Charlie", report.Watch.Longest?.App);
        Assert.Contains(report.Insights, i => i.Title == "Longest sitting" && i.Text.StartsWith("TV 2 Charlie for 56 min", StringComparison.Ordinal));

        var item = ActivityFeed.Describe(events, name => name == "TV 2 Charlie" ? "tv2-charlie-dk.png" : null).Single();
        Assert.Equal("tv2-charlie-dk.png", item.Logo);
    }

    [Fact]
    public void Feed_names_the_program_and_channel_and_carries_the_logo()
    {
        var started = ActivityFeed.Describe(Ev("session.started", Now, new { kind = "tv_channel", title = "Deadline", channelName = "DR1", channelLogo = "dr1-dk.png" }));
        Assert.Equal("Started Deadline · DR1", started?.Text);
        Assert.Equal("dr1-dk.png", started?.Logo);

        var channel = ActivityFeed.Describe(JsonSerializer.SerializeToElement(new
        {
            ts = "2026-11-23T21:00:00+01:00", stream = "tv", device = "smart-tv", @event = "channel.changed", origin = "auto",
            data = new { channelName = "TV 2", channelNumber = "2" },
        }));
        Assert.Equal("TV channel TV 2 (Ch 2)", channel?.Text);
    }

    [Theory]
    [InlineData("dr1-dk.png", "dr1-dk.png")]
    [InlineData("pinned-0a1b2c3d4e5f.webp", "pinned-0a1b2c3d4e5f.webp")]
    [InlineData("../settings.json", null)]
    [InlineData("..png", null)]
    [InlineData("DR1-DK.png", null)]
    [InlineData("dr1.svg", null)]
    [InlineData(null, null)]
    public void Logo_names_are_plain_cached_file_names(string? name, string? expected)
    {
        Assert.Equal(expected, InsightsReport.LogoName(name));
    }
}
