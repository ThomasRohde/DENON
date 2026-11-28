using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Denon.App.Core.Heos;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The receiver links (Telnet on port 23 and HEOS on port 1255), checked against small fake receivers on loopback:
/// Disconnect never blocks (#28), one command is one line (#30), Zone 2 and the grace window (#39), fast replies
/// and fades (#43), HEOS-only scenes (#66), and the push listener's heartbeat (#38).
/// </summary>
public sealed class ReceiverTransportTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-transport-" + Guid.NewGuid().ToString("N"));

    public ReceiverTransportTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static ReceiverSettings Loopback(int heosPort = 1255) => new() { Host = "127.0.0.1", Port = heosPort };

    // ---- #28: Disconnect never blocks -------------------------------------------------------------------------

    [Fact]
    public async Task Avr_disconnect_returns_at_once_while_a_command_waits_for_the_receiver()
    {
        using var avr = new FakeAvr { Silent = true };
        var client = new DenonAvrClient(avr.Port);

        var command = client.SendRawCommandAsync(Loopback(), "PW?", CancellationToken.None);
        await avr.WaitForCommandAsync("PW?");

        var watch = Stopwatch.StartNew();
        client.Disconnect();
        Assert.True(watch.ElapsedMilliseconds < 200, $"Disconnect took {watch.ElapsedMilliseconds} ms");

        await command;

        // The command that held the link finished; the next command opens a new connection.
        avr.Silent = false;
        await client.SendRawCommandAsync(Loopback(), "PW?", CancellationToken.None);
        Assert.Equal(2, avr.Connections);
    }

    [Fact]
    public void Avr_disconnect_on_a_ui_like_thread_does_not_deadlock_a_command_started_there()
    {
        using var avr = new FakeAvr { Silent = true };
        var client = new DenonAvrClient(avr.Port);
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            var context = new PumpContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                context.RunUntil(Scenario());
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            async Task Scenario()
            {
                // Like the volume slider: the command's continuations need this thread.
                var command = client.SendRawCommandAsync(Loopback(), "MV?", CancellationToken.None);
                await avr.WaitForCommandAsync("MV?");
                client.Disconnect(); // Like Reset or Save & Sync on the UI thread.
                await command;
            }
        });

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The UI-like thread deadlocked.");
        Assert.Null(failure);
    }

    [Fact]
    public async Task Heos_disconnect_returns_at_once_while_a_command_waits()
    {
        using var heos = new FakeHeos { Silent = true };
        var client = new HeosClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var command = client.SendRawCommandAsync(Loopback(heos.Port), "heos://player/get_players", cts.Token);
        await heos.WaitForCommandAsync("player/get_players");

        var watch = Stopwatch.StartNew();
        client.Disconnect();
        Assert.True(watch.ElapsedMilliseconds < 200, $"Disconnect took {watch.ElapsedMilliseconds} ms");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
    }

    // ---- #30: one command is one line --------------------------------------------------------------------------

    [Theory]
    [InlineData("TV\rMV98")]
    [InlineData("TV\nPWSTANDBY")]
    [InlineData("TV\u0000")]
    public async Task Avr_refuses_a_command_with_a_control_character(string code)
    {
        using var avr = new FakeAvr();
        var client = new DenonAvrClient(avr.Port);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetInputSourceAsync(Loopback(), code, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetSoundModeAsync(Loopback(), code, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendRawCommandAsync(Loopback(), "SI" + code, CancellationToken.None));

        Assert.Empty(avr.Commands);
    }

    [Fact]
    public async Task Heos_refuses_a_stream_url_or_raw_command_with_a_line_break()
    {
        using var heos = new FakeHeos();
        var client = new HeosClient();
        var settings = Loopback(heos.Port);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PlayUrlAsync(settings, "http://radio.example/a\r\nheos://player/set_volume?pid=1&level=98", volume: null, unmute: false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SendRawCommandAsync(settings, "heos://player/get_players\r\nheos://player/set_volume?pid=1&level=98", CancellationToken.None));

        Assert.DoesNotContain(heos.Commands, c => c.Contains("set_volume", StringComparison.Ordinal));
        Assert.DoesNotContain(heos.Commands, c => c.Contains("play_stream", StringComparison.Ordinal));
    }

    // ---- #39 and #43: Zone 2 reply and fast status -----------------------------------------------------------

    [Theory]
    [InlineData("Z2ON", "Z2TV", "Z240")]
    [InlineData("Z2TV", "Z2ON", "Z240")]
    [InlineData("Z240", "Z2TV", "Z2ON")]
    public void Zone2_is_on_when_any_reply_line_says_Z2ON(string a, string b, string c)
    {
        Assert.True(DenonAvrClient.IsZone2OnReply([a, b, c]));
        Assert.False(DenonAvrClient.IsZone2OnReply(["Z2OFF", "Z2TV", "Z240"]));
        Assert.False(DenonAvrClient.IsZone2OnReply([]));
    }

    [Fact]
    public async Task Status_reads_zone2_from_any_line_and_does_not_wait_after_complete_replies()
    {
        using var avr = new FakeAvr();
        avr.Replies["Z2?"] = "Z2TV\rZ2ON\rZ240\r";
        var client = new DenonAvrClient(avr.Port);

        await client.GetStatusAsync(Loopback(), CancellationToken.None); // Opens the link.
        var watch = Stopwatch.StartNew();
        var status = await client.GetStatusAsync(Loopback(), CancellationToken.None);
        watch.Stop();

        Assert.True(status.IsZone2On);
        Assert.True(status.IsPoweredOn);
        Assert.Equal(45, status.Volume);
        Assert.Equal(98, status.MaxVolume);
        Assert.Equal("TV", status.InputSource);
        Assert.Equal("STEREO", status.SoundMode);

        // Before, every one of the 9 queries waited 120 ms after its answer (over 1 s).
        Assert.True(watch.ElapsedMilliseconds < 900, $"Status took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Commands_keep_at_least_50_ms_apart()
    {
        using var avr = new FakeAvr();
        var client = new DenonAvrClient(avr.Port);

        await client.GetStatusAsync(Loopback(), CancellationToken.None);

        var times = avr.CommandTimes.ToArray();
        Assert.True(times.Length >= 9);
        for (var i = 1; i < times.Length; i++)
        {
            Assert.True(times[i] - times[i - 1] >= TimeSpan.FromMilliseconds(45), $"Gap {i}: {(times[i] - times[i - 1]).TotalMilliseconds} ms");
        }
    }

    // ---- ReceiverController with both fakes -------------------------------------------------------------------

    private async Task<(ReceiverController Controller, ReceiverStateStore State, EventTelemetryLog Telemetry)> CreateControllerAsync(
        FakeAvr avr,
        FakeHeos heos)
    {
        var log = new AppLogStore(Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".json"));
        await store.InitializeAsync();
        await store.UpdateAsync(s =>
        {
            s.Receiver.Host = "127.0.0.1";
            s.Receiver.Port = heos.Port;
        });
        var telemetry = new EventTelemetryLog(store, log, new RoomEventBus(log), Path.Combine(_tempDir, "events-" + Guid.NewGuid().ToString("N")));
        var state = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(avr.Port), store, state, telemetry, log);
        return (controller, state, telemetry);
    }

    [Fact]
    public async Task A_short_port_23_outage_keeps_the_real_input_for_two_polls()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);

        var confirmed = await controller.RefreshAsync();
        Assert.Equal("TV", confirmed.InputSource);
        Assert.True(confirmed.IsAvrConfirmed);

        avr.HangUp = true; // Another app holds port 23 for a moment.
        var first = await controller.RefreshAsync();
        var second = await controller.RefreshAsync();
        var third = await controller.RefreshAsync();

        Assert.Equal("TV", first.InputSource);
        Assert.False(first.IsAvrConfirmed);
        Assert.Equal("TV", second.InputSource); // Before, the second failed poll already showed NET / HEOS.
        Assert.Equal("NET", third.InputSource);

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_fade_step_sets_the_volume_without_a_full_status_read()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();
        avr.ClearCommands();

        var step = await controller.SetVolumeAsync(40, logSuccess: false, refreshStatus: false);

        Assert.Equal(["MV40"], avr.Commands.ToArray());
        Assert.Equal(40, step.Volume);
        Assert.Equal("TV", step.InputSource);

        avr.ClearCommands();
        await controller.SetVolumeAsync(42);
        Assert.Contains("PW?", avr.Commands);

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_scene_on_a_heos_only_speaker_sets_volume_then_unmutes()
    {
        using var avr = new FakeAvr { HangUp = true }; // No AVR control on port 23 (for example Denon Home).
        using var heos = new FakeHeos { Volume = 60 };
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();
        Assert.False(controller.IsAvrProtocolAvailable);
        heos.ClearCommands();

        var scene = new ScenePreset { Name = "Night", PowerOn = true, InputSource = "TV", SoundMode = "STEREO", TargetVolume = 20, Unmute = true };
        await controller.ApplySceneAsync(scene);

        var sent = heos.Commands.ToList();
        var volumeAt = sent.FindIndex(c => c.StartsWith("player/set_volume", StringComparison.Ordinal) && c.Contains("level=20", StringComparison.Ordinal));
        var unmuteAt = sent.FindIndex(c => c.StartsWith("player/set_mute", StringComparison.Ordinal) && c.Contains("state=off", StringComparison.Ordinal));
        Assert.True(volumeAt >= 0, string.Join(" | ", sent));
        Assert.True(unmuteAt > volumeAt, string.Join(" | ", sent));

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task Fix_silent_audio_on_a_heos_only_speaker_raises_the_volume_and_unmutes()
    {
        using var avr = new FakeAvr { HangUp = true };
        using var heos = new FakeHeos { Volume = 10 };
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();
        heos.ClearCommands();

        await controller.FixSilentAudioAsync(35);

        Assert.Contains(heos.Commands, c => c.StartsWith("player/set_volume", StringComparison.Ordinal) && c.Contains("level=35", StringComparison.Ordinal));
        Assert.Contains(heos.Commands, c => c.StartsWith("player/set_mute", StringComparison.Ordinal) && c.Contains("state=off", StringComparison.Ordinal));

        await telemetry.DisposeAsync();
    }

    // ---- #38: the push listener finds a silent link ------------------------------------------------------------

    [Fact]
    public async Task The_push_listener_sends_heartbeats_and_reconnects_when_the_link_goes_silent()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos { Silent = true }; // Accepts, then never answers: a half-open link.
        var (controller, state, telemetry) = await CreateControllerAsync(avr, heos);
        var log = new AppLogStore(Path.Combine(_tempDir, "listener.log"));
        var storeLog = new AppLogStore(Path.Combine(_tempDir, "listener-store.log"));
        var store = new AppSettingsStore(storeLog, Path.Combine(_tempDir, "listener.json"));
        await store.InitializeAsync();
        await store.UpdateAsync(s =>
        {
            s.Receiver.Host = "127.0.0.1";
            s.Receiver.Port = heos.Port;
        });

        using var listener = new HeosEventListenerService(store, state, controller, log)
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            SilenceLimit = TimeSpan.FromMilliseconds(400),
        };
        await listener.StartAsync(CancellationToken.None);
        try
        {
            await heos.WaitForCommandAsync("system/heart_beat");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (heos.Connections < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.True(heos.Connections >= 2, "The listener did not reconnect after the link went silent.");
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
            await telemetry.DisposeAsync();
        }
    }

    // ---- Fakes ---------------------------------------------------------------------------------------------------

    /// <summary>Runs posted continuations on one thread, like the WPF Dispatcher.</summary>
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                ThreadPool.QueueUserWorkItem(_ => d(state)); // The pump has finished.
            }
        }

        public void RunUntil(Task task)
        {
            task.ContinueWith(_ => _queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }

            task.GetAwaiter().GetResult();
        }
    }

    private abstract class FakeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _commands = new();
        private int _connections;

        protected FakeServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public int Connections => Volatile.Read(ref _connections);

        public IReadOnlyCollection<string> Commands => _commands;

        public ConcurrentQueue<DateTimeOffset> CommandTimes { get; } = new();

        /// <summary>Accept, read, and never answer.</summary>
        public volatile bool Silent;

        /// <summary>Close each connection at once (port busy or not there).</summary>
        public volatile bool HangUp;

        public void ClearCommands() => _commands.Clear();

        public async Task WaitForCommandAsync(string command)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!_commands.Any(c => c.StartsWith(command, StringComparison.Ordinal)))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"The fake never got '{command}'. Got: {string.Join(" | ", _commands)}");
                }

                await Task.Delay(10);
            }
        }

        protected abstract string LineEnd { get; }

        protected abstract string? Answer(string command);

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                if (HangUp)
                {
                    client.Dispose();
                    continue;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var pending = new StringBuilder();
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read <= 0)
                        {
                            return;
                        }

                        pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        var text = pending.ToString();
                        var end = text.LastIndexOf(LineEnd[0]);
                        if (end < 0)
                        {
                            continue;
                        }

                        pending.Clear().Append(text[(end + 1)..]);
                        foreach (var raw in text[..end].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                        {
                            var command = raw.Trim();
                            if (command.StartsWith("heos://", StringComparison.Ordinal))
                            {
                                command = command["heos://".Length..];
                            }

                            _commands.Enqueue(command);
                            CommandTimes.Enqueue(DateTimeOffset.UtcNow);
                            if (HangUp)
                            {
                                return; // Drops this link too.
                            }

                            if (Silent)
                            {
                                continue;
                            }

                            if (Answer(command) is { } answer)
                            {
                                var bytes = Encoding.UTF8.GetBytes(answer);
                                await stream.WriteAsync(bytes, _stop.Token);
                            }
                        }
                    }
                }
                catch
                {
                    // Connection closed.
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    /// <summary>A Denon AVR on Telnet: answers status queries, echoes set commands.</summary>
    private sealed class FakeAvr : FakeServer
    {
        public ConcurrentDictionary<string, string> Replies { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PW?"] = "PWON\r",
            ["ZM?"] = "ZMON\r",
            ["MV?"] = "MV45\rMVMAX 98\r",
            ["MU?"] = "MUOFF\r",
            ["SI?"] = "SITV\r",
            ["MS?"] = "MSSTEREO\r",
            ["ECO?"] = "ECOOFF\r",
            ["SLP?"] = "SLPOFF\r",
            ["Z2?"] = "Z2OFF\rZ2TV\rZ240\r",
        };

        protected override string LineEnd => "\r";

        protected override string? Answer(string command) =>
            Replies.TryGetValue(command, out var reply) ? reply
            : command.EndsWith('?') ? null
            : command + "\r";
    }

    /// <summary>A HEOS speaker: one player (pid 1), volume and mute, and an empty now-playing.</summary>
    private sealed class FakeHeos : FakeServer
    {
        public volatile int Volume = 30;

        protected override string LineEnd => "\n";

        protected override string? Answer(string command)
        {
            var path = command;
            var query = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : string.Empty;
            path = path.Contains('?') ? path[..path.IndexOf('?')] : path;

            string message;
            var payload = string.Empty;
            switch (path)
            {
                case "player/get_players":
                    message = string.Empty;
                    payload = ",\"payload\":[{\"name\":\"Fake\",\"pid\":1,\"model\":\"Fake AVR\",\"version\":\"1\",\"ip\":\"127.0.0.1\",\"network\":\"wired\",\"lineout\":0,\"serial\":\"\"}]";
                    break;
                case "player/get_play_state":
                    message = "pid=1&state=stop";
                    break;
                case "player/get_volume":
                    message = $"pid=1&level={Volume}";
                    break;
                case "player/get_mute":
                    message = "pid=1&state=off";
                    break;
                case "player/get_now_playing_media":
                    message = "pid=1";
                    payload = ",\"payload\":{}";
                    break;
                case "player/set_volume":
                    var level = query.Split('&').FirstOrDefault(p => p.StartsWith("level=", StringComparison.Ordinal))?["level=".Length..];
                    if (int.TryParse(level, out var parsed))
                    {
                        Volume = parsed;
                    }

                    message = query;
                    break;
                case "system/heart_beat":
                case "system/register_for_change_events":
                    message = query;
                    break;
                default:
                    message = query;
                    break;
            }

            return $"{{\"heos\":{{\"command\":\"{path}\",\"result\":\"success\",\"message\":\"{message}\"}}{payload}}}\r\n";
        }
    }
}
