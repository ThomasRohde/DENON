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
}
