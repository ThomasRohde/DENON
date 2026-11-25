using System.Collections.ObjectModel;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;
using Denon.App.PluginSdk;

namespace Denon.App.Desktop.ViewModels;

public sealed record PluginActionChoice(
    string PluginId,
    string PluginName,
    string ActionId,
    string ActionName,
    string FullLabel,
    IReadOnlyList<PluginActionParameter>? Parameters = null)
{
    public IReadOnlyList<PluginActionParameter> DeclaredParameters => Parameters ?? [];
}

/// <summary>One automation in the Automation Rules list. The graph itself is edited in the node editor.</summary>
public sealed class AutomationGraphItem : ObservableObject
{
    private readonly Action<AutomationGraphItem, bool> _onEnabledChanged;
    private bool _enabled;
    private string _lastRunText = "Not run since the app started";
    private bool _lastRunFailed;

    public AutomationGraphItem(AutomationGraph graph, Action<AutomationGraphItem, bool> onEnabledChanged)
    {
        _onEnabledChanged = onEnabledChanged;
        Id = graph.Id;
        Name = graph.Name;
        _enabled = graph.Enabled;
        (TriggerBadge, Summary) = Describe(graph);
    }

    public string Id { get; }

    public string Name { get; }

    public string TriggerBadge { get; }

    public string Summary { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _onEnabledChanged(this, value);
            }
        }
    }

    public string LastRunText
    {
        get => _lastRunText;
        private set => SetProperty(ref _lastRunText, value);
    }

    public bool LastRunFailed
    {
        get => _lastRunFailed;
        private set => SetProperty(ref _lastRunFailed, value);
    }

    public void ApplyRun(AutomationRun run)
    {
        LastRunText = $"{(run.DryRun ? "Dry run" : "Ran")} {run.At.LocalDateTime:HH:mm:ss}: {run.Summary}";
        LastRunFailed = !run.Success;
    }

    /// <summary>Sets the switch without saving (the settings already say so).</summary>
    public void SyncEnabled(bool enabled)
    {
        if (_enabled != enabled)
        {
            _enabled = enabled;
            OnPropertyChanged(nameof(Enabled));
        }
    }

    private static (string Badge, string Summary) Describe(AutomationGraph graph)
    {
        var triggers = graph.Nodes.Where(static n => AutomationNodeTypes.IsTrigger(n.Type)).ToList();
        var conditions = graph.Nodes.Count(static n => AutomationNodeTypes.IsCondition(n.Type));
        var actions = graph.Nodes.Count(static n => AutomationNodeTypes.Find(n.Type)?.Group == AutomationNodeTypes.GroupAction);

        var badge = triggers.Count switch
        {
            0 => "NO TRIGGER",
            > 1 => $"{triggers.Count} TRIGGERS",
            _ => triggers[0].Type switch
            {
                AutomationNodeTypes.TriggerEvent => "ROOM EVENT",
                AutomationNodeTypes.TriggerWebhook => "WEBHOOK",
                AutomationNodeTypes.TriggerSchedule => "SCHEDULE",
                AutomationNodeTypes.TriggerAppLaunch => "APP START",
                _ => "TRIGGER",
            },
        };

        var when = triggers.Count == 0
            ? "No trigger yet"
            : string.Join(" or ", triggers.Select(static t => t.Type switch
            {
                AutomationNodeTypes.TriggerEvent => $"On {t.Get("event")}" + (t.Get("filter").Length > 0 ? $" [{t.Get("filter")}]" : string.Empty),
                AutomationNodeTypes.TriggerWebhook => $"On webhook '{t.Get("hook")}'",
                AutomationNodeTypes.TriggerSchedule => $"At {t.Get("time")} ({t.Get("days")})",
                AutomationNodeTypes.TriggerAppLaunch => $"When {t.Get("processes")} starts",
                _ => t.Type,
            }));

        var then = actions == 0
            ? "no actions yet"
            : (conditions > 0 ? $"{conditions} check(s), " : string.Empty) + $"{actions} action(s)";
        return (badge, $"{when}  →  {then}");
    }
}

public sealed partial class MainViewModel
{
    private AutomationEngineService _automationEngine = null!;
    private AutomationEditorWindow? _automationEditorWindow;

