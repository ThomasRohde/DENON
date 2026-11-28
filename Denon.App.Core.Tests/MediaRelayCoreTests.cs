using System.Globalization;
using System.Text;
using System.Text.Json;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The local relay and Core services: the Jukebox waits for a track's real end (#37), the Phone Remote server refuses
/// cross-site POSTs and large bodies and sends security headers (#31, #64), one broken log line hides nothing
/// (#41), and a hand-edited settings file is filled in, not taken for a corrupt one (#42).
/// </summary>
public sealed class MediaRelayCoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-media-core-" + Guid.NewGuid().ToString("N"));

    public MediaRelayCoreTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ---- #37: the Jukebox drain ----

    [Theory]
    [InlineData(180, 60, 125)] // 2 minutes left, plus 5 s.
    [InlineData(180, 0, 185)]
    [InlineData(180, 179, 10)] // At least 10 s.
    [InlineData(3600, 0, 900)] // At most 15 minutes.
    public void The_jukebox_waits_for_what_is_left_of_the_track(int lengthSeconds, double played, double expected) =>
        Assert.Equal(expected, LocalMediaJukeboxService.DrainLimitSeconds(TimeSpan.FromSeconds(lengthSeconds), played));

    [Fact]
    public void A_track_without_a_known_length_waits_for_the_stop_up_to_15_minutes()
    {
        // Before, 45 s for every track: HEOS buffers far ahead, so songs were cut.
        Assert.Equal(LocalMediaJukeboxService.UnknownLengthDrainLimitSeconds, LocalMediaJukeboxService.DrainLimitSeconds(null, 30));
        Assert.Equal(900, LocalMediaJukeboxService.UnknownLengthDrainLimitSeconds);
    }

    // ---- #31 and #64: the Phone Remote server ----

    [Theory]
    [InlineData(null, "192.168.1.31:18925", true)] // An app, no Origin.
    [InlineData("http://192.168.1.31:18925", "192.168.1.31:18925", true)] // The phone page itself.
    [InlineData("http://localhost:18925", "localhost:18925", true)]
    [InlineData("https://evil.example", "192.168.1.31:18925", false)] // A web page on another site (CSRF).
    [InlineData("http://192.168.1.31:8080", "192.168.1.31:18925", false)]
    [InlineData("null", "192.168.1.31:18925", false)]
    [InlineData("http://192.168.1.31:18925", null, false)]
    public void A_post_must_come_from_the_page_it_was_sent_to(string? origin, string? host, bool allowed) =>
        Assert.Equal(allowed, LocalAudioRelayServer.IsSameOriginOrNone(origin, host));

    [Fact]
    public async Task The_parser_reads_origin_and_host_and_flags_a_large_body()
    {
        var request = Encoding.ASCII.GetBytes(
            "POST /api/volume HTTP/1.1\r\nHost: 192.168.1.31:18925\r\nOrigin: https://evil.example\r\nContent-Length: 70000\r\n\r\n{}");
        await using var stream = new MemoryStream(request);

        var parsed = await LocalAudioRelayServer.ReadHttpRequestAsync(stream, CancellationToken.None);

        Assert.NotNull(parsed);
        Assert.Equal("https://evil.example", parsed.Origin);
        Assert.Equal("192.168.1.31:18925", parsed.Host);
        Assert.True(parsed.BodyTooLarge);
    }

    [Theory]
    [InlineData(405, "405 Method Not Allowed")]
    [InlineData(409, "409 Conflict")]
    [InlineData(413, "413 Payload Too Large")]
    [InlineData(503, "503 Service Unavailable")]
    [InlineData(418, "500 Internal Server Error")]
    public void Api_status_codes_keep_their_meaning(int code, string line) =>
        Assert.Equal(line, LocalAudioRelayServer.StatusLine(code));

    // ---- #41: one broken log line ----

    [Fact]
    public async Task One_broken_line_does_not_hide_the_older_events_in_the_file()
    {
        var log = new AppLogStore(Path.Combine(_tempDir, "app.log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, "settings.json"));
        await store.InitializeAsync();
        var eventsDir = Path.Combine(_tempDir, "events");
        Directory.CreateDirectory(eventsDir);
        var now = DateTimeOffset.Now;
        string Line(string type, DateTimeOffset at) =>
            JsonSerializer.Serialize(new { ts = at.ToString("O", CultureInfo.InvariantCulture), stream = "avr", type });
        File.WriteAllLines(
            Path.Combine(eventsDir, $"avr-{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.jsonl"),
            [
                Line("older", now.AddMinutes(-10)),
                "{\"ts\":\"2026-11-28T10:00", // The tail of a crash mid-write, with the next start's lines after it.
                Line("newer", now.AddMinutes(-1)),
            ]);
        var telemetry = new EventTelemetryLog(store, log, new RoomEventBus(log), eventsDir);

        var events = telemetry.QueryRecentEvents("avr", hours: 1, limit: 10);

        Assert.Equal(["newer", "older"], events.Select(e => e.GetProperty("type").GetString()!).ToArray());
        await telemetry.DisposeAsync();
    }

    // ---- #42: hand-edited settings ----

    [Fact]
    public async Task A_settings_file_with_null_sections_loads_and_keeps_its_values()
    {
        var path = Path.Combine(_tempDir, "null-sections.json");
        File.WriteAllText(path, """
            {
              "receiver": { "host": "192.168.1.3", "stations": null, "scenes": null, "volumePresets": null },
              "ui": null,
              "media": null,
              "shortcuts": { "bindings": null },
              "plugins": null,
              "telemetry": null,
              "volumeMemory": { "minDifference": 500 },
              "automationGraphs": null,
              "mcp": { "bearerToken": "ddn_keep_this" },
              "remote": { "ownerToken": "own_keep", "guestToken": "gst_keep" }
            }
            """);
        var store = new AppSettingsStore(new AppLogStore(Path.Combine(_tempDir, "null.log")), path);

        var settings = await store.InitializeAsync();

        Assert.Null(store.LastLoadWarning); // Not taken for a corrupt file.
        Assert.Equal("192.168.1.3", settings.Receiver.Host);
        Assert.Equal("ddn_keep_this", settings.Mcp.BearerToken);
        Assert.Equal(20, settings.VolumeMemory.MinDifference);
        Assert.NotNull(settings.Ui);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("99999999999")]
    public async Task An_unreadable_content_length_counts_as_too_large(string length)
    {
        var request = Encoding.ASCII.GetBytes($"POST /api/volume HTTP/1.1\r\nHost: 192.168.1.31:18925\r\nContent-Length: {length}\r\n\r\n{{}}");
        await using var stream = new MemoryStream(request);

        var parsed = await LocalAudioRelayServer.ReadHttpRequestAsync(stream, CancellationToken.None);

        Assert.NotNull(parsed);
        Assert.True(parsed.BodyTooLarge);
    }

    [Fact]
    public async Task A_file_without_an_mcp_section_saves_the_new_token_once()
    {
        var path = Path.Combine(_tempDir, "no-mcp.json");
        File.WriteAllText(path, """{ "receiver": { "host": "192.168.1.3" }, "remote": { "ownerToken": "own_a", "guestToken": "gst_b" } }""");
        var first = await new AppSettingsStore(new AppLogStore(Path.Combine(_tempDir, "m1.log")), path).InitializeAsync();
        var second = await new AppSettingsStore(new AppLogStore(Path.Combine(_tempDir, "m2.log")), path).InitializeAsync();

        Assert.Equal(first.Mcp.BearerToken, second.Mcp.BearerToken);
    }

    [Fact]
    public async Task An_empty_token_gets_a_new_one_that_is_saved_once()
    {
        var path = Path.Combine(_tempDir, "empty-token.json");
        File.WriteAllText(path, """{ "receiver": { "host": "192.168.1.3" }, "mcp": { "bearerToken": "" }, "remote": { "ownerToken": "own_a", "guestToken": "gst_b" } }""");
        var first = await new AppSettingsStore(new AppLogStore(Path.Combine(_tempDir, "t1.log")), path).InitializeAsync();
        var second = await new AppSettingsStore(new AppLogStore(Path.Combine(_tempDir, "t2.log")), path).InitializeAsync();

        Assert.False(string.IsNullOrWhiteSpace(first.Mcp.BearerToken));
        Assert.Equal(first.Mcp.BearerToken, second.Mcp.BearerToken); // Before, a new token on every start.
    }
}
