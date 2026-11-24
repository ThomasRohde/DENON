using System.Collections.ObjectModel;
using System.Windows.Media;
using Denon.App.Core.Plugins;
using Denon.App.Desktop.Infrastructure;
using Denon.App.PluginSdk;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Denon.App.Desktop.ViewModels;

public sealed class PluginCardViewModel : ObservableObject
{
    private readonly Color _accentColor;
    private bool _isSelected;
    private bool _isEnabled;
    private bool _isStarted;
    private PluginHealthState _health;
    private string _statusText = string.Empty;
    private string? _lastError;

    public PluginCardViewModel(PluginRecordSnapshot snapshot)
    {
        Id = snapshot.Id;
        Name = snapshot.Name;
        Version = snapshot.Version;
        Author = snapshot.Author;
        Description = snapshot.Description;
        Category = snapshot.Category;
        IconGlyph = snapshot.IconGlyph;
        AccentHex = snapshot.AccentHex;
        _accentColor = ParseColor(snapshot.AccentHex, "#00D4FF");
        OriginLabel = snapshot.Origin == PluginOrigin.BuiltIn ? "BUILT-IN" : "EXTERNAL DLL";
        AssemblyPath = snapshot.AssemblyPath ?? "Bundled inside Denon Desktop";
        HasDashboardView = snapshot.HasDashboardView;
        CapabilitiesText = FormatCapabilities(snapshot.Capabilities);
        Actions = snapshot.Actions;
        UpdateFrom(snapshot);
    }

    public string Id { get; }

    public string Name { get; }

    public string ShortName => Id.ToLowerInvariant() switch
    {
        "philips-hue" => "Philips Hue",
        "smart-tv" => "LG webOS TV",
        "home-assistant" => "Home Assistant",
        _ => Name,
    };

    public string Version { get; }

    public string VersionTag => $"v{Version}";

    public string Author { get; }

    public string Description { get; }

    public string Category { get; }

    public string IconGlyph { get; }

    public string AccentHex { get; }

    public Brush AccentBrush => BadgeContrastHelper.CreateReadableAccentBrush(_accentColor);

    public void RefreshThemeBrushes() => OnPropertyChanged(nameof(AccentBrush));

    public string OriginLabel { get; }

    public string AssemblyPath { get; }

    public bool HasDashboardView { get; }

    public string CapabilitiesText { get; }

