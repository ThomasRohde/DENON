using System.Text;
using System.Text.Json;
using Denon.App.Core.Heos;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Media;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.Desktop.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// Jukebox tags into Now Playing (title, artist, album, length, cover), the cover art cache, and the Jukebox and
/// YouTube queues saved and loaded again over an app restart (#79).
/// </summary>
public sealed class MediaQueueTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4];

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-queue-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _disposables = [];

    public MediaQueueTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_disposables))
        {
            try { item.Dispose(); } catch { }
        }

        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string QueueDir => Path.Combine(_tempDir, "queues");

    private string ArtDir => Path.Combine(_tempDir, "jukebox-art");

    // ---------------------------------------------------------------- tags into Now Playing

    [Fact]
    public async Task Added_files_get_their_tags_and_cover_and_now_playing_shows_them()
    {
        var file = WriteFlac("01 - file name title.flac", title: "Tag Title", artist: "Tag Artist", album: "Tag Album", seconds: 183, cover: Png);
        var (jukebox, _, cache) = CreateJukebox();

        var (added, _, _) = jukebox.AddPathsDetailed([file]);
        Assert.Equal(1, added);
        await jukebox.WhenTagsReadAsync();

        var track = Assert.Single(jukebox.Snapshot.Tracks);
        Assert.True(track.TagsRead);
        Assert.Equal("Tag Title", track.Title);
        Assert.Equal("Tag Artist", track.Artist);
        Assert.Equal("Tag Album", track.Album);
        Assert.Equal(TimeSpan.FromSeconds(183), track.Duration);
        Assert.Equal("3:03", track.DurationDisplay);
        Assert.Equal("Tag Artist • Tag Album", track.ArtistAlbumDisplay);
        Assert.True(cache.Contains(track.CoverArtPath));
        Assert.Equal(Png, File.ReadAllBytes(track.CoverArtPath!));

        var resolved = NowPlayingResolver.Resolve(PlayingRelayStatus(), null, [], activeLocalTrack: track);
        Assert.Equal("Tag Title", resolved.Title);
        Assert.StartsWith("Tag Artist • Tag Album • Local FLAC", resolved.Detail);
        Assert.Equal(new Uri(track.CoverArtPath!).AbsoluteUri, resolved.ImageUrl);
        Assert.StartsWith("file:///", resolved.ImageUrl);
    }

    [Fact]
    public async Task A_file_without_tags_keeps_the_name_from_its_path_and_has_no_cover()
    {
        var folder = Path.Combine(_tempDir, "Some Band");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Plain Song.flac");
        File.WriteAllBytes(file, new byte[64]);
        var (jukebox, _, _) = CreateJukebox();

        jukebox.AddPaths([file]);
        await jukebox.WhenTagsReadAsync();

        var track = Assert.Single(jukebox.Snapshot.Tracks);
        Assert.True(track.TagsRead);
        Assert.Equal("Plain Song", track.Title);
        Assert.Equal("Some Band", track.Artist);
        Assert.Null(track.Album);
        Assert.Null(track.CoverArtPath);
        Assert.Equal("Some Band", track.ArtistAlbumDisplay);
        Assert.Equal(string.Empty, NowPlayingResolver.Resolve(PlayingRelayStatus(), null, [], activeLocalTrack: track).ImageUrl);
    }

    [Fact]
    public void WithTags_uses_the_tag_fields_and_keeps_path_names_for_missing_ones()
    {
        var track = new LocalAudioTrackItem("t1", @"C:\Music\Band X\07 - Path Title.flac", "07 - Path Title.flac", "Path Title", "Band X", ".flac", 10);
        var tags = new AudioTags(null, "Tag Artist", "Tag Album", 7, TimeSpan.FromSeconds(61), null);

        var updated = LocalMediaJukeboxService.WithTags(track, tags, coverArtCache: null);

        Assert.Equal("Path Title", updated.Title);
        Assert.Equal("Tag Artist", updated.Artist);
        Assert.Equal("Tag Album", updated.Album);
        Assert.Equal(7, updated.TrackNumber);
        Assert.Equal(TimeSpan.FromSeconds(61), updated.Duration);
        Assert.True(updated.TagsRead);
        Assert.Equal("t1", updated.TrackId);
    }

    [Fact]
    public void The_auto_advance_wait_uses_the_track_length_from_the_tags()
    {
        // 183 s track, 100 s played when the file was sent: wait what is left plus 5 s.
        Assert.Equal(88, LocalMediaJukeboxService.DrainLimitSeconds(TimeSpan.FromSeconds(183), 100), 3);
        Assert.Equal(LocalMediaJukeboxService.UnknownLengthDrainLimitSeconds, LocalMediaJukeboxService.DrainLimitSeconds(null, 100));
    }

    // ---------------------------------------------------------------- cover art cache

    [Fact]
    public void Cover_cache_keeps_one_file_per_picture_and_refuses_large_pictures()
    {
        var cache = new LocalCoverArtCache(ArtDir);

        var first = cache.Store(new AudioCoverArt(Png, "image/png"));
        var second = cache.Store(new AudioCoverArt(Png.ToArray(), "image/png"));
        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.EndsWith(".png", first);
        Assert.Single(Directory.GetFiles(ArtDir));

        Assert.Null(cache.Store(new AudioCoverArt(new byte[LocalCoverArtCache.MaxCoverBytes + 1], "image/jpeg")));
        Assert.Null(cache.Store(null));
        Assert.Single(Directory.GetFiles(ArtDir));
    }

    [Fact]
    public void Cover_cache_accepts_only_full_paths_directly_in_its_folder()
    {
        var cache = new LocalCoverArtCache(ArtDir);

        Assert.True(cache.IsInCacheFolder(Path.Combine(ArtDir, "abc.jpg")));
        Assert.False(cache.IsInCacheFolder(Path.Combine(ArtDir, "sub", "abc.jpg")));
        Assert.False(cache.IsInCacheFolder(Path.Combine(ArtDir, "..", "abc.jpg")));
        Assert.False(cache.IsInCacheFolder(@"\\server\share\abc.jpg"));
        Assert.False(cache.IsInCacheFolder("abc.jpg"));
        Assert.False(cache.IsInCacheFolder(null));
    }

    // ---------------------------------------------------------------- queue save and load

    [Fact]
    public async Task Jukebox_queue_comes_back_after_a_restart_without_playing()
    {
        var a = WriteFlac("a.flac", title: "First", artist: "Artist", album: "Album", seconds: 90, cover: Png);
        var b = WriteFlac("b.flac", title: "Second", artist: "Artist", album: "Album", seconds: 120, cover: Png);
        var (jukebox, store, _) = CreateJukebox();
        jukebox.AddPaths([a, b]);
        await jukebox.WhenTagsReadAsync();
        jukebox.ToggleShuffle();
        jukebox.CycleRepeatMode();
        var before = jukebox.Snapshot.Tracks;
        jukebox.Dispose();
        store.Flush();

        var (restored, _, _) = CreateJukebox();
        var snapshot = restored.Snapshot;

        Assert.Equal(before.Select(static t => t.TrackId), snapshot.Tracks.Select(static t => t.TrackId));
        Assert.Equal(["First", "Second"], snapshot.Tracks.Select(static t => t.Title));
        Assert.All(snapshot.Tracks, static t => Assert.True(t.TagsRead));
        Assert.Equal(TimeSpan.FromSeconds(120), snapshot.Tracks[1].Duration);
        Assert.Equal(before[0].CoverArtPath, snapshot.Tracks[0].CoverArtPath);
        Assert.True(snapshot.IsShuffleEnabled);
        Assert.Equal(YouTubeRepeatMode.All, snapshot.RepeatMode);

        // Only the queue comes back: nothing plays.
        Assert.False(snapshot.IsActive);
        Assert.Null(snapshot.CurrentTrack);
        Assert.False(restored.IsLocalPlaybackActive);
    }

    [Fact]
    public async Task Jukebox_restore_drops_bad_entries_and_reads_a_lost_cover_again()
    {
        var good = WriteFlac("good.flac", title: "Good", artist: "Artist", album: "Album", seconds: 30, cover: Png);
        Directory.CreateDirectory(QueueDir);
        var saved = new SavedJukeboxQueue(
            [
                new SavedLocalTrack("id1", good, "Good", "Artist", 10, "Album", 30, 1, @"\\server\share\cover.jpg", TagsRead: true),
                new SavedLocalTrack("id2", good, "Duplicate", "Artist", 10),
                new SavedLocalTrack("id3", "relative.flac", "Relative", "Artist", 10),
                new SavedLocalTrack("id4", Path.Combine(_tempDir, "notes.txt"), "Text", "Artist", 10),
            ],
            IsShuffleEnabled: false,
            RepeatMode: (YouTubeRepeatMode)99);
        File.WriteAllText(Path.Combine(QueueDir, "jukebox-queue.json"), JsonSerializer.Serialize(saved, JsonOptions));

        var (jukebox, _, cache) = CreateJukebox();
        var restored = Assert.Single(jukebox.Snapshot.Tracks);
        Assert.Equal("id1", restored.TrackId);
        Assert.Null(restored.CoverArtPath); // The share path never comes back.
        Assert.False(restored.TagsRead);
        Assert.Equal(YouTubeRepeatMode.Off, jukebox.Snapshot.RepeatMode);

        await jukebox.WhenTagsReadAsync();
        var read = Assert.Single(jukebox.Snapshot.Tracks);
        Assert.True(read.TagsRead);
        Assert.True(cache.Contains(read.CoverArtPath));
    }

    [Fact]
    public void Clearing_the_jukebox_saves_an_empty_queue()
    {
        var file = WriteFlac("a.flac", title: "A", artist: "B", album: "C", seconds: 10, cover: null);
        var (jukebox, store, _) = CreateJukebox();
        jukebox.AddPaths([file]);
        jukebox.Clear();
        store.Flush();

        Assert.Empty(store.LoadJukebox()!.Tracks);
    }

    [Fact]
    public async Task A_file_changed_between_runs_gets_its_tags_read_again()
    {
        var file = WriteFlac("song.flac", title: "Old Title", artist: "Artist", album: "Album", seconds: 60, cover: null);
        var (jukebox, store, _) = CreateJukebox();
        jukebox.AddPaths([file]);
        await jukebox.WhenTagsReadAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(jukebox.Snapshot.Tracks).Duration);
        jukebox.Dispose();
        store.Flush();

        // The same path holds a new, longer recording now.
        WriteFlac("song.flac", title: "New Title", artist: "Artist", album: "Album", seconds: 300, cover: null);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));

        var (restored, _, _) = CreateJukebox();
        await restored.WhenTagsReadAsync();

        var track = Assert.Single(restored.Snapshot.Tracks);
        Assert.Equal("New Title", track.Title);
        Assert.Equal(TimeSpan.FromSeconds(300), track.Duration);
    }

    [Fact]
    public async Task A_locked_file_is_read_again_when_the_lock_is_gone()
    {
        var file = WriteFlac("locked.flac", title: "Tag Title", artist: "Artist", album: "Album", seconds: 90, cover: null);
        var (jukebox, _, _) = CreateJukebox();
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            jukebox.AddPaths([file]);
            await jukebox.WhenTagsReadAsync();
            Assert.False(Assert.Single(jukebox.Snapshot.Tracks).TagsRead);
        }

        // The next read (the worker or a play) gets the tags.
        var id = Assert.Single(jukebox.Snapshot.Tracks).TrackId;
        Assert.True(jukebox.ReadTagsNowForTest(id));
        var track = Assert.Single(jukebox.Snapshot.Tracks);
        Assert.True(track.TagsRead);
        Assert.Equal("Tag Title", track.Title);
        Assert.Equal(TimeSpan.FromSeconds(90), track.Duration);
    }

    [Fact]
    public void Shuffle_and_repeat_come_back_with_an_empty_queue()
    {
        var (jukebox, store, _) = CreateJukebox();
        jukebox.ToggleShuffle();
        jukebox.CycleRepeatMode();
        jukebox.Clear();
        jukebox.Dispose();
        store.Flush();

        var (restored, _, _) = CreateJukebox();

        Assert.Empty(restored.Snapshot.Tracks);
        Assert.True(restored.Snapshot.IsShuffleEnabled);
        Assert.Equal(YouTubeRepeatMode.All, restored.Snapshot.RepeatMode);
    }

    [Fact]
    public void A_damaged_queue_file_loads_as_no_queue()
    {
        Directory.CreateDirectory(QueueDir);
        File.WriteAllText(Path.Combine(QueueDir, "jukebox-queue.json"), "{ not json");
        File.WriteAllText(Path.Combine(QueueDir, "youtube-queue.json"), "[1,2,3]");

        var (jukebox, store, _) = CreateJukebox();

        Assert.Empty(jukebox.Snapshot.Tracks);
        Assert.Null(store.LoadYouTube());
    }

    [Fact]
    public void YouTube_queue_and_history_come_back_after_a_restart_without_playing()
    {
        var (queue, store) = CreateYouTubeQueue();
        queue.Enqueue(new YouTubeTrackItem("abcdefghijk", "One", "Author A", TimeSpan.FromSeconds(200), "https://i.ytimg.com/vi/abcdefghijk/maxresdefault.jpg", "https://www.youtube.com/watch?v=abcdefghijk"));
        queue.Enqueue(new YouTubeTrackItem("ABCDEFGHIJK", "Two", "Author B", null, "https://i.ytimg.com/vi/ABCDEFGHIJK/hqdefault.jpg", "https://www.youtube.com/watch?v=ABCDEFGHIJK"));
        queue.CycleRepeatMode();
        var before = queue.Snapshot.Queue;
        queue.Dispose();
        store.Flush();

        var (restored, _) = CreateYouTubeQueue();
        var snapshot = restored.Snapshot;

        Assert.Equal(before.Select(static t => t.EntryId), snapshot.Queue.Select(static t => t.EntryId));
        Assert.Equal(before, snapshot.Queue);
        Assert.Equal(YouTubeRepeatMode.All, snapshot.RepeatMode);
        Assert.False(snapshot.IsActive);
        Assert.Null(snapshot.CurrentTrack);
        Assert.False(restored.IsYouTubePlaybackActive);
    }

    [Fact]
    public void YouTube_restore_makes_the_watch_address_from_the_id_and_drops_bad_entries()
    {
        var saved = new List<SavedYouTubeTrack>
        {
            new("abcdefghijk", "Good\u0007 title", "Author", 200, "http://evil.example/thumb.jpg", "entry1"),
            new("not-an-id", "Bad id", "Author", 10, null, "entry2"),
            new("ABCDEFGHIJK", "Same entry id", "Author", -5, "https://i.ytimg.com/vi/ABCDEFGHIJK/hqdefault.jpg", "entry1"),
        };

        var tracks = YouTubeQueueService.RestoreTracks(saved, max: 10);

        Assert.Equal(2, tracks.Count);
        Assert.Equal("Good title", tracks[0].Title);
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", tracks[0].WatchUrl);
        Assert.Equal("https://i.ytimg.com/vi/abcdefghijk/hqdefault.jpg", tracks[0].ThumbnailUrl);
        Assert.Equal("entry1", tracks[0].EntryId);
        Assert.NotEqual("entry1", tracks[1].EntryId); // A second entry with the same id gets a new one.
        Assert.Null(tracks[1].Duration);
        Assert.Single(YouTubeQueueService.RestoreTracks(saved, max: 1));
    }

    // ---------------------------------------------------------------- artwork addresses

    [Fact]
    public void The_phone_remote_gets_only_web_artwork()
    {
        Assert.True(RemoteWebApiHandler.IsWebAddress("https://i.ytimg.com/vi/abcdefghijk/hqdefault.jpg"));
        Assert.True(RemoteWebApiHandler.IsWebAddress("http://192.168.1.20/art.jpg"));
        Assert.False(RemoteWebApiHandler.IsWebAddress("file:///C:/Users/x/AppData/Local/DenonDesktop/jukebox-art/a.jpg"));
        Assert.False(RemoteWebApiHandler.IsWebAddress(string.Empty));
    }

    [Fact]
    public void Album_art_colours_read_web_pictures_and_jukebox_covers_only()
    {
        var cover = new Uri(Path.Combine(AppDataPaths.JukeboxArtDirectory, "0123.jpg"));
        var logo = new Uri(Path.Combine(AppDataPaths.PluginsDirectory, "data", "smart-tv", "logo.png"));

        Assert.True(AlbumArtPaletteService.IsSupportedSource(new Uri("https://example.com/a.jpg")));
        Assert.True(AlbumArtPaletteService.IsSupportedSource(cover));
        Assert.False(AlbumArtPaletteService.IsSupportedSource(logo));
        Assert.False(AlbumArtPaletteService.IsSupportedSource(new Uri(@"\\server\share\jukebox-art\a.jpg")));
    }

    // ---------------------------------------------------------------- helpers

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static ReceiverStatus PlayingRelayStatus() => new(
        new ReceiverPlayerInfo("Test AVR", 1, "Test AVR", "1.0", "192.0.2.10", "wired", 1, "SER"),
        "play", 40, false,
        new NowPlayingMedia("song", "Url Stream", "Url Stream", "Url Stream", "", "1", "1", 1, 1024),
        DateTimeOffset.Now,
        IsPoweredOn: true, IsMainZoneOn: true, InputSource: "NET", SoundMode: "STEREO");

    private (LocalMediaJukeboxService Jukebox, MediaQueueStore Store, LocalCoverArtCache Cache) CreateJukebox()
    {
        var (relay, controller, stateStore, log) = CreateMediaHost();
        var store = Track(new MediaQueueStore(QueueDir, log));
        var cache = new LocalCoverArtCache(ArtDir);
        var jukebox = Track(new LocalMediaJukeboxService(relay, controller, stateStore, log, store, cache));
        return (jukebox, store, cache);
    }

    private (YouTubeQueueService Queue, MediaQueueStore Store) CreateYouTubeQueue()
    {
        var (relay, controller, stateStore, log) = CreateMediaHost();
        var store = Track(new MediaQueueStore(QueueDir, log));
        return (Track(new YouTubeQueueService(relay, controller, stateStore, log, store)), store);
    }

    private (LocalAudioRelayServer Relay, ReceiverController Controller, ReceiverStateStore StateStore, AppLogStore Log) CreateMediaHost()
    {
        var name = Guid.NewGuid().ToString("N")[..6];
        var log = new AppLogStore(Path.Combine(_tempDir, $"app-{name}.log"));
        var settings = new AppSettingsStore(log, Path.Combine(_tempDir, $"settings-{name}.json"));
        var telemetry = new EventTelemetryLog(settings, log, new RoomEventBus(log), Path.Combine(_tempDir, $"events-{name}"));
        var stateStore = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(), settings, stateStore, telemetry, log);
        var relay = Track(new LocalAudioRelayServer(new YouTubeMediaService(settings, log), settings, log));
        return (relay, controller, stateStore, log);
    }

    private T Track<T>(T item)
        where T : IDisposable
    {
        _disposables.Add(item);
        return item;
    }

    private string WriteFlac(string fileName, string title, string artist, string album, int seconds, byte[]? cover)
    {
        var blocks = new List<byte[]>
        {
            Ascii("fLaC"),
            FlacBlock(0, last: false, StreamInfo(44100, 44100L * seconds)),
            FlacBlock(4, last: cover is null, VorbisComments($"TITLE={title}", $"ARTIST={artist}", $"ALBUM={album}", "TRACKNUMBER=1")),
        };
        if (cover is not null)
        {
            blocks.Add(FlacBlock(6, last: true, FlacPicture(3, "image/png", cover)));
        }

        blocks.Add(new byte[512]);
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, Concat([.. blocks]));
        return path;
    }

    private static byte[] FlacBlock(int type, bool last, byte[] data) =>
        Concat([(byte)((last ? 0x80 : 0) | type), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length], data);

    private static byte[] StreamInfo(int sampleRate, long totalSamples)
    {
        var info = new byte[34];
        info[10] = (byte)(sampleRate >> 12);
        info[11] = (byte)(sampleRate >> 4);
        info[12] = (byte)(((sampleRate & 0xF) << 4) | (1 << 1));
        info[13] = (byte)(0xF0 | (int)((totalSamples >> 32) & 0xF));
        Be32((uint)totalSamples).CopyTo(info, 14);
        return info;
    }

    private static byte[] VorbisComments(params string[] comments)
    {
        var vendor = Encoding.UTF8.GetBytes("test");
        var parts = new List<byte[]> { Le32(vendor.Length), vendor, Le32(comments.Length) };
        foreach (var comment in comments)
        {
            var bytes = Encoding.UTF8.GetBytes(comment);
            parts.Add(Le32(bytes.Length));
            parts.Add(bytes);
        }

        return Concat([.. parts]);
    }

    private static byte[] FlacPicture(int pictureType, string mimeType, byte[] image)
    {
        var description = Encoding.UTF8.GetBytes("cover");
        return Concat(
            Be32((uint)pictureType), Be32((uint)mimeType.Length), Ascii(mimeType),
            Be32((uint)description.Length), description, new byte[16], Be32((uint)image.Length), image);
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Be32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Le32(int value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(static part => part).ToArray();
}
