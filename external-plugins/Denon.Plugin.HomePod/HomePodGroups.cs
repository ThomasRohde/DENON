using Denon.Plugin.AppleTv.Discovery;

namespace Denon.Plugin.HomePod;

/// <summary>
/// A speaker found by discovery: one HomePod, or a stereo pair with its leader. <see cref="Members"/> are the
/// HomePods' names (for display); <see cref="MemberIds"/> their device ids, which say which HomePods it is.
/// </summary>
internal sealed record DiscoveredSpeaker(
    string Id,
    string Name,
    string Host,
    int AirPlayPort,
    string ModelName,
    string? OsVersion,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> MemberIds);

/// <summary>
/// The saved speakers after a discovery pass, and what the plugin must do about it.
/// <see cref="Restart"/> holds each speaker whose client must restart, with the id it had before: a stereo pair's
/// leader can switch (and its id with it), and the old client, listen-log slot and dashboard key use the old id.
/// <see cref="Removed"/> holds speakers now part of another one (two single HomePods that became a pair).
/// </summary>
internal sealed record HomePodMerge(
    List<HomePodSpeakerConfig> Speakers,
    int Added,
    IReadOnlyList<(string OldId, HomePodSpeakerConfig Speaker)> Restart,
    IReadOnlyList<HomePodSpeakerConfig> Removed);

