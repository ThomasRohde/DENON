using System.Globalization;
using System.Net;
using Denon.Plugin.AppleTv.Discovery;

// atvprobe — developer probe for the Apple TV plugin.
//   atvprobe discover [--seconds N] [--host IP ...] [--all] [--txt]
// Read-only: it only sends DNS-SD queries. It never pairs, wakes or controls a device.

var argList = args.ToList();
var command = argList.FirstOrDefault() ?? "discover";
if (command != "discover")
{
    Console.Error.WriteLine("Usage: atvprobe discover [--seconds N] [--host IP ...] [--all] [--txt]");
    return 2;
}

var seconds = 4.0;
var hosts = new List<IPAddress>();
var showAll = argList.Contains("--all");
var showTxt = argList.Contains("--txt");
for (var i = 1; i < argList.Count; i++)
{
    if (argList[i] == "--seconds" && i + 1 < argList.Count)
    {
        seconds = double.Parse(argList[++i], CultureInfo.InvariantCulture);
    }
    else if (argList[i] == "--host" && i + 1 < argList.Count && IPAddress.TryParse(argList[++i], out var ip))
    {
        hosts.Add(ip);
    }
}

Console.WriteLine($"Browsing {string.Join(", ", AppleServiceTypes.All)} for {seconds:0.#} s" +
                  (hosts.Count > 0 ? $" (also unicast to {string.Join(", ", hosts)})" : string.Empty) + " ...");
var devices = await AppleDeviceDiscovery.DiscoverAsync(TimeSpan.FromSeconds(seconds), hosts, CancellationToken.None);

var shown = devices.Where(d => showAll || d.Kind == AppleDeviceKind.AppleTv).ToList();
Console.WriteLine($"Found {devices.Count} Apple device(s): {devices.Count(d => d.Kind == AppleDeviceKind.AppleTv)} Apple TV, " +
                  $"{devices.Count(d => d.Kind == AppleDeviceKind.HomePod)} HomePod, {devices.Count(d => d.Kind == AppleDeviceKind.Other)} other.");

foreach (var d in devices.Where(d => !shown.Contains(d)))
{
    Console.WriteLine($"  - {d.Name} ({d.ModelName}) at {d.Address}");
}

foreach (var d in shown)
{
    Console.WriteLine();
    Console.WriteLine($"== {d.Name} ==");
    Console.WriteLine($"   Address   : {d.Address}");
    Console.WriteLine($"   Model     : {d.ModelName}{(d.ModelId is null ? "" : $" [{d.ModelId}]")}");
    Console.WriteLine($"   OS        : {d.OsVersion ?? "?"}");
    Console.WriteLine($"   Device id : {d.DeviceId ?? "?"}");
    Console.WriteLine($"   Companion : {(d.HasCompanion ? "yes" : "no")}   AirPlay: {(d.HasAirPlay ? "yes" : "no")}");
    foreach (var s in d.Services)
    {
        Console.WriteLine($"   {s.Type,-28} port {s.Port,-5} \"{s.InstanceLabel}\"");
        if (showTxt)
        {
            foreach (var kv in s.Txt.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                // Public keys are long; shorten them for reading.
                var value = kv.Value.Length > 72 ? kv.Value[..72] + "…" : kv.Value;
                Console.WriteLine($"        {kv.Key} = {value}");
            }
        }
    }
}

return devices.Any(d => d.Kind == AppleDeviceKind.AppleTv) ? 0 : 1;
