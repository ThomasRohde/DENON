namespace Denon.App.Core.Models;

public sealed record ReceiverStateSnapshot
{
    public static ReceiverStateSnapshot Empty { get; } = new();

    public bool IsBusy { get; init; }

    public bool IsConnected { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset? LastSuccessfulRefresh { get; init; }

    public DateTimeOffset? LastAttemptedRefresh { get; init; }

    public ReceiverStatus? Status { get; init; }

    /// <summary>The last acoustic read (Audyssey, dimmer, trims), kept current by the receiver's PS and DIM lines. Null until read.</summary>
    public AcousticStatus? Acoustics { get; init; }

    /// <summary>The HEOS players and multi-room groups, kept current by the HEOS group events. Null until read.</summary>
    public HeosGroupsState? HeosGroups { get; init; }
}
