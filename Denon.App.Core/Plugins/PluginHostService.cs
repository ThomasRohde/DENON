using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.PluginSdk;

namespace Denon.App.Core.Plugins;

public enum PluginOrigin
{
    BuiltIn = 0,
    ExternalDll = 1,
}

public sealed class PluginRecordSnapshot
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string Author { get; init; }

    public required string Description { get; init; }

    public required string Category { get; init; }

    public required string AccentHex { get; init; }

    public required string IconGlyph { get; init; }

    public required PluginCapabilities Capabilities { get; init; }

    public required PluginOrigin Origin { get; init; }

    public string? AssemblyPath { get; init; }

    public bool IsEnabled { get; init; }

    public bool IsStarted { get; init; }

    public bool HasDashboardView { get; init; }

    public PluginHealthState Health { get; init; }

    public string StatusText { get; init; } = string.Empty;

    public string? LastError { get; init; }

    public IReadOnlyList<PluginActionDescriptor> Actions { get; init; } = [];
}

public sealed class PluginHostService : IAsyncDisposable
{
    private readonly List<IDenonPlugin> _builtInPlugins;
    private readonly AppSettingsStore _settingsStore;
    private readonly ReceiverStateStore _stateStore;
    private readonly ReceiverController _receiverController;
    private readonly EventTelemetryLog _telemetry;
    private readonly AppLogStore _logStore;
    private readonly object _syncRoot = new();
    private readonly List<ManagedPluginEntry> _entries = [];
    private readonly List<PluginAssemblyLoadContext> _externalContexts = [];
    private volatile IAudioReactivePlugin[] _activeAudioPlugins = [];

    private PluginReceiverSnapshot? _lastBroadcastReceiver;
    private bool _isInitialized;
    private bool _disposed;

    public PluginHostService(
        IEnumerable<IDenonPlugin> builtInPlugins,
        AppSettingsStore settingsStore,
        ReceiverStateStore stateStore,
        ReceiverController receiverController,
        EventTelemetryLog telemetry,
        AppLogStore logStore)
    {
        _builtInPlugins = builtInPlugins.ToList();
        _settingsStore = settingsStore;
        _stateStore = stateStore;
        _receiverController = receiverController;
        _telemetry = telemetry;
        _logStore = logStore;
    }

    public event Action? PluginsChanged;

    public string ExternalPluginsDirectory => AppDataPaths.ExternalPluginsDirectory;

    public IReadOnlyList<PluginRecordSnapshot> GetPlugins()
    {
        lock (_syncRoot)
        {
            return _entries.Select(ToSnapshot).ToList();
        }
    }

    public IDenonPlugin? GetPluginInstance(string pluginId)
    {
        lock (_syncRoot)
        {
            return _entries.FirstOrDefault(e => string.Equals(e.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))?.Plugin;
        }
    }

    public async Task InitializeAndStartAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        AppDataPaths.EnsureCreated();

        var disabledSet = new HashSet<string>(
            _settingsStore.Current.Plugins.DisabledPluginIds ?? [],
            StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in _builtInPlugins)
        {
            await RegisterAndInitEntryAsync(plugin, PluginOrigin.BuiltIn, null, disabledSet, cancellationToken);
        }

        await DiscoverExternalPluginsCoreAsync(disabledSet, cancellationToken);

        _lastBroadcastReceiver = MapSnapshot(_stateStore.Snapshot);
        _stateStore.StateChanged += OnReceiverStateChanged;
        _receiverController.SceneActivated += OnReceiverSceneActivated;

