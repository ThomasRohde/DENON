using System.Text.Json;
using Denon.App.Core.Heos;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// A phone (also a Guest phone) sends the whole YouTube track when it queues one. The app must not trust the
/// addresses in it: Cinema opens the watch address and the app loads the thumbnail. The phone page must also
/// not put setting values inside inline JavaScript.
/// </summary>
public sealed class GuestInputSecurityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-guest-input-" + Guid.NewGuid().ToString("N"));

    public GuestInputSecurityTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(@"\\attacker\share\run.exe", @"file://attacker/share/a.png")]
    [InlineData("search-ms:query=x&crumb=location:\\\\attacker\\share", "javascript:alert(1)")]
    [InlineData("", "")]
    public void A_phone_track_gets_its_addresses_from_the_video_id(string watchUrl, string thumbnailUrl)
    {
        var sent = new YouTubeTrackItem("dQw4w9WgXcQ", "Song", "Artist", TimeSpan.FromMinutes(3), thumbnailUrl, watchUrl, "phone-set-id");

        var track = RemoteWebApiHandler.BuildRemoteTrack(sent);

        Assert.NotNull(track);
        Assert.Equal("dQw4w9WgXcQ", track.VideoId);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", track.WatchUrl);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", track.ThumbnailUrl);
        Assert.Null(track.QueueEntryId);
        Assert.Equal("Song", track.Title);
        Assert.Equal("Artist", track.Author);
        Assert.Equal(TimeSpan.FromMinutes(3), track.Duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("dQw4w9WgXcQx")]
    [InlineData("../../etc/pa")]
    [InlineData("dQw4w9WgX/Q")]
    [InlineData("dQw4w9WgX%Q")]
    public void A_phone_track_with_a_bad_video_id_is_refused(string videoId)
    {
        var sent = new YouTubeTrackItem(videoId, "Song", "Artist", null, "", "");

        Assert.Null(RemoteWebApiHandler.BuildRemoteTrack(sent));
    }

    [Fact]
    public void A_phone_track_has_clean_text_and_a_sane_duration()
    {
        var sent = new YouTubeTrackItem("dQw4w9WgXcQ", "Bad\r\nTitle\u0007" + new string('x', 500), "  ", TimeSpan.FromSeconds(-5), "", "");

        var track = RemoteWebApiHandler.BuildRemoteTrack(sent);

        Assert.NotNull(track);
        Assert.DoesNotContain('\r', track.Title);
        Assert.DoesNotContain('\n', track.Title);
        Assert.DoesNotContain('\u0007', track.Title);
        Assert.True(track.Title.Length <= 200);
        Assert.StartsWith("BadTitle", track.Title);
        Assert.Equal(string.Empty, track.Author);
        Assert.Null(track.Duration);

        Assert.Equal("YouTube track", RemoteWebApiHandler.BuildRemoteTrack(sent with { Title = "" })!.Title);
        Assert.Null(RemoteWebApiHandler.BuildRemoteTrack(sent with { Duration = TimeSpan.FromDays(3) })!.Duration);
    }

    [Fact]
    public async Task A_guest_queue_request_stores_only_youtube_addresses()
    {
        var log = new AppLogStore(Path.Combine(_tempDir, "app.log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, "settings.json"));
        var cfg = await store.InitializeAsync();
        var telemetry = new EventTelemetryLog(store, log, new RoomEventBus(log), Path.Combine(_tempDir, "events"));
        var stateStore = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(), store, stateStore, telemetry, log);
        var ytMedia = new YouTubeMediaService(store, log);
        using var relay = new LocalAudioRelayServer(ytMedia, store, log);
        using var ytQueue = new YouTubeQueueService(relay, controller, stateStore, log);
        using var localJukebox = new LocalMediaJukeboxService(relay, controller, stateStore, log);
        var plugins = new PluginHostService([], store, stateStore, controller, telemetry, log);
        using var automations = new AutomationEngineService(store, controller, stateStore, plugins, telemetry, new RoomEventBus(log), log);
        var handler = new RemoteWebApiHandler(controller, stateStore, store, ytMedia, ytQueue, localJukebox, plugins, telemetry, automations, log);

        var hostile = JsonSerializer.Serialize(
            new YouTubeTrackItem("dQw4w9WgXcQ", "Song", "Artist", null, @"file://attacker/share/a.png", @"\\attacker\share\run.exe"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var queued = await handler.HandleAsync("POST", "/api/youtube/queue", null, hostile, cfg.Remote.GuestToken, CancellationToken.None);

        Assert.Equal(200, queued.StatusCode);
        var item = Assert.Single(ytQueue.Snapshot.Queue);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", item.WatchUrl);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", item.ThumbnailUrl);

        var badId = JsonSerializer.Serialize(
            new YouTubeTrackItem("not-an-id", "Song", "Artist", null, "", ""),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var refused = await handler.HandleAsync("POST", "/api/youtube/queue", null, badId, cfg.Remote.GuestToken, CancellationToken.None);

        Assert.Equal(400, refused.StatusCode);
        Assert.Single(ytQueue.Snapshot.Queue);

        await telemetry.DisposeAsync();
    }

    [Fact]
    public void The_phone_page_keeps_values_out_of_inline_javascript()
    {
        var html = RemoteWebUiHtml.PageHtml;

        // No rendered chip builds an onclick from a value.
        Assert.DoesNotContain("onclick=\"post('/api/scene'", html);
        Assert.DoesNotContain("onclick=\"post('/api/input'", html);
        Assert.DoesNotContain("onclick=\"post('/api/station'", html);
        Assert.DoesNotContain("onclick=\"post('/api/plugins/", html);
        Assert.DoesNotContain("${esc(", html.Split('\n').Where(l => l.Contains("onclick=")).Aggregate(string.Empty, string.Concat));

        // Values ride in data-* attributes and one listener reads them.
        Assert.Contains("data-act=\"scene\" data-id=\"${esc(sc.id)}\"", html);
        Assert.Contains("e.target.closest('[data-act]')", html);

        // Colours go into style attributes only as plain hex.
        Assert.DoesNotContain("background:${esc(", html);
        Assert.Contains("function safeColor(c)", html);
    }
}
