using System.Windows.Media;
using Denon.App.Desktop.Infrastructure;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Denon.App.Desktop.ViewModels;

public enum DiagnosticCheckState
{
    Pending,
    Running,
    Pass,
    Warning,
    Fail,
}

public sealed class DiagnosticCheckItem : ObservableObject
{
    private static readonly Brush PendingBrush = CreateFrozenBrush(100, 116, 139);
    private static readonly Brush RunningBrush = CreateFrozenBrush(56, 189, 248);
    private static readonly Brush PassBrush = CreateFrozenBrush(16, 185, 129);
    private static readonly Brush WarningBrush = CreateFrozenBrush(245, 158, 11);
    private static readonly Brush FailBrush = CreateFrozenBrush(239, 68, 68);

    private static readonly Brush PendingBg = CreateFrozenBrush(24, 28, 38);
    private static readonly Brush RunningBg = CreateFrozenBrush(12, 46, 61);
    private static readonly Brush PassBg = CreateFrozenBrush(12, 45, 34);
    private static readonly Brush WarningBg = CreateFrozenBrush(54, 36, 10);
    private static readonly Brush FailBg = CreateFrozenBrush(58, 20, 24);

    private DiagnosticCheckState _state = DiagnosticCheckState.Pending;
    private string _summary;
    private string _advice = string.Empty;

    public DiagnosticCheckItem(string title, string initialSummary)
    {
        Title = title;
        _summary = initialSummary;
    }

    public string Title { get; }

    public DiagnosticCheckState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusGlyph));
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusBackgroundBrush));
            }
        }
    }

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public string Advice
    {
        get => _advice;
        set => SetProperty(ref _advice, value);
    }

    public string StatusGlyph => State switch
    {
        DiagnosticCheckState.Running => "◌",
        DiagnosticCheckState.Pass => "✓",
        DiagnosticCheckState.Warning => "!",
        DiagnosticCheckState.Fail => "✕",
        _ => "•",
    };

    public string StatusLabel => State switch
    {
        DiagnosticCheckState.Running => "CHECKING",
        DiagnosticCheckState.Pass => "OK",
        DiagnosticCheckState.Warning => "ATTENTION",
        DiagnosticCheckState.Fail => "ISSUE",
        _ => "READY",
    };

    public Brush StatusBrush => State switch
    {
        DiagnosticCheckState.Running => RunningBrush,
        DiagnosticCheckState.Pass => PassBrush,
        DiagnosticCheckState.Warning => WarningBrush,
        DiagnosticCheckState.Fail => FailBrush,
        _ => PendingBrush,
    };

    public Brush StatusBackgroundBrush => State switch
    {
        DiagnosticCheckState.Running => RunningBg,
        DiagnosticCheckState.Pass => PassBg,
        DiagnosticCheckState.Warning => WarningBg,
        DiagnosticCheckState.Fail => FailBg,
        _ => PendingBg,
    };

    public void SetResult(DiagnosticCheckState state, string summary, string advice = "")
    {
        State = state;
        Summary = summary;
        Advice = advice;
    }

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
