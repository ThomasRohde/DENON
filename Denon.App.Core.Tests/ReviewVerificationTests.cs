using System.Text;
using System.Text.Json;
using Denon.App.Core.Heos;
using Denon.App.Core.Mcp;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.Desktop.Services;
using NAudio.Wave;
using Xunit;

namespace Denon.App.Core.Tests;

public sealed class ReviewVerificationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public ReviewVerificationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SettingsStore_PreservesCustomShortcuts_AndIntentionallyEmptyLibraries()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var log = new AppLogStore();
        var store = new AppSettingsStore(log, path);
        await store.InitializeAsync();

        var edited = store.Current.Clone();
        edited.Receiver.Stations.Clear();
        edited.Receiver.Scenes.Clear();
        edited.Shortcuts.Bindings.Add(new ShortcutBinding("scene:movie-night", "Scene: Movie Night", true, "Ctrl+Alt+1"));
        edited.Shortcuts.Bindings.Add(new ShortcutBinding("plugin:philips-hue:hue.movie_dim", "Hue: Cinema Dim", true, "Ctrl+Alt+2"));
        await store.SaveAsync(edited);

        var reloadedStore = new AppSettingsStore(log, path);
        var reloaded = await reloadedStore.InitializeAsync();

        Assert.Empty(reloaded.Receiver.Stations);
        Assert.Empty(reloaded.Receiver.Scenes);
        Assert.Contains(reloaded.Shortcuts.Bindings, b => b.ActionId == "scene:movie-night" && b.Gesture == "Ctrl+Alt+1");
        Assert.Contains(reloaded.Shortcuts.Bindings, b => b.ActionId == "plugin:philips-hue:hue.movie_dim" && b.Gesture == "Ctrl+Alt+2");
    }

    [Fact]
    public async Task SettingsStore_DoesNotOverwriteCorruptFile_AndRecoversBackup()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var log = new AppLogStore();
        var store = new AppSettingsStore(log, path);
        await store.InitializeAsync();

        var custom = store.Current.Clone();
        custom.Receiver.Host = "192.168.1.99";
        await store.SaveAsync(custom);

        // Save once more so .bak holds the 192.168.1.99 good copy
        custom.Receiver.PlayerName = "Living Room AVR";
        await store.SaveAsync(custom);

        // Corrupt primary settings.json
        await File.WriteAllTextAsync(path, "{ invalid json !!!");

        var recoveryStore = new AppSettingsStore(log, path);
        var loaded = await recoveryStore.InitializeAsync();

        Assert.Equal("192.168.1.99", loaded.Receiver.Host);
        Assert.NotNull(recoveryStore.LastLoadWarning);
        // Corrupt file must be preserved untouched on disk, not overwritten with defaults!
        Assert.Contains("invalid json", await File.ReadAllTextAsync(path));
        Assert.True(Directory.GetFiles(_tempDir, "settings.json.corrupt-*").Length >= 1);
    }

    [Fact]
    public async Task SettingsStore_ExportRedactsSecrets_AndImportPreservesLocalSecrets()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var log = new AppLogStore();
        var store = new AppSettingsStore(log, path);
        var init = await store.InitializeAsync();

        var origMcp = init.Mcp.BearerToken;
        var origOwner = init.Remote.OwnerToken;
        var origGuest = init.Remote.GuestToken;

        var exported = store.ExportJson();
        Assert.DoesNotContain(origMcp, exported);
        Assert.DoesNotContain(origOwner, exported);
        Assert.DoesNotContain(origGuest, exported);
        Assert.Contains(AppSettingsStore.RedactedSecretPlaceholder, exported);

        var imported = await store.ImportJsonAsync(exported);
        Assert.Equal(origMcp, imported.Mcp.BearerToken);
        Assert.Equal(origOwner, imported.Remote.OwnerToken);
        Assert.Equal(origGuest, imported.Remote.GuestToken);
    }

    [Theory]
    [InlineData("bytes=20-39", 100, true, 20, 39)]
    [InlineData("bytes=-20", 100, true, 80, 99)]
    [InlineData("bytes=-250", 100, true, 0, 99)]
    [InlineData("bytes=50-", 100, true, 50, 99)]
    [InlineData("bytes=100-120", 100, false, 0, 99)]
    [InlineData("bytes=60-20", 100, false, 0, 99)]
    [InlineData("bytes=-0", 100, false, 0, 99)]
    public void RelayRangeParser_HandlesStandardSuffixAndUnsatisfiableRanges(
        string header, long totalSize, bool expectedValid, long expectedStart, long expectedEnd)
    {
        var ok = LocalAudioRelayServer.TryParseRangeHeader(header, totalSize, out var start, out var end);
        Assert.Equal(expectedValid, ok);
        if (expectedValid)
        {
            Assert.Equal(expectedStart, start);
            Assert.Equal(expectedEnd, end);
        }
    }

    [Fact]
    public async Task RelayHttpReader_PreservesUtf8BodyAcrossSplitReads_AndExtractsToken()
    {
        var jsonBody = "{\"title\":\"Björk — Don't Stop 🎵\"}";
        var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
        var headerBytes = Encoding.ASCII.GetBytes(
            $"POST /api/youtube/queue HTTP/1.1\r\nHost: localhost\r\nX-Denon-Token: own_abc123\r\nContent-Length: {bodyBytes.Length}\r\n\r\n");

        var full = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, full, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, full, headerBytes.Length, bodyBytes.Length);

        // Split right in the middle of the 2-byte UTF-8 'ö' sequence!
        await using var chunked = new SplitReadStream(full, maxChunkBytes: 7);
        var req = await LocalAudioRelayServer.ReadHttpRequestAsync(chunked, CancellationToken.None);

        Assert.NotNull(req);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/api/youtube/queue", req.Path);
        Assert.Equal("own_abc123", req.AuthToken);
        Assert.Equal(jsonBody, req.Body);
    }

    [Fact]
    public void PcAudioCast_Converts16Bit24BitFloatAndMultichannelToExactStereo16FrameCount()
    {
        const int frames = 480;
        const int expectedStereo16Bytes = frames * 2 * 2; // 480 frames * 2 ch * 2 bytes = 1920 bytes

        var fmt16 = new WaveFormat(48000, 16, 2);
        var out16 = PcAudioCastService.ConvertToStereo16Pcm(new byte[frames * fmt16.BlockAlign], fmt16);
        Assert.Equal(expectedStereo16Bytes, out16.Length);

        var fmt24 = new WaveFormat(48000, 24, 2);
        var out24 = PcAudioCastService.ConvertToStereo16Pcm(new byte[frames * fmt24.BlockAlign], fmt24);
        Assert.Equal(expectedStereo16Bytes, out24.Length);
        Assert.Equal("s24le", PcAudioCastService.ResolveFfmpegInputFormat(fmt24));

        var fmt6Ch = new WaveFormat(48000, 16, 6);
        var out6Ch = PcAudioCastService.ConvertToStereo16Pcm(new byte[frames * fmt6Ch.BlockAlign], fmt6Ch);
        Assert.Equal(expectedStereo16Bytes, out6Ch.Length);

        var fmtFloat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var floatInput = new byte[frames * fmtFloat.BlockAlign];
        BitConverter.GetBytes(0.5f).CopyTo(floatInput, 0);
        BitConverter.GetBytes(-0.5f).CopyTo(floatInput, 4);
        var outFloat = PcAudioCastService.ConvertToStereo16Pcm(floatInput, fmtFloat);
        Assert.Equal(expectedStereo16Bytes, outFloat.Length);
        Assert.Equal("f32le", PcAudioCastService.ResolveFfmpegInputFormat(fmtFloat));
        Assert.InRange(BitConverter.ToInt16(outFloat, 0), 16000, 16500);
        Assert.InRange(BitConverter.ToInt16(outFloat, 2), -16500, -16000);
    }

    [Fact]
    public void RadioBrowser_HiResFilter_HonorsFlacOr256kUnion()
    {
        var input = new[]
        {
            new RadioBrowserStation("1", "MP3 Low", "http://a", "", "DK", "jazz", "MP3", 128, 100),
            new RadioBrowserStation("2", "MP3 High", "http://b", "", "DK", "jazz", "MP3", 320, 50),
            new RadioBrowserStation("3", "FLAC Unknown Bitrate", "http://c", "", "US", "jazz", "FLAC", 0, 10),
        };

        var filtered = RadioBrowserService.FilterAndSortHiRes(input, limit: 10);

        Assert.Equal(2, filtered.Count);
        Assert.Equal("FLAC Unknown Bitrate", filtered[0].Name);
        Assert.Equal("MP3 High", filtered[1].Name);
    }

    [Fact]
    public async Task PhoneRemote_EnforcesPairingGuestQueueRoleAndInstantRevocation()
    {
        var path = Path.Combine(_tempDir, "remote-settings.json");
        var log = new AppLogStore();
        var store = new AppSettingsStore(log, path);
        var cfg = await store.InitializeAsync();

        var telemetry = new EventTelemetryLog(store, log);
        var stateStore = new ReceiverStateStore();
        var heos = new HeosClient();
        var avr = new DenonAvrClient();
        var controller = new ReceiverController(heos, avr, store, stateStore, telemetry, log);
        var ytMedia = new YouTubeMediaService(store, log);
        var relay = new LocalAudioRelayServer(ytMedia, store, log);
        var ytQueue = new YouTubeQueueService(relay, controller, stateStore, log);
        var localJukebox = new LocalMediaJukeboxService(relay, controller, stateStore, log);
        var plugins = new PluginHostService([], store, stateStore, controller, telemetry, log);
        var handler = new RemoteWebApiHandler(controller, stateStore, store, ytMedia, ytQueue, localJukebox, plugins, telemetry, log);

        // 1. Fresh phone without token -> 401 pairing required
        var unauth = await handler.HandleAsync("GET", "/api/status", null, "", null, CancellationToken.None);
        Assert.Equal(401, unauth.StatusCode);

        // 2. Guest token -> can read status (guest role) and queue YouTube, CANNOT adjust volume or toggle plugins
        var guestStatus = await handler.HandleAsync("GET", "/api/status", null, "", cfg.Remote.GuestToken, CancellationToken.None);
        Assert.Equal(200, guestStatus.StatusCode);
        Assert.Contains("\"role\":\"guest\"", guestStatus.Body);

        var trackJson = JsonSerializer.Serialize(new YouTubeTrackItem("abc12345678", "Don't Stop", "Artist", TimeSpan.FromMinutes(3), "", "https://youtu.be/abc12345678"));
        var guestQueue = await handler.HandleAsync("POST", "/api/youtube/queue", null, trackJson, cfg.Remote.GuestToken, CancellationToken.None);
        Assert.Equal(200, guestQueue.StatusCode);
        Assert.Single(ytQueue.Snapshot.Queue);

        var guestVol = await handler.HandleAsync("POST", "/api/volume", null, "{\"level\":40}", cfg.Remote.GuestToken, CancellationToken.None);
        Assert.Equal(403, guestVol.StatusCode);

        var guestPlugin = await handler.HandleAsync("POST", "/api/plugins/toggle", null, "{\"pluginId\":\"smart-tv\",\"enabled\":false}", cfg.Remote.GuestToken, CancellationToken.None);
        Assert.Equal(403, guestPlugin.StatusCode);

        // 3. Owner token -> gets owner status
        var ownerStatus = await handler.HandleAsync("GET", "/api/status", null, "", cfg.Remote.OwnerToken, CancellationToken.None);
        Assert.Equal(200, ownerStatus.StatusCode);
        Assert.Contains("\"role\":\"owner\"", ownerStatus.Body);

        // 4. Instant revocation via token rotation -> previous owner token immediately rejected (401)
        var oldOwner = cfg.Remote.OwnerToken;
        var rotated = store.Current.Clone();
        rotated.Remote.OwnerToken = RemoteAccessSettings.GeneratePairingToken("own");
        await store.SaveAsync(rotated);

        var revoked = await handler.HandleAsync("GET", "/api/status", null, "", oldOwner, CancellationToken.None);
        Assert.Equal(401, revoked.StatusCode);

        // 5. Explicit switch off -> 403 even with valid new token
        var disabled = store.Current.Clone();
        disabled.Remote.Enabled = false;
        await store.SaveAsync(disabled);

        var offResp = await handler.HandleAsync("GET", "/api/status", null, "", rotated.Remote.OwnerToken, CancellationToken.None);
        Assert.Equal(403, offResp.StatusCode);

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task RoomUndo_DoesNotCaptureUnconfirmedState_AndOriginWatermarksPreserveSpecificity()
    {
        var path = Path.Combine(_tempDir, "undo-settings.json");
        var log = new AppLogStore();
        var store = new AppSettingsStore(log, path);
        await store.InitializeAsync();

        var telemetry = new EventTelemetryLog(store, log);
        var stateStore = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(), store, stateStore, telemetry, log);
        var ytMedia = new YouTubeMediaService(store, log);
        var relay = new LocalAudioRelayServer(ytMedia, store, log);
        var ytQueue = new YouTubeQueueService(relay, controller, stateStore, log);
        var plugins = new PluginHostService([], store, stateStore, controller, telemetry, log);
        var heosEvents = new HeosEventListenerService(store, stateStore, controller, log);
        var room = new RoomIntelligenceService(stateStore, controller, store, plugins, ytQueue, heosEvents, log);

        // Disconnected receiver must not synthesize an assumed standby/vol 25 snapshot!
        Assert.Null(room.CaptureSnapshot("test-disconnected"));

        // High-specificity MCP origin is preserved over subsequent desktop_ui fallback watermark
        telemetry.MarkOrigin("avr", "mcp_agent");
        telemetry.MarkOrigin("avr", "desktop_ui", overwriteRecent: false);
        Assert.Equal("mcp_agent", telemetry.ResolveOrigin("avr"));
        Assert.Equal("external", telemetry.ResolveOrigin("tv"));

        await telemetry.DisposeAsync();
    }

    private sealed class SplitReadStream(byte[] data, int maxChunkBytes) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length) return 0;
            var take = Math.Min(Math.Min(count, maxChunkBytes), data.Length - _pos);
            Buffer.BlockCopy(data, _pos, buffer, offset, take);
            _pos += take;
            return take;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos >= data.Length) return ValueTask.FromResult(0);
            var take = Math.Min(Math.Min(buffer.Length, maxChunkBytes), data.Length - _pos);
            data.AsSpan(_pos, take).CopyTo(buffer.Span);
            _pos += take;
            return ValueTask.FromResult(take);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
