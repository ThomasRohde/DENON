using System.Net;

namespace Denon.Plugin.AppleTv.Discovery;

internal enum AppleDeviceKind
{
    Other,
    AppleTv,
    HomePod,
}

/// <summary>DNS-SD service types that Apple TVs (and HomePods) announce.</summary>
internal static class AppleServiceTypes
{
    /// <summary>Companion link (tvOS 13+): pairing, power, apps, keys, and the MRP tunnel on newer tvOS.</summary>
    public const string Companion = "_companion-link._tcp.local";

    /// <summary>AirPlay 2: on tvOS 15+ the MRP (now playing) channel runs inside an encrypted AirPlay session.</summary>
    public const string AirPlay = "_airplay._tcp.local";

    /// <summary>Remote Audio Output Protocol (AirPlay audio); carries the model id in "am".</summary>
    public const string Raop = "_raop._tcp.local";

    /// <summary>Media Remote Protocol as its own service (older tvOS only).</summary>
    public const string MediaRemote = "_mediaremotetv._tcp.local";

    /// <summary>Generic device info (model) that many Apple devices announce.</summary>
    public const string DeviceInfo = "_device-info._tcp.local";

    public static IReadOnlyList<string> All { get; } = [Companion, AirPlay, Raop, MediaRemote, DeviceInfo];
}

/// <summary>One Apple device on the LAN, built from all services it announces at one address.</summary>
internal sealed record AppleDevice(
    string Name,
    IPAddress Address,
    string? ModelId,
    string ModelName,
    AppleDeviceKind Kind,
    string? OsVersion,
    string? DeviceId,
    IReadOnlyList<MdnsService> Services)
{
    public MdnsService? Service(string type) =>
        Services.FirstOrDefault(s => s.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    /// <summary>Companion link is there: pairing, power, keys and apps are possible (milestone 1).</summary>
    public bool HasCompanion => Service(AppleServiceTypes.Companion) is { Port: > 0 };

    /// <summary>AirPlay is there: the now-playing (MRP) tunnel is possible on current tvOS (milestone 2).</summary>
    public bool HasAirPlay => Service(AppleServiceTypes.AirPlay) is { Port: > 0 };
}

internal static class AppleDeviceDiscovery
{
    public static async Task<IReadOnlyList<AppleDevice>> DiscoverAsync(
        TimeSpan duration,
        IReadOnlyList<IPAddress>? unicastHosts,
        CancellationToken cancellationToken)
    {
        var services = await MdnsBrowser.BrowseAsync(AppleServiceTypes.All, duration, unicastHosts, cancellationToken);
        return Group(services);
    }

    /// <summary>Groups services by address and reads name, model, OS version and device id from their TXT records.</summary>
    internal static IReadOnlyList<AppleDevice> Group(IEnumerable<MdnsService> services)
    {
        var devices = new List<AppleDevice>();
        foreach (var group in services.Where(s => s.Address is not null).GroupBy(s => s.Address!.ToString()))
        {
            var list = group.OrderBy(s => s.Type, StringComparer.Ordinal).ToList();
            string? Txt(string type, string key) =>
                list.FirstOrDefault(s => s.Type.Equals(type, StringComparison.OrdinalIgnoreCase))?.Txt.GetValueOrDefault(key) is { Length: > 0 } v ? v : null;

            var modelId = Txt(AppleServiceTypes.AirPlay, "model")
                          ?? Txt(AppleServiceTypes.Raop, "am")
                          ?? Txt(AppleServiceTypes.DeviceInfo, "model")
                          ?? Txt(AppleServiceTypes.Companion, "rpMd")
                          ?? Txt(AppleServiceTypes.MediaRemote, "ModelName");

            var name = NameFrom(list);
            var osVersion = Txt(AppleServiceTypes.AirPlay, "osvers") ?? Txt(AppleServiceTypes.MediaRemote, "SystemBuildVersion");
            var deviceId = Txt(AppleServiceTypes.AirPlay, "deviceid") ?? Txt(AppleServiceTypes.MediaRemote, "UniqueIdentifier");

            devices.Add(new AppleDevice(
                name,
                list[0].Address!,
                modelId,
                AppleModels.FriendlyName(modelId),
                AppleModels.KindOf(modelId),
                osVersion,
                deviceId,
                list));
        }

        return devices.OrderBy(d => d.Kind == AppleDeviceKind.AppleTv ? 0 : 1).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string NameFrom(IReadOnlyList<MdnsService> services)
    {
        foreach (var type in new[] { AppleServiceTypes.AirPlay, AppleServiceTypes.Companion, AppleServiceTypes.MediaRemote, AppleServiceTypes.DeviceInfo })
        {
            if (services.FirstOrDefault(s => s.Type.Equals(type, StringComparison.OrdinalIgnoreCase)) is { } s)
            {
                return s.InstanceLabel;
            }
        }

        // RAOP instances are "<MAC>@<Name>".
        var raop = services.FirstOrDefault(s => s.Type.Equals(AppleServiceTypes.Raop, StringComparison.OrdinalIgnoreCase));
        if (raop is not null)
        {
            var label = raop.InstanceLabel;
            var at = label.IndexOf('@');
            return at >= 0 ? label[(at + 1)..] : label;
        }

        return services[0].InstanceLabel;
    }
}

/// <summary>Apple model identifiers → product names (Apple TV and HomePod).</summary>
internal static class AppleModels
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AppleTV2,1"] = "Apple TV (2nd generation)",
        ["AppleTV3,1"] = "Apple TV (3rd generation)",
        ["AppleTV3,2"] = "Apple TV (3rd generation, Rev A)",
        ["AppleTV5,3"] = "Apple TV HD",
        ["AppleTV6,2"] = "Apple TV 4K (1st generation)",
        ["AppleTV11,1"] = "Apple TV 4K (2nd generation)",
        ["AppleTV14,1"] = "Apple TV 4K (3rd generation)",
        ["AudioAccessory1,1"] = "HomePod",
        ["AudioAccessory1,2"] = "HomePod",
        ["AudioAccessory5,1"] = "HomePod mini",
        ["AudioAccessory6,1"] = "HomePod (2nd generation)",
    };

    public static string FriendlyName(string? modelId) =>
        modelId is null ? "Unknown Apple device"
        : Names.TryGetValue(modelId, out var name) ? name
        : modelId.StartsWith("AppleTV", StringComparison.OrdinalIgnoreCase) ? $"Apple TV ({modelId})"
        : modelId;

    public static AppleDeviceKind KindOf(string? modelId) =>
        modelId is null ? AppleDeviceKind.Other
        : modelId.StartsWith("AppleTV", StringComparison.OrdinalIgnoreCase) || modelId.Equals("Apple TV", StringComparison.OrdinalIgnoreCase) ? AppleDeviceKind.AppleTv
        : modelId.StartsWith("AudioAccessory", StringComparison.OrdinalIgnoreCase) ? AppleDeviceKind.HomePod
        : AppleDeviceKind.Other;
}
