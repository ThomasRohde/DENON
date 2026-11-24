using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Denon.Plugin.PhilipsHue.Models;

namespace Denon.Plugin.PhilipsHue.Services;

public sealed class HueBridgeClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly object _demoLock = new();
    private readonly List<DemoLight> _demoLights;
    private readonly List<DemoRoom> _demoRooms;
    private readonly List<HueSceneItem> _demoScenes;

    public HueBridgeClient(HttpClient httpClient)
    {
        _http = httpClient;

        _demoLights =
        [
            new("1", "TV Play Gradient Strip", "Extended color light", "Hue Play Gradient Lightstrip", true, 65, 366, true, true, 0.168, 0.340),
            new("2", "Left Tower Floor Lamp", "Extended color light", "Hue Signe Floor", true, 58, 410, true, true, 0.505, 0.385),
            new("3", "Right Tower Floor Lamp", "Extended color light", "Hue Signe Floor", true, 58, 410, true, true, 0.505, 0.385),
            new("4", "Ceiling Recessed Spots", "Color temperature light", "Hue White Ambiance Spot", true, 42, 370, false, true, null, null),
            new("5", "Listening Chair Lamp", "Extended color light", "Hue Iris", true, 72, 445, true, true, 0.542, 0.402),
            new("6", "Desk Bias Light", "Extended color light", "Hue Lightstrip Plus", false, 50, 290, true, true, 0.195, 0.115),
        ];

        _demoRooms =
        [
            new("1", "Home Theater", "Room", "TV", ["1", "2", "3", "4"]),
            new("2", "Listening Lounge", "Room", "Living room", ["5", "6"]),
        ];

        _demoScenes =
        [
            new("demo-cinema", "Cinema Night", "1"),
            new("demo-tokyo", "Tokyo Neon", "1"),
            new("demo-savanna", "Savanna Sunset", "1"),
            new("demo-relax", "Warm Relax", "1"),
            new("demo-concentrate", "Hi-Fi Focus", "2"),
            new("demo-candle", "Vinyl Lounge", "2"),
        ];
    }

    public async Task<IReadOnlyList<HueDiscoveredBridge>> DiscoverBridgesAsync(CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, HueDiscoveredBridge>(StringComparer.OrdinalIgnoreCase);

        // 1. Official Hue N-UPnP discovery endpoint
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://discovery.meethue.com/");
            using var resp = await _http.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var ip = item.TryGetProperty("internalipaddress", out var ipEl) ? ipEl.GetString() : null;
                        var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : string.Empty;
                        if (!string.IsNullOrWhiteSpace(ip))
                        {
                            results[ip] = new HueDiscoveredBridge(id ?? string.Empty, ip, "Hue Cloud Discovery");
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall through to local LAN SSDP
        }

        // 2. Local LAN SSDP M-SEARCH fallback (works even offline / without internet)
        try
        {
            var ssdpBridges = await DiscoverViaSsdpAsync(cancellationToken);
            foreach (var bridge in ssdpBridges)
            {
                results.TryAdd(bridge.IpAddress, bridge);
            }
        }
        catch
        {
        }

        return results.Values.OrderBy(static b => b.IpAddress, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<HuePairingResult> TryPairAsync(string bridgeIp, bool preferHttps, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            devicetype = "denon_desktop#windows",
            generateclientkey = true,
        });

        var json = await SendWithSchemeFallbackAsync(
            bridgeIp,
            preferHttps,
            HttpMethod.Post,
            "/api",
            payload,
            cancellationToken);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
        {
            var first = doc.RootElement[0];
            if (first.TryGetProperty("success", out var successEl) &&
                successEl.TryGetProperty("username", out var userEl))
            {
                var key = userEl.GetString();
                var clientKey = successEl.TryGetProperty("clientkey", out var ckEl) ? ckEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return new HuePairingResult(true, false, key, clientKey, "Paired with Philips Hue Bridge (Entertainment Key ready).");
                }
            }

            if (first.TryGetProperty("error", out var errorEl))
            {
                var type = errorEl.TryGetProperty("type", out var tEl) ? tEl.GetInt32() : 0;
                var desc = errorEl.TryGetProperty("description", out var dEl) ? dEl.GetString() : "Pairing error.";
                if (type == 101)
                {
                    return new HuePairingResult(false, true, null, null, "Press the round link button on top of your Philips Hue Bridge...");
                }

                return new HuePairingResult(false, false, null, null, desc ?? "Bridge rejected pairing.");
            }
        }

        return new HuePairingResult(false, false, null, null, "Unexpected response from Hue Bridge.");
    }

    public async Task<(IReadOnlyList<HueRoomGroup> Rooms, IReadOnlyList<HueLightState> Lights, IReadOnlyList<HueSceneItem> Scenes)> FetchFullStateAsync(
        HuePluginConfig config,
        CancellationToken cancellationToken = default)
    {
        if (config.DemoMode)
        {
            return GetDemoState();
        }

        EnsureConfigured(config);

        var basePath = $"/api/{Uri.EscapeDataString(config.ApplicationKey)}";
        var lightsJson = await SendWithSchemeFallbackAsync(config.BridgeIp, config.UseHttps, HttpMethod.Get, $"{basePath}/lights", null, cancellationToken);
        var groupsJson = await SendWithSchemeFallbackAsync(config.BridgeIp, config.UseHttps, HttpMethod.Get, $"{basePath}/groups", null, cancellationToken);
        var scenesJson = await SendWithSchemeFallbackAsync(config.BridgeIp, config.UseHttps, HttpMethod.Get, $"{basePath}/scenes", null, cancellationToken);

        var lights = ParseLights(lightsJson);
        var rooms = ParseGroups(groupsJson);
        var scenes = ParseScenes(scenesJson);

        return (rooms, lights, scenes);
    }

    public async Task SetLightStateAsync(
        HuePluginConfig config,
        string lightId,
        bool? isOn = null,
        int? brightnessPercent = null,
        int? colorTempMirek = null,
        double? x = null,
        double? y = null,
        int transitionDeciseconds = 4,
        CancellationToken cancellationToken = default)
    {
        if (config.DemoMode)
        {
            UpdateDemoLight(lightId, isOn, brightnessPercent, colorTempMirek, x, y);
            return;
        }

        EnsureConfigured(config);

        var body = BuildStatePayload(isOn, brightnessPercent, colorTempMirek, x, y, sceneId: null, transitionDeciseconds);
        var path = $"/api/{Uri.EscapeDataString(config.ApplicationKey)}/lights/{Uri.EscapeDataString(lightId)}/state";
        await SendWithSchemeFallbackAsync(config.BridgeIp, config.UseHttps, HttpMethod.Put, path, body, cancellationToken);
    }

    public async Task SetGroupActionAsync(
        HuePluginConfig config,
        string groupId,
        bool? isOn = null,
        int? brightnessPercent = null,
        int? colorTempMirek = null,
        double? x = null,
        double? y = null,
        string? sceneId = null,
        int transitionDeciseconds = 5,
        CancellationToken cancellationToken = default)
    {
        if (config.DemoMode)
        {
            UpdateDemoGroup(groupId, isOn, brightnessPercent, colorTempMirek, x, y, sceneId);
            return;
        }

        EnsureConfigured(config);

        var effectiveGroupId = string.IsNullOrWhiteSpace(groupId) ? "0" : groupId;
        var body = BuildStatePayload(isOn, brightnessPercent, colorTempMirek, x, y, sceneId, transitionDeciseconds);
        var path = $"/api/{Uri.EscapeDataString(config.ApplicationKey)}/groups/{Uri.EscapeDataString(effectiveGroupId)}/action";
        await SendWithSchemeFallbackAsync(config.BridgeIp, config.UseHttps, HttpMethod.Put, path, body, cancellationToken);
    }

    public async Task<IReadOnlyList<HueEntertainmentArea>> FetchEntertainmentAreasAsync(
        HuePluginConfig config,
        CancellationToken cancellationToken = default)
    {
        if (config.DemoMode)
        {
            return [new HueEntertainmentArea("demo-ent-1", "Home Theater Sync Zone", 4)];
        }

        if (string.IsNullOrWhiteSpace(config.BridgeIp) || string.IsNullOrWhiteSpace(config.ApplicationKey))
        {
            return [];
        }

        try
        {
            var cleanHost = config.BridgeIp.Trim()
                .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/');

            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{cleanHost}/clip/v2/resource/entertainment_configuration");
            req.Headers.Add("hue-application-key", config.ApplicationKey);
            using var resp = await _http.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var list = new List<HueEntertainmentArea>();
            if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataEl.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var name = item.TryGetProperty("metadata", out var metaEl) && metaEl.TryGetProperty("name", out var nEl)
                        ? nEl.GetString()
                        : (item.TryGetProperty("name", out var n2El) ? n2El.GetString() : "Entertainment Area");
                    var channels = item.TryGetProperty("channels", out var chEl) && chEl.ValueKind == JsonValueKind.Array
                        ? chEl.GetArrayLength()
                        : 0;

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        list.Add(new HueEntertainmentArea(id, name ?? "Entertainment Area", channels));
                    }
                }
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    public Task PushFastSyncLightAsync(
        HuePluginConfig config,
        string lightId,
        int brightnessPercent,
        double x,
        double y,
        int transitionDeciseconds = 1,
        CancellationToken cancellationToken = default) =>
        SetLightStateAsync(
            config,
            lightId,
            isOn: true,
            brightnessPercent: Math.Clamp(brightnessPercent, 1, 100),
            x: x,
            y: y,
            transitionDeciseconds: transitionDeciseconds,
            cancellationToken: cancellationToken);

    public void Dispose()
    {
        _http.Dispose();
    }

    private static string BuildStatePayload(
        bool? isOn,
        int? brightnessPercent,
        int? colorTempMirek,
        double? x,
        double? y,
        string? sceneId,
        int transitionDeciseconds)
    {
        var dict = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(sceneId))
        {
            dict["scene"] = sceneId;
            return JsonSerializer.Serialize(dict);
        }

        if (isOn.HasValue)
        {
            dict["on"] = isOn.Value;
        }

        if (brightnessPercent.HasValue)
        {
            if (brightnessPercent.Value <= 0)
            {
                dict["on"] = false;
            }
            else
            {
                dict["on"] = isOn ?? true;
                dict["bri"] = HueColorConverter.PercentToHueBri(brightnessPercent.Value);
            }
        }

        if (x.HasValue && y.HasValue)
        {
            dict["xy"] = new[] { Math.Clamp(x.Value, 0.0, 1.0), Math.Clamp(y.Value, 0.0, 1.0) };
        }
        else if (colorTempMirek.HasValue)
        {
            dict["ct"] = Math.Clamp(colorTempMirek.Value, 153, 500);
        }

        if (transitionDeciseconds >= 0)
        {
            dict["transitiontime"] = transitionDeciseconds;
        }

        return JsonSerializer.Serialize(dict);
    }

    private async Task<string> SendWithSchemeFallbackAsync(
        string bridgeIp,
        bool preferHttps,
        HttpMethod method,
        string path,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        var cleanHost = bridgeIp.Trim()
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        var schemes = preferHttps ? new[] { "https", "http" } : new[] { "http", "https" };
        Exception? lastError = null;

        foreach (var scheme in schemes)
        {
            try
            {
                using var req = new HttpRequestMessage(method, $"{scheme}://{cleanHost}{path}");
                if (jsonBody is not null)
                {
                    req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                }

                using var resp = await _http.SendAsync(req, cancellationToken);
                var text = await resp.Content.ReadAsStringAsync(cancellationToken);
                resp.EnsureSuccessStatusCode();

                // Detect Hue API unauthorized error returned inside 200 OK JSON array
                if (text.StartsWith("[", StringComparison.Ordinal) &&
                    text.Contains("\"unauthorized user\"", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Hue Bridge rejected the saved key (unauthorized). Click 'Press Bridge Button & Pair' to re-link.");
                }

                return text;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException($"Could not reach Philips Hue Bridge at {cleanHost}: {lastError?.Message}");
    }

    private static IReadOnlyList<HueLightState> ParseLights(string json)
    {
        var list = new List<HueLightState>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var obj = prop.Value;
            var name = obj.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? $"Light {prop.Name}" : $"Light {prop.Name}";
            var typeName = obj.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "Light" : "Light";
            var productName = obj.TryGetProperty("productname", out var pEl) ? pEl.GetString() ?? typeName : typeName;

            var isOn = false;
            var isReachable = true;
            var briPct = 100;
            int? mirek = null;
            double? x = null;
            double? y = null;

            if (obj.TryGetProperty("state", out var state))
            {
                if (state.TryGetProperty("on", out var onEl) && onEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    isOn = onEl.GetBoolean();
                }

                if (state.TryGetProperty("reachable", out var rEl) && rEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    isReachable = rEl.GetBoolean();
                }

                if (state.TryGetProperty("bri", out var bEl) && bEl.TryGetInt32(out var bri))
                {
                    briPct = HueColorConverter.HueBriToPercent(bri);
                }

                if (state.TryGetProperty("ct", out var ctEl) && ctEl.TryGetInt32(out var ctVal))
                {
                    mirek = ctVal;
                }

                if (state.TryGetProperty("xy", out var xyEl) && xyEl.ValueKind == JsonValueKind.Array && xyEl.GetArrayLength() >= 2)
                {
                    x = xyEl[0].GetDouble();
                    y = xyEl[1].GetDouble();
                }
            }

            var supportsColor = x.HasValue || typeName.Contains("color", StringComparison.OrdinalIgnoreCase) && !typeName.Contains("temperature", StringComparison.OrdinalIgnoreCase);
            var supportsCt = mirek.HasValue || typeName.Contains("temperature", StringComparison.OrdinalIgnoreCase) || typeName.Contains("extended color", StringComparison.OrdinalIgnoreCase);
            var previewHex = HueColorConverter.XyOrCtToHex(supportsColor ? x : null, supportsColor ? y : null, mirek, isOn);

            list.Add(new HueLightState(
                prop.Name,
                name,
                typeName,
                productName,
                isOn,
                isReachable,
                briPct,
                mirek,
                supportsColor,
                supportsCt,
                x,
                y,
                previewHex));
        }

        return list.OrderBy(static l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<HueRoomGroup> ParseGroups(string json)
    {
        var list = new List<HueRoomGroup>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var obj = prop.Value;
            var groupType = obj.TryGetProperty("type", out var gtEl) ? gtEl.GetString() ?? "Room" : "Room";
            if (!groupType.Equals("Room", StringComparison.OrdinalIgnoreCase) &&
                !groupType.Equals("Zone", StringComparison.OrdinalIgnoreCase) &&
                !groupType.Equals("LightGroup", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = obj.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? $"Room {prop.Name}" : $"Room {prop.Name}";
            // Skip internal Hue Bridge auto-generated system groups (e.g. hgrp-0000000014)
            if (name.StartsWith("hgrp-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var roomClass = obj.TryGetProperty("class", out var cEl) ? cEl.GetString() ?? groupType : groupType;

            var anyOn = false;
            var allOn = false;
            if (obj.TryGetProperty("state", out var stateEl))
            {
                if (stateEl.TryGetProperty("any_on", out var aEl) && aEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    anyOn = aEl.GetBoolean();
                }

                if (stateEl.TryGetProperty("all_on", out var allEl) && allEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    allOn = allEl.GetBoolean();
                }
            }

            var briPct = 80;
            if (obj.TryGetProperty("action", out var actionEl) &&
                actionEl.TryGetProperty("bri", out var briEl) &&
                briEl.TryGetInt32(out var bri))
            {
                briPct = HueColorConverter.HueBriToPercent(bri);
            }

            var lightIds = new List<string>();
            if (obj.TryGetProperty("lights", out var lightsEl) && lightsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in lightsEl.EnumerateArray())
                {
                    var id = l.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        lightIds.Add(id);
                    }
                }
            }

            list.Add(new HueRoomGroup(prop.Name, name, groupType, roomClass, anyOn, allOn, briPct, lightIds));
        }

        return list.OrderBy(static g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<HueSceneItem> ParseScenes(string json)
    {
        var list = new List<HueSceneItem>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var obj = prop.Value;
            var name = obj.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            var groupId = obj.TryGetProperty("group", out var gEl) ? gEl.GetString() : string.Empty;

            // "recycle" scenes are the Hue app's (and other apps') own temporary scenes, often named by an ID such as
            // "13n3NlxMbQ-8QYch"; the Hue app does not list them either.
            var recycle = obj.TryGetProperty("recycle", out var rEl) && rEl.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(name) || recycle)
            {
                continue;
            }

            list.Add(new HueSceneItem(prop.Name, name, groupId ?? string.Empty));
        }

        return list.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<IReadOnlyList<HueDiscoveredBridge>> DiscoverViaSsdpAsync(CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, HueDiscoveredBridge>(StringComparer.OrdinalIgnoreCase);

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.ReceiveTimeout = 1200;

        var requestBytes = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: ssdp:all\r\n\r\n");

        var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        await udp.SendAsync(requestBytes, requestBytes.Length, multicast);

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(1800);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(remaining);

            try
            {
                var recv = await udp.ReceiveAsync(cts.Token);
                var text = Encoding.ASCII.GetString(recv.Buffer);
                if (!text.Contains("IpBridge", StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains("hue-bridgeid", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ip = recv.RemoteEndPoint.Address.ToString();
                var bridgeId = ExtractHeader(text, "hue-bridgeid") ?? string.Empty;
                found[ip] = new HueDiscoveredBridge(bridgeId, ip, "LAN SSDP");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }

        return found.Values.ToList();
    }

    private static string? ExtractHeader(string raw, string headerName)
    {
        foreach (var line in raw.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
            {
                return line[(headerName.Length + 1)..].Trim();
            }
        }

        return null;
    }

    private static void EnsureConfigured(HuePluginConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.BridgeIp))
        {
            throw new InvalidOperationException("Enter or discover your Philips Hue Bridge IP first.");
        }

        if (string.IsNullOrWhiteSpace(config.ApplicationKey))
        {
            throw new InvalidOperationException("Bridge is not paired yet. Click 'Press Bridge Button & Pair' to link.");
        }
    }

    private (IReadOnlyList<HueRoomGroup>, IReadOnlyList<HueLightState>, IReadOnlyList<HueSceneItem>) GetDemoState()
    {
        lock (_demoLock)
        {
            var lights = _demoLights.Select(l => new HueLightState(
                l.Id,
                l.Name,
                l.TypeName,
                l.ProductName,
                l.IsOn,
                IsReachable: true,
                l.BrightnessPercent,
                l.ColorTempMirek,
                l.SupportsColor,
                l.SupportsColorTemp,
                l.X,
                l.Y,
                HueColorConverter.XyOrCtToHex(l.SupportsColor ? l.X : null, l.SupportsColor ? l.Y : null, l.ColorTempMirek, l.IsOn))).ToList();

            var rooms = _demoRooms.Select(r =>
            {
                var members = _demoLights.Where(l => r.LightIds.Contains(l.Id)).ToList();
                var anyOn = members.Any(m => m.IsOn);
                var allOn = members.Count > 0 && members.All(m => m.IsOn);
                var avgBri = members.Count > 0 ? (int)Math.Round(members.Average(m => m.BrightnessPercent)) : 0;
                return new HueRoomGroup(r.Id, r.Name, r.GroupType, r.RoomClass, anyOn, allOn, avgBri, r.LightIds);
            }).ToList();

            return (rooms, lights, _demoScenes.ToList());
        }
    }

    private void UpdateDemoLight(string lightId, bool? isOn, int? brightnessPercent, int? colorTempMirek, double? x, double? y)
    {
        lock (_demoLock)
        {
            var light = _demoLights.FirstOrDefault(l => l.Id == lightId);
            if (light is null)
            {
                return;
            }

            if (isOn.HasValue)
            {
                light.IsOn = isOn.Value;
            }

            if (brightnessPercent.HasValue)
            {
                if (brightnessPercent.Value <= 0)
                {
                    light.IsOn = false;
                }
                else
                {
                    light.IsOn = isOn ?? true;
                    light.BrightnessPercent = Math.Clamp(brightnessPercent.Value, 1, 100);
                }
            }

            if (x.HasValue && y.HasValue && light.SupportsColor)
            {
                light.X = x.Value;
                light.Y = y.Value;
            }
            else if (colorTempMirek.HasValue && light.SupportsColorTemp)
            {
                light.ColorTempMirek = Math.Clamp(colorTempMirek.Value, 153, 500);
                light.X = null;
                light.Y = null;
            }
        }
    }

    private void UpdateDemoGroup(string groupId, bool? isOn, int? brightnessPercent, int? colorTempMirek, double? x, double? y, string? sceneId)
    {
        lock (_demoLock)
        {
            var targets = string.IsNullOrWhiteSpace(groupId) || groupId == "0"
                ? _demoLights
                : _demoLights.Where(l => _demoRooms.FirstOrDefault(r => r.Id == groupId)?.LightIds.Contains(l.Id) == true).ToList();

            if (!string.IsNullOrWhiteSpace(sceneId))
            {
                var (bri, ct, sx, sy) = sceneId switch
                {
                    "demo-cinema" => (16, (int?)454, (double?)null, (double?)null),
                    "demo-tokyo" => (72, (int?)null, (double?)0.175, (double?)0.135),
                    "demo-savanna" => (64, (int?)null, (double?)0.561, (double?)0.328),
                    "demo-concentrate" => (82, (int?)220, (double?)null, (double?)null),
                    "demo-candle" => (26, (int?)485, (double?)null, (double?)null),
                    _ => (60, (int?)366, (double?)null, (double?)null),
                };

                foreach (var l in targets)
                {
                    l.IsOn = true;
                    l.BrightnessPercent = bri;
                    if (sx.HasValue && sy.HasValue && l.SupportsColor)
                    {
                        l.X = sx;
                        l.Y = sy;
                    }
                    else if (ct.HasValue)
                    {
                        l.ColorTempMirek = ct;
                        l.X = null;
                        l.Y = null;
                    }
                }

                return;
            }

            foreach (var l in targets)
            {
                if (isOn.HasValue)
                {
                    l.IsOn = isOn.Value;
                }

                if (brightnessPercent.HasValue)
                {
                    if (brightnessPercent.Value <= 0)
                    {
                        l.IsOn = false;
                    }
                    else
                    {
                        l.IsOn = isOn ?? true;
                        l.BrightnessPercent = Math.Clamp(brightnessPercent.Value, 1, 100);
                    }
                }

                if (x.HasValue && y.HasValue && l.SupportsColor)
                {
                    l.X = x.Value;
                    l.Y = y.Value;
                }
                else if (colorTempMirek.HasValue && l.SupportsColorTemp)
                {
                    l.ColorTempMirek = Math.Clamp(colorTempMirek.Value, 153, 500);
                    l.X = null;
                    l.Y = null;
                }
            }
        }
    }

    private sealed class DemoLight(
        string id,
        string name,
        string typeName,
        string productName,
        bool isOn,
        int brightnessPercent,
        int? colorTempMirek,
        bool supportsColor,
        bool supportsColorTemp,
        double? x,
        double? y)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string TypeName { get; } = typeName;
        public string ProductName { get; } = productName;
        public bool IsOn { get; set; } = isOn;
        public int BrightnessPercent { get; set; } = brightnessPercent;
        public int? ColorTempMirek { get; set; } = colorTempMirek;
        public bool SupportsColor { get; } = supportsColor;
        public bool SupportsColorTemp { get; } = supportsColorTemp;
        public double? X { get; set; } = x;
        public double? Y { get; set; } = y;
    }

    private sealed record DemoRoom(string Id, string Name, string GroupType, string RoomClass, IReadOnlyList<string> LightIds);
}
