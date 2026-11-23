using System.Net;
using System.Net.Sockets;
using Denon.Plugin.AppleTv.Protocol;
using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Hap;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

/// <summary>
/// End-to-end over real TCP against a local fake Apple TV: pair-setup, pair-verify, then encrypted OPACK
/// requests, responses and a pushed event. Catches framing, counter and AAD mistakes.
/// </summary>
public sealed class CompanionLoopbackTests
{
    [Fact]
    public async Task Pairs_verifies_and_talks_encrypted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var accessory = new FakeAccessory("4321");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(() => ServeAsync(listener, accessory, connections: 2, cts.Token));

        // 1. Pair (the "PIN" is known to the fake).
        HapCredentials credentials;
        await using (var pairing = await CompanionPairing.StartAsync("127.0.0.1", port, "Denon Desktop", cts.Token))
        {
            credentials = await pairing.FinishAsync("4321", cts.Token);
            Assert.True(pairing.AccessorySignatureVerified);
        }

        // 2. Connect with the stored credentials: verify, encryption, system info, session start.
        var events = new List<string>();
        var eventSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var client = await CompanionClient.ConnectAsync("127.0.0.1", port, credentials, CompanionIdentity.Create("Denon Desktop"), cts.Token))
        {
            client.EventReceived += (name, _) =>
            {
                lock (events)
                {
                    events.Add(name);
                }

                eventSeen.TrySetResult();
            };

            Assert.Equal(AttentionState.Awake, await client.FetchAttentionStateAsync(cts.Token));
            var apps = await client.FetchAppsAsync(cts.Token);
            Assert.Equal("TV", apps["com.apple.TVWatchList"]);
            await client.PressAsync(HidCommand.Select, cts.Token);

            await client.SubscribeAsync("TVSystemStatus", cts.Token);
            await eventSeen.Task.WaitAsync(cts.Token);
        }

        Assert.Contains("TVSystemStatus", events);
        Assert.Equal("Denon Desktop", accessory.ControllerName);
        Assert.Equal(2, accessory.HidPresses); // down + up
        await server.WaitAsync(cts.Token);
        listener.Stop();
    }

    private static async Task ServeAsync(TcpListener listener, FakeAccessory accessory, int connections, CancellationToken ct)
    {
        for (var c = 0; c < connections; c++)
        {
            using var tcp = await listener.AcceptTcpClientAsync(ct);
            var stream = tcp.GetStream();
            byte[]? outKey = null, inKey = null;
            ulong outCounter = 0, inCounter = 0;
            var header = new byte[4];

            async Task SendAsync(CompanionFrameType type, Dictionary<string, object?> message)
            {
                var payload = Opack.Pack(message);
                var length = payload.Length + (outKey is null ? 0 : 16);
                byte[] h = [(byte)type, (byte)(length >> 16), (byte)(length >> 8), (byte)length];
                var body = outKey is null ? payload : HapCrypto.Encrypt(outKey, CompanionConnection.CounterNonce(outCounter++), payload, h);
                await stream.WriteAsync(h, ct);
                await stream.WriteAsync(body, ct);
            }

            while (true)
            {
                try
                {
                    await stream.ReadExactlyAsync(header, ct);
                }
                catch (EndOfStreamException)
                {
                    break;
                }

                var length = (header[1] << 16) | (header[2] << 8) | header[3];
                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload, ct);
                if (inKey is not null && length > 0)
                {
                    payload = HapCrypto.Decrypt(inKey, CompanionConnection.CounterNonce(inCounter++), payload, header);
                }

                var message = Opack.UnpackDictionary(payload);
                var type = (CompanionFrameType)header[0];
                switch (type)
                {
                    case CompanionFrameType.PsStart or CompanionFrameType.PsNext:
                        await SendAsync(CompanionFrameType.PsNext, new() { ["_pd"] = accessory.HandleSetup((byte[])message["_pd"]!) });
                        break;

                    case CompanionFrameType.PvStart or CompanionFrameType.PvNext:
                        var reply = accessory.HandleVerify((byte[])message["_pd"]!);
                        await SendAsync(CompanionFrameType.PvNext, new() { ["_pd"] = reply });
                        if (Tlv8.Read(reply)[TlvTag.SeqNo][0] == 4)
                        {
                            outKey = accessory.Key("ServerEncrypt-main");
                            inKey = accessory.Key("ClientEncrypt-main");
                        }

                        break;

                    case CompanionFrameType.EOpack:
                        var id = (string)message["_i"]!;
                        if ((long)message["_t"]! == (long)CompanionMessageType.Event)
                        {
                            if (id == "_interest")
                            {
                                await SendAsync(CompanionFrameType.EOpack, new() { ["_i"] = "TVSystemStatus", ["_t"] = 1, ["_c"] = new Dictionary<string, object?> { ["state"] = 3 } });
                            }

                            break;
                        }

                        Dictionary<string, object?> content = id switch
                        {
                            "FetchAttentionState" => new() { ["state"] = 3 },
                            "_sessionStart" => new() { ["_sid"] = 7 },
                            "FetchLaunchableApplicationsEvent" => new() { ["com.apple.TVWatchList"] = "TV", ["com.apple.TVSettings"] = "Settings" },
                            _ => [],
                        };

                        if (id == "_hidC")
                        {
                            accessory.HidPresses++;
                        }

                        await SendAsync(CompanionFrameType.EOpack, new() { ["_c"] = content, ["_t"] = 3, ["_x"] = message["_x"] });
                        break;
                }
            }
        }
    }
}
