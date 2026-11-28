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
/// and fades (#43), HEOS-only scenes (#66), the push listener's heartbeat (#38), and the port 23 reader with its pushes (#74).
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

        // The volume, then the input and the real volume (so a fade sees a new source or a turned knob), not a full read.
        Assert.Equal(["MV40", "ZM?", "SI?", "MV?"], avr.Commands.ToArray());
        Assert.Equal(40, step.Volume);
        Assert.Equal("TV", step.InputSource);

        avr.ClearCommands();
        await controller.SetVolumeAsync(42);
        Assert.Contains("PW?", avr.Commands);

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_fade_step_reads_the_full_status_when_the_input_changed()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();

        avr.Replies["SI?"] = "SIGAME\r"; // Someone switched the input with the remote during the fade.
        avr.ClearCommands();
        var step = await controller.SetVolumeAsync(40, logSuccess: false, refreshStatus: false);

        Assert.Equal("GAME", step.InputSource);
        Assert.Contains("PW?", avr.Commands);

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_fade_step_reads_the_full_status_when_the_zone_went_to_standby()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();

        avr.Replies["ZM?"] = "ZMOFF\r"; // Someone put the receiver in standby during the fade.
        avr.ClearCommands();
        var step = await controller.SetVolumeAsync(40, logSuccess: false, refreshStatus: false);

        Assert.False(step.IsMainZoneOn);
        Assert.Contains("PW?", avr.Commands);

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_fade_step_reports_the_real_volume_when_the_knob_was_turned()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();

        avr.KeepVolume = 30; // Someone turned the volume down to 30 right after the step.
        var step = await controller.SetVolumeAsync(40, logSuccess: false, refreshStatus: false);

        Assert.Equal(30, step.Volume); // Not the 40 that was sent: Volume Memory stops the fade.

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task Another_receiver_address_gets_no_grace_window()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var log = new AppLogStore(Path.Combine(_tempDir, "grace.log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, "grace.json"));
        await store.InitializeAsync();
        await store.UpdateAsync(s =>
        {
            s.Receiver.Host = "127.0.0.1";
            s.Receiver.Port = heos.Port;
        });
        var telemetry = new EventTelemetryLog(store, log, new RoomEventBus(log), Path.Combine(_tempDir, "grace-events"));
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(avr.Port), store, new ReceiverStateStore(), telemetry, log);

        Assert.Equal("TV", (await controller.RefreshAsync()).InputSource);

        // The settings now name another receiver (here the same fakes under another name), and its port 23 is busy.
        await store.UpdateAsync(s => s.Receiver.Host = "localhost");
        avr.HangUp = true;

        var next = await controller.RefreshAsync();
        Assert.Equal("NET", next.InputSource); // Not the first receiver's TV input.

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
    public async Task A_scene_uses_the_avr_steps_when_port_23_is_back_after_a_failed_poll()
    {
        using var avr = new FakeAvr { HangUp = true }; // Port 23 busy during the last poll.
        using var heos = new FakeHeos();
        var (controller, _, telemetry) = await CreateControllerAsync(avr, heos);
        await controller.RefreshAsync();
        Assert.False(controller.IsAvrProtocolAvailable);

        avr.HangUp = false; // Free again before the scene starts.
        avr.ClearCommands();
        var scene = new ScenePreset { Name = "Movie", PowerOn = false, Unmute = false, InputSource = "BD", SmoothVolumeRamp = false, TargetVolume = 20 };
        await controller.ApplySceneAsync(scene);

        Assert.True(controller.IsAvrProtocolAvailable);
        Assert.Contains("SIBD", avr.Commands);
        Assert.Contains("MV20", avr.Commands);

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

    // ---- #74: one reader on port 23, pushes update the state at once ----------------------------------------------

    private async Task<(ReceiverController Controller, ReceiverStateStore State, EventTelemetryLog Telemetry, AppSettingsStore Store, AppLogStore Log)> CreateRigAsync(
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
        var bus = new RoomEventBus(log);
        var telemetry = new EventTelemetryLog(store, log, bus, Path.Combine(_tempDir, "events-" + Guid.NewGuid().ToString("N")));
        var state = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(avr.Port), store, state, telemetry, log);
        return (controller, state, telemetry, store, log);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, double seconds = 5)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {seconds} s for: {what}");
            }

            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData("PWSTANDBY", nameof(AvrLiveState.IsPoweredOn), false)]
    [InlineData("ZMOFF", nameof(AvrLiveState.IsMainZoneOn), false)]
    [InlineData("MV505", nameof(AvrLiveState.VolumeExact), 50.5)]
    [InlineData("MVMAX 80", nameof(AvrLiveState.MaxVolume), 80)]
    [InlineData("MUON", nameof(AvrLiveState.IsMuted), true)]
    [InlineData("SIGAME", nameof(AvrLiveState.InputSource), "GAME")]
    [InlineData("MSDOLBY DIGITAL", nameof(AvrLiveState.SoundMode), "DOLBY DIGITAL")]
    [InlineData("ECOAUTO", nameof(AvrLiveState.EcoMode), "AUTO")]
    [InlineData("SLP030", nameof(AvrLiveState.SleepTimerMinutes), 30)]
    [InlineData("Z2ON", nameof(AvrLiveState.IsZone2On), true)]
    public void A_receiver_line_sets_its_field(string line, string field, object expected)
    {
        var live = AvrLiveState.Empty.Apply(line);
        var value = typeof(AvrLiveState).GetProperty(field)!.GetValue(live);
        Assert.Equal(Convert.ToString(expected, System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("Z2TV")]       // zone 2 source, not its power
    [InlineData("Z240")]       // zone 2 volume
    [InlineData("MSQUICK1")]   // a Quick Select memory, not a sound mode
    [InlineData("MVxx")]
    [InlineData("SSINFAISFSV 441")]
    public void Other_lines_leave_the_state_as_it_is(string line) =>
        Assert.Equal(AvrLiveState.Empty, AvrLiveState.Empty.Apply(line));

    [Fact]
    public void A_part_line_waits_for_its_end()
    {
        var pending = new StringBuilder("MV45\rMVMA");
        Assert.Equal(["MV45"], DenonAvrClient.TakeCompleteLines(pending));
        pending.Append("X 98\r");
        Assert.Equal(["MVMAX 98"], DenonAvrClient.TakeCompleteLines(pending));
        Assert.Equal(0, pending.Length);
    }

    [Theory]
    [InlineData(2, 30)]
    [InlineData(5, 45)]
    [InlineData(6, 54)]
    [InlineData(60, 60)]
    public void The_slow_check_is_nine_polls_from_30_to_60_s(int pollSeconds, int slowSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(slowSeconds), ReceiverPollingService.SlowCheckInterval(pollSeconds));

    [Fact]
    public async Task State_changes_from_many_threads_are_raised_in_order_and_end_on_the_newest()
    {
        var state = new ReceiverStateStore();
        var player = new ReceiverPlayerInfo("Fake", 1, "Fake AVR", "1", "127.0.0.1", "wired", 0, "");
        var media = new NowPlayingMedia("", "", "", "", "", "", "", null, null);
        var start = new ReceiverStatus(player, "stop", 0, false, media, DateTimeOffset.Now, IsPoweredOn: true, IsMainZoneOn: true);
        state.UpdateStatus(start); // A status to patch.
        var raised = new List<int>();
        state.StateChanged += snapshot =>
        {
            lock (raised)
            {
                raised.Add(snapshot.Status?.Volume ?? -1);
            }
        };

        var order = 0;
        // Like a full read and the receiver's pushes at the same time. The stamp is taken inside the store's lock,
        // so it is the order of the writes.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                if (i % 2 == 0)
                {
                    state.UpdateStatus(start, s => s with { Volume = Interlocked.Increment(ref order) });
                }
                else
                {
                    state.PatchFromReceiver(s => s with { Volume = Interlocked.Increment(ref order) });
                }
            }
        })));

        Assert.Equal(1600, order);
        Assert.Equal(1600, raised[^1]); // The last raised state is the newest.
        for (var i = 1; i < raised.Count; i++)
        {
            Assert.True(raised[i] > raised[i - 1], $"State {raised[i]} was raised after {raised[i - 1]}.");
        }
    }

    [Fact]
    public async Task A_query_gets_its_answer_while_a_push_comes_in_between()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        avr.Replies["SI?"] = "MV50\rSITV\r"; // The knob turned to 50 just before the receiver answered SI?.
        var (controller, state, telemetry, _, _) = await CreateRigAsync(avr, heos);

        var status = await controller.RefreshAsync();

        Assert.Equal("TV", status.InputSource);   // SI? got its answer past the push.
        Assert.Equal(50, status.Volume);          // The MV? answer said 45; the push after it wins.
        Assert.Equal(50, state.Snapshot.Status!.Volume);

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_push_with_no_query_updates_the_state_at_once()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, state, telemetry, _, _) = await CreateRigAsync(avr, heos);
        await controller.RefreshAsync();
        Assert.False(state.Snapshot.Status!.IsMuted);
        avr.ClearCommands();

        var seen = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        state.StateChanged += snapshot =>
        {
            if (snapshot.Status is { IsMuted: true })
            {
                seen.TrySetResult(watch.Elapsed);
            }
        };
        await avr.PushAsync("MUON\r"); // The mute button on the remote.

        var after = await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(after < TimeSpan.FromMilliseconds(250), $"The push took {after.TotalMilliseconds:0} ms");
        Assert.Empty(avr.Commands); // No query: the receiver said it by itself.

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_pushed_input_change_reads_the_full_state_soon_after()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, state, telemetry, _, _) = await CreateRigAsync(avr, heos);
        await controller.RefreshAsync();
        avr.ClearCommands();

        avr.Replies["SI?"] = "SIGAME\r";
        await avr.PushAsync("SIGAME\r"); // Someone pressed GAME on the remote.

        await WaitUntilAsync(() => state.Snapshot.Status?.InputSource == "GAME", "the pushed input");
        await avr.WaitForCommandAsync("PW?"); // Then a full read, for what HEOS plays now.
        Assert.Equal("GAME", state.Snapshot.Status!.InputSource);

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_volume_burst_writes_one_settled_event()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var log = new AppLogStore(Path.Combine(_tempDir, "burst.log"));
        var store = new AppSettingsStore(log, Path.Combine(_tempDir, "burst.json"));
        await store.InitializeAsync();
        await store.UpdateAsync(s =>
        {
            s.Receiver.Host = "127.0.0.1";
            s.Receiver.Port = heos.Port;
        });
        var bus = new RoomEventBus(log);
        var telemetry = new EventTelemetryLog(store, log, bus, Path.Combine(_tempDir, "burst-events"));
        var state = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(avr.Port), store, state, telemetry, log);
        var ytMedia = new YouTubeMediaService(store, log);
        using var relay = new LocalAudioRelayServer(ytMedia, store, log);
        using var ytQueue = new YouTubeQueueService(relay, controller, state, log);
        var plugins = new Denon.App.Core.Plugins.PluginHostService([], store, state, controller, telemetry, log);
        using var monitor = new EventTelemetryMonitorService(telemetry, store, state, ytQueue, plugins, log);
        var seen = new List<RoomEvent>();
        using var subscription = bus.Subscribe(e => { lock (seen) seen.Add(e); });
        state.StateChanged += monitor.ProcessReceiverSnapshot;

        await controller.RefreshAsync(); // Volume 45.
        for (var level = 46; level <= 55; level++)
        {
            await avr.PushAsync($"MV{level}\r"); // The knob turned up, one line per step.
            await Task.Delay(15);
        }

        await WaitUntilAsync(() => state.Snapshot.Status?.Volume == 55, "the last pushed volume");
        monitor.EvaluateVolumeSettle(DateTimeOffset.UtcNow.AddSeconds(30));

        var marker = Guid.NewGuid().ToString("N");
        telemetry.Record("control", "test", "test.marker", "test", new Dictionary<string, object?> { ["id"] = marker });
        await WaitUntilAsync(() => { lock (seen) return seen.Any(e => e.GetText("id") == marker); }, "the event bus");

        List<RoomEvent> settled;
        lock (seen)
        {
            settled = seen.Where(static e => e.Event == "volume.settled").ToList();
        }

        var only = Assert.Single(settled);
        Assert.Equal("45", only.GetText("from"));
        Assert.Equal("55", only.GetText("to"));

        await telemetry.DisposeAsync();
        bus.Dispose();
    }

    [Fact]
    public async Task A_pushed_acoustic_line_updates_the_kept_acoustics()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        avr.Replies["PSMULTEQ: ?"] = "PSMULTEQ:AUDYSSEY\r";
        avr.Replies["PSDYNEQ ?"] = "PSDYNEQ ON\r";
        avr.Replies["PSDYNVOL ?"] = "PSDYNVOL OFF\r";
        avr.Replies["PSRSTR ?"] = "PSRSTR OFF\r";
        avr.Replies["DIM ?"] = "DIM BRI\r";
        avr.Replies["PSSWL ?"] = "PSSWL ON\rPSSWL 50\r";
        avr.Replies["PSDIL ?"] = "PSDIL OFF\rPSDIL 50\r";
        avr.Replies["PSTONE CTRL ?"] = "PSTONE CTRL ON\r";
        avr.Replies["PSBAS ?"] = "PSBAS 50\r";
        avr.Replies["PSTRE ?"] = "PSTRE 50\r";
        var (controller, state, telemetry, _, _) = await CreateRigAsync(avr, heos);
        await controller.RefreshAsync();
        Assert.Null(state.Snapshot.Acoustics); // Not read yet.

        await controller.GetAcousticStatusAsync();
        Assert.True(state.Snapshot.Acoustics!.DynamicEq);

        await avr.PushAsync("PSDYNEQ OFF\rPSBAS 53\r"); // Changed on the remote's menu.

        await WaitUntilAsync(() => state.Snapshot.Acoustics?.DynamicEq == false, "the pushed Dynamic EQ");
        Assert.Equal(3, state.Snapshot.Acoustics!.BassDb);

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task An_acoustic_push_during_the_full_acoustic_read_is_kept()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        avr.Replies["PSDYNEQ ?"] = "PSDYNEQ ON\r";
        // Dynamic EQ is turned off on the remote after PSDYNEQ ? answered, while the read asks for the treble.
        avr.Replies["PSTRE ?"] = "PSDYNEQ OFF\rPSTRE 50\r";
        var (controller, state, telemetry, _, _) = await CreateRigAsync(avr, heos);
        await controller.RefreshAsync();

        var read = await controller.GetAcousticStatusAsync();

        Assert.False(read.DynamicEq);
        Assert.False(state.Snapshot.Acoustics!.DynamicEq);
        await Task.Delay(100); // The event task has the lines too: it must not bring back the older answer.
        Assert.False(state.Snapshot.Acoustics!.DynamicEq);

        await telemetry.DisposeAsync();
    }

    [Theory]
    [InlineData("PSDYNEQ ON", "PSDYNEQ")]
    [InlineData("PSSWL ON", "PSSWL:ONOFF")]
    [InlineData("PSSWL 52", "PSSWL:LEVEL")]
    [InlineData("DIM BRI", "DIM")]
    [InlineData("PSSWL2 50", null)] // a second subwoofer: not read
    [InlineData("PSBAS UP", null)]
    [InlineData("MV45", null)]
    public void An_acoustic_line_is_kept_per_setting(string line, string? key) =>
        Assert.Equal(key, AvrLiveState.AcousticKey(line));

    [Fact]
    public async Task A_change_sent_just_before_the_link_closes_is_kept_and_an_old_link_is_ignored()
    {
        using var avr = new FakeAvr();
        var client = new DenonAvrClient(avr.Port);
        await client.GetStatusAsync(Loopback(), CancellationToken.None); // Opens link 1.
        var firstLink = client.OpenLinkGeneration;

        var seen = new TaskCompletionSource<AvrLiveState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.LinesReceived += received =>
        {
            if (!received.Lines.Contains("PSDYNEQ OFF"))
            {
                return;
            }

            // Handle the event only once the link is closed, as when the event task is behind.
            SpinWait.SpinUntil(() => client.OpenLinkGeneration == 0, TimeSpan.FromSeconds(5));
            seen.TrySetResult(client.LatestLiveState(received));
        };

        await avr.PushAsync("PSDYNEQ OFF\r");
        avr.DropAll(); // The receiver sends a change, then drops the link.

        var live = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(live);
        Assert.False(live!.ApplyAcousticsTo(AcousticStatus.Empty).DynamicEq);

        // A newer link is open: lines from the old one are old and change nothing.
        await client.GetStatusAsync(Loopback(), CancellationToken.None);
        Assert.True(client.OpenLinkGeneration > firstLink);
        Assert.Null(client.LatestLiveState(new AvrLinesReceived("127.0.0.1", firstLink, ["MV10"], AvrLiveState.Empty.Apply("MV10"))));
    }

    [Fact]
    public async Task A_pushed_change_then_a_drop_still_updates_the_state()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        avr.Replies["PSDYNEQ ?"] = "PSDYNEQ ON\r";
        var (controller, state, telemetry, _, _) = await CreateRigAsync(avr, heos);
        await controller.RefreshAsync();
        await controller.GetAcousticStatusAsync();
        Assert.True(state.Snapshot.Acoustics!.DynamicEq);

        await avr.PushAsync("PSDYNEQ OFF\rMUON\r");
        avr.DropAll();

        await WaitUntilAsync(() => state.Snapshot.Acoustics?.DynamicEq == false, "the acoustic line before the drop");
        await WaitUntilAsync(() => state.Snapshot.Status?.IsMuted == true, "the mute line before the drop");

        await telemetry.DisposeAsync();
    }

    [Fact]
    public async Task A_refused_command_sends_nothing_and_the_link_keeps_listening()
    {
        using var avr = new FakeAvr();
        var client = new DenonAvrClient(avr.Port);
        await client.GetStatusAsync(Loopback(), CancellationToken.None); // Opens the link.
        avr.ClearCommands();

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendRawCommandAsync(Loopback(), "SITV\rMV98", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetInputSourceAsync(Loopback(), "TV\nPWSTANDBY", CancellationToken.None));
        Assert.Empty(avr.Commands);

        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.LinesReceived += received =>
        {
            if (received.Lines.Contains("MUON"))
            {
                heard.TrySetResult();
            }
        };
        await avr.PushAsync("MUON\r");
        await heard.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, avr.Connections); // The same link.
        Assert.True(client.GetLiveState("127.0.0.1").IsMuted);
    }

    [Fact]
    public async Task After_a_drop_the_poll_connects_again_reads_the_full_state_and_slows_down_again()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, state, telemetry, store, log) = await CreateRigAsync(avr, heos);
        using var heosEvents = new HeosEventListenerService(store, state, controller, log);
        using var poller = new ReceiverPollingService(controller, new DenonDiscoveryService(), store, heosEvents, log)
        {
            NormalIntervalOverride = TimeSpan.FromMilliseconds(150),
            SlowIntervalOverride = TimeSpan.FromMinutes(5), // Only a wake can start the next read in this test.
        };
        await heosEvents.StartAsync(CancellationToken.None);
        await poller.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => poller.IsSlowCheck, "the slow check");
            Assert.Equal(1, avr.Connections);

            avr.ClearCommands();
            avr.DropAll(); // The receiver restarts its network module.

            // At once (the slow check waits 5 minutes): a new connection and the full read, then one more read at the
            // normal pace to see that the new link holds, then the slow check again.
            await WaitUntilAsync(() => avr.Connections >= 2, "a new connection");
            await WaitUntilAsync(() => avr.Commands.Count(static c => c == "Z2?") >= 2, "the full read and one at the normal pace");
            await WaitUntilAsync(() => poller.IsSlowCheck, "the slow check again");
            var reads = avr.Commands.Count(static c => c == "PW?");
            await Task.Delay(500);
            Assert.Equal(reads, avr.Commands.Count(static c => c == "PW?")); // No read at the normal pace now.

            // The new link listens too.
            await avr.PushAsync("MUON\r");
            await WaitUntilAsync(() => state.Snapshot.Status?.IsMuted == true, "a push on the new link");
        }
        finally
        {
            await poller.StopAsync(CancellationToken.None);
            await heosEvents.StopAsync(CancellationToken.None);
            await telemetry.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_silent_receiver_drops_the_link_and_the_poll_goes_back_to_its_normal_pace()
    {
        using var avr = new FakeAvr();
        using var heos = new FakeHeos();
        var (controller, state, telemetry, store, log) = await CreateRigAsync(avr, heos);
        using var heosEvents = new HeosEventListenerService(store, state, controller, log);
        using var poller = new ReceiverPollingService(controller, new DenonDiscoveryService(), store, heosEvents, log)
        {
            NormalIntervalOverride = TimeSpan.FromMilliseconds(150),
            SlowIntervalOverride = TimeSpan.FromMilliseconds(300),
        };
        await heosEvents.StartAsync(CancellationToken.None);
        await poller.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => poller.IsSlowCheck, "the slow check");

            avr.Silent = true; // A half-open link: the receiver reads, and never answers.
            await WaitUntilAsync(() => !poller.IsSlowCheck, "the normal pace", seconds: 10);

            avr.Silent = false;
            await WaitUntilAsync(() => avr.Connections >= 2, "a new connection", seconds: 10);
            await WaitUntilAsync(() => poller.IsSlowCheck, "the slow check again", seconds: 10);
        }
        finally
        {
            await poller.StopAsync(CancellationToken.None);
            await heosEvents.StopAsync(CancellationToken.None);
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

        private readonly ConcurrentDictionary<TcpClient, NetworkStream> _open = new();

        /// <summary>Sends text to every open connection, with no command first: what the receiver pushes by itself.</summary>
        public async Task PushAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            foreach (var stream in _open.Values)
            {
                await stream.WriteAsync(bytes);
            }
        }

        /// <summary>Closes every open connection (the receiver restarts, or another app takes the port).</summary>
        public void DropAll()
        {
            foreach (var client in _open.Keys)
            {
                _open.TryRemove(client, out _);
                client.Dispose();
            }
        }

        public async Task WaitForConnectionsAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Connections < count || _open.Count == 0)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"The fake has {Connections} connections, not {count}.");
                }

                await Task.Delay(10);
            }
        }

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
                _open[client] = stream;
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
                finally
                {
                    _open.TryRemove(client, out _);
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

        /// <summary>When set, MV? answers this volume whatever was set (someone turns the knob).</summary>
        public volatile object? KeepVolume;

        protected override string LineEnd => "\r";

        protected override string? Answer(string command)
        {
            if (command.Equals("MV?", StringComparison.OrdinalIgnoreCase) && KeepVolume is int kept)
            {
                return $"MV{kept:00}\rMVMAX 98\r";
            }

            if (command.Length is 4 && command.StartsWith("MV", StringComparison.Ordinal) && int.TryParse(command[2..], out var level))
            {
                Replies["MV?"] = $"MV{level:00}\rMVMAX 98\r"; // The receiver now has this volume.
            }

            return Replies.TryGetValue(command, out var reply) ? reply
                : command.EndsWith('?') ? null
                : command + "\r";
        }
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