    private string _acousticsSubTab = "Scenes";
    private PluginActionChoice? _selectedScenePluginAction;
    private string _sceneActionDelayMsText = "0";
    private string _automationsStatusText = "Automations run Smart Scenes, plugin actions and receiver commands on room events, webhooks, schedules or app starts.";

    // Shortcut quick-bind pickers (Settings Shortcut Editor)
    private ScenePresetEditor? _shortcutSceneToAdd;
    private PluginActionChoice? _shortcutPluginActionToAdd;

    public ObservableCollection<PluginActionChoice> AvailablePluginActions { get; } = [];
    public ObservableCollection<PluginActionParameterEditor> PendingSceneActionParameters { get; } = [];
    public ObservableCollection<AutomationGraphItem> AutomationItems { get; } = [];
    public ObservableCollection<string> AutomationActivityLog { get; } = [];

    public bool HasPendingSceneActionParameters => PendingSceneActionParameters.Count > 0;

    public RelayCommand SetAcousticsSubTabCommand { get; private set; } = null!;
    public RelayCommand AddPluginActionToSceneCommand { get; private set; } = null!;
    public RelayCommand RemovePluginActionFromSceneCommand { get; private set; } = null!;

    public AsyncRelayCommand OpenAutomationEditorCommand { get; private set; } = null!;
    public AsyncRelayCommand RunAutomationNowCommand { get; private set; } = null!;

    public RelayCommand AddSceneShortcutCommand { get; private set; } = null!;
    public RelayCommand AddPluginActionShortcutCommand { get; private set; } = null!;
    public RelayCommand RemoveCustomShortcutCommand { get; private set; } = null!;

    public string AcousticsSubTab
    {
        get => _acousticsSubTab;
        set => SetProperty(ref _acousticsSubTab, value);
    }

    public PluginActionChoice? SelectedScenePluginAction
    {
        get => _selectedScenePluginAction;
        set
        {
            if (SetProperty(ref _selectedScenePluginAction, value))
            {
                RebuildPendingParameters(PendingSceneActionParameters, value?.DeclaredParameters);
                OnPropertyChanged(nameof(HasPendingSceneActionParameters));
            }
        }
    }

    public string SceneActionDelayMsText
    {
        get => _sceneActionDelayMsText;
        set => SetProperty(ref _sceneActionDelayMsText, value);
    }

    public string AutomationsStatusText
    {
        get => _automationsStatusText;
        private set => SetProperty(ref _automationsStatusText, value);
    }

    /// <summary>"0/4 on": automations switched on, of all automations (a bare "0" read as "none").</summary>
    public string EnabledAutomationsBadge =>
        $"{AutomationItems.Count(static r => r.Enabled)}/{AutomationItems.Count} on";

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

        OpenAutomationEditorCommand = new AsyncRelayCommand(OpenAutomationEditorAsync);
        RunAutomationNowCommand = new AsyncRelayCommand(RunAutomationNowAsync);

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
        _automationEngine.RunCompleted += run => _ = _dispatcher.InvokeAsync(() =>
            AutomationItems.FirstOrDefault(i => i.Id == run.GraphId)?.ApplyRun(run));

