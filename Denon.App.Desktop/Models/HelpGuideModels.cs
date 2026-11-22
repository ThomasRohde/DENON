using System.Windows.Media;
using Denon.App.Desktop.Infrastructure;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Denon.App.Desktop.Models;

public sealed record HelpStepItem(string StepNumber, string Title, string Description);

public sealed record HelpQaItem(string Question, string Answer);

public sealed record HelpQuickJump(string Label, string TargetSection, string? TargetPluginId = null);

public sealed record HelpShortcutRow(string Keys, string Scope, string Action);

public sealed record HelpPortRow(string ProtocolPort, string Service, string Purpose);

public sealed class HelpGuideTopic : ObservableObject
{
    private bool _isSelected;

    public HelpGuideTopic(
        string id,
        string badge,
        string accentHex,
        string category,
        string title,
        string subtitle,
        string overview,
        IReadOnlyList<HelpStepItem> steps,
        IReadOnlyList<string> proTips,
        IReadOnlyList<HelpQaItem> troubleshooting,
        IReadOnlyList<HelpQuickJump> quickJumps,
        string? contextSection = null,
        string? contextPluginId = null,
        bool showShortcutsTable = false,
        bool showPortsTable = false)
    {
        Id = id;
        Badge = badge;
        AccentHex = accentHex;
        AccentBrush = CreateBrush(accentHex, "#00D4FF");
        Category = category;
        Title = title;
        Subtitle = subtitle;
        Overview = overview;
        Steps = steps;
        ProTips = proTips;
        Troubleshooting = troubleshooting;
        QuickJumps = quickJumps;
        ContextSection = contextSection;
        ContextPluginId = contextPluginId;
        ShowShortcutsTable = showShortcutsTable;
        ShowPortsTable = showPortsTable;

        SearchIndex = string.Join(
            ' ',
            [
                title,
                subtitle,
                category,
                overview,
                .. steps.Select(s => $"{s.Title} {s.Description}"),
                .. proTips,
                .. troubleshooting.Select(q => $"{q.Question} {q.Answer}"),
            ]).ToLowerInvariant();
    }

    public string Id { get; }

    public string Badge { get; }

    public string AccentHex { get; }

    public Brush AccentBrush { get; }

    public string Category { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Overview { get; }

    public IReadOnlyList<HelpStepItem> Steps { get; }

    public IReadOnlyList<string> ProTips { get; }

    public IReadOnlyList<HelpQaItem> Troubleshooting { get; }

    public IReadOnlyList<HelpQuickJump> QuickJumps { get; }

    public string? ContextSection { get; }

    public string? ContextPluginId { get; }

    public bool ShowShortcutsTable { get; }

    public bool ShowPortsTable { get; }

    public bool HasProTips => ProTips.Count > 0;

    public bool HasTroubleshooting => Troubleshooting.Count > 0;

    public bool HasQuickJumps => QuickJumps.Count > 0;

    public string SearchIndex { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private static Brush CreateBrush(string hex, string fallbackHex)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        catch
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex));
            b.Freeze();
            return b;
        }
    }
}

public sealed class SetupChecklistItem : ObservableObject
{
    private bool _isComplete;
    private string _statusSummary = string.Empty;

    public SetupChecklistItem(
        string id,
        string title,
        string actionLabel,
        string targetSection,
        string? targetPluginId = null)
    {
        Id = id;
        Title = title;
        ActionLabel = actionLabel;
        TargetSection = targetSection;
        TargetPluginId = targetPluginId;
        JumpTarget = new HelpQuickJump(actionLabel, targetSection, targetPluginId);
    }

    public string Id { get; }

    public string Title { get; }

    public string ActionLabel { get; }

    public string TargetSection { get; }

    public string? TargetPluginId { get; }

    public HelpQuickJump JumpTarget { get; }

    public bool IsComplete
    {
        get => _isComplete;
        set
        {
            if (SetProperty(ref _isComplete, value))
            {
                OnPropertyChanged(nameof(StateBadgeText));
            }
        }
    }

    public string StatusSummary
    {
        get => _statusSummary;
        set => SetProperty(ref _statusSummary, value);
    }

    public string StateBadgeText => IsComplete ? "READY" : "SETUP";
}