/// <summary>
/// Turns discovered HomePods into speakers. A stereo pair shows up as two HomePods: the member has a persistent
/// group id (AirPlay TXT <c>pgid</c>), and the leader has that id as its group id (<c>gid</c>) and <c>igl=1</c>.
/// Only the pair (<c>pgid</c>) groups HomePods: a short AirPlay multi-room group (same <c>gid</c>, no <c>pgid</c>)
/// does not, so a kitchen HomePod stays its own speaker while it joins the living room for a song.
/// </summary>
internal static class HomePodGroups
{
    /// <summary>
    /// How many discovery passes in a row a known stereo pair may answer in part before discovery's view wins
    /// (discovery runs every 10 minutes, so about half an hour).
    /// </summary>
    public const int MaxPartialPasses = 3;

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
            speakers.Add(ToSpeaker(leader.Device, PairName(names), names, members.Select(m => DeviceKey(m.Device)).ToList()));
            used.UnionWith(members.Select(m => m.Device));
        }

        foreach (var (device, _) in pods.Where(p => !used.Contains(p.Device)))
        {
            speakers.Add(ToSpeaker(device, device.Name, [device.Name], [DeviceKey(device)]));
        }

        return speakers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Merges discovered speakers into the saved ones. A speaker is found by its leader's id, else by a shared
    /// member HomePod (the pair's leader can change). Each saved speaker matches one discovered speaker at most.
    /// Members are compared by device id, never by name: two HomePods may have the same name.
    /// A saved stereo pair of which only one HomePod answered, while the other answered nowhere in this pass (a
    /// missed mDNS reply), stays as saved: its leader keeps the link, and only the leader's address is updated. After
    /// <see cref="MaxPartialPasses"/> such passes in a row, discovery's view wins (the pair was split or a HomePod
    /// is gone). A HomePod that answers as part of another speaker means the pair really changed.
    /// </summary>
    public static HomePodMerge Merge(IEnumerable<HomePodSpeakerConfig> saved, IReadOnlyList<DiscoveredSpeaker> found, DateTimeOffset now)
    {
        var speakers = saved.ToList();
        var matched = new HashSet<HomePodSpeakerConfig>();
        var restart = new List<(string OldId, HomePodSpeakerConfig Speaker)>();
        var removed = new List<HomePodSpeakerConfig>();
        var answered = found.SelectMany(f => f.MemberIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var speaker in found)
        {
            var existing = speakers.FirstOrDefault(s => !matched.Contains(s) && s.Id.Equals(speaker.Id, StringComparison.OrdinalIgnoreCase)) ??
                           speakers.FirstOrDefault(s => !matched.Contains(s) && Keys(s).Intersect(speaker.MemberIds, StringComparer.OrdinalIgnoreCase).Any());
            var isNew = existing is null;
            if (existing is null)
            {
                existing = new HomePodSpeakerConfig { Id = speaker.Id };
                speakers.Add(existing);
                added++;
            }

            matched.Add(existing);
            if (!isNew && IsPartialReply(existing, speaker, answered) && ++existing.PartialPasses < MaxPartialPasses)
            {
                KeepPair(existing, speaker, now, restart);
                continue;
            }

            existing.PartialPasses = 0;
            var oldId = existing.Id; // before the update: the running client and the listen log use it
            var moved = isNew || !string.Equals(oldId, speaker.Id, StringComparison.Ordinal) ||
                        existing.Host != speaker.Host || existing.AirPlayPort != speaker.AirPlayPort;
            existing.Id = speaker.Id;
            existing.Name = speaker.Name;
            existing.Host = speaker.Host;
            existing.AirPlayPort = speaker.AirPlayPort;
            existing.ModelName = speaker.ModelName;
            existing.OsVersion = speaker.OsVersion;
            existing.Members = speaker.Members.ToList();
            existing.MemberIds = speaker.MemberIds.ToList();
            existing.LastSeenUtc = now;
            if (moved)
            {
                restart.Add((oldId, existing));
            }

            // A saved speaker whose HomePods all belong to this one now (they became a stereo pair) goes;
            // otherwise it would open a second link and count the same music twice.
            foreach (var subsumed in speakers.Where(s => s != existing && !matched.Contains(s) &&
                                                         Keys(s).All(m => speaker.MemberIds.Contains(m, StringComparer.OrdinalIgnoreCase))).ToList())
            {
                speakers.Remove(subsumed);
                removed.Add(subsumed);
            }
        }

        var merged = speakers
            .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new HomePodMerge(merged, added, restart, removed);
    }

    /// <summary>
    /// True when <paramref name="speaker"/> is part of the saved pair <paramref name="existing"/> and the pair's
    /// other HomePods answered nowhere in this pass.
    /// </summary>
    private static bool IsPartialReply(HomePodSpeakerConfig existing, DiscoveredSpeaker speaker, HashSet<string> answered)
    {
        var keys = Keys(existing).ToList();
        if (!existing.IsStereoPair || !speaker.MemberIds.All(m => keys.Contains(m, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        var missing = keys.Except(speaker.MemberIds, StringComparer.OrdinalIgnoreCase).ToList();
        return missing.Count > 0 && !missing.Any(answered.Contains);
    }

    /// <summary>
    /// A pair that answered in part keeps its name, members and leader. When the leader answered, its address
    /// is updated (a new address restarts the link); when only another member answered, nothing changes.
    /// </summary>
    private static void KeepPair(HomePodSpeakerConfig existing, DiscoveredSpeaker speaker, DateTimeOffset now,
        List<(string OldId, HomePodSpeakerConfig Speaker)> restart)
    {
        if (!existing.Id.Equals(speaker.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var moved = existing.Host != speaker.Host || existing.AirPlayPort != speaker.AirPlayPort;
        existing.Host = speaker.Host;
        existing.AirPlayPort = speaker.AirPlayPort;
        existing.OsVersion = speaker.OsVersion;
        existing.LastSeenUtc = now;
        if (moved)
        {
            restart.Add((existing.Id, existing));
        }
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

    /// <summary>
    /// The device ids of a saved speaker's HomePods. Its own id is always one of them (the leader's device id), so a
    /// speaker saved before member ids were stored still matches its pair.
    /// </summary>
    private static IEnumerable<string> Keys(HomePodSpeakerConfig speaker) =>
        speaker.MemberIds.Append(speaker.Id).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>A HomePod's stable key: its device id, else its address.</summary>
    private static string DeviceKey(AppleDevice device) => device.DeviceId ?? device.Address.ToString();

    private static DiscoveredSpeaker ToSpeaker(AppleDevice leader, string name, IReadOnlyList<string> members, IReadOnlyList<string> memberIds) => new(
        DeviceKey(leader),
        name,
        leader.Address.ToString(),
        leader.Service(AppleServiceTypes.AirPlay)?.Port is > 0 and var port ? port : 7000,
        members.Count > 1 ? $"{leader.ModelName} stereo pair" : leader.ModelName,
        leader.OsVersion,
        members,
        memberIds);
}
