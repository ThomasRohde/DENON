using System.Globalization;
using System.Net;
using System.Text.Json;
using Denon.Plugin.AppleTv.Discovery;
using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Hap;

// atvprobe — developer probe for the Apple TV plugin.
//
// Read-only:
//   atvprobe discover [--seconds N] [--host IP ...] [--all] [--txt]
//   atvprobe status   --host IP          power state (pair-verify + encrypted session)
//   atvprobe apps     --host IP          launchable apps
//   atvprobe watch    --host IP [--seconds N]   print events
//   atvprobe nowplaying --host IP [--seconds N] now playing over the AirPlay MRP tunnel
//   atvprobe airplay-pair --host IP [--pin-file PATH]   separate AirPlay pairing (shows a PIN)
// Pairing (the Apple TV shows a PIN):
//   atvprobe pair     --host IP [--name NAME] [--pin-file PATH]
// Changes the Apple TV (ask the owner first):
//   atvprobe key <up|down|left|right|select|menu|home|playpause|...> --host IP
//   atvprobe power <on|off> --host IP
//   atvprobe launch <bundle-id> --host IP

var argList = args.ToList();
var command = argList.FirstOrDefault() ?? "discover";
string? Option(string name) => argList.IndexOf(name) is var i and >= 0 && i + 1 < argList.Count ? argList[i + 1] : null;
var host = Option("--host");
var seconds = double.Parse(Option("--seconds") ?? (command == "watch" ? "30" : "4"), CultureInfo.InvariantCulture);

try
{
    switch (command)
    {
        case "discover":
            return await DiscoverAsync(argList, seconds);
        case "pair":
            return await PairAsync(RequireHost(host), Option("--name") ?? "Denon Desktop", Option("--pin-file"));
        case "status":
            return await WithClientAsync(RequireHost(host), async (client, ct) =>
            {
                Console.WriteLine($"Power state: {await client.FetchAttentionStateAsync(ct)}");
                return 0;
            });
        case "apps":
            return await WithClientAsync(RequireHost(host), async (client, ct) =>
            {
                var apps = await client.FetchAppsAsync(ct);
                Console.WriteLine($"{apps.Count} launchable app(s):");
                foreach (var (id, name) in apps.OrderBy(a => a.Value, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  {name,-28} {id}");
                }

                return 0;
            });
        case "watch":
            return await WithClientAsync(RequireHost(host), async (client, ct) =>
            {
                client.EventReceived += (name, content) =>
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} event {name}: {Describe(content)}");
                await client.SubscribeAsync("TVSystemStatus", ct);
                Console.WriteLine($"Power state now: {await client.FetchAttentionStateAsync(ct)}. Watching events for {seconds:0} s ...");
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                return 0;
            });
        case "nowplaying":
            return await NowPlayingAsync(RequireHost(host), seconds);
        case "airplay-pair":
            return await AirPlayPairAsync(RequireHost(host), Option("--name") ?? "Denon Desktop", Option("--pin-file"));
        case "key":
            var keyName = argList.ElementAtOrDefault(1) ?? "";
            if (!Enum.TryParse<HidCommand>(keyName, ignoreCase: true, out var key) || keyName.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Unknown key. Use one of: {string.Join(", ", Enum.GetNames<HidCommand>())}");
                return 2;
            }

            return await WithClientAsync(RequireHost(host), async (client, ct) =>
            {
                await client.PressAsync(key, ct);
                Console.WriteLine($"Pressed {key}.");
                return 0;
            });
        case "power":
            var on = (argList.ElementAtOrDefault(1) ?? "").Equals("on", StringComparison.OrdinalIgnoreCase);
            var off = (argList.ElementAtOrDefault(1) ?? "").Equals("off", StringComparison.OrdinalIgnoreCase);
            if (!on && !off)
            {
                Console.Error.WriteLine("Usage: atvprobe power <on|off> --host IP");
                return 2;
            }

            return await WithClientAsync(RequireHost(host), async (client, ct) =>
            {
                await client.PressAsync(on ? HidCommand.Wake : HidCommand.Sleep, ct);
                await Task.Delay(1500, ct);
                Console.WriteLine($"Sent {(on ? "Wake" : "Sleep")}. Power state now: {await client.FetchAttentionStateAsync(ct)}");
                return 0;
            });
        case "launch":
            var bundle = argList.ElementAtOrDefault(1);
            if (string.IsNullOrWhiteSpace(bundle) || bundle.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Usage: atvprobe launch <bundle-id> --host IP");
                return 2;
            }

            return await WithClientAsync(RequireHost(host), async (client, ct) =>
            {
                await client.LaunchAppAsync(bundle, ct);
                Console.WriteLine($"Launched {bundle}.");
                return 0;
            });
        default:
            Console.Error.WriteLine("Commands: discover, pair, status, apps, watch, key, power, launch.");
            return 2;
    }
}
catch (HapPairingException ex)
{
    Console.Error.WriteLine($"Pairing error: {ex.Message}");
    return 3;
}
catch (CompanionProtocolException ex)
{
    Console.Error.WriteLine($"Companion error: {ex.Message}");
    return 4;
}
catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException or OperationCanceledException)
{
    Console.Error.WriteLine($"Connection error: {ex.Message}");
    return 5;
}