        _pluginHost.PluginsChanged += () => _ = _dispatcher.InvokeAsync(RefreshAvailablePluginActions);
        RefreshAvailablePluginActions();
    }

    private void RefreshAvailablePluginActions()
    {
        var prevSceneKey = SelectedScenePluginAction is null ? null : $"{SelectedScenePluginAction.PluginId}:{SelectedScenePluginAction.ActionId}";
        var prevShortcutKey = ShortcutPluginActionToAdd is null ? null : $"{ShortcutPluginActionToAdd.PluginId}:{ShortcutPluginActionToAdd.ActionId}";

        // PluginsChanged also fires for plain status reports. Keep what the user is typing in the parameter
        // fields: clearing the list clears the selections, and that rebuilds the fields empty.
        var fresh = _pluginHost.GetPlugins()
            .SelectMany(plugin => plugin.Actions.Select(action => new PluginActionChoice(
                plugin.Id,
                plugin.Name,
                action.ActionId,
                action.DisplayName,
                $"{plugin.Name}: {action.DisplayName}",
                action.Parameters)))
            .ToList();

        // Most PluginsChanged events are status reports with the same actions: leave the lists (and the field
        // being typed in) alone.
        if (fresh.Select(ActionSignature).SequenceEqual(AvailablePluginActions.Select(ActionSignature)))
        {
            return;
        }

        var sceneDraft = SnapshotPendingParameters(PendingSceneActionParameters);

        AvailablePluginActions.Clear();
        foreach (var choice in fresh)
        {
            AvailablePluginActions.Add(choice);
        }

        SelectedScenePluginAction = AvailablePluginActions.FirstOrDefault(a => $"{a.PluginId}:{a.ActionId}" == prevSceneKey)
                                    ?? AvailablePluginActions.FirstOrDefault();
        ShortcutPluginActionToAdd = AvailablePluginActions.FirstOrDefault(a => $"{a.PluginId}:{a.ActionId}" == prevShortcutKey)
                                    ?? AvailablePluginActions.FirstOrDefault();

        if (SelectedScenePluginAction is { } scene && $"{scene.PluginId}:{scene.ActionId}" == prevSceneKey)
        {
            RestorePendingParameters(PendingSceneActionParameters, sceneDraft);
        }

        SyncPluginActionDescriptors();
    }

    private IReadOnlyList<PluginActionParameter>? FindPluginActionParameters(string pluginId, string actionId) =>
        AvailablePluginActions
            .FirstOrDefault(a =>
                string.Equals(a.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.ActionId, actionId, StringComparison.OrdinalIgnoreCase))
            ?.DeclaredParameters;

    private void SyncPluginActionDescriptors()
    {
        foreach (var scene in ScenePresets)
        {
            foreach (var step in scene.PluginActions)
            {
                step.ApplyDescriptors(FindPluginActionParameters(step.PluginId, step.ActionId));
            }
        }
    }

    private static string ActionSignature(PluginActionChoice choice) =>
        $"{choice.PluginId}|{choice.ActionId}|{choice.FullLabel}|" +
        string.Join(";", choice.DeclaredParameters.Select(static p => $"{p.Name}:{p.Type}:{p.Required}:{p.Description}"));

    private static Dictionary<string, string> SnapshotPendingParameters(IEnumerable<PluginActionParameterEditor> pending) =>
        pending.Where(static p => !string.IsNullOrEmpty(p.Value))
            .GroupBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First().Value, StringComparer.OrdinalIgnoreCase);

    private static void RestorePendingParameters(IEnumerable<PluginActionParameterEditor> pending, Dictionary<string, string> draft)
    {
        foreach (var parameter in pending)
        {
            if (draft.TryGetValue(parameter.Name, out var value))
            {
                parameter.Value = value;
            }
        }
    }

    private static void RebuildPendingParameters(
        ObservableCollection<PluginActionParameterEditor> target,
        IReadOnlyList<PluginActionParameter>? descriptors)
    {
        target.Clear();
        if (descriptors is null)
        {
            return;
        }

        foreach (var descriptor in descriptors)
        {
            target.Add(new PluginActionParameterEditor(
                descriptor.Name,
                descriptor.Type,
                descriptor.Description,
                descriptor.Required));
        }
    }

    private void LoadAutomationsFromSettings(AppSettings settings)
    {
        var graphs = settings.AutomationGraphs ?? [];
        var runs = _automationEngine.GetLastRuns();

        // A saved switch from this list comes back as a settings reload: keep the same items when nothing else changed.
        var sameShape = graphs.Count == AutomationItems.Count &&
                        graphs.Zip(AutomationItems).All(p => p.First.Id == p.Second.Id && p.First.Name == p.Second.Name &&
                                                            new AutomationGraphItem(p.First, static (_, _) => { }).Summary == p.Second.Summary);
        if (sameShape)
        {
            foreach (var (graph, item) in graphs.Zip(AutomationItems))
            {
                item.SyncEnabled(graph.Enabled);
            }
        }
        else
        {
            AutomationItems.Clear();
            foreach (var graph in graphs)
            {
                var item = new AutomationGraphItem(graph, OnAutomationEnabledChanged);
                if (runs.TryGetValue(graph.Id, out var run))
                {
                    item.ApplyRun(run);
                }

                AutomationItems.Add(item);
            }
        }

        ShortcutSceneToAdd = ScenePresets.FirstOrDefault();
        OnPropertyChanged(nameof(EnabledAutomationsBadge));
    }

    private void OnAutomationEnabledChanged(AutomationGraphItem item, bool enabled)
    {
        OnPropertyChanged(nameof(EnabledAutomationsBadge));
        _ = SaveAutomationEnabledAsync(item, enabled);
    }

    private async Task SaveAutomationEnabledAsync(AutomationGraphItem item, bool enabled)
    {
        try
        {
            await _settingsStore.UpdateAsync(s =>
            {
                if (s.AutomationGraphs.FirstOrDefault(g => g.Id == item.Id) is { } graph)
                {
                    graph.Enabled = enabled;
                }
            });
            AutomationsStatusText = $"'{item.Name}' is {(enabled ? "on" : "off")}.";
        }
        catch (Exception ex)
        {
            AutomationsStatusText = $"Could not save '{item.Name}': {ex.Message}";
        }
    }

    private async Task OpenAutomationEditorAsync(object? parameter)
    {
        if (_insightsServer is null)
        {
            AutomationsStatusText = "The automation editor is not ready yet. Try again in a moment.";
            return;
        }

        if (_automationEditorWindow is null)
        {
            _automationEditorWindow = new AutomationEditorWindow(_insightsServer)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            _automationEditorWindow.Closed += (_, _) => _automationEditorWindow = null;
        }

        await _automationEditorWindow.ShowEditorAsync((parameter as AutomationGraphItem)?.Id);
    }

    private async Task RunAutomationNowAsync(object? parameter)
    {
        if (parameter is not AutomationGraphItem item ||
            _settingsStore.Current.AutomationGraphs.FirstOrDefault(g => g.Id == item.Id) is not { } graph)
        {
            return;
        }

        AutomationsStatusText = $"Running '{item.Name}'...";
        try
        {
            var run = await _automationEngine.RunGraphNowAsync(graph.Clone(), null, null, dryRun: false);
            AutomationsStatusText = $"'{item.Name}': {run.Summary}";
            StatusMessage = AutomationsStatusText;
        }
        catch (Exception ex)
        {
            AutomationsStatusText = $"'{item.Name}' could not run: {ex.Message}";
        }
    }

    private void AddPluginActionToSelectedScene()
    {
        if (SelectedScene is null || SelectedScenePluginAction is null)
        {
            return;
        }

        var delayMs = int.TryParse(SceneActionDelayMsText?.Trim(), out var d) ? Math.Clamp(d, 0, 30_000) : 0;
        var step = new ScenePluginActionStepEditor(
            SelectedScenePluginAction.PluginId,
            SelectedScenePluginAction.ActionId,
            SelectedScenePluginAction.FullLabel,
            delayMs,
            PendingSceneActionParameters.Select(static p => p.Clone()));
        SelectedScene.PluginActions.Add(step);
        SelectedScene.NotifyPluginActionsChanged();

        RebuildPendingParameters(PendingSceneActionParameters, SelectedScenePluginAction.DeclaredParameters);
        OnPropertyChanged(nameof(HasPendingSceneActionParameters));

        var missing = step.Parameters.Where(static p => p.IsMissingRequired).Select(static p => p.Name).ToList();
        StatusMessage = missing.Count > 0
            ? $"Added '{SelectedScenePluginAction.FullLabel}' to '{SelectedScene.Name}'. Fill required parameter(s) ({string.Join(", ", missing)}) and click Save Scenes."
            : $"Added '{SelectedScenePluginAction.FullLabel}' to '{SelectedScene.Name}'. Click Save Scenes to persist.";
    }

    private void RemovePluginActionFromSelectedScene(object? parameter)
    {
        if (SelectedScene is null || parameter is not ScenePluginActionStepEditor step)
        {
            return;
        }

        SelectedScene.PluginActions.Remove(step);
        SelectedScene.NotifyPluginActionsChanged();
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
