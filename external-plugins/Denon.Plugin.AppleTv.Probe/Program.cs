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

    public sealed record Entry(string Credentials, CompanionIdentity Identity, int Port, DateTimeOffset PairedAt);

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
