using System.Windows.Media;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Denon.App.Desktop.ViewModels;

public sealed class ShortcutItemEditor : ObservableObject
{
    private static readonly Brush OkBrush = Freeze(Color.FromRgb(16, 185, 129));
    private static readonly Brush ConflictBrush = Freeze(Color.FromRgb(239, 68, 68));
    private static readonly Brush WarningBrush = Freeze(Color.FromRgb(245, 158, 11));
    private static readonly Brush RecordingBrush = Freeze(Color.FromRgb(0, 212, 255));
    private static readonly Brush MutedBrush = Freeze(Color.FromRgb(100, 116, 139));

    private string _gesture = string.Empty;
    private bool _isRecording;
    private bool _hasCollision;
    private bool _isWarningOnly;
    private string _collisionMessage = string.Empty;
    private string _statusLabel = "OK";
    private Brush _statusBrush = OkBrush;

    public ShortcutItemEditor(string actionId, string label, bool isGlobal, string gesture)
    {
        ActionId = actionId;
        Label = label;
        IsGlobal = isGlobal;
        _gesture = gesture;
    }

    public string ActionId { get; }

    public string Label { get; }

    public bool IsGlobal { get; }

    public string ScopeBadge => IsGlobal ? "GLOBAL" : "IN-APP";

    public string Gesture
    {
        get => _gesture;
        set
        {
            if (SetProperty(ref _gesture, value))
            {
                OnPropertyChanged(nameof(GestureDisplay));
            }
        }
    }

    public string GestureDisplay => IsRecording
        ? "Press keys... (Esc to cancel)"
        : (string.IsNullOrWhiteSpace(Gesture) ? "Not Assigned" : Gesture);

    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(GestureDisplay));
                OnPropertyChanged(nameof(RecordButtonText));
                if (value)
                {
                    StatusLabel = "LISTENING";
                    StatusBrush = RecordingBrush;
                }
            }
        }
    }

    public string RecordButtonText => IsRecording ? "Cancel" : "Record";

    public bool HasCollision
    {
        get => _hasCollision;
        private set => SetProperty(ref _hasCollision, value);
    }

    public bool IsWarningOnly
    {
        get => _isWarningOnly;
        private set => SetProperty(ref _isWarningOnly, value);
    }

    public string CollisionMessage
    {
        get => _collisionMessage;
        private set => SetProperty(ref _collisionMessage, value);
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => SetProperty(ref _statusLabel, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public void SetValidationResult(bool hasError, bool isWarning, string? message, string badge)
    {
        HasCollision = hasError;
        IsWarningOnly = isWarning;
        CollisionMessage = message ?? string.Empty;
        StatusLabel = badge;
        StatusBrush = hasError
            ? ConflictBrush
            : (isWarning
                ? WarningBrush
                : (string.IsNullOrWhiteSpace(Gesture) ? MutedBrush : OkBrush));
    }

    public ShortcutBinding ToModel() => new(ActionId, Label, IsGlobal, Gesture.Trim());

    public static ShortcutItemEditor FromModel(ShortcutBinding model) =>
        new(model.ActionId, model.Label, model.IsGlobal, model.Gesture);

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
