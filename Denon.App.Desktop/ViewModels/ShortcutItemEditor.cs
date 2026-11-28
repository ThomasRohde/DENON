using System.Windows.Media;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Denon.App.Desktop.ViewModels;

public sealed class ShortcutItemEditor : ObservableObject
{
    // Tokens, not brushes: a theme switch rebuilds the brush from the token (docs/theming.md).
    private const string OkTone = "AccentEmerald";
    private const string ConflictTone = "AccentCrimson";
    private const string WarningTone = "AccentAmber";
    private const string RecordingTone = "AccentCyan";
    private const string MutedTone = "TextFaint";

    private string _gesture = string.Empty;
    private bool _isRecording;
    private bool _hasCollision;
    private bool _isWarningOnly;
    private string _collisionMessage = string.Empty;
    private string _statusLabel = "OK";
    private string _statusTone = OkTone;

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
                    StatusTone = RecordingTone;
                }
            }
        }
    }

    public string RecordButtonText => IsRecording ? "Cancel" : "Record";

    /// <summary>A row the user added for a Smart Scene or a plugin action: it can be removed, not only cleared.</summary>
    public bool IsCustom =>
        ActionId.StartsWith("scene:", StringComparison.OrdinalIgnoreCase) ||
        ActionId.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase);

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

    public Brush StatusBrush => ThemeResources.Brush(_statusTone);

    private string StatusTone
    {
        set
        {
            if (_statusTone != value)
            {
                _statusTone = value;
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public void SetValidationResult(bool hasError, bool isWarning, string? message, string badge)
    {
        HasCollision = hasError;
        IsWarningOnly = isWarning;
        CollisionMessage = message ?? string.Empty;
        StatusLabel = badge;
        StatusTone = hasError
            ? ConflictTone
            : (isWarning
                ? WarningTone
                : (string.IsNullOrWhiteSpace(Gesture) ? MutedTone : OkTone));
    }

    public ShortcutBinding ToModel() => new(ActionId, Label, IsGlobal, Gesture.Trim());

    public static ShortcutItemEditor FromModel(ShortcutBinding model) =>
        new(model.ActionId, model.Label, model.IsGlobal, model.Gesture);
}
