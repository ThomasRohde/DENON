using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

public sealed class ReceiverStateStore
{
    private readonly object _syncRoot = new();
    private ReceiverStateSnapshot _current = ReceiverStateSnapshot.Empty;

    public event Action<ReceiverStateSnapshot>? StateChanged;

    public ReceiverStateSnapshot Snapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return _current;
            }
        }
    }

    public void SetBusy(bool isBusy)
    {
        Update(snapshot => snapshot with
        {
            IsBusy = isBusy,
            LastAttemptedRefresh = DateTimeOffset.Now,
        });
    }

    public void UpdateStatus(ReceiverStatus status)
    {
        Update(snapshot => snapshot with
        {
            Status = status,
            IsConnected = true,
            LastError = null,
            LastAttemptedRefresh = status.RetrievedAt,
            LastSuccessfulRefresh = status.RetrievedAt,
        });
    }

    public void UpdateError(string message)
    {
        Update(snapshot => snapshot with
        {
            IsConnected = false,
            LastError = message,
            LastAttemptedRefresh = DateTimeOffset.Now,
        });
    }

    public void PatchLiveTelemetry(int? volume = null, bool? isMuted = null, string? playState = null)
    {
        Update(snapshot =>
        {
            if (snapshot.Status is null)
            {
                return snapshot;
            }

            var now = DateTimeOffset.Now;
            var updatedStatus = snapshot.Status with
            {
                Volume = volume ?? snapshot.Status.Volume,
                VolumeExact = volume.HasValue ? volume.Value : snapshot.Status.VolumeExact,
                IsMuted = isMuted ?? snapshot.Status.IsMuted,
                PlayState = !string.IsNullOrWhiteSpace(playState) ? playState : snapshot.Status.PlayState,
                RetrievedAt = now,
            };

            return snapshot with
            {
                Status = updatedStatus,
                IsConnected = true,
                LastSuccessfulRefresh = now,
            };
        });
    }

    private void Update(Func<ReceiverStateSnapshot, ReceiverStateSnapshot> updater)
    {
        ReceiverStateSnapshot snapshot;
        lock (_syncRoot)
        {
            _current = updater(_current);
            snapshot = _current;
        }

        StateChanged?.Invoke(snapshot);
    }
}
