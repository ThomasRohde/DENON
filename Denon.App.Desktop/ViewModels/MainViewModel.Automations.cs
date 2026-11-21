using System.Collections.ObjectModel;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private AutomationEngineService _automationEngine = null!;

    private string _acousticsSubTab = "Scenes";
    private AutomationRuleEditor? _selectedAutomationRule;
    private PluginActionChoice? _selectedScenePluginAction;
    private string _sceneActionDelayMsText = "0";
    private PluginActionChoice? _selectedRulePluginAction;
    private string _ruleActionDelayMsText = "0";
    private string _automationsStatusText = "Automate Smart Scenes & plugin actions when apps launch or on a daily schedule.";

    // Shortcut quick-bind pickers (Settings Shortcut Editor)
    private ScenePresetEditor? _shortcutSceneToAdd;
    private PluginActionChoice? _shortcutPluginActionToAdd;

    public ObservableCollection<PluginActionChoice> AvailablePluginActions { get; } = [];
    public ObservableCollection<AutomationRuleEditor> AutomationRules { get; } = [];
    public ObservableCollection<string> AutomationActivityLog { get; } = [];

    public IReadOnlyList<string> ScheduleDayOptions { get; } = ["Daily", "Weekdays", "Weekends"];

    public RelayCommand SetAcousticsSubTabCommand { get; private set; } = null!;
    public RelayCommand AddPluginActionToSceneCommand { get; private set; } = null!;
    public RelayCommand RemovePluginActionFromSceneCommand { get; private set; } = null!;

    public RelayCommand SelectAutomationRuleCommand { get; private set; } = null!;
    public RelayCommand AddAppLaunchRuleCommand { get; private set; } = null!;
    public RelayCommand AddScheduleRuleCommand { get; private set; } = null!;
    public RelayCommand RemoveAutomationRuleCommand { get; private set; } = null!;
    public RelayCommand AddPluginActionToRuleCommand { get; private set; } = null!;
    public RelayCommand RemovePluginActionFromRuleCommand { get; private set; } = null!;
    public AsyncRelayCommand TestAutomationRuleCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveAutomationsCommand { get; private set; } = null!;

    public RelayCommand AddSceneShortcutCommand { get; private set; } = null!;
    public RelayCommand AddPluginActionShortcutCommand { get; private set; } = null!;
    public RelayCommand RemoveCustomShortcutCommand { get; private set; } = null!;

    public string AcousticsSubTab
    {
        get => _acousticsSubTab;
        set => SetProperty(ref _acousticsSubTab, value);
    }

    public AutomationRuleEditor? SelectedAutomationRule
    {
        get => _selectedAutomationRule;
        set
        {
            if (SetProperty(ref _selectedAutomationRule, value))
            {
                foreach (var rule in AutomationRules)
                {
                    rule.IsSelected = ReferenceEquals(rule, value);
                }

                OnPropertyChanged(nameof(HasSelectedAutomationRule));
            }
        }
    }

    public bool HasSelectedAutomationRule => SelectedAutomationRule is not null;

    public PluginActionChoice? SelectedScenePluginAction
    {
        get => _selectedScenePluginAction;
        set => SetProperty(ref _selectedScenePluginAction, value);
    }

    public string SceneActionDelayMsText
    {
        get => _sceneActionDelayMsText;
        set => SetProperty(ref _sceneActionDelayMsText, value);
    }

    public PluginActionChoice? SelectedRulePluginAction
    {
        get => _selectedRulePluginAction;
        set => SetProperty(ref _selectedRulePluginAction, value);
    }

    public string RuleActionDelayMsText
    {
        get => _ruleActionDelayMsText;
        set => SetProperty(ref _ruleActionDelayMsText, value);
    }

    public string AutomationsStatusText
    {
        get => _automationsStatusText;
        private set => SetProperty(ref _automationsStatusText, value);
    }

    public string EnabledAutomationsBadge =>
        AutomationRules.Count(static r => r.IsEnabled).ToString();

    public ScenePresetEditor? ShortcutSceneToAdd
    {
        get => _shortcutSceneToAdd;
        set => SetProperty(ref _shortcutSceneToAdd, value);
    }

    public PluginActionChoice? ShortcutPluginActionToAdd
    {
        get => _shortcutPluginActionToAdd;
        set => SetProperty(ref _shortcutPluginActionToAdd, value);
    }

    private void InitializeAutomations(AutomationEngineService automationEngine)
    {
        _automationEngine = automationEngine;

        SetAcousticsSubTabCommand = new RelayCommand(param =>
        {
            if (param is string tab)
            {
                AcousticsSubTab = tab;
            }
        });

        AddPluginActionToSceneCommand = new RelayCommand(_ => AddPluginActionToSelectedScene());
        RemovePluginActionFromSceneCommand = new RelayCommand(RemovePluginActionFromSelectedScene);

        SelectAutomationRuleCommand = new RelayCommand(param =>
        {
            if (param is AutomationRuleEditor rule)
            {
                SelectedAutomationRule = rule;
            }
        });
        AddAppLaunchRuleCommand = new RelayCommand(_ => AddAutomationRule(AutomationTriggerType.AppLaunch));
        AddScheduleRuleCommand = new RelayCommand(_ => AddAutomationRule(AutomationTriggerType.TimeOfDay));
        RemoveAutomationRuleCommand = new RelayCommand(RemoveAutomationRule);
        AddPluginActionToRuleCommand = new RelayCommand(_ => AddPluginActionToSelectedRule());
        RemovePluginActionFromRuleCommand = new RelayCommand(RemovePluginActionFromSelectedRule);
        TestAutomationRuleCommand = new AsyncRelayCommand(TestAutomationRuleAsync);
        SaveAutomationsCommand = new AsyncRelayCommand(_ => SaveAutomationsAsync());

        AddSceneShortcutCommand = new RelayCommand(_ => AddSceneHotkeyBinding());
        AddPluginActionShortcutCommand = new RelayCommand(_ => AddPluginActionHotkeyBinding());
        RemoveCustomShortcutCommand = new RelayCommand(RemoveCustomShortcutBinding);

        _automationEngine.ActivityLogged += entry => _ = _dispatcher.InvokeAsync(() =>
        {
            var icon = entry.Success ? "✓" : "⚠";
            AutomationActivityLog.Insert(0, $"{entry.Timestamp.LocalDateTime:HH:mm:ss}  {icon}  [{entry.RuleName}] {entry.TriggerDetail}");
            while (AutomationActivityLog.Count > 30)
            {
                AutomationActivityLog.RemoveAt(AutomationActivityLog.Count - 1);
            }

            AutomationsStatusText = $"[{entry.RuleName}] {entry.TriggerDetail}";
        });

        _pluginHost.PluginsChanged += () => _ = _dispatcher.InvokeAsync(RefreshAvailablePluginActions);
        RefreshAvailablePluginActions();
    }

    private void RefreshAvailablePluginActions()
    {
        var prevSceneKey = SelectedScenePluginAction is null ? null : $"{SelectedScenePluginAction.PluginId}:{SelectedScenePluginAction.ActionId}";
        var prevRuleKey = SelectedRulePluginAction is null ? null : $"{SelectedRulePluginAction.PluginId}:{SelectedRulePluginAction.ActionId}";
        var prevShortcutKey = ShortcutPluginActionToAdd is null ? null : $"{ShortcutPluginActionToAdd.PluginId}:{ShortcutPluginActionToAdd.ActionId}";

        AvailablePluginActions.Clear();
        foreach (var plugin in _pluginHost.GetPlugins())
        {
            foreach (var action in plugin.Actions)
            {
                AvailablePluginActions.Add(new PluginActionChoice(
                    plugin.Id,
                    plugin.Name,
                    action.ActionId,
                    action.DisplayName,
                    $"{plugin.Name}: {action.DisplayName}"));
            }
        }

        SelectedScenePluginAction = AvailablePluginActions.FirstOrDefault(a => $"{a.PluginId}:{a.ActionId}" == prevSceneKey)
                                    ?? AvailablePluginActions.FirstOrDefault();
        SelectedRulePluginAction = AvailablePluginActions.FirstOrDefault(a => $"{a.PluginId}:{a.ActionId}" == prevRuleKey)
                                   ?? AvailablePluginActions.FirstOrDefault();
        ShortcutPluginActionToAdd = AvailablePluginActions.FirstOrDefault(a => $"{a.PluginId}:{a.ActionId}" == prevShortcutKey)
                                    ?? AvailablePluginActions.FirstOrDefault();
    }

    private void LoadAutomationsFromSettings(AppSettings settings)
    {
        var previousId = SelectedAutomationRule?.Id;
        AutomationRules.Clear();
        foreach (var rule in settings.Automations ?? AutomationRule.CreateDefaults())
        {
            AutomationRules.Add(AutomationRuleEditor.FromModel(rule));
        }

        SelectedAutomationRule = AutomationRules.FirstOrDefault(r => string.Equals(r.Id, previousId, StringComparison.OrdinalIgnoreCase))
                                 ?? AutomationRules.FirstOrDefault();
        ShortcutSceneToAdd = ScenePresets.FirstOrDefault();
        OnPropertyChanged(nameof(EnabledAutomationsBadge));
    }

    private void AddPluginActionToSelectedScene()
    {
        if (SelectedScene is null || SelectedScenePluginAction is null)
        {
            return;
        }

        var delayMs = int.TryParse(SceneActionDelayMsText?.Trim(), out var d) ? Math.Clamp(d, 0, 30_000) : 0;
        SelectedScene.PluginActions.Add(new ScenePluginActionStep
        {
            PluginId = SelectedScenePluginAction.PluginId,
            ActionId = SelectedScenePluginAction.ActionId,
            DisplayName = SelectedScenePluginAction.FullLabel,
            DelayMs = delayMs,
        });
        SelectedScene.NotifyPluginActionsChanged();
        StatusMessage = $"Added '{SelectedScenePluginAction.FullLabel}' to '{SelectedScene.Name}'. Click Save Scenes to persist.";
    }

    private void RemovePluginActionFromSelectedScene(object? parameter)
    {
        if (SelectedScene is null || parameter is not ScenePluginActionStep step)
        {
            return;
        }

        SelectedScene.PluginActions.Remove(step);
        SelectedScene.NotifyPluginActionsChanged();
    }

    private void AddAutomationRule(AutomationTriggerType triggerType)
    {
        var editor = new AutomationRuleEditor
        {
            Id = $"auto-{Guid.NewGuid().ToString("N")[..6]}",
            Name = triggerType == AutomationTriggerType.AppLaunch ? "New App Launch Rule" : "New Scheduled Rule",
            IsEnabled = true,
            TriggerType = triggerType,
            ProcessNames = triggerType == AutomationTriggerType.AppLaunch ? "vlc, Plex" : string.Empty,
            ScheduleTime = "22:00",
            DaysFilter = "Daily",
            TargetSceneId = ScenePresets.FirstOrDefault()?.Id ?? string.Empty,
            CooldownSecondsText = "120",
        };

        AutomationRules.Add(editor);
        SelectedAutomationRule = editor;
        OnPropertyChanged(nameof(EnabledAutomationsBadge));
        AutomationsStatusText = $"Created '{editor.Name}'. Configure trigger & actions, then click Save Automations.";
    }

    private void RemoveAutomationRule(object? parameter)
    {
        var target = parameter as AutomationRuleEditor ?? SelectedAutomationRule;
        if (target is null)
        {
            return;
        }

        var name = target.Name;
        AutomationRules.Remove(target);
        SelectedAutomationRule = AutomationRules.FirstOrDefault();
        OnPropertyChanged(nameof(EnabledAutomationsBadge));
        AutomationsStatusText = $"Removed rule '{name}'. Click Save Automations to persist.";
    }

    private void AddPluginActionToSelectedRule()
    {
        if (SelectedAutomationRule is null || SelectedRulePluginAction is null)
        {
            return;
        }

        var delayMs = int.TryParse(RuleActionDelayMsText?.Trim(), out var d) ? Math.Clamp(d, 0, 30_000) : 0;
        SelectedAutomationRule.PluginActions.Add(new ScenePluginActionStep
        {
            PluginId = SelectedRulePluginAction.PluginId,
            ActionId = SelectedRulePluginAction.ActionId,
            DisplayName = SelectedRulePluginAction.FullLabel,
            DelayMs = delayMs,
        });
        SelectedAutomationRule.NotifyPluginActionsChanged();
    }

    private void RemovePluginActionFromSelectedRule(object? parameter)
    {
        if (SelectedAutomationRule is null || parameter is not ScenePluginActionStep step)
        {
            return;
        }

        SelectedAutomationRule.PluginActions.Remove(step);
        SelectedAutomationRule.NotifyPluginActionsChanged();
    }

    private async Task TestAutomationRuleAsync(object? parameter)
    {
        var target = parameter as AutomationRuleEditor ?? SelectedAutomationRule;
        if (target is null)
        {
            return;
        }

        AutomationsStatusText = $"Testing automation '{target.Name}'...";
        var summary = await _automationEngine.TriggerRuleNowAsync(target.ToModel(), "Manual test");
        AutomationsStatusText = $"Tested '{target.Name}': {summary}";
        StatusMessage = summary;
    }

    private async Task SaveAutomationsAsync()
    {
        var updated = _settingsStore.Current.Clone();
        updated.Automations = AutomationRules.Select(static r => r.ToModel()).ToList();
        await _settingsStore.SaveAsync(updated);
        OnPropertyChanged(nameof(EnabledAutomationsBadge));
        AutomationsStatusText = $"Saved {updated.Automations.Count} automation rule(s) ({updated.Automations.Count(static r => r.IsEnabled)} active).";
        StatusMessage = AutomationsStatusText;
    }

    private void AddSceneHotkeyBinding()
    {
        var scene = ShortcutSceneToAdd ?? ScenePresets.FirstOrDefault();
        if (scene is null)
        {
            return;
        }

        var actionId = $"scene:{scene.Id}";
        if (ShortcutEditors.Any(s => string.Equals(s.ActionId, actionId, StringComparison.OrdinalIgnoreCase)))
        {
            ShortcutSummaryText = $"A shortcut entry for scene '{scene.Name}' already exists.";
            return;
        }

        var item = ShortcutItemEditor.FromModel(new ShortcutBinding(actionId, $"Scene: {scene.Name}", isGlobal: true, gesture: string.Empty));
        ShortcutEditors.Add(item);
        ValidateShortcutCollisions(reportClean: false);
        ShortcutSummaryText = $"Added global shortcut row for Scene '{scene.Name}'. Click Record, press your key combo, then Save Settings.";
    }

    private void AddPluginActionHotkeyBinding()
    {
        var choice = ShortcutPluginActionToAdd ?? AvailablePluginActions.FirstOrDefault();
        if (choice is null)
        {
            return;
        }

        var actionId = $"plugin:{choice.PluginId}:{choice.ActionId}";
        if (ShortcutEditors.Any(s => string.Equals(s.ActionId, actionId, StringComparison.OrdinalIgnoreCase)))
        {
            ShortcutSummaryText = $"A shortcut entry for '{choice.FullLabel}' already exists.";
            return;
        }

        var item = ShortcutItemEditor.FromModel(new ShortcutBinding(actionId, choice.FullLabel, isGlobal: true, gesture: string.Empty));
        ShortcutEditors.Add(item);
        ValidateShortcutCollisions(reportClean: false);
        ShortcutSummaryText = $"Added global shortcut row for '{choice.FullLabel}'. Click Record, press your key combo, then Save Settings.";
    }

    private void RemoveCustomShortcutBinding(object? parameter)
    {
        if (parameter is not ShortcutItemEditor editor)
        {
            return;
        }

        if (!editor.ActionId.StartsWith("scene:", StringComparison.OrdinalIgnoreCase) &&
            !editor.ActionId.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
        {
            ShortcutSummaryText = "Built-in shortcuts can be cleared with ✕, while custom Scene/Plugin hotkeys can be removed.";
            return;
        }

        ShortcutEditors.Remove(editor);
        ValidateShortcutCollisions(reportClean: false);
        ShortcutSummaryText = $"Removed custom shortcut '{editor.Label}'. Click Save Settings to apply.";
    }
}