static string RequireHost(string? host) =>
    host is not null && IPAddress.TryParse(host, out _) ? host : throw new ArgumentException("Give --host <ip address>.");

static async Task<int> DiscoverAsync(List<string> argList, double seconds)
{
    var hosts = new List<IPAddress>();
    for (var i = 1; i < argList.Count; i++)
    {
        if (argList[i] == "--host" && i + 1 < argList.Count && IPAddress.TryParse(argList[++i], out var ip))
        {
            hosts.Add(ip);
        }
    }

    var showAll = argList.Contains("--all");
    var showTxt = argList.Contains("--txt");
    Console.WriteLine($"Browsing {string.Join(", ", AppleServiceTypes.All)} for {seconds:0.#} s ...");
    var devices = await AppleDeviceDiscovery.DiscoverAsync(TimeSpan.FromSeconds(seconds), hosts, CancellationToken.None);
    Console.WriteLine($"Found {devices.Count} Apple device(s): {devices.Count(d => d.Kind == AppleDeviceKind.AppleTv)} Apple TV, " +
                      $"{devices.Count(d => d.Kind == AppleDeviceKind.HomePod)} HomePod, {devices.Count(d => d.Kind == AppleDeviceKind.Other)} other.");

    foreach (var d in devices.Where(d => showAll || d.Kind == AppleDeviceKind.AppleTv))
    {
        Console.WriteLine();
        Console.WriteLine($"== {d.Name} ==  {d.Address}  {d.ModelName}  OS {d.OsVersion ?? "?"}");
        foreach (var s in d.Services)
        {
            Console.WriteLine($"   {s.Type,-28} port {s.Port,-5} \"{s.InstanceLabel}\"");
            if (showTxt)
            {
                foreach (var kv in s.Txt.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"        {kv.Key} = {(kv.Value.Length > 72 ? kv.Value[..72] + "…" : kv.Value)}");
                }
            }
        }
    }

    return devices.Any(d => d.Kind == AppleDeviceKind.AppleTv) ? 0 : 1;
}