    public IReadOnlyList<PluginActionDescriptor> Actions { get; private set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(EnableButtonLabel));
            }
        }
    }

    public bool IsStarted
    {
        get => _isStarted;
        private set => SetProperty(ref _isStarted, value);
    }

    public PluginHealthState Health
    {
        get => _health;
        private set
        {
            if (SetProperty(ref _health, value))
            {
                OnPropertyChanged(nameof(HealthBadgeText));
                OnPropertyChanged(nameof(HealthBadgeBrush));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public string EnableButtonLabel => IsEnabled ? "Disable" : "Enable";

    public string HealthBadgeText => Health switch
    {
        PluginHealthState.Connected => "ACTIVE",
        PluginHealthState.Warning => "WARNING",
        PluginHealthState.Error => "ERROR",
        PluginHealthState.Disabled => "DISABLED",
        _ => "READY",
    };

    public Brush HealthBadgeBrush => Health switch
    {
        PluginHealthState.Connected => ThemeResources.Brush("AccentEmerald"),
        PluginHealthState.Warning => ThemeResources.Brush("AccentAmber"),
        PluginHealthState.Error => ThemeResources.Brush("AccentCrimson"),
        PluginHealthState.Disabled => ThemeResources.Brush("TextFaint"),
        _ => ThemeResources.Brush("AccentCyanBright"),
    };

    public void UpdateFrom(PluginRecordSnapshot snapshot)
    {
        IsEnabled = snapshot.IsEnabled;
        IsStarted = snapshot.IsStarted;
        Health = snapshot.Health;
        StatusText = snapshot.StatusText;
        LastError = snapshot.LastError;
        Actions = snapshot.Actions;
    }

    private static string FormatCapabilities(PluginCapabilities caps)
    {
        var list = new List<string>();
        if (caps.HasFlag(PluginCapabilities.DashboardUi)) list.Add("Dashboard UI");
        if (caps.HasFlag(PluginCapabilities.ReceiverEvents)) list.Add("AVR Events");
        if (caps.HasFlag(PluginCapabilities.SceneAutomation)) list.Add("Smart Scene Sync");
        if (caps.HasFlag(PluginCapabilities.Actions)) list.Add("Actions");
        if (caps.HasFlag(PluginCapabilities.LocalNetwork)) list.Add("Local LAN");
        if (caps.HasFlag(PluginCapabilities.AudioReactive)) list.Add("Audio Reactive FFT");
        if (caps.HasFlag(PluginCapabilities.AlbumArtSync)) list.Add("Album Art Sync");
        return list.Count > 0 ? string.Join(" • ", list) : "Extension";
    }

    private static Color ParseColor(string hex, string fallbackHex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return (Color)ColorConverter.ConvertFromString(fallbackHex);
        }
    }
}

public sealed partial class MainViewModel
{
    private PluginHostService _pluginHost = null!;
    private readonly Dictionary<string, object?> _pluginViewCache = new(StringComparer.OrdinalIgnoreCase);

    private PluginCardViewModel? _selectedPlugin;
    private object? _selectedPluginView;
    private string _pluginsStatusText = "Manage built-in & drop-in .NET plugins that extend Denon Desktop.";
    private string _activePluginsBadgeText = "1";

    public ObservableCollection<PluginCardViewModel> InstalledPlugins { get; } = [];
    public ObservableCollection<PluginActionDescriptor> SelectedPluginActions { get; } = [];

    public RelayCommand SelectPluginCommand { get; private set; } = null!;
    public RelayCommand OpenPluginCommand { get; private set; } = null!;
    public AsyncRelayCommand TogglePluginEnabledCommand { get; private set; } = null!;
    public AsyncRelayCommand RescanPluginsCommand { get; private set; } = null!;
    public RelayCommand OpenExternalPluginsFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand ExecutePluginActionCommand { get; private set; } = null!;

    public string ExternalPluginsFolderPath => _pluginHost?.ExternalPluginsDirectory ?? string.Empty;

    public PluginCardViewModel? SelectedPlugin
    {
        get => _selectedPlugin;
        set
        {
            if (SetProperty(ref _selectedPlugin, value))
            {
                foreach (var item in InstalledPlugins)
                {
                    item.IsSelected = ReferenceEquals(item, value);
                }

                RefreshSelectedPluginPresentation();
                OnPropertyChanged(nameof(HasSelectedPlugin));
                OnPropertyChanged(nameof(HasSelectedPluginView));
                OnPropertyChanged(nameof(HasSelectedPluginActions));
            }
        }
    }

    public object? SelectedPluginView
    {
        get => _selectedPluginView;
        private set
        {
            if (SetProperty(ref _selectedPluginView, value))
            {
                OnPropertyChanged(nameof(HasSelectedPluginView));
            }
        }
    }

    public bool HasSelectedPlugin => SelectedPlugin is not null;

    public bool HasSelectedPluginView => SelectedPluginView is not null && SelectedPlugin?.IsEnabled == true;

    public bool HasSelectedPluginActions => SelectedPluginActions.Count > 0;

    /// <summary>"SDK v2.0" from the SDK assembly version.</summary>
    public string PluginSdkBadgeText { get; } =
        "SDK v" + (typeof(IDenonPlugin).Assembly.GetName().Version?.ToString(2) ?? "?");

    public string PluginsStatusText
    {
        get => _pluginsStatusText;
        private set => SetProperty(ref _pluginsStatusText, value);
    }

    public string ActivePluginsBadgeText
    {
        get => _activePluginsBadgeText;
        private set => SetProperty(ref _activePluginsBadgeText, value);
    }

    private void InitializePlugins(PluginHostService pluginHost)
    {
        _pluginHost = pluginHost;

        SelectPluginCommand = new RelayCommand(param =>
        {
            if (param is PluginCardViewModel card)
            {
                SelectedPlugin = card;
            }
        });

        OpenPluginCommand = new RelayCommand(param =>
        {
            if (param is PluginCardViewModel card)
            {
                SelectedPlugin = card;
                ActiveSection = "Plugins";
            }
        });

        TogglePluginEnabledCommand = new AsyncRelayCommand(TogglePluginEnabledAsync);
        RescanPluginsCommand = new AsyncRelayCommand(_ => RescanExternalPluginsAsync());
        OpenExternalPluginsFolderCommand = new RelayCommand(_ => OpenFolder(_pluginHost.ExternalPluginsDirectory));
        ExecutePluginActionCommand = new AsyncRelayCommand(ExecuteSelectedPluginActionAsync);

        _pluginHost.PluginsChanged += () => _ = _dispatcher.InvokeAsync(RefreshPluginsList);
        RefreshPluginsList();
    }

    private void RefreshPluginsList()
    {
        var snapshots = _pluginHost.GetPlugins();
        var currentSelectedId = SelectedPlugin?.Id ?? _settingsStore.Current.Plugins.SelectedPluginId;

        // Update in place where possible to preserve bindings and selection smoothly
        foreach (var snap in snapshots)
        {
            var existing = InstalledPlugins.FirstOrDefault(p => string.Equals(p.Id, snap.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.UpdateFrom(snap);
            }
            else
            {
                InstalledPlugins.Add(new PluginCardViewModel(snap));
            }
        }

        // Remove any unloaded plugins
        for (var i = InstalledPlugins.Count - 1; i >= 0; i--)
        {
            if (!snapshots.Any(s => string.Equals(s.Id, InstalledPlugins[i].Id, StringComparison.OrdinalIgnoreCase)))
            {
                InstalledPlugins.RemoveAt(i);
            }
        }

        var enabledCount = InstalledPlugins.Count(p => p.IsEnabled);
        ActivePluginsBadgeText = enabledCount.ToString();

        var target = InstalledPlugins.FirstOrDefault(p => string.Equals(p.Id, currentSelectedId, StringComparison.OrdinalIgnoreCase))
                     ?? InstalledPlugins.FirstOrDefault();

        if (!ReferenceEquals(SelectedPlugin, target))
        {
            SelectedPlugin = target;
        }
        else
        {
            RefreshSelectedPluginPresentation();
        }

        // Re-evaluate Now Playing immediately whenever Smart TV or other live plugins update
        // (e.g., switching LG TV apps, channels, or HDMI inputs with the physical TV remote).
        if (_stateStore is not null && _stateStore.Snapshot.Status is not null)
        {
            ApplyStateSnapshot(_stateStore.Snapshot);
        }
    }

    private void RefreshSelectedPluginPresentation()
    {
        SelectedPluginActions.Clear();
        if (SelectedPlugin is null)
        {
            SelectedPluginView = null;
            OnPropertyChanged(nameof(HasSelectedPluginActions));
            OnPropertyChanged(nameof(HasSelectedPluginView));
            return;
        }

        foreach (var action in SelectedPlugin.Actions)
        {
            SelectedPluginActions.Add(action);
        }

        if (!SelectedPlugin.IsEnabled)
        {
            SelectedPluginView = null;
        }
        else
        {
            if (!_pluginViewCache.TryGetValue(SelectedPlugin.Id, out var cachedView))
            {
                var instance = _pluginHost.GetPluginInstance(SelectedPlugin.Id);
                cachedView = instance is IPluginViewProvider viewProvider
                    ? viewProvider.CreateDashboardView()
                    : null;
                _pluginViewCache[SelectedPlugin.Id] = cachedView;
            }

            SelectedPluginView = cachedView;
        }

        OnPropertyChanged(nameof(HasSelectedPluginActions));
        OnPropertyChanged(nameof(HasSelectedPluginView));
    }

    private async Task TogglePluginEnabledAsync(object? parameter)
    {
        var target = parameter as PluginCardViewModel ?? SelectedPlugin;
        if (target is null)
        {
            return;
        }

        var nextState = !target.IsEnabled;
        await _pluginHost.SetPluginEnabledAsync(target.Id, nextState);
        PluginsStatusText = nextState
            ? $"Enabled plugin '{target.Name}'."
            : $"Disabled plugin '{target.Name}'.";
    }

    private async Task RescanExternalPluginsAsync()
    {
        PluginsStatusText = "Scanning external plugins directory for .NET plugin DLLs...";
        var added = await _pluginHost.RescanExternalPluginsAsync();
        PluginsStatusText = added > 0
            ? $"Discovered and loaded {added} new external plugin(s)."
            : $"Scan complete — no new external plugin DLLs found in {_pluginHost.ExternalPluginsDirectory}.";
    }

    private async Task ExecuteSelectedPluginActionAsync(object? parameter)
    {
        if (SelectedPlugin is null || parameter is not PluginActionDescriptor action)
        {
            return;
        }

        var result = await _pluginHost.ExecuteActionAsync(SelectedPlugin.Id, action.ActionId);
        PluginsStatusText = result.Success
            ? $"✓ {action.DisplayName}: {result.Message}"
            : $"⚠ {action.DisplayName}: {result.Message}";
        StatusMessage = result.Message;
    }
}
