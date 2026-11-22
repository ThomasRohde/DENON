using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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

    private AppLogStore CreateIsolatedLog(string name = "app.log") =>
        new(Path.Combine(_tempDir, name));

    private EventTelemetryLog CreateIsolatedTelemetry(AppSettingsStore store, AppLogStore log, string folder = "events") =>
        new(store, log, Path.Combine(_tempDir, folder));

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task SettingsStore_PreservesCustomShortcuts_AndIntentionallyEmptyLibraries()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var log = CreateIsolatedLog();
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
    public async Task SettingsStore_DoesNotOverwriteCorruptFile_RecoversBackup_AndKeepsBackupValidOnSubsequentSave()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var log = CreateIsolatedLog();
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
        // Corrupt file must be preserved untouched on disk until the user explicitly saves!
        Assert.Contains("invalid json", await File.ReadAllTextAsync(path));
        Assert.True(Directory.GetFiles(_tempDir, "settings.json.corrupt-*").Length >= 1);

        // Saving after recovery must NOT overwrite the valid .bak with the corrupt primary file!
        await recoveryStore.UpdateAsync(s => s.Receiver.PlayerName = "Recovered Living Room");

        var bakPath = path + ".bak";
        Assert.True(File.Exists(bakPath));
        var primaryJson = await File.ReadAllTextAsync(path);
        var backupJson = await File.ReadAllTextAsync(bakPath);
        using var primaryDoc = JsonDocument.Parse(primaryJson);
        using var backupDoc = JsonDocument.Parse(backupJson);
        Assert.Contains("Recovered Living Room", primaryJson);
        Assert.Contains("192.168.1.99", backupJson);
    }

    [Fact]
    public async Task SettingsStore_AtomicUpdate_MergesIndependentFieldEditsWithoutClobbering()
    {
        var path = Path.Combine(_tempDir, "atomic-settings.json");
        var log = CreateIsolatedLog();
        var store = new AppSettingsStore(log, path);
        await store.InitializeAsync();

        var t1 = store.UpdateAsync(s => s.Receiver.PlayerName = "Cinema AVR");
        var t2 = store.UpdateAsync(s => s.Media.RelayPort = 18999);
        var t3 = store.UpdateAsync(s => s.Ui.DisplayVolumeInDb = true);
        await Task.WhenAll(t1, t2, t3);

        Assert.Equal("Cinema AVR", store.Current.Receiver.PlayerName);
        Assert.Equal(18999, store.Current.Media.RelayPort);
        Assert.True(store.Current.Ui.DisplayVolumeInDb);

        var reloaded = await new AppSettingsStore(log, path).InitializeAsync();
        Assert.Equal("Cinema AVR", reloaded.Receiver.PlayerName);
        Assert.Equal(18999, reloaded.Media.RelayPort);
        Assert.True(reloaded.Ui.DisplayVolumeInDb);
    }

    [Fact]
    public async Task SettingsStore_ExportRedactsSecrets_AndImportPreservesLocalSecrets()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var log = CreateIsolatedLog();
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
    public async Task LocalRelay_SuffixRangeProbe_DoesNotInvalidateUrlOrFireCompletion_AndFullReadFiresCompletion()
    {
        var path = Path.Combine(_tempDir, "relay-settings.json");
        var log = CreateIsolatedLog("relay.log");
        var store = new AppSettingsStore(log, path);
        await store.InitializeAsync();
        await store.UpdateAsync(s => s.Media.RelayPort = GetFreeTcpPort());

        var sampleFile = Path.Combine(_tempDir, "sample.flac");
        var payload = new byte[1024];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        await File.WriteAllBytesAsync(sampleFile, payload);

        var track = new LocalAudioTrackItem("t1", sampleFile, "sample.flac", "Sample", "Artist", ".flac", payload.Length);
        var ytMedia = new YouTubeMediaService(store, log);
        using var relay = new LocalAudioRelayServer(ytMedia, store, log);

        var completionCount = 0;
        var completedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.LocalTrackStreamCompleted += (_, _) =>
        {
            Interlocked.Increment(ref completionCount);
            completedTcs.TrySetResult(true);
        };

        var (_, token) = await relay.RegisterLocalTrackAsync(track);
        var url = $"http://127.0.0.1:{relay.BoundPort}/local/{token}.flac";

        using var http = new HttpClient();

        // 1. Metadata suffix probe (bytes=-1) must succeed (206) without invalidating URL or firing completion
        using (var req1 = new HttpRequestMessage(HttpMethod.Get, url))
        {
            req1.Headers.Range = new RangeHeaderValue(null, 1);
            using var resp1 = await http.SendAsync(req1);
            Assert.Equal(HttpStatusCode.PartialContent, resp1.StatusCode);
            var body1 = await resp1.Content.ReadAsByteArrayAsync();
            Assert.Single(body1);
            Assert.Equal(payload[^1], body1[0]);
        }

        Assert.Equal(0, Volatile.Read(ref completionCount));

        // 2. Subsequent partial read (bytes=0-9) must STILL succeed (token remains registered!)
        using (var req2 = new HttpRequestMessage(HttpMethod.Get, url))
        {
            req2.Headers.Range = new RangeHeaderValue(0, 9);
            using var resp2 = await http.SendAsync(req2);
            Assert.Equal(HttpStatusCode.PartialContent, resp2.StatusCode);
            var body2 = await resp2.Content.ReadAsByteArrayAsync();
            Assert.Equal(10, body2.Length);
            Assert.Equal(payload[..10], body2);
        }

        Assert.Equal(0, Volatile.Read(ref completionCount));

        // 3. Full read from start to EOF must return 200 and fire LocalTrackStreamCompleted once
        using (var resp3 = await http.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);
            var body3 = await resp3.Content.ReadAsByteArrayAsync();
            Assert.Equal(payload, body3);
        }

        var completed = await Task.WhenAny(completedTcs.Task, Task.Delay(2000)) == completedTcs.Task;
        Assert.True(completed);
        Assert.Equal(1, Volatile.Read(ref completionCount));
    }

    [Fact]
    public async Task YouTubeQueue_ClearWhenInactive_DoesNotInterruptUnrelatedRelayStream()
    {
        var path = Path.Combine(_tempDir, "yt-clear-settings.json");
        var log = CreateIsolatedLog("yt-clear.log");
        var store = new AppSettingsStore(log, path);
        await store.InitializeAsync();
        await store.UpdateAsync(s => s.Media.RelayPort = GetFreeTcpPort());

        var telemetry = CreateIsolatedTelemetry(store, log, "yt-clear-events");
        var stateStore = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(), store, stateStore, telemetry, log);
        var ytMedia = new YouTubeMediaService(store, log);
        using var relay = new LocalAudioRelayServer(ytMedia, store, log);
        using var ytQueue = new YouTubeQueueService(relay, controller, stateStore, log);

        // Register an unrelated live PC stream on the relay
        var streamEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.AttachLivePcAudioHandler(async (_, _, ct) =>
        {
            streamEntered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                streamCanceled.TrySetResult(true);
                throw;
            }
        });

        var (_, pcToken) = await relay.RegisterLivePcCastAsync(PcCastFormat.Wav_Lossless);
        var liveUrl = $"http://127.0.0.1:{relay.BoundPort}/live/pc/{pcToken}.wav";

        using var http = new HttpClient();
        using var reqCts = new CancellationTokenSource();
        var streamTask = http.GetAsync(liveUrl, HttpCompletionOption.ResponseHeadersRead, reqCts.Token);

        Assert.True(await Task.WhenAny(streamEntered.Task, Task.Delay(2000)) == streamEntered.Task);

        // Add & clear YouTube queue while YouTube is NOT the active relay source
        ytQueue.Enqueue(new YouTubeTrackItem("vid12345678", "Test Track", "Artist", TimeSpan.FromMinutes(3), "", "https://youtu.be/vid12345678"));
        ytQueue.ClearQueue();

        // Give any accidental cancellation time to propagate — PC Cast stream MUST still be alive!
        await Task.Delay(150);
        Assert.False(streamCanceled.Task.IsCompleted);

        // Cleanly cancel the PC Cast stream by its own token
        relay.CancelStream(pcToken);
        Assert.True(await Task.WhenAny(streamCanceled.Task, Task.Delay(2000)) == streamCanceled.Task);
        reqCts.Cancel();
        try { await streamTask; } catch { }
        await telemetry.DisposeAsync();
    }

    [Fact]
    public void PcAudioCast_Converts16Bit24BitFloat64BitFloatAndMultichannelToExactStereo16FrameCount()
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

        // 64-bit IEEE float stereo input (e.g., high-precision studio loopback)
        var fmtFloat64 = WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, 48000, 2, 48000 * 16, 16, 64);
        var float64Input = new byte[frames * fmtFloat64.BlockAlign];
        BitConverter.GetBytes(0.5d).CopyTo(float64Input, 0);
        BitConverter.GetBytes(-0.5d).CopyTo(float64Input, 8);
        var outFloat64 = PcAudioCastService.ConvertToStereo16Pcm(float64Input, fmtFloat64);
        Assert.Equal(expectedStereo16Bytes, outFloat64.Length);
        Assert.InRange(BitConverter.ToInt16(outFloat64, 0), 16000, 16500);
        Assert.InRange(BitConverter.ToInt16(outFloat64, 2), -16500, -16000);
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
        var log = CreateIsolatedLog("remote.log");
        var store = new AppSettingsStore(log, path);
        var cfg = await store.InitializeAsync();

        var telemetry = CreateIsolatedTelemetry(store, log, "remote-events");
        var stateStore = new ReceiverStateStore();
        var heos = new HeosClient();
        var avr = new DenonAvrClient();
        var controller = new ReceiverController(heos, avr, store, stateStore, telemetry, log);
        var ytMedia = new YouTubeMediaService(store, log);
        using var relay = new LocalAudioRelayServer(ytMedia, store, log);
        using var ytQueue = new YouTubeQueueService(relay, controller, stateStore, log);
        using var localJukebox = new LocalMediaJukeboxService(relay, controller, stateStore, log);
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
        var newOwner = RemoteAccessSettings.GeneratePairingToken("own");
        await store.UpdateAsync(s => s.Remote.OwnerToken = newOwner);

        var revoked = await handler.HandleAsync("GET", "/api/status", null, "", oldOwner, CancellationToken.None);
        Assert.Equal(401, revoked.StatusCode);

        // 5. Explicit switch off -> 403 even with valid new token
        await store.UpdateAsync(s => s.Remote.Enabled = false);

        var offResp = await handler.HandleAsync("GET", "/api/status", null, "", newOwner, CancellationToken.None);
        Assert.Equal(403, offResp.StatusCode);

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task RoomUndo_ScopesSnapshotsPerReceiver_RejectsHeosOnlySyntheticState_AndPreservesOriginSpecificity()
    {
        var path = Path.Combine(_tempDir, "undo-settings.json");
        var log = CreateIsolatedLog("undo.log");
        var store = new AppSettingsStore(log, path);
        await store.InitializeAsync();

        var telemetry = CreateIsolatedTelemetry(store, log, "undo-events");
        var stateStore = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(), store, stateStore, telemetry, log);
        var ytMedia = new YouTubeMediaService(store, log);
        using var relay = new LocalAudioRelayServer(ytMedia, store, log);
        using var ytQueue = new YouTubeQueueService(relay, controller, stateStore, log);
        var plugins = new PluginHostService([], store, stateStore, controller, telemetry, log);
        var heosEvents = new HeosEventListenerService(store, stateStore, controller, log);
        var room = new RoomIntelligenceService(
            stateStore,
            controller,
            store,
            plugins,
            ytQueue,
            heosEvents,
            log,
            Path.Combine(_tempDir, "room-habits.json"));

        // 1. Disconnected receiver must not synthesize an assumed standby/vol 25 snapshot
        Assert.Null(room.CaptureSnapshot("test-disconnected"));

        // 2. HEOS-only synthetic status (IsAvrConfirmed == false) must NOT be captured for Undo
        var media = new NowPlayingMedia("song", "Song", "Album", "Artist", "", "", "", null, null);
        var roomAPlayer = new ReceiverPlayerInfo("Living Room AVR", 101, "Denon AVR-X3800H", "1.0", "192.168.1.50", "wired", 1, "SER-A");
        stateStore.UpdateStatus(new ReceiverStatus(
            roomAPlayer, "play", 42, false, media, DateTimeOffset.Now,
            IsPoweredOn: true, IsMainZoneOn: true, InputSource: "NET", SoundMode: "HEOS", IsAvrConfirmed: false));
        Assert.Null(room.CaptureSnapshot("heos-only-unconfirmed"));

        // 3. Confirmed Room A status captures a room-scoped snapshot
        stateStore.UpdateStatus(new ReceiverStatus(
            roomAPlayer, "play", 42, false, media, DateTimeOffset.Now,
            IsPoweredOn: true, IsMainZoneOn: true, InputSource: "TV", SoundMode: "MOVIE", IsAvrConfirmed: true));
        var snapA = room.CaptureSnapshot("before-movie-scene");
        Assert.NotNull(snapA);
        Assert.True(room.CanUndo);
        Assert.Equal(snapA, room.LastSnapshot);

        // 4. Switching active receiver to Room B must NOT expose or apply Room A's snapshot!
        var roomBPlayer = new ReceiverPlayerInfo("Kitchen HEOS", 202, "Denon Home 250", "1.0", "192.168.1.51", "wifi", 1, "SER-B");
        stateStore.UpdateStatus(new ReceiverStatus(
            roomBPlayer, "stop", 20, false, media, DateTimeOffset.Now,
            IsPoweredOn: true, IsMainZoneOn: true, InputSource: "NET", SoundMode: "STEREO", IsAvrConfirmed: true));

        Assert.False(room.CanUndo);
        Assert.Null(room.LastSnapshot);
        var (restored, message) = await room.RestoreLastSnapshotAsync();
        Assert.False(restored);
        Assert.Contains("Kitchen HEOS", message);

        // 5. High-specificity MCP origin is preserved over subsequent desktop_ui fallback watermark
        telemetry.MarkOrigin("avr", "mcp_agent");
        telemetry.MarkOrigin("avr", "desktop_ui", overwriteRecent: false);
        Assert.Equal("mcp_agent", telemetry.ResolveOrigin("avr"));
        Assert.Equal("external", telemetry.ResolveOrigin("tv"));

        await telemetry.DisposeAsync();
    }

    [Fact]
    public void NowPlayingResolver_ReflectsExternalRemotesSmartTvAndPhoneAppsAccurately()
    {
        var player = new ReceiverPlayerInfo("Denon AVR-X2400H", 101, "Denon AVR-X2400H", "3.1", "192.168.1.3", "wired", 1, "SER");
        var staleUrlMedia = new NowPlayingMedia("song", "Url Stream", "Url Stream", "Url Stream", "", "", "", null, 1029);

        var smartTvPlugin = new Denon.App.Core.Plugins.PluginRecordSnapshot
        {
            Id = "smart-tv",
            Name = "Smart TV Control (LG webOS)",
            Version = "2.2.0",
            Author = "Denon Desktop",
            Description = "",
            Category = "TV",
            AccentHex = "#38BDF8",
            IconGlyph = "TV",
            Capabilities = Denon.App.PluginSdk.PluginCapabilities.Actions,
            Origin = Denon.App.Core.Plugins.PluginOrigin.BuiltIn,
            AssemblyPath = null,
            IsEnabled = true,
            IsStarted = true,
            HasDashboardView = true,
            Health = Denon.App.PluginSdk.PluginHealthState.Connected,
            StatusText = "LIVE • 55UR73006LA • DVD Player (HDMI 1)",
            LastError = null,
            Actions = [],
        };

        // 1. AVR on TV Audio with stale HEOS "Url Stream" + live LG TV -> shows real TV source & "Live TV" (never "Stop" or "Url Stream")
        var tvStatus = new ReceiverStatus(
            player, "stop", 73, false, staleUrlMedia, DateTimeOffset.Now,
            IsPoweredOn: true, IsMainZoneOn: true, InputSource: "TV", SoundMode: "DOLBY SURROUND");
        var tvResolved = NowPlayingResolver.Resolve(tvStatus, activeStationName: null, plugins: [smartTvPlugin]);
        Assert.Equal("DVD Player (HDMI 1)", tvResolved.Title);
        Assert.Contains("55UR73006LA", tvResolved.Detail);
        Assert.Contains("DOLBY SURROUND", tvResolved.Detail);
        Assert.Equal("Live TV", tvResolved.PlayStateLabel);

        // 2. Phone starts Spotify Connect (sid=4) while stale YouTube overlay exists -> shows real Spotify track!
        var spotifyMedia = new NowPlayingMedia("song", "Midnight City", "Hurry Up, We're Dreaming", "M83", "http://img/m83.jpg", "", "", null, 4);
        var spotifyStatus = new ReceiverStatus(
            player, "play", 45, false, spotifyMedia, DateTimeOffset.Now,
            IsPoweredOn: true, IsMainZoneOn: true, InputSource: "NET", SoundMode: "STEREO");
        var staleYt = new YouTubeTrackItem("yt123456789", "Old YouTube Song", "Old Channel", TimeSpan.FromMinutes(3), "", "https://youtu.be/yt123456789");
        var spotifyResolved = NowPlayingResolver.Resolve(spotifyStatus, activeStationName: null, plugins: [], activeYouTubeTrack: staleYt);
        Assert.Equal("Midnight City", spotifyResolved.Title);
        Assert.Contains("M83", spotifyResolved.Detail);
        Assert.Contains("Spotify", spotifyResolved.Detail);
        Assert.Equal("http://img/m83.jpg", spotifyResolved.ImageUrl);

        // 3. Phone plays TuneIn radio station (empty song, populated station field, sid=3)
        var tuneInMedia = new NowPlayingMedia("station", "", "", "", "http://img/p8.png", "", "", null, 3, Station: "DR P8 Jazz");
        var tuneInStatus = new ReceiverStatus(
            player, "play", 38, false, tuneInMedia, DateTimeOffset.Now,
            IsPoweredOn: true, IsMainZoneOn: true, InputSource: "NET", SoundMode: "STEREO");
        var tuneInResolved = NowPlayingResolver.Resolve(tuneInStatus, activeStationName: null, plugins: []);
        Assert.Equal("DR P8 Jazz", tuneInResolved.Title);
        Assert.Contains("TuneIn", tuneInResolved.Detail);

        // 4. Phone streams over Bluetooth with live AVRCP metadata
        var btMedia = new NowPlayingMedia("song", "Take Five", "Time Out", "Dave Brubeck", "", "", "", null, null);
        var btStatus = new ReceiverStatus(
            player, "play", 40, false, btMedia, DateTimeOffset.Now,
            IsPoweredOn: true, IsMainZoneOn: true, InputSource: "BT", SoundMode: "STEREO");
        var btResolved = NowPlayingResolver.Resolve(btStatus, activeStationName: null, plugins: []);
        Assert.Equal("Take Five", btResolved.Title);
        Assert.Contains("Dave Brubeck", btResolved.Detail);
        Assert.Contains("Bluetooth", btResolved.Detail);

        // 5. External HDMI (Blu-ray) with HEOS stopped -> shows "Active", not misleading "Stop"
        var bdStatus = new ReceiverStatus(
            player, "stop", 50, false, staleUrlMedia, DateTimeOffset.Now,
            IsPoweredOn: true, IsMainZoneOn: true, InputSource: "BD", SoundMode: "MOVIE");
        var bdResolved = NowPlayingResolver.Resolve(bdStatus, activeStationName: null, plugins: []);
        Assert.Equal("Blu-ray", bdResolved.Title);
        Assert.Equal("Active", bdResolved.PlayStateLabel);
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