static async Task<int> PairAsync(string host, string name, string? pinFile)
{
    var port = await CompanionPortAsync(host);
    Console.WriteLine($"Pairing with {host}:{port} as \"{name}\". The Apple TV now shows a PIN ...");
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    await using var pairing = await CompanionPairing.StartAsync(host, port, name, cts.Token);

    string? pin;
    if (pinFile is not null)
    {
        Console.WriteLine($"Waiting for the PIN in {pinFile} ...");
        while (!File.Exists(pinFile))
        {
            await Task.Delay(500, cts.Token);
        }

        await Task.Delay(200, cts.Token);
        pin = (await File.ReadAllTextAsync(pinFile, cts.Token)).Trim();
        File.Delete(pinFile);
    }
    else
    {
        Console.Write("PIN shown on the Apple TV: ");
        pin = Console.ReadLine()?.Trim();
    }

    if (string.IsNullOrWhiteSpace(pin) || !pin.All(char.IsDigit))
    {
        Console.Error.WriteLine("No valid PIN given; pairing cancelled.");
        return 2;
    }

    var credentials = await pairing.FinishAsync(pin, cts.Token);
    var identity = CompanionIdentity.Create(name);
    ProbeStore.Save(host, new ProbeStore.Entry(credentials.ToString(), identity, port, DateTimeOffset.Now));
    Console.WriteLine($"Paired. {credentials.ToSafeString()}");
    Console.WriteLine($"Apple TV signature (M6): {(pairing.AccessorySignatureVerified == true ? "verified" : "NOT verified (noted; pairing kept)")}");
    Console.WriteLine($"Credentials saved to {ProbeStore.FilePath} (private key: keep this file private).");

    // Prove the credentials: pair-verify + encrypted session + a read-only request.
    await using var client = await CompanionClient.ConnectAsync(host, port, credentials, identity, cts.Token);
    Console.WriteLine($"Verified session OK. Power state: {await client.FetchAttentionStateAsync(cts.Token)}");
    return 0;
}

