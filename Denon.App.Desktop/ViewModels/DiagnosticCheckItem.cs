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
    private static Brush PendingBrush => ThemeResources.Brush("TextFaint");
    private static Brush RunningBrush => ThemeResources.Brush("AccentCyanBright");
    private static Brush PassBrush => ThemeResources.Brush("AccentEmerald");
    private static Brush WarningBrush => ThemeResources.Brush("AccentAmber");
    private static Brush FailBrush => ThemeResources.Brush("AccentCrimson");

    private static Brush PendingBg => ThemeResources.Brush("CardElevated");
    private static Brush RunningBg => ThemeResources.Brush("AccentCyanSubtleBg");
    private static Brush PassBg => ThemeResources.Brush("AccentEmeraldSubtleBg");
    private static Brush WarningBg => ThemeResources.Brush("AccentAmberBg");
    private static Brush FailBg => ThemeResources.Brush("AccentCrimsonBg");

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
}
