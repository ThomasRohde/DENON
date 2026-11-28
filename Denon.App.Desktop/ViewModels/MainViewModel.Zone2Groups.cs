using System.Collections.ObjectModel;
using System.Globalization;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

/// <summary>Another HEOS player on the network, as a chip: lit while it is in this receiver's group.</summary>
public sealed class HeosPlayerChip : ObservableObject
{
    private bool _isActive;
    private bool _isLeader;

    public HeosPlayerChip(int playerId, string name)
    {
        PlayerId = playerId;
        Name = name;
    }

    public int PlayerId { get; }

    public string Name { get; }

    /// <summary>True while the player is in this receiver's group.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    /// <summary>True when this player leads the group this receiver is in.</summary>
    public bool IsLeader
    {
        get => _isLeader;
        set
        {
            if (SetProperty(ref _isLeader, value))
            {
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string AccessibleName =>
        IsActive ? $"{Name}: in the group{(IsLeader ? ", leads" : string.Empty)}. Select to take it out." : $"{Name}: select to group with this receiver.";
}

/// <summary>Zone 2 (volume, source, mute) and the HEOS multi-room groups on the Control deck.</summary>
public sealed partial class MainViewModel
{
    private string _zone2VolumeText = "—";
    private bool _isZone2Muted;
    private InputSourceItem? _selectedZone2Source;
    private bool _isApplyingZone2Status;
    private string _heosGroupSummary = "Plays alone.";
    private bool _isInHeosGroup;
    private bool _isHeosGroupMuted;
    private string _heosGroupVolumeText = "—";
    private int? _heosGroupVolume;
    private int? _heosGroupId;

    public IReadOnlyList<InputSourceItem> Zone2Sources { get; } =
        DenonCatalog.Zone2Sources.Select(static s => new InputSourceItem(s.Code, s.DisplayName, s.ShortLabel)).ToList();

    public ObservableCollection<HeosPlayerChip> HeosPlayerChips { get; } = [];

    public AsyncRelayCommand StepZone2VolumeCommand { get; private set; } = null!;

    public AsyncRelayCommand ToggleZone2MuteCommand { get; private set; } = null!;

    public AsyncRelayCommand ToggleHeosGroupMemberCommand { get; private set; } = null!;

    public AsyncRelayCommand StepHeosGroupVolumeCommand { get; private set; } = null!;

    public AsyncRelayCommand ToggleHeosGroupMuteCommand { get; private set; } = null!;

    public AsyncRelayCommand LeaveHeosGroupCommand { get; private set; } = null!;

    /// <summary>The Zone 2 volume as the receiver shows it ("40"), or a dash before the first read.</summary>
    public string Zone2VolumeText
    {
        get => _zone2VolumeText;
        private set => SetProperty(ref _zone2VolumeText, value);
    }

    public bool IsZone2Muted
    {
        get => _isZone2Muted;
        private set => SetProperty(ref _isZone2Muted, value);
    }

    /// <summary>The Zone 2 source. A pick sends it to the receiver; a status read only shows it.</summary>
    public InputSourceItem? SelectedZone2Source
    {
        get => _selectedZone2Source;
        set
        {
            if (!SetProperty(ref _selectedZone2Source, value) || _isApplyingZone2Status || value is null)
            {
                return;
            }

            _ = ExecuteReceiverActionAsync(
                () => _receiverController.SetZone2SourceAsync(value.Code),
                $"Zone 2 plays {value.DisplayName}.");
        }
    }

    public string HeosGroupSummary
    {
        get => _heosGroupSummary;
        private set => SetProperty(ref _heosGroupSummary, value);
    }

    public bool IsInHeosGroup
    {
        get => _isInHeosGroup;
        private set => SetProperty(ref _isInHeosGroup, value);
    }

    public bool IsHeosGroupMuted
    {
        get => _isHeosGroupMuted;
        private set => SetProperty(ref _isHeosGroupMuted, value);
    }

    /// <summary>The group's volume ("30"), or a dash while it is not known.</summary>
    public string HeosGroupVolumeText
    {
        get => _heosGroupVolumeText;
        private set => SetProperty(ref _heosGroupVolumeText, value);
    }

    private void InitializeZone2Groups()
    {
        StepZone2VolumeCommand = new AsyncRelayCommand(StepZone2VolumeAsync, CanControlReceiver);
        ToggleZone2MuteCommand = new AsyncRelayCommand(
            _ => ExecuteReceiverActionAsync(
                () => _receiverController.SetZone2MuteAsync(!IsZone2Muted),
                IsZone2Muted ? "Zone 2 unmuted." : "Zone 2 muted."),
            CanControlReceiver);
        ToggleHeosGroupMemberCommand = new AsyncRelayCommand(ToggleHeosGroupMemberAsync, p => !IsBusy && p is HeosPlayerChip);
        StepHeosGroupVolumeCommand = new AsyncRelayCommand(StepHeosGroupVolumeAsync, _ => !IsBusy && _heosGroupId is not null);
        ToggleHeosGroupMuteCommand = new AsyncRelayCommand(
            _ => RunHeosGroupActionAsync(
                () => _receiverController.SetHeosGroupMuteAsync(_heosGroupId!.Value, !IsHeosGroupMuted),
                IsHeosGroupMuted ? "HEOS group unmuted." : "HEOS group muted."),
            _ => !IsBusy && _heosGroupId is not null);
        LeaveHeosGroupCommand = new AsyncRelayCommand(
            _ => RunHeosGroupActionAsync(
                async () =>
                {
                    var own = await _receiverController.GetOwnHeosPlayerAsync();
                    return await _receiverController.LeaveHeosGroupAsync(own.PlayerId);
                },
                "This receiver plays alone."),
            _ => !IsBusy && IsInHeosGroup);
    }

    private void NotifyZone2GroupCommands()
    {
        StepZone2VolumeCommand?.NotifyCanExecuteChanged();
        ToggleZone2MuteCommand?.NotifyCanExecuteChanged();
        ToggleHeosGroupMemberCommand?.NotifyCanExecuteChanged();
        StepHeosGroupVolumeCommand?.NotifyCanExecuteChanged();
        ToggleHeosGroupMuteCommand?.NotifyCanExecuteChanged();
        LeaveHeosGroupCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>Shows the Zone 2 part of the status and the HEOS groups. Runs on the UI thread.</summary>
    private void ApplyZone2AndGroups(ReceiverStateSnapshot snapshot)
    {
        if (snapshot.Status is { } status)
        {
            Zone2VolumeText = status.Zone2Volume?.ToString(CultureInfo.InvariantCulture) ?? "—";
            IsZone2Muted = status.IsZone2Muted;
            _isApplyingZone2Status = true;
            try
            {
                SelectedZone2Source = Zone2Sources.FirstOrDefault(s => string.Equals(s.Code, status.Zone2Source, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _isApplyingZone2Status = false;
            }
        }

        ApplyHeosGroups(snapshot.HeosGroups, snapshot.Status?.Player.PlayerId);
    }

    private void ApplyHeosGroups(HeosGroupsState? state, int? ownPlayerId)
    {
        if (state is null || ownPlayerId is not int own)
        {
            return;
        }

        var group = state.GroupOf(own);
        _heosGroupId = group?.GroupId;
        _heosGroupVolume = group?.Volume;
        IsInHeosGroup = group is not null;
        IsHeosGroupMuted = group?.IsMuted ?? false;
        HeosGroupVolumeText = group?.Volume?.ToString(CultureInfo.InvariantCulture) ?? "—";
        HeosGroupSummary = group is null
            ? "Plays alone."
            : $"Group: {string.Join(" + ", group.Members.Select(static m => m.Name))} ({group.Leader?.Name} leads).";

        // The chips: every other player, in the speaker's order. Kept in place, so a tap never moves the list.
        var others = state.Players.Where(p => p.PlayerId != own).ToList();
        for (var i = HeosPlayerChips.Count - 1; i >= 0; i--)
        {
            if (others.All(p => p.PlayerId != HeosPlayerChips[i].PlayerId || p.Name != HeosPlayerChips[i].Name))
            {
                HeosPlayerChips.RemoveAt(i);
            }
        }

        for (var i = 0; i < others.Count; i++)
        {
            var player = others[i];
            var chip = HeosPlayerChips.FirstOrDefault(c => c.PlayerId == player.PlayerId);
            if (chip is null)
            {
                chip = new HeosPlayerChip(player.PlayerId, player.Name);
                HeosPlayerChips.Insert(Math.Min(i, HeosPlayerChips.Count), chip);
            }

            chip.IsActive = group?.Contains(player.PlayerId) == true;
            chip.IsLeader = group?.Leader?.PlayerId == player.PlayerId;
        }

        NotifyZone2GroupCommands();
    }

    private Task StepZone2VolumeAsync(object? parameter)
    {
        var up = !string.Equals(parameter as string, "-1", StringComparison.Ordinal);
        return ExecuteReceiverActionAsync(
            () => _receiverController.StepZone2VolumeAsync(up),
            up ? "Zone 2 volume up." : "Zone 2 volume down.");
    }

    private Task ToggleHeosGroupMemberAsync(object? parameter)
    {
        if (parameter is not HeosPlayerChip chip)
        {
            return Task.CompletedTask;
        }

        return RunHeosGroupActionAsync(
            () => _receiverController.ToggleHeosGroupMemberAsync(chip.PlayerId),
            chip.IsActive ? $"{chip.Name} left the group." : $"{chip.Name} joined the group.");
    }

    private Task StepHeosGroupVolumeAsync(object? parameter)
    {
        if (_heosGroupId is not int gid || _heosGroupVolume is not int volume)
        {
            return Task.CompletedTask;
        }

        var step = int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
        var level = Math.Clamp(volume + step, 0, 100);
        return RunHeosGroupActionAsync(
            () => _receiverController.SetHeosGroupVolumeAsync(gid, level),
            $"HEOS group volume {level}.");
    }

    private async Task RunHeosGroupActionAsync(Func<Task<HeosGroupsState>> action, string successMessage)
    {
        try
        {
            await action();
            StatusMessage = successMessage;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            StatusMessage = exception.Message;
        }
    }
}