static async Task<int> NowPlayingAsync(string host, double seconds)
{
    var entry = ProbeStore.Load(host) ?? throw new HapPairingException($"No pairing for {host}. Run: atvprobe pair --host {host}");
    var port = await ServicePortAsync(host, AppleServiceTypes.AirPlay, 7000);
    var useAirPlayCredentials = entry.AirPlayCredentials is not null;
    var credentials = HapCredentials.Parse(entry.AirPlayCredentials ?? entry.Credentials);
    Console.WriteLine($"Opening the AirPlay remote-control tunnel to {host}:{port} with the {(useAirPlayCredentials ? "AirPlay" : "Companion")} pairing ...");

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 30));
    Denon.Plugin.AppleTv.Protocol.Mrp.MrpClient client;
    try
    {
        client = await Denon.Plugin.AppleTv.Protocol.Mrp.MrpClient.ConnectAsync(host, port, credentials, entry.Identity, cts.Token,
            Environment.GetCommandLineArgs().Contains("--dump") ? m => Console.WriteLine($"    {DateTime.Now:HH:mm:ss.f} raw {Dump(m)}") : null);
    }
    catch (Exception ex) when (!useAirPlayCredentials && ex is HapPairingException or Denon.Plugin.AppleTv.Protocol.AirPlay.AirPlayHttpException)
    {
        Console.Error.WriteLine($"The Apple TV did not accept the Companion pairing on AirPlay: {ex.Message}");
        Console.Error.WriteLine($"AirPlay needs its own pairing: atvprobe airplay-pair --host {host}");
        return 6;
    }

    await using (client)
    {
        var info = client.DeviceInfo;
        Console.WriteLine($"MRP tunnel open. Device: {info?.String(2)} ({info?.String(39) ?? info?.String(3)}, build {info?.String(4)}).");

        client.NowPlayingChanged += now => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {Format(now)}");

        client.Closed += ex => Console.WriteLine($"{DateTime.Now:HH:mm:ss} SESSION CLOSED: {ex?.GetType().Name}: {ex?.Message}");
        Console.WriteLine($"Now: {Format(client.NowPlaying.Current)}");
        Console.WriteLine($"Watching for {seconds:0} s ...");
        await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
        Console.WriteLine($"Final: {Format(client.NowPlaying.Current)}");
        Console.WriteLine($"MRP message types seen: {string.Join(", ", client.SeenTypes)}");
    }

    return 0;

    static string Path(Denon.Plugin.AppleTv.Protocol.ProtoReader? p) =>
        p is null ? "-" : $"{p.Message(2)?.String(2) ?? "?"}/{p.Message(3)?.String(1) ?? "?"}";

    static string Dump(Denon.Plugin.AppleTv.Protocol.ProtoReader m)
    {
        var type = (int)(m.Uint(1) ?? 0);
        switch (type)
        {
            case 4:
                var s = m.Message(9);
                if (s is null) return "SET_STATE (empty)";
                var q = s.Message(3);
                var item = q?.Messages(2).FirstOrDefault();
                var md = item?.Message(2);
                var info = s.Message(1);
                return $"SET_STATE path={Path(s.Message(9))} state={s.Uint(6)?.ToString() ?? "-"} ts={s.Double(11)?.ToString("0.0") ?? "-"} " +
                       $"info[title={info?.String(9) ?? "-"} rate={info?.Float(5)?.ToString() ?? "-"} el={info?.Double(4)?.ToString("0") ?? "-"} dur={info?.Double(3)?.ToString("0") ?? "-"}] " +
                       $"queue[loc={q?.Int(1)?.ToString() ?? "-"} n={q?.Messages(2).Count() ?? 0} id={item?.String(1) ?? "-"} title={md?.String(1) ?? "-"} rate={md?.Float(39)?.ToString() ?? "-"} el={md?.Double(35)?.ToString("0") ?? "-"} dur={md?.Double(14)?.ToString("0") ?? "-"} playing={md?.Bool(27)?.ToString() ?? "-"}] cmds={(s.Has(2) ? "yes" : "-")}";
            case 46:
                var c = m.Message(50)?.Message(1);
                return $"SET_NOW_PLAYING_CLIENT {c?.String(2)} \"{c?.String(7)}\"";
            case 47:
                return $"SET_NOW_PLAYING_PLAYER {Path(m.Message(51)?.Message(1))}";
            case 56:
                var u = m.Message(60);
                var ui = u?.Messages(1).FirstOrDefault();
                var um = ui?.Message(2);
                return $"UPDATE_CONTENT_ITEM path={Path(u?.Message(2))} id={ui?.String(1) ?? "-"} title={um?.String(1) ?? "-"} rate={um?.Float(39)?.ToString() ?? "-"} el={um?.Double(35)?.ToString("0") ?? "-"} dur={um?.Double(14)?.ToString("0") ?? "-"}";
            default:
                return $"type {type}";
        }
    }

    static string Format(Denon.Plugin.AppleTv.Protocol.Mrp.NowPlayingSnapshot n) =>
        n.AppBundleId is null && n.Title is null
            ? "nothing reported yet"
            : $"[{n.State}] {n.AppName} ({n.AppBundleId}) — {n.Title ?? "(no title)"}" +
              (n.SeriesName is not null && n.SeriesName != n.Title ? $" / {n.SeriesName}" : "") +
              (n.Season is not null ? $" S{n.Season}E{n.Episode}" : "") +
              (n.Artist is not null ? $" · {n.Artist}" : "") +
              (n.DurationSeconds is { } d ? $" · {TimeSpan.FromSeconds(n.ElapsedSeconds ?? 0):h\\:mm\\:ss}/{TimeSpan.FromSeconds(d):h\\:mm\\:ss}" : "") +
              (n.ArtworkAvailable ? " · artwork" : "");
}

