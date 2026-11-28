namespace Denon.App.Core.Models;

/// <summary>One player in a HEOS group. The leader plays the source; the members play what the leader plays.</summary>
public sealed record HeosGroupMember(int PlayerId, string Name, bool IsLeader);

/// <summary>
/// A HEOS multi-room group (heos://group/get_groups). <see cref="Volume"/> and <see cref="IsMuted"/> are the group's
/// own values (0–100 on the HEOS scale), null until the speaker reported them.
/// </summary>
public sealed record HeosGroup(int GroupId, string Name, IReadOnlyList<HeosGroupMember> Members, int? Volume = null, bool? IsMuted = null)
{
    public HeosGroupMember? Leader => Members.FirstOrDefault(static m => m.IsLeader) ?? Members.FirstOrDefault();

    public bool Contains(int playerId) => Members.Any(m => m.PlayerId == playerId);

    /// <summary>Value equality on the members too (a record compares a list by reference).</summary>
    public bool Equals(HeosGroup? other) =>
        other is not null &&
        GroupId == other.GroupId &&
        string.Equals(Name, other.Name, StringComparison.Ordinal) &&
        Volume == other.Volume &&
        IsMuted == other.IsMuted &&
        Members.SequenceEqual(other.Members);

    public override int GetHashCode() => HashCode.Combine(GroupId, Name, Volume, IsMuted, Members.Count);
}

/// <summary>The HEOS players on the network and the groups they form, as last read or pushed.</summary>
public sealed record HeosGroupsState(IReadOnlyList<ReceiverPlayerInfo> Players, IReadOnlyList<HeosGroup> Groups, DateTimeOffset RetrievedAt)
{
    public static HeosGroupsState Empty { get; } = new([], [], DateTimeOffset.MinValue);

    /// <summary>The group <paramref name="playerId"/> is in, or null when it plays alone.</summary>
    public HeosGroup? GroupOf(int playerId) => Groups.FirstOrDefault(g => g.Contains(playerId));

    public bool Equals(HeosGroupsState? other) =>
        other is not null &&
        Players.SequenceEqual(other.Players) &&
        Groups.SequenceEqual(other.Groups);

    public override int GetHashCode() => HashCode.Combine(Players.Count, Groups.Count);
}
