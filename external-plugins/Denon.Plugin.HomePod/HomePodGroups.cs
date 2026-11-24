using Denon.Plugin.AppleTv.Discovery;

namespace Denon.Plugin.HomePod;

/// <summary>A speaker found by discovery: one HomePod, or a stereo pair with its leader.</summary>
internal sealed record DiscoveredSpeaker(
    string Id,
    string Name,
    string Host,
    int AirPlayPort,
    string ModelName,
    string? OsVersion,
    IReadOnlyList<string> Members);

/// <summary>
/// Turns discovered HomePods into speakers. A stereo pair shows up as two HomePods: the member has a persistent
/// group id (AirPlay TXT <c>pgid</c>), and the leader has that id as its group id (<c>gid</c>) and <c>igl=1</c>.
/// Only the pair (<c>pgid</c>) groups HomePods: a short AirPlay multi-room group (same <c>gid</c>, no <c>pgid</c>)
/// does not, so a kitchen HomePod stays its own speaker while it joins the living room for a song.
/// </summary>
internal static class HomePodGroups
{
    public static IReadOnlyList<DiscoveredSpeaker> Build(IEnumerable<AppleDevice> devices)
    {
        var pods = devices
            .Where(d => d.Kind == AppleDeviceKind.HomePod && d.HasAirPlay)
            .Select(d => (Device: d, Txt: d.Service(AppleServiceTypes.AirPlay)!.Txt))
            .ToList();

        string? Txt(IReadOnlyDictionary<string, string> txt, string key) =>
            txt.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        var pairIds = pods.Select(p => Txt(p.Txt, "pgid")).Where(id => id is not null).Select(id => id!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var speakers = new List<DiscoveredSpeaker>();
        var used = new HashSet<AppleDevice>();

        foreach (var pairId in pairIds.Order(StringComparer.OrdinalIgnoreCase))
        {
            var members = pods
                .Where(p => string.Equals(Txt(p.Txt, "pgid"), pairId, StringComparison.OrdinalIgnoreCase) ||
                            (Txt(p.Txt, "pgid") is null && string.Equals(Txt(p.Txt, "gid"), pairId, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.Device.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (members.Count == 0)
            {
                continue;
            }

            var leader = members.FirstOrDefault(p => Txt(p.Txt, "igl") == "1");
            if (leader.Device is null)
            {
                leader = members[0];
            }

            var names = members.Select(m => m.Device.Name).ToList();
            speakers.Add(ToSpeaker(leader.Device, PairName(names), names));
            used.UnionWith(members.Select(m => m.Device));
        }

        foreach (var (device, _) in pods.Where(p => !used.Contains(p.Device)))
        {
            speakers.Add(ToSpeaker(device, device.Name, [device.Name]));
        }

        return speakers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"HomePod Left" + "HomePod Right" → "HomePod Left + Right"; without a shared first word "A + B".</summary>
    public static string PairName(IReadOnlyList<string> names)
    {
        if (names.Count == 1)
        {
            return names[0];
        }

        var words = names.Select(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var shared = 0;
        while (words.All(w => w.Length > shared + 1) && words.All(w => string.Equals(w[shared], words[0][shared], StringComparison.OrdinalIgnoreCase)))
        {
            shared++;
        }

        return shared == 0
            ? string.Join(" + ", names)
            : string.Join(' ', words[0].Take(shared)) + " " + string.Join(" + ", words.Select(w => string.Join(' ', w.Skip(shared))));
    }

    private static DiscoveredSpeaker ToSpeaker(AppleDevice leader, string name, IReadOnlyList<string> members) => new(
        leader.DeviceId ?? leader.Address.ToString(),
        name,
        leader.Address.ToString(),
        leader.Service(AppleServiceTypes.AirPlay)?.Port is > 0 and var port ? port : 7000,
        members.Count > 1 ? $"{leader.ModelName} stereo pair" : leader.ModelName,
        leader.OsVersion,
        members);
}