static async Task<int> AirPlayPairAsync(string host, string name, string? pinFile)
{
    var entry = ProbeStore.Load(host) ?? throw new HapPairingException($"Pair Companion first: atvprobe pair --host {host}");
    var port = await ServicePortAsync(host, AppleServiceTypes.AirPlay, 7000);
    Console.WriteLine($"AirPlay pairing with {host}:{port} as \"{name}\". The Apple TV now shows a PIN ...");
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    await using var connection = await Denon.Plugin.AppleTv.Protocol.AirPlay.AirPlayConnection.ConnectAsync(host, port, cts.Token);
    var setup = await Denon.Plugin.AppleTv.Protocol.AirPlay.AirPlayAuth.StartPairingAsync(connection, name, cts.Token);

    var pin = await ReadPinAsync(pinFile, cts.Token);
    if (pin is null)
    {
        return 2;
    }

    var credentials = await Denon.Plugin.AppleTv.Protocol.AirPlay.AirPlayAuth.FinishPairingAsync(connection, setup, pin, cts.Token);
    ProbeStore.Save(host, entry with { AirPlayCredentials = credentials.ToString() });
    Console.WriteLine($"AirPlay paired. {credentials.ToSafeString()}. Signature (M6): {(setup.AccessorySignatureVerified == true ? "verified" : "NOT verified")}");
    return 0;
}

static async Task<string?> ReadPinAsync(string? pinFile, CancellationToken ct)
{
    string? pin;
    if (pinFile is not null)
    {
        Console.WriteLine($"Waiting for the PIN in {pinFile} ...");
        while (!File.Exists(pinFile))
        {
            await Task.Delay(500, ct);
        }

        await Task.Delay(200, ct);
        pin = (await File.ReadAllTextAsync(pinFile, ct)).Trim();
        File.Delete(pinFile);
    }
    else
    {
        Console.Write("PIN shown on the Apple TV: ");
        pin = Console.ReadLine()?.Trim();
    }

    if (string.IsNullOrWhiteSpace(pin) || !pin.All(char.IsDigit))
    {
        Console.Error.WriteLine("No valid PIN given; pairing cancelled.");
        return null;
    }

    return pin;
}

static async Task<int> ServicePortAsync(string host, string serviceType, int fallback)
{
    var devices = await AppleDeviceDiscovery.DiscoverAsync(TimeSpan.FromSeconds(2), [IPAddress.Parse(host)], CancellationToken.None);
    var port = devices.FirstOrDefault(d => d.Address.ToString() == host)?.Service(serviceType)?.Port ?? 0;
    return port > 0 ? port : fallback;
}

static async Task<int> WithClientAsync(string host, Func<CompanionClient, CancellationToken, Task<int>> action)
{
    var entry = ProbeStore.Load(host) ?? throw new HapPairingException($"No pairing for {host}. Run: atvprobe pair --host {host}");
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await using var client = await CompanionClient.ConnectAsync(host, entry.Port, HapCredentials.Parse(entry.Credentials), entry.Identity, cts.Token);
    return await action(client, cts.Token);
}

static async Task<int> CompanionPortAsync(string host)
{
    var devices = await AppleDeviceDiscovery.DiscoverAsync(TimeSpan.FromSeconds(2), [IPAddress.Parse(host)], CancellationToken.None);
    var port = devices.FirstOrDefault(d => d.Address.ToString() == host)?.Service(AppleServiceTypes.Companion)?.Port ?? 0;
    return port > 0 ? port : 49153;
}

static string Describe(Dictionary<object, object?> content) =>
    string.Join(", ", content.Select(kv => $"{kv.Key}={(kv.Value is byte[] b ? $"<{b.Length} bytes>" : kv.Value is System.Collections.IDictionary or System.Collections.IList ? "{…}" : kv.Value)}"));

/// <summary>Pairings for the probe, outside the repository (they hold a private key).</summary>
internal static class ProbeStore
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DenonDesktop", "appletv-probe", "pairings.json");

    public sealed record Entry(string Credentials, CompanionIdentity Identity, int Port, DateTimeOffset PairedAt, string? AirPlayCredentials = null);

    public static Entry? Load(string host) => ReadAll().GetValueOrDefault(host);

    public static void Save(string host, Entry entry)
    {
        var all = ReadAll();
        all[host] = entry;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, Entry> ReadAll() =>
        File.Exists(FilePath)
            ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? []
            : [];
}
