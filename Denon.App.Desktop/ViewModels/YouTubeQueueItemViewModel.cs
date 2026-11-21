using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

public sealed class YouTubeQueueItemViewModel : ObservableObject
{
    private bool _isCurrent;
    private int _position;

    public YouTubeQueueItemViewModel(YouTubeTrackItem track, int position, bool isCurrent)
    {
        Track = track;
        _position = position;
        _isCurrent = isCurrent;
    }

    public YouTubeTrackItem Track { get; }

    public string EntryId => Track.EntryId;

    public string VideoId => Track.VideoId;

    public string Title => Track.Title;

    public string Author => Track.Author;

    public string DurationDisplay => Track.DurationDisplay;

    public string ThumbnailUrl => Track.ThumbnailUrl;

    public int Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value))
            {
                OnPropertyChanged(nameof(PositionLabel));
            }
        }
    }

    public string PositionLabel => IsCurrent ? "▶" : $"{Position:00}";

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(PositionLabel));
            }
        }
    }
}

public sealed class AcousticChipItem : ObservableObject
{
    private bool _isActive;

    public AcousticChipItem(string code, string displayName)
    {
        Code = code;
        DisplayName = displayName;
    }

    public string Code { get; }

    public string DisplayName { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}
