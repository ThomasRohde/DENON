using System.Net;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>Logos for the saved stations: the TuneIn search, the match, the file on this PC and the hero.</summary>
public sealed class StationLogoTests : IDisposable
{
    private const string SearchAnswer = """
        { "head": { "status": "200" }, "body": [
          { "element": "outline", "type": "audio", "text": "DR P4 København", "item": "station", "guide_id": "s37309",
            "image": "http://cdn-profiles.tunein.com/s37309/images/logoq.png" },
          { "element": "outline", "type": "audio", "text": "DR P4 Fyn", "item": "station", "guide_id": "s8330",
            "image": "http://cdn-profiles.tunein.com/s8330/images/logoq.png" },
          { "element": "outline", "type": "link", "text": "More stations", "item": "show" },
          { "element": "outline", "text": "Group", "children": [
            { "element": "outline", "type": "audio", "text": "Groove Salad", "item": "station", "guide_id": "s2591",
              "image": "http://cdn-radiotime-logos.tunein.com/s2591q.png" } ] }
        ] }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "denon-logo-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FakeDirectory(string answer, HttpStatusCode largeLogoStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsoluteUri}");
            return Task.FromResult(request.Method == HttpMethod.Head
                ? new HttpResponseMessage(largeLogoStatus)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) });
        }
    }

    private StationLogoService Service(HttpMessageHandler directory) =>
        new(new AppLogStore(Path.Combine(_folder, "app.log")), directory, Path.Combine(_folder, "station-logos.json"));

    [Theory]
    [InlineData("DR P4 København", "dr p4 kobenhavn")]
    [InlineData("DR P4 Kobenhavn", "dr p4 kobenhavn")]
    [InlineData("Radio Paradise (Main)", "radio paradise")]
    [InlineData("  Café  Zürich – Jazz! ", "cafe zurich jazz")]
    [InlineData("", "")]
    public void A_name_is_compared_without_case_accents_and_brackets(string name, string expected) =>
        Assert.Equal(expected, StationLogoService.Normalize(name));

    [Fact]
    public void The_match_is_the_same_name_then_a_name_whose_words_are_all_shared()
    {
        var results = StationLogoService.ParseSearch(SearchAnswer);
        Assert.Equal(3, results.Count); // the link has no picture; the station in the group counts

        Assert.Equal("s37309", StationLogoService.PickMatch("DR P4 Kobenhavn", results)!.GuideId);
        Assert.Equal("s2591", StationLogoService.PickMatch("SomaFM Groove Salad", results)!.GuideId);
        // One shared word is not enough, and another DR P4 station is not this one.
        Assert.Null(StationLogoService.PickMatch("Salad", results));
        Assert.Null(StationLogoService.PickMatch("DR P4 Bornholm", results));
    }

    [Fact]
    public void The_larger_logo_is_used_when_the_address_has_the_known_form()
    {
        Assert.Equal("http://cdn-profiles.tunein.com/s45455/images/logod.png?t=1",
            StationLogoService.LargerLogo("http://cdn-profiles.tunein.com/s45455/images/logoq.png?t=1"));
        Assert.Equal("http://cdn-radiotime-logos.tunein.com/s2591q.png",
            StationLogoService.LargerLogo("http://cdn-radiotime-logos.tunein.com/s2591q.png"));
    }

    [Fact]
    public async Task A_station_is_searched_once_and_the_logo_is_kept_in_the_file()
    {
        var directory = new FakeDirectory(SearchAnswer);
        var service = Service(directory);
        var found = new List<string>();
        service.LogoFound += found.Add;

        Assert.Null(service.TryGet("DR P4 Kobenhavn"));
        var logo = await service.EnsureAsync("DR P4 Kobenhavn");

        Assert.Equal("http://cdn-profiles.tunein.com/s37309/images/logod.png", logo);
        Assert.Equal(logo, service.TryGet("dr p4 københavn"));
        Assert.Equal(["DR P4 Kobenhavn"], found);
        Assert.Contains("query=DR%20P4%20Kobenhavn", directory.Requests[0]);

        // The same start: no second search. A new start: the file answers, the network is not asked.
        await service.EnsureAsync("DR P4 København");
        var requests = directory.Requests.Count;
        var restarted = Service(new FakeDirectory("{}"));
        Assert.Equal(logo, restarted.TryGet("DR P4 Kobenhavn"));
        Assert.Equal(logo, await restarted.EnsureAsync("DR P4 Kobenhavn"));
        Assert.Equal(requests, directory.Requests.Count);
    }

    [Fact]
    public async Task The_small_logo_stays_when_the_large_one_does_not_load()
    {
        var service = Service(new FakeDirectory(SearchAnswer, HttpStatusCode.NotFound));
        Assert.Equal("http://cdn-profiles.tunein.com/s37309/images/logoq.png", await service.EnsureAsync("DR P4 København"));
    }

    [Fact]
    public async Task A_station_that_the_directory_does_not_know_gets_no_logo_and_one_search()
    {
        var directory = new FakeDirectory(SearchAnswer);
        var service = Service(directory);

        Assert.Null(await service.EnsureAsync("My Own Stream"));
        Assert.Null(await service.EnsureAsync("My Own Stream"));
        Assert.Null(service.TryGet("My Own Stream"));
        Assert.Single(directory.Requests);
    }

    [Fact]
    public void The_hero_shows_the_station_logo_only_for_a_stream_that_this_app_started()
    {
        var player = new ReceiverPlayerInfo("Denon AVR-X2400H", 101, "Denon AVR-X2400H", "3.1", "192.168.1.3", "wired", 1, "SER");
        ReceiverStatus Status(NowPlayingMedia media) => new(
            player, "play", 41, false, media, DateTimeOffset.Now,
            IsPoweredOn: true, IsMainZoneOn: true, InputSource: "NET", SoundMode: "STEREO");
        const string logo = "http://cdn-profiles.tunein.com/s45455/images/logod.png";

        // The app's own stream: HEOS reports the "Url Stream" placeholder with no picture.
        var ownStream = Status(new NowPlayingMedia("song", "Url Stream", "Url Stream", "Url Stream", "", "1", "1", 1, 1024));
        Assert.Equal(logo, NowPlayingResolver.Resolve(ownStream, "DR P6 Beat", [], activeStationLogoUrl: logo).ImageUrl);
        Assert.Equal(string.Empty, NowPlayingResolver.Resolve(ownStream, "DR P6 Beat", []).ImageUrl);
        Assert.Equal(string.Empty, NowPlayingResolver.Resolve(ownStream, null, [], activeStationLogoUrl: logo).ImageUrl);

        // HEOS has its own picture, or another service plays: the station logo stays out.
        var withArt = Status(new NowPlayingMedia("station", "Song", "Album", "Artist", "http://heos/art.jpg", "", "", null, 3, "TuneIn Station"));
        Assert.Equal("http://heos/art.jpg", NowPlayingResolver.Resolve(withArt, "DR P6 Beat", [], activeStationLogoUrl: logo).ImageUrl);
        var spotify = Status(new NowPlayingMedia("song", "Song", "Album", "Artist", "", "", "", null, 4));
        Assert.Equal(string.Empty, NowPlayingResolver.Resolve(spotify, "DR P6 Beat", [], activeStationLogoUrl: logo).ImageUrl);
    }
}
