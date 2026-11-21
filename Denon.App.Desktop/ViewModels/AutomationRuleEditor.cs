using System.Collections.ObjectModel;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

public sealed record PluginActionChoice(
    string PluginId,
    string PluginName,
    string ActionId,
    string ActionName,
    string FullLabel);

public sealed class AutomationRuleEditor : ObservableObject
{
    private string _id = string.Empty;
    private string _name = "New Automation";
    private bool _isEnabled;
    private AutomationTriggerType _triggerType = AutomationTriggerType.AppLaunch;
    private string _processNames = string.Empty;
    private string _scheduleTime = "22:00";
    private string _daysFilter = "Daily";
    private string _targetSceneId = string.Empty;
    private string _cooldownSecondsText = "120";
    private bool _isSelected;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public AutomationTriggerType TriggerType
    {
        get => _triggerType;
        set
        {
            if (SetProperty(ref _triggerType, value))
            {
                OnPropertyChanged(nameof(IsAppLaunchTrigger));
                OnPropertyChanged(nameof(IsTimeScheduleTrigger));
                OnPropertyChanged(nameof(TriggerBadge));
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public bool IsAppLaunchTrigger => TriggerType == AutomationTriggerType.AppLaunch;

    public bool IsTimeScheduleTrigger => TriggerType == AutomationTriggerType.TimeOfDay;

    public string TriggerBadge => TriggerType == AutomationTriggerType.AppLaunch ? "APP LAUNCH" : "SCHEDULE";

    public string StatusLabel => IsEnabled ? "ACTIVE" : "PAUSED";

    public string ProcessNames
    {
        get => _processNames;
        set
        {
            if (SetProperty(ref _processNames, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string ScheduleTime
    {
        get => _scheduleTime;
        set
        {
            if (SetProperty(ref _scheduleTime, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string DaysFilter
    {
        get => _daysFilter;
        set
        {
            if (SetProperty(ref _daysFilter, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string TargetSceneId
    {
        get => _targetSceneId;
        set
        {
            if (SetProperty(ref _targetSceneId, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string CooldownSecondsText
    {
        get => _cooldownSecondsText;
        set => SetProperty(ref _cooldownSecondsText, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ObservableCollection<ScenePluginActionStep> PluginActions { get; } = [];

    public bool HasPluginActions => PluginActions.Count > 0;

    public string SummaryLine
    {
        get
        {
            var trigger = TriggerType == AutomationTriggerType.AppLaunch
                ? (string.IsNullOrWhiteSpace(ProcessNames) ? "No apps set" : $"When {ProcessNames} launches")
                : $"At {ScheduleTime} ({DaysFilter})";

            var actions = new List<string>();
            if (!string.IsNullOrWhiteSpace(TargetSceneId))
            {
                actions.Add($"Scene '{TargetSceneId}'");
            }

            if (PluginActions.Count > 0)
            {
                actions.Add($"{PluginActions.Count} plugin step(s)");
            }

            var target = actions.Count > 0 ? string.Join(" + ", actions) : "No actions yet";
            return $"{trigger}  →  {target}";
        }
    }

    public void NotifyPluginActionsChanged()
    {
        OnPropertyChanged(nameof(HasPluginActions));
        OnPropertyChanged(nameof(SummaryLine));
    }

    public static AutomationRuleEditor FromModel(AutomationRule model)
    {
        var editor = new AutomationRuleEditor
        {
            Id = model.Id,
            Name = model.Name,
            IsEnabled = model.IsEnabled,
            TriggerType = model.TriggerType,
            ProcessNames = model.ProcessNames,
            ScheduleTime = model.ScheduleTime,
            DaysFilter = string.IsNullOrWhiteSpace(model.DaysFilter) ? "Daily" : model.DaysFilter,
            TargetSceneId = model.TargetSceneId ?? string.Empty,
            CooldownSecondsText = model.CooldownSeconds.ToString(),
        };

        if (model.PluginActions is { Count: > 0 })
        {
            foreach (var step in model.PluginActions)
            {
                editor.PluginActions.Add(step.Clone());
            }
        }

        return editor;
    }

    public AutomationRule ToModel() =>
        new()
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N")[..8] : Id,
            Name = string.IsNullOrWhiteSpace(Name) ? "Custom Automation" : Name.Trim(),
            IsEnabled = IsEnabled,
            TriggerType = TriggerType,
            ProcessNames = ProcessNames.Trim(),
            ScheduleTime = string.IsNullOrWhiteSpace(ScheduleTime) ? "22:00" : ScheduleTime.Trim(),
            DaysFilter = string.IsNullOrWhiteSpace(DaysFilter) ? "Daily" : DaysFilter.Trim(),
            TargetSceneId = string.IsNullOrWhiteSpace(TargetSceneId) ? null : TargetSceneId.Trim(),
            PluginActions = PluginActions.Select(static s => s.Clone()).ToList(),
            CooldownSeconds = int.TryParse(CooldownSecondsText, out var cd) ? Math.Clamp(cd, 10, 86400) : 120,
        };
}