        NotifyPluginsChanged();
    }

    public async Task<int> RescanExternalPluginsAsync(CancellationToken cancellationToken = default)
    {
        AppDataPaths.EnsureCreated();
        var disabledSet = new HashSet<string>(
            _settingsStore.Current.Plugins.DisabledPluginIds ?? [],
            StringComparer.OrdinalIgnoreCase);

        var added = await DiscoverExternalPluginsCoreAsync(disabledSet, cancellationToken);
        NotifyPluginsChanged();
        return added;
    }

    public async Task SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        ManagedPluginEntry? entry;
        lock (_syncRoot)
        {
            entry = _entries.FirstOrDefault(e => string.Equals(e.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }

        if (entry is null)
        {
            return;
        }

        if (entry.IsEnabled == enabled)
        {
            return;
        }

        entry.IsEnabled = enabled;
        if (enabled)
        {
            await StartEntryAsync(entry, cancellationToken);
        }
        else
        {
            await StopEntryAsync(entry, cancellationToken);
        }

        var updated = _settingsStore.Current.Clone();
        var disabled = new HashSet<string>(updated.Plugins.DisabledPluginIds ?? [], StringComparer.OrdinalIgnoreCase);
        if (enabled)
        {
            disabled.Remove(entry.Manifest.Id);
        }
        else
        {
            disabled.Add(entry.Manifest.Id);
        }

        updated.Plugins.DisabledPluginIds = disabled.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToList();
        await _settingsStore.SaveAsync(updated, cancellationToken);

        NotifyPluginsChanged();
    }

    public async Task<PluginActionResult> ExecuteActionAsync(
        string pluginId,
        string actionId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ManagedPluginEntry? entry;
        lock (_syncRoot)
        {
            entry = _entries.FirstOrDefault(e => string.Equals(e.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }

        if (entry is null)
        {
            return PluginActionResult.Fail($"Plugin '{pluginId}' was not found.");
        }

        if (!entry.IsEnabled)
        {
            return PluginActionResult.Fail($"Plugin '{entry.Manifest.Name}' is disabled.");
        }

        try
        {
            var isReadOnlyRefresh = actionId.EndsWith(".refresh", StringComparison.OrdinalIgnoreCase);
            var domain = MapPluginToDomain(entry.Manifest.Id);
            if (!isReadOnlyRefresh)
            {
                _telemetry.MarkOrigin(domain, "desktop_ui", overwriteRecent: false);
            }

            var result = await entry.Plugin.ExecuteActionAsync(actionId, parameters, cancellationToken);
            if (!result.Success)
            {
                _logStore.Warning($"[Plugin:{entry.Manifest.Id}] Action '{actionId}' failed: {result.Message}");
            }
            else
            {
                _logStore.Info($"[Plugin:{entry.Manifest.Id}] Action '{actionId}': {result.Message}");
                if (!isReadOnlyRefresh)
                {
                    var effectiveOrigin = _telemetry.ResolveOrigin(domain, fallback: "desktop_ui");
                    _telemetry.Record(
                        EventTelemetryLog.StreamControl,
                        entry.Manifest.Id,
                        "plugin.action",
                        effectiveOrigin,
                        new Dictionary<string, object?>
                        {
                            ["pluginId"] = entry.Manifest.Id,
                            ["actionId"] = actionId,
                        });
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            entry.LastError = ex.Message;
            entry.Health = PluginHealthState.Error;
            entry.StatusText = $"Action error: {ex.Message}";
            _logStore.Error($"[Plugin:{entry.Manifest.Id}] Action '{actionId}' threw an exception.", ex);
            NotifyPluginsChanged();
            return PluginActionResult.Fail(ex.Message);
        }
    }

    public Task NotifySceneActivatedAsync(ScenePreset scene, CancellationToken cancellationToken = default) =>
        NotifySceneActivatedCoreAsync(scene, cancellationToken);

    public bool HasActiveAudioReactivePlugins()
    {
        var plugins = _activeAudioPlugins;
        for (var i = 0; i < plugins.Length; i++)
        {
            if (plugins[i].IsAudioSyncActive)
            {
                return true;
            }
        }

        return false;
    }

    public void PublishAudioFrame(in PluginAudioFrame frame)
    {
        var plugins = _activeAudioPlugins;
        for (var i = 0; i < plugins.Length; i++)
        {
            var plugin = plugins[i];
            if (!plugin.IsAudioSyncActive)
            {
                continue;
            }

            try
            {
                plugin.OnAudioFrame(in frame);
            }
            catch
            {
                // Isolate audio hot-path exceptions from host capture loop
            }
        }
    }

    public async Task PublishAlbumArtPaletteAsync(PluginAlbumArtPalette palette, CancellationToken cancellationToken = default)
    {
        var plugins = _activeAudioPlugins;
        foreach (var plugin in plugins)
        {
            try
            {
                await plugin.OnAlbumArtPaletteChangedAsync(palette, cancellationToken);
            }
            catch (Exception ex)
            {
                _logStore.Warning($"[Plugin] Album art palette callback warning: {ex.Message}");
            }
        }
    }

    private async Task NotifySceneActivatedCoreAsync(ScenePreset scene, CancellationToken cancellationToken = default)
    {
        var explicitlyHandledPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (scene.PluginActions is { Count: > 0 })
        {
            foreach (var step in scene.PluginActions)
            {
                if (!string.IsNullOrWhiteSpace(step.PluginId))
                {
                    explicitlyHandledPlugins.Add(step.PluginId);
                }
            }

            await ExecuteActionChainAsync(scene.PluginActions, cancellationToken);
        }

        var snapshot = MapSnapshot(_stateStore.Snapshot);
        var evt = new PluginReceiverEvent(
            PluginReceiverEventType.SceneActivated,
            snapshot,
            SceneId: scene.Id,
            SceneName: scene.Name,
            Timestamp: DateTimeOffset.Now);

        await BroadcastReceiverEventAsync(evt, cancellationToken, skipPluginIds: explicitlyHandledPlugins);
    }

    public async Task<IReadOnlyList<(ScenePluginActionStep Step, PluginActionResult Result)>> ExecuteActionChainAsync(
        IEnumerable<ScenePluginActionStep> steps,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<(ScenePluginActionStep Step, PluginActionResult Result)>();
        foreach (var step in steps)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (step.DelayMs > 0)
            {
                await Task.Delay(Math.Clamp(step.DelayMs, 0, 30_000), cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(step.PluginId) && !string.IsNullOrWhiteSpace(step.ActionId))
            {
                var res = await ExecuteActionAsync(step.PluginId, step.ActionId, step.Parameters, cancellationToken);
                outcomes.Add((step, res));
            }
        }

        return outcomes;
    }

    /// <summary>True from the first <see cref="StopAllAsync"/> call (app exit). Lets telemetry tell an app
    /// shutdown apart from a device that went offline while plugins report their stop.</summary>
    public bool IsStoppingAll { get; private set; }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        IsStoppingAll = true;
        _stateStore.StateChanged -= OnReceiverStateChanged;
        _receiverController.SceneActivated -= OnReceiverSceneActivated;

        List<ManagedPluginEntry> snapshot;
        lock (_syncRoot)
        {
            snapshot = [.. _entries];
        }

        foreach (var entry in snapshot)
        {
            await StopEntryAsync(entry, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAllAsync();

        List<ManagedPluginEntry> snapshot;
        lock (_syncRoot)
        {
            snapshot = [.. _entries];
            _entries.Clear();
        }

        foreach (var entry in snapshot)
        {
            try
            {
                await entry.Plugin.DisposeAsync();
            }
            catch
            {
            }
        }

        foreach (var ctx in _externalContexts)
        {
            try
            {
                ctx.Unload();
            }
            catch
            {
            }
        }

        _externalContexts.Clear();
    }

    private async Task<int> DiscoverExternalPluginsCoreAsync(HashSet<string> disabledSet, CancellationToken cancellationToken)
    {
        var added = 0;
        var candidateFolders = new[]
        {
            AppDataPaths.ExternalPluginsDirectory,
            Path.Combine(AppContext.BaseDirectory, "plugins"),
        };

        var dllFiles = new List<string>();
        foreach (var folder in candidateFolders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                dllFiles.AddRange(Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                _logStore.Warning($"Could not scan plugin folder '{folder}': {ex.Message}");
            }
        }

        foreach (var dllPath in dllFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(dllPath);
            if (fileName.StartsWith("Denon.App.", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool alreadyLoaded;
            lock (_syncRoot)
            {
                alreadyLoaded = _entries.Any(e => string.Equals(e.AssemblyPath, dllPath, StringComparison.OrdinalIgnoreCase));
            }

            if (alreadyLoaded)
            {
                continue;
            }

            try
            {
                var loadContext = new PluginAssemblyLoadContext(dllPath);
                var assembly = loadContext.LoadFromAssemblyPath(dllPath);
                var pluginTypes = assembly
                    .GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IDenonPlugin).IsAssignableFrom(t))
                    .ToList();

                if (pluginTypes.Count == 0)
                {
                    loadContext.Unload();
                    continue;
                }

                _externalContexts.Add(loadContext);

                foreach (var pluginType in pluginTypes)
                {
                    if (Activator.CreateInstance(pluginType) is IDenonPlugin instance)
                    {
                        var registered = await RegisterAndInitEntryAsync(
                            instance,
                            PluginOrigin.ExternalDll,
                            dllPath,
                            disabledSet,
                            cancellationToken);
                        if (registered)
                        {
                            added++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logStore.Warning($"Failed to inspect external plugin assembly '{fileName}': {ex.Message}");
            }
        }

        return added;
    }

    private async Task<bool> RegisterAndInitEntryAsync(
        IDenonPlugin plugin,
        PluginOrigin origin,
        string? assemblyPath,
        HashSet<string> disabledSet,
        CancellationToken cancellationToken)
    {
        var manifest = plugin.Manifest;
        lock (_syncRoot)
        {
            if (_entries.Any(e => string.Equals(e.Manifest.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        var entry = new ManagedPluginEntry(plugin, manifest, origin, assemblyPath)
        {
            IsEnabled = !disabledSet.Contains(manifest.Id),
            Health = PluginHealthState.Idle,
            StatusText = "Initializing...",
        };

        var context = new PluginContextImpl(
            manifest.Id,
            new PluginLoggerImpl(manifest.Id, _logStore),
            new PluginStorageImpl(AppDataPaths.GetPluginDataDirectory(manifest.Id)),
            new PluginReceiverBridgeImpl(_stateStore),
            manifest.Capabilities.HasFlag(PluginCapabilities.ReceiverControl)
                ? new PluginReceiverControlImpl(manifest.Id, _receiverController, _stateStore, _logStore)
                : null,
            new PluginEventLoggerImpl(manifest.Id, _telemetry),
            (statusText, health) =>
            {
                entry.StatusText = statusText;
                entry.Health = entry.IsEnabled ? health : PluginHealthState.Disabled;
                NotifyPluginsChanged();
            });

        lock (_syncRoot)
        {
            _entries.Add(entry);
        }

        try
        {
            await plugin.InitializeAsync(context, cancellationToken);
            entry.Actions = plugin.GetActions() ?? [];
            _logStore.Info($"Loaded plugin '{manifest.Name}' v{manifest.Version} ({origin}).");

            if (entry.IsEnabled)
            {
                await StartEntryAsync(entry, cancellationToken);
            }
            else
            {
                entry.Health = PluginHealthState.Disabled;
                entry.StatusText = "Disabled";
            }
        }
        catch (Exception ex)
        {
            entry.LastError = ex.Message;
            entry.Health = PluginHealthState.Error;
            entry.StatusText = $"Init failed: {ex.Message}";
            _logStore.Error($"[Plugin:{manifest.Id}] Initialization failed.", ex);
        }

        return true;
    }

    private async Task StartEntryAsync(ManagedPluginEntry entry, CancellationToken cancellationToken)
    {
        if (entry.IsStarted)
        {
            return;
        }

        try
        {
            entry.LastError = null;
            await entry.Plugin.StartAsync(cancellationToken);
            entry.IsStarted = true;
            entry.Actions = entry.Plugin.GetActions() ?? [];
            if (entry.Health == PluginHealthState.Disabled)
            {
                entry.Health = PluginHealthState.Idle;
            }
        }
        catch (Exception ex)
        {
            entry.LastError = ex.Message;
            entry.Health = PluginHealthState.Error;
            entry.StatusText = $"Start failed: {ex.Message}";
            _logStore.Error($"[Plugin:{entry.Manifest.Id}] Start failed.", ex);
        }
    }

    private async Task StopEntryAsync(ManagedPluginEntry entry, CancellationToken cancellationToken)
    {
        if (!entry.IsStarted)
        {
            if (!entry.IsEnabled)
            {
                entry.Health = PluginHealthState.Disabled;
                entry.StatusText = "Disabled";
            }

            return;
        }

        try
        {
            await entry.Plugin.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logStore.Warning($"[Plugin:{entry.Manifest.Id}] Stop warning: {ex.Message}");
        }
        finally
        {
            entry.IsStarted = false;
            if (!entry.IsEnabled)
            {
                entry.Health = PluginHealthState.Disabled;
                entry.StatusText = "Disabled";
            }
        }
    }

    private void OnReceiverSceneActivated(ScenePreset scene)
    {
        _ = Task.Run(() => NotifySceneActivatedAsync(scene, CancellationToken.None));
    }

    private void OnReceiverStateChanged(ReceiverStateSnapshot rawSnapshot)
    {
        var current = MapSnapshot(rawSnapshot);
        var previous = _lastBroadcastReceiver;
        _lastBroadcastReceiver = current;

        if (previous is null || !current.IsConnected)
        {
            return;
        }

        var eventsToDispatch = new List<PluginReceiverEvent>();

        if (previous.IsConnected && previous.IsPoweredOn != current.IsPoweredOn)
        {
            eventsToDispatch.Add(new PluginReceiverEvent(PluginReceiverEventType.PowerChanged, current));
        }

        if (previous.IsConnected &&
            !string.Equals(previous.InputSource, current.InputSource, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(current.InputSource))
        {
            eventsToDispatch.Add(new PluginReceiverEvent(PluginReceiverEventType.InputChanged, current));
        }

        if (previous.IsConnected &&
            !string.Equals(previous.PlayState, current.PlayState, StringComparison.OrdinalIgnoreCase))
        {
            eventsToDispatch.Add(new PluginReceiverEvent(PluginReceiverEventType.PlaybackChanged, current));
        }

        if (previous.IsConnected &&
            !string.Equals(previous.SoundMode, current.SoundMode, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(current.SoundMode))
        {
            eventsToDispatch.Add(new PluginReceiverEvent(PluginReceiverEventType.SoundModeChanged, current));
        }

        if (eventsToDispatch.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var evt in eventsToDispatch)
            {
                await BroadcastReceiverEventAsync(evt, CancellationToken.None);
            }
        });
    }

    private async Task BroadcastReceiverEventAsync(
        PluginReceiverEvent evt,
        CancellationToken cancellationToken,
        ISet<string>? skipPluginIds = null)
    {
        List<ManagedPluginEntry> activeEntries;
        lock (_syncRoot)
        {
            activeEntries = _entries.Where(e => e.IsEnabled && e.IsStarted).ToList();
        }

        foreach (var entry in activeEntries)
        {
            if (skipPluginIds is not null && skipPluginIds.Contains(entry.Manifest.Id))
            {
                continue;
            }

            try
            {
                await entry.Plugin.OnReceiverEventAsync(evt, cancellationToken);
            }
            catch (Exception ex)
            {
                _logStore.Warning($"[Plugin:{entry.Manifest.Id}] Receiver event '{evt.EventType}' error: {ex.Message}");
            }
        }
    }

    private void NotifyPluginsChanged()
    {
        lock (_syncRoot)
        {
            _activeAudioPlugins = _entries
                .Where(static e => e.IsEnabled && e.IsStarted && e.Plugin is IAudioReactivePlugin)
                .Select(static e => (IAudioReactivePlugin)e.Plugin)
                .ToArray();
        }

        PluginsChanged?.Invoke();
    }

    private static PluginRecordSnapshot ToSnapshot(ManagedPluginEntry entry) =>
        new()
        {
            Id = entry.Manifest.Id,
            Name = entry.Manifest.Name,
            Version = entry.Manifest.Version,
            Author = entry.Manifest.Author,
            Description = entry.Manifest.Description,
            Category = entry.Manifest.Category,
            AccentHex = entry.Manifest.AccentHex,
            IconGlyph = entry.Manifest.IconGlyph,
            Capabilities = entry.Manifest.Capabilities,
            Origin = entry.Origin,
            AssemblyPath = entry.AssemblyPath,
            IsEnabled = entry.IsEnabled,
            IsStarted = entry.IsStarted,
            HasDashboardView = entry.Plugin is IPluginViewProvider,
            Health = entry.Health,
            StatusText = entry.StatusText,
            LastError = entry.LastError,
            Actions = entry.Actions,
        };

    private static PluginReceiverSnapshot MapSnapshot(ReceiverStateSnapshot snapshot)
    {
        var status = snapshot.Status;
        if (status is null)
        {
            return PluginReceiverSnapshot.Empty;
        }

        return new PluginReceiverSnapshot(
            IsConnected: snapshot.IsConnected,
            IsPoweredOn: status.IsPoweredOn,
            Volume: status.Volume,
            IsMuted: status.IsMuted,
            InputSource: status.InputSource,
            SoundMode: status.SoundMode,
            PlayState: status.PlayState,
            NowPlayingTitle: status.NowPlaying?.Song ?? string.Empty,
            ModelName: status.Player?.Model ?? string.Empty);
    }

    private sealed class ManagedPluginEntry
    {
        public ManagedPluginEntry(IDenonPlugin plugin, PluginManifest manifest, PluginOrigin origin, string? assemblyPath)
        {
            Plugin = plugin;
            Manifest = manifest;
            Origin = origin;
            AssemblyPath = assemblyPath;
        }

        public IDenonPlugin Plugin { get; }

        public PluginManifest Manifest { get; }

        public PluginOrigin Origin { get; }

        public string? AssemblyPath { get; }

        public bool IsEnabled { get; set; }

        public bool IsStarted { get; set; }

        public PluginHealthState Health { get; set; } = PluginHealthState.Idle;

        public string StatusText { get; set; } = string.Empty;

        public string? LastError { get; set; }

        public IReadOnlyList<PluginActionDescriptor> Actions { get; set; } = [];
    }

    private sealed class PluginAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginAssemblyLoadContext(string pluginPath)
            : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share the Plugin SDK and host framework assemblies with the default context so
            // IDenonPlugin / WPF types match across the boundary.
            if (string.Equals(assemblyName.Name, "Denon.App.PluginSdk", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath is not null ? LoadFromAssemblyPath(assemblyPath) : null;
        }
    }

    private sealed class PluginLoggerImpl(string pluginId, AppLogStore logStore) : IPluginLogger
    {
        public void Info(string message) => logStore.Info($"[Plugin:{pluginId}] {message}");

        public void Warning(string message) => logStore.Warning($"[Plugin:{pluginId}] {message}");

        public void Error(string message, Exception? exception = null)
        {
            if (exception is null)
            {
                logStore.Error($"[Plugin:{pluginId}] {message}");
            }
            else
            {
                logStore.Error($"[Plugin:{pluginId}] {message}", exception);
            }
        }
    }

    private sealed class PluginStorageImpl(string pluginDataDirectory) : IPluginStorage
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        private readonly SemaphoreSlim _ioGate = new(1, 1);

        public string PluginDataDirectory { get; } = pluginDataDirectory;

        public async Task<T> LoadConfigAsync<T>(Func<T> defaultFactory, CancellationToken cancellationToken = default)
            where T : class
        {
            await _ioGate.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(PluginDataDirectory);
                var configPath = Path.Combine(PluginDataDirectory, "config.json");
                if (!File.Exists(configPath))
                {
                    return defaultFactory();
                }

                var json = await File.ReadAllTextAsync(configPath, cancellationToken);
                return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? defaultFactory();
            }
            catch
            {
                return defaultFactory();
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public async Task SaveConfigAsync<T>(T config, CancellationToken cancellationToken = default)
            where T : class
        {
            await _ioGate.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(PluginDataDirectory);
                var configPath = Path.Combine(PluginDataDirectory, "config.json");
                var tempPath = configPath + ".tmp";
                var json = JsonSerializer.Serialize(config, JsonOptions);
                await File.WriteAllTextAsync(tempPath, json, cancellationToken);
                File.Move(tempPath, configPath, overwrite: true);
            }
            finally
            {
                _ioGate.Release();
            }
        }
    }

    private sealed class PluginReceiverBridgeImpl(ReceiverStateStore stateStore) : IPluginReceiverBridge
    {
        public PluginReceiverSnapshot CurrentSnapshot => MapSnapshot(stateStore.Snapshot);
    }

    /// <summary>
    /// Receiver command channel for plugins that declare <see cref="PluginCapabilities.ReceiverControl"/>.
    /// Every command is logged with the plugin id.
    /// </summary>
    private sealed class PluginReceiverControlImpl(
        string pluginId,
        ReceiverController controller,
        ReceiverStateStore stateStore,
        AppLogStore logStore) : IPluginReceiverControl
    {
        public Task<PluginReceiverSnapshot> SetPowerAsync(bool powerOn, CancellationToken cancellationToken = default) =>
            RunAsync($"power {(powerOn ? "on" : "off")}", ct => controller.SetPowerAsync(powerOn, ct), cancellationToken);

        public Task<PluginReceiverSnapshot> SetInputSourceAsync(string sourceCode, CancellationToken cancellationToken = default)
        {
            var code = RequireCode(sourceCode, nameof(sourceCode));
            return RunAsync($"input {code}", ct => controller.SetInputSourceAsync(code, ct), cancellationToken);
        }

        public Task<PluginReceiverSnapshot> SetSoundModeAsync(string modeCode, CancellationToken cancellationToken = default)
        {
            var code = RequireCode(modeCode, nameof(modeCode));
            return RunAsync($"sound mode {code}", ct => controller.SetSoundModeAsync(code, ct), cancellationToken);
        }

        public Task<PluginReceiverSnapshot> SetVolumeAsync(int level, CancellationToken cancellationToken = default)
        {
            var clamped = Math.Clamp(level, 0, 98);
            return RunAsync($"volume {clamped}", ct => controller.SetVolumeAsync(clamped, ct), cancellationToken);
        }

        public Task<PluginReceiverSnapshot> SetMuteAsync(bool isMuted, CancellationToken cancellationToken = default) =>
            RunAsync($"mute {(isMuted ? "on" : "off")}", ct => controller.SetMuteAsync(isMuted, ct), cancellationToken);

        public IReadOnlyList<PluginSceneInfo> GetScenes() =>
            controller.GetScenes().Select(s => new PluginSceneInfo(s.Id, s.Name, s.Description)).ToList();

        public Task<PluginReceiverSnapshot> RunSceneAsync(string sceneIdOrName, CancellationToken cancellationToken = default)
        {
            var key = (sceneIdOrName ?? string.Empty).Trim();
            var scenes = controller.GetScenes();
            var scene = scenes.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase))
                        ?? scenes.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No Smart Scene matches '{key}'.", nameof(sceneIdOrName));
            return RunAsync($"scene '{scene.Name}'", ct => controller.ApplySceneAsync(scene, ct), cancellationToken);
        }

        private async Task<PluginReceiverSnapshot> RunAsync(
            string description,
            Func<CancellationToken, Task<ReceiverStatus>> command,
            CancellationToken cancellationToken)
        {
            logStore.Info($"[Plugin:{pluginId}] Receiver command: {description}");
            try
            {
                await command(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logStore.Warning($"[Plugin:{pluginId}] Receiver command '{description}' failed: {ex.Message}");
                throw;
            }

            return MapSnapshot(stateStore.Snapshot);
        }

        private static string RequireCode(string value, string paramName)
        {
            var code = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (code.Length is 0 or > 16 || code.Any(c => char.IsControl(c)))
            {
                throw new ArgumentException("Code must be 1–16 printable characters.", paramName);
            }

            return code;
        }
    }

    private sealed class PluginEventLoggerImpl(string pluginId, EventTelemetryLog telemetry) : IPluginEventLogger
    {
        public void RecordEvent(
            string eventName,
            IReadOnlyDictionary<string, object?>? data = null,
            string? stream = null,
            string origin = "plugin")
        {
            var defaultDomain = MapPluginToDomain(pluginId);
            var targetStream = !string.IsNullOrWhiteSpace(stream)
                ? stream
                : defaultDomain is EventTelemetryLog.StreamTv or EventTelemetryLog.StreamLighting
                    ? defaultDomain
                    : $"plugin:{pluginId}";

            var resolvedOrigin = string.IsNullOrWhiteSpace(origin) ||
                                 origin.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                                 origin.Equals("external", StringComparison.OrdinalIgnoreCase)
                ? telemetry.ResolveOrigin(defaultDomain, fallback: "external")
                : origin.Equals("plugin", StringComparison.OrdinalIgnoreCase)
                    ? telemetry.ResolveOrigin(defaultDomain, fallback: "plugin")
                    : origin;

            telemetry.Record(targetStream, pluginId, eventName, resolvedOrigin, data);
        }
    }

    private static string MapPluginToDomain(string pluginId) => pluginId.Trim().ToLowerInvariant() switch
    {
        "smart-tv" => EventTelemetryLog.StreamTv,
        "philips-hue" => EventTelemetryLog.StreamLighting,
        _ => $"plugin:{pluginId.Trim().ToLowerInvariant()}",
    };

    private sealed class PluginContextImpl(
        string pluginId,
        IPluginLogger logger,
        IPluginStorage storage,
        IPluginReceiverBridge receiver,
        IPluginReceiverControl? receiverControl,
        IPluginEventLogger events,
        Action<string, PluginHealthState> reportStatus) : IPluginContext
    {
        public string PluginId { get; } = pluginId;

        public IPluginLogger Logger { get; } = logger;

        public IPluginStorage Storage { get; } = storage;

        public IPluginReceiverBridge Receiver { get; } = receiver;

        public IPluginReceiverControl? ReceiverControl { get; } = receiverControl;

        public IPluginEventLogger Events { get; } = events;

        public HttpClient CreateHttpClient(bool allowLocalSelfSignedTls = false, TimeSpan? timeout = null)
        {
            HttpMessageHandler handler;
            if (allowLocalSelfSignedTls)
            {
                handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (request, _, _, sslErrors) =>
                    {
                        if (sslErrors == System.Net.Security.SslPolicyErrors.None)
                        {
                            return true;
                        }

                        var host = request.RequestUri?.Host;
                        return !string.IsNullOrWhiteSpace(host) &&
                               (IsPrivateLanAddress(host) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase));
                    },
                };
            }
            else
            {
                handler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                };
            }

            return new HttpClient(handler)
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(8),
            };
        }

        public void ReportStatus(string statusText, PluginHealthState health) =>
            reportStatus(statusText, health);

        internal static bool IsPrivateLanAddress(string host)
        {
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            var bytes = ip.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 127);
        }
    }
}
