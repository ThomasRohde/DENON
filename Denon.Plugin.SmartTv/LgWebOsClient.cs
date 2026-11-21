using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Denon.Plugin.SmartTv;

public sealed class SmartTvConfig
{
    public string TvName { get; set; } = "Living Room OLED TV";

    public string TvIp { get; set; } = string.Empty;

    public string MacAddress { get; set; } = string.Empty;

    public string ClientKey { get; set; } = string.Empty;

    public bool DemoMode { get; set; }

    public bool WakeWithDenonPowerOn { get; set; }

    public bool TurnOffWithDenonStandby { get; set; }

    public bool ShowSceneToastOnTv { get; set; } = true;

    public string MovieNightInputAppId { get; set; } = "com.webos.app.hdmi1";
}

/// <summary>
/// LG webOS SSAP WebSocket (:3000) + Wake-on-LAN (UDP :9) client with built-in Demo Simulator.
/// </summary>
public sealed class LgWebOsClient
{
    private const string RegistrationManifest = """
        {
          "forcePairing": false,
          "pairingType": "PROMPT",
          "manifest": {
            "manifestVersion": 1,
            "appVersion": "1.3.0",
            "permissions": [
              "LAUNCH", "LAUNCH_WEBAPP", "CONTROL_AUDIO", "CONTROL_POWER",
              "READ_INSTALLED_APPS", "CONTROL_DISPLAY", "CONTROL_INPUT_MEDIA_PLAYBACK",
              "WRITE_NOTIFICATION_TOAST", "READ_POWER_STATE", "READ_CURRENT_CHANNEL"
            ]
          }
        }
        """;

    private int _msgId;

    public async Task<string> PairAsync(string tvIp, string? existingClientKey, CancellationToken ct = default)
    {
        using var ws = await ConnectAsync(tvIp, ct);
        var key = await RegisterSessionAsync(ws, existingClientKey, ct);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Paired", CancellationToken.None); } catch { }
        return key;
    }

    public async Task SendCommandAsync(SmartTvConfig config, string uri, object? payload = null, CancellationToken ct = default)
    {
        if (config.DemoMode)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.TvIp))
        {
            throw new InvalidOperationException("Enter your Smart TV IP address first (or enable Demo Mode).");
        }

        using var ws = await ConnectAsync(config.TvIp, ct);
        var returnedKey = await RegisterSessionAsync(ws, config.ClientKey, ct);
        if (!string.IsNullOrWhiteSpace(returnedKey))
        {
            config.ClientKey = returnedKey;
        }

        var id = $"cmd_{Interlocked.Increment(ref _msgId)}";
        var msg = JsonSerializer.Serialize(new
        {
            id,
            type = "request",
            uri,
            payload = payload ?? new { },
        });

        await SendJsonAsync(ws, msg, ct);
        await ReceiveJsonAsync(ws, ct);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); } catch { }
    }

    public async Task SendWakeOnLanAsync(string macAddress, CancellationToken ct = default)
    {
        var hex = new string(macAddress.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12)
        {
            throw new InvalidOperationException("Enter a valid 12-hex MAC address (e.g. AA:BB:CC:DD:EE:FF) for Wake-on-LAN.");
        }

        var macBytes = Convert.FromHexString(hex);
        var packet = new byte[6 + (16 * 6)];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var rep = 0; rep < 16; rep++)
        {
            Buffer.BlockCopy(macBytes, 0, packet, 6 + (rep * 6), 6);
        }

        using var udp = new UdpClient { EnableBroadcast = true };
        await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
        await Task.Delay(120, ct);
        await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 7));
    }

    private static async Task<ClientWebSocket> ConnectAsync(string tvIp, CancellationToken ct)
    {
        var cleanHost = tvIp.Trim()
            .Replace("wss://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        // Modern LG webOS (2022+ webOS 22/23/24/25) requires TLS on wss://<ip>:3001 with self-signed TV cert;
        // older webOS TVs use plaintext ws://<ip>:3000. Try wss://:3001 first, then fall back to ws://:3000.
        var endpoints = new[]
        {
            new Uri($"wss://{cleanHost}:3001/"),
            new Uri($"ws://{cleanHost}:3000/"),
        };

        Exception? lastError = null;
        foreach (var uri in endpoints)
        {
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            if (uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
            {
                ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                await ws.ConnectAsync(uri, cts.Token);
                return ws;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
                ws.Dispose();
            }
        }

        throw new InvalidOperationException(
            $"Could not connect to LG webOS TV at {cleanHost} (tried wss://:3001 & ws://:3000). Make sure the TV is powered ON and on the same Wi-Fi/LAN. ({lastError?.Message})",
            lastError);
    }

    private async Task<string> RegisterSessionAsync(ClientWebSocket ws, string? clientKey, CancellationToken ct)
    {
        using var manifestDoc = JsonDocument.Parse(RegistrationManifest);
        var payloadDict = new Dictionary<string, object?>
        {
            ["forcePairing"] = false,
            ["pairingType"] = "PROMPT",
            ["manifest"] = manifestDoc.RootElement.GetProperty("manifest"),
        };

        if (!string.IsNullOrWhiteSpace(clientKey))
        {
            payloadDict["client-key"] = clientKey;
        }

        var regMsg = JsonSerializer.Serialize(new
        {
            id = $"reg_{Interlocked.Increment(ref _msgId)}",
            type = "register",
            payload = payloadDict,
        });

        await SendJsonAsync(ws, regMsg, ct);

        using var pairTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pairTimeoutCts.CancelAfter(TimeSpan.FromSeconds(string.IsNullOrWhiteSpace(clientKey) ? 35 : 8));

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var respText = await ReceiveJsonAsync(ws, pairTimeoutCts.Token);
            if (string.IsNullOrWhiteSpace(respText))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(respText);
            var type = doc.RootElement.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
            if (string.Equals(type, "registered", StringComparison.OrdinalIgnoreCase))
            {
                if (doc.RootElement.TryGetProperty("payload", out var pEl) &&
                    pEl.TryGetProperty("client-key", out var ckEl))
                {
                    return ckEl.GetString() ?? clientKey ?? string.Empty;
                }

                return clientKey ?? string.Empty;
            }

            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            {
                var err = doc.RootElement.TryGetProperty("error", out var eEl) ? eEl.GetString() : "TV rejected registration.";
                throw new InvalidOperationException(err ?? "TV rejected registration.");
            }
        }

        throw new TimeoutException("Timed out waiting for LG TV pairing confirmation. Accept the prompt on your TV screen.");
    }

    private static Task SendJsonAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            ms.Write(buf, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
