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

    /// <param name="overlay">
    /// Runs inside the store's lock on the status before it is kept, so a line that came in after the status was read
    /// (and was already patched in) is not lost again. The status kept is returned.
    /// </param>
    public ReceiverStatus UpdateStatus(ReceiverStatus status, Func<ReceiverStatus, ReceiverStatus>? overlay = null)
    {
        var kept = status;
        Update(snapshot =>
        {
            kept = overlay?.Invoke(status) ?? status;
            return snapshot with
            {
                Status = kept,
                IsConnected = true,
                LastError = null,
                LastAttemptedRefresh = status.RetrievedAt,
                LastSuccessfulRefresh = status.RetrievedAt,
            };
        });
        return kept;
    }

    /// <summary>
    /// Applies what the receiver just said by itself (a pushed status or acoustic line) to the kept status. Nothing is
    /// raised when nothing changed (for example the echo of a command the app sent).
    /// </summary>
    public void PatchFromReceiver(Func<ReceiverStatus, ReceiverStatus> status, Func<AcousticStatus, AcousticStatus>? acoustics = null)
    {
        ReceiverStateSnapshot? changed = null;
        lock (_syncRoot)
        {
            var current = _current;
            var newStatus = current.Status is { } known ? status(known) : null;
            var newAcoustics = current.Acoustics is { } a && acoustics is not null ? acoustics(a) : current.Acoustics;
            var statusChanged = newStatus is not null && newStatus != current.Status;
            var acousticsChanged = newAcoustics != current.Acoustics;
            if (statusChanged || acousticsChanged)
            {
                var now = DateTimeOffset.Now;
                _current = current with
                {
                    Status = statusChanged ? newStatus! with { RetrievedAt = now } : current.Status,
                    Acoustics = acousticsChanged ? newAcoustics! with { RetrievedAt = now } : current.Acoustics,
                    LastSuccessfulRefresh = statusChanged ? now : current.LastSuccessfulRefresh,
                };
                changed = _current;
            }
        }

        if (changed is not null)
        {
            StateChanged?.Invoke(changed);
        }
    }

    /// <param name="overlay">Runs inside the store's lock, as for <see cref="UpdateStatus"/>. The acoustics kept are returned.</param>
    public AcousticStatus UpdateAcoustics(AcousticStatus acoustics, Func<AcousticStatus, AcousticStatus>? overlay = null)
    {
        var kept = acoustics;
        Update(snapshot =>
        {
            kept = overlay?.Invoke(acoustics) ?? acoustics;
            return snapshot with { Acoustics = kept };
        });
        return kept;
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
