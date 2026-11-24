using System.Collections.ObjectModel;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;
using Denon.App.PluginSdk;

namespace Denon.App.Desktop.ViewModels;

/// <summary>Editable single parameter row for a plugin action step inside a Smart Scene or Automation Rule.</summary>
public sealed class PluginActionParameterEditor : ObservableObject
{
    private string _type;
    private string _description;
    private bool _required;
    private string _value;

    public PluginActionParameterEditor(
        string name,
        string type = "string",
        string description = "",
        bool required = false,
        string value = "")
    {
        Name = name;
        _type = string.IsNullOrWhiteSpace(type) ? "string" : type;
        _description = description ?? string.Empty;
        _required = required;
        _value = value ?? string.Empty;
    }

    public string Name { get; }

    public string Type
    {
        get => _type;
        private set
        {
            if (SetProperty(ref _type, value))
            {
                OnPropertyChanged(nameof(PlaceholderText));
            }
        }
    }

    public string Description
    {
        get => _description;
        private set
        {
            if (SetProperty(ref _description, value))
            {
                OnPropertyChanged(nameof(HasDescription));
                OnPropertyChanged(nameof(PlaceholderText));
            }
        }
    }

    public bool Required
    {
        get => _required;
        private set
        {
            if (SetProperty(ref _required, value))
            {
                OnPropertyChanged(nameof(IsMissingRequired));
            }
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _value, next))
            {
                OnPropertyChanged(nameof(IsMissingRequired));
            }
        }
    }

    public bool HasDescription => !string.IsNullOrWhiteSpace(_description);

    public bool IsMissingRequired => _required && string.IsNullOrWhiteSpace(_value);

    public string PlaceholderText => HasDescription ? _description : $"Enter {Name} ({Type})";

    public void UpdateDescriptor(PluginActionParameter descriptor)
    {
        Type = string.IsNullOrWhiteSpace(descriptor.Type) ? "string" : descriptor.Type;
        Description = descriptor.Description ?? string.Empty;
        Required = descriptor.Required;
    }

    public PluginActionParameterEditor Clone() => new(Name, Type, Description, Required, Value);
}

/// <summary>Editable plugin action step inside a Smart Scene or Automation Rule, including declared action parameters.</summary>
public sealed class ScenePluginActionStepEditor : ObservableObject
{
    private int _delayMs;

    public ScenePluginActionStepEditor(
        string pluginId,
        string actionId,
        string displayName,
        int delayMs = 0,
        IEnumerable<PluginActionParameterEditor>? parameters = null)
    {
        PluginId = pluginId;
        ActionId = actionId;
        DisplayName = displayName;
        _delayMs = delayMs;

        if (parameters is not null)
        {
            foreach (var param in parameters)
            {
                AttachParameter(param);
            }
        }
    }

    public string PluginId { get; }

    public string ActionId { get; }

    public string DisplayName { get; }

    public int DelayMs
    {
        get => _delayMs;
        set
        {
            if (SetProperty(ref _delayMs, value))
            {
                OnPropertyChanged(nameof(HasDelay));
            }
        }
    }

    public bool HasDelay => DelayMs > 0;

    public ObservableCollection<PluginActionParameterEditor> Parameters { get; } = [];

    public bool HasParameters => Parameters.Count > 0;

    public bool HasMissingRequiredParameters => Parameters.Any(static p => p.IsMissingRequired);

    public string ParametersSummary =>
        string.Join(", ", Parameters
            .Where(static p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(static p => $"{p.Name}={p.Value.Trim()}"));

    public void ApplyDescriptors(IReadOnlyList<PluginActionParameter>? descriptors)
    {
        if (descriptors is null || descriptors.Count == 0)
        {
            return;
        }

        var existingValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in Parameters)
        {
            existingValues[existing.Name] = existing.Value;
        }

        var declaredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rebuilt = new List<PluginActionParameterEditor>(descriptors.Count + existingValues.Count);
        foreach (var descriptor in descriptors)
        {
            declaredNames.Add(descriptor.Name);
            existingValues.TryGetValue(descriptor.Name, out var saved);
            rebuilt.Add(new PluginActionParameterEditor(
                descriptor.Name,
                descriptor.Type,
                descriptor.Description,
                descriptor.Required,
                saved ?? string.Empty));
        }

        // Preserve any extra saved keys not declared by the current descriptor so no user data is dropped.
        foreach (var existing in Parameters)
        {
            if (!declaredNames.Contains(existing.Name) && !string.IsNullOrWhiteSpace(existing.Value))
            {
                rebuilt.Add(existing.Clone());
            }
        }

        Parameters.Clear();
        foreach (var item in rebuilt)
        {
            AttachParameter(item);
        }

        NotifyParametersChanged();
    }

    public static ScenePluginActionStepEditor FromModel(
        ScenePluginActionStep model,
        IReadOnlyList<PluginActionParameter>? descriptors = null)
    {
        var saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (model.Parameters is not null)
        {
            foreach (var (key, val) in model.Parameters)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    saved[key] = val ?? string.Empty;
                }
            }
        }

        var declaredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<PluginActionParameterEditor>();

        if (descriptors is not null)
        {
            foreach (var descriptor in descriptors)
            {
                declaredNames.Add(descriptor.Name);
                saved.TryGetValue(descriptor.Name, out var value);
                items.Add(new PluginActionParameterEditor(
                    descriptor.Name,
                    descriptor.Type,
                    descriptor.Description,
                    descriptor.Required,
                    value ?? string.Empty));
            }
        }

        foreach (var (key, val) in saved)
        {
            if (!declaredNames.Contains(key))
            {
                items.Add(new PluginActionParameterEditor(key, "string", string.Empty, required: false, value: val));
            }
        }

        return new ScenePluginActionStepEditor(model.PluginId, model.ActionId, model.DisplayName, model.DelayMs, items);
    }

    public ScenePluginActionStep ToModel()
    {
        Dictionary<string, string>? dict = null;
        foreach (var param in Parameters)
        {
            if (!string.IsNullOrWhiteSpace(param.Value))
            {
                dict ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                dict[param.Name] = param.Value.Trim();
            }
        }

        return new ScenePluginActionStep
        {
            PluginId = PluginId,
            ActionId = ActionId,
            DisplayName = DisplayName,
            DelayMs = DelayMs,
            Parameters = dict,
        };
    }

    public ScenePluginActionStepEditor Clone() =>
        new(PluginId, ActionId, DisplayName, DelayMs, Parameters.Select(static p => p.Clone()));

    private void AttachParameter(PluginActionParameterEditor param)
    {
        param.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMissingRequiredParameters));
            OnPropertyChanged(nameof(ParametersSummary));
        };
        Parameters.Add(param);
    }

    private void NotifyParametersChanged()
    {
        OnPropertyChanged(nameof(HasParameters));
        OnPropertyChanged(nameof(HasMissingRequiredParameters));
        OnPropertyChanged(nameof(ParametersSummary));
    }
}
