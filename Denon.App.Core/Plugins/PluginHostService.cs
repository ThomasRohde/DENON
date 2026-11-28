using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Channels;
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

    /// <summary>Structured details from the plugin's last status report (empty when it sent none).</summary>
    public IReadOnlyDictionary<string, string> StatusDetails { get; init; } = EmptyDetails;

    public string? LastError { get; init; }

    internal static readonly IReadOnlyDictionary<string, string> EmptyDetails = new Dictionary<string, string>();

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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _sceneActionRuns = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>One enable or disable at a time (the Plugins page and the Phone Remote can toggle at once).</summary>
    private readonly SemaphoreSlim _enableGate = new(1, 1);

    private readonly object _receiverEventLock = new();
    private Task? _stopAllTask;
    private CancellationTokenSource? _volumeEventCts;

    /// <summary>
    /// A volume change waits this long for the next one before plugins hear of it, so a slider drag or a fade is one
    /// VolumeChanged event, not one per step.
    /// </summary>
    internal TimeSpan VolumeEventDelay { get; set; } = TimeSpan.FromMilliseconds(400);

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

        // One toggle at a time: two at once could both start (or stop) the same plugin.
        await _enableGate.WaitAsync(cancellationToken);
        try
        {
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

            // Inside the store's lock: a clone saved whole would undo a settings change made at the same moment.
            var id = entry.Manifest.Id;
            await _settingsStore.UpdateAsync(settings =>
            {
                var disabled = new HashSet<string>(settings.Plugins.DisabledPluginIds ?? [], StringComparer.OrdinalIgnoreCase);
                if (enabled)
                {
                    disabled.Remove(id);
                }
                else
                {
                    disabled.Add(id);
                }

                settings.Plugins.DisabledPluginIds = disabled.OrderBy(static d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }, cancellationToken);
        }
        finally
        {
            _enableGate.Release();
        }

        NotifyPluginsChanged();
    }

    /// <summary>True for an action the plugin declares read-only, or (undeclared) one named *.refresh.</summary>
    internal static bool IsReadOnlyAction(IReadOnlyList<PluginActionDescriptor> actions, string actionId)
    {
        var declared = actions.FirstOrDefault(a => string.Equals(a.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
        return declared is not null
            ? Mcp.McpPluginToolBridge.ResolveHints(declared).ReadOnly
            : actionId.EndsWith(".refresh", StringComparison.OrdinalIgnoreCase);
    }

    public Task<PluginActionResult> ExecuteActionAsync(
        string pluginId,
        string actionId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(pluginId, actionId, parameters, "desktop_ui", cancellationToken);

    /// <param name="origin">Who asked, for the telemetry: "desktop_ui", or "plugin" for a call from another plugin.</param>
    internal async Task<PluginActionResult> ExecuteActionAsync(
        string pluginId,
        string actionId,
        IReadOnlyDictionary<string, string>? parameters,
        string origin,
        CancellationToken cancellationToken)
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

        if (!entry.IsStarted)
        {
            return PluginActionResult.Fail($"Plugin '{entry.Manifest.Name}' is not running ({entry.StatusText}).");
        }

        try
        {
            // A read (declared read-only, or *.refresh) changes nothing: it is not an action of the user. It writes no
            // plugin.action event and no log line, and it does not mark the domain, so the real changes that follow
            // (a cast pausing) keep their own origin.
            var isRead = IsReadOnlyAction(entry.Actions, actionId);
            var domain = MapPluginToDomain(entry.Manifest.Id);
            if (!isRead)
            {
                _telemetry.MarkOrigin(domain, origin, overwriteRecent: false);
            }

            var result = await entry.Plugin.ExecuteActionAsync(actionId, parameters, cancellationToken);
            if (!result.Success)
            {
                _logStore.Warning($"[Plugin:{entry.Manifest.Id}] Action '{actionId}' failed: {result.Message}");
            }
            else if (!isRead)
            {
                _logStore.Info($"[Plugin:{entry.Manifest.Id}] Action '{actionId}': {result.Message}");
                var effectiveOrigin = _telemetry.ResolveOrigin(domain, fallback: origin);
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
            if (IsAudioSyncActive(plugins[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A plugin's getter that throws counts as "not active": it must not end the host's audio loops.</summary>
    private static bool IsAudioSyncActive(IAudioReactivePlugin plugin)
    {
        try
        {
            return plugin.IsAudioSyncActive;
        }
        catch
        {
            return false;
        }
    }

    public void PublishAudioFrame(in PluginAudioFrame frame)
    {
        var plugins = _activeAudioPlugins;
        for (var i = 0; i < plugins.Length; i++)
        {
            var plugin = plugins[i];
            if (!IsAudioSyncActive(plugin))
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

    /// <summary>
    /// Stops every plugin at the same time, so one slow plugin does not keep the others from stopping (and writing
    /// their session end). The first call does the work; later calls (DisposeAsync) wait for the same stop.
    /// </summary>
    public Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            return _stopAllTask ??= StopAllCoreAsync(cancellationToken);
        }
    }

    private async Task StopAllCoreAsync(CancellationToken cancellationToken)
    {
        IsStoppingAll = true;
        _stateStore.StateChanged -= OnReceiverStateChanged;
        _receiverController.SceneActivated -= OnReceiverSceneActivated;

        List<ManagedPluginEntry> snapshot;
        lock (_syncRoot)
        {
            snapshot = [.. _entries];
            _activeAudioPlugins = [];
        }

        await Task.WhenAll(snapshot.Select(entry => StopEntryAsync(entry, cancellationToken)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAllAsync();
        }
        catch
        {
            // Each stop logs its own failure.
        }

        List<ManagedPluginEntry> snapshot;
        lock (_syncRoot)
        {
            snapshot = [.. _entries];
            _entries.Clear();
        }

        foreach (var entry in snapshot)
        {
            entry.Events?.Writer.TryComplete();
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

            PluginAssemblyLoadContext? loadContext = null;
            var keepContext = false;
            try
            {
                loadContext = new PluginAssemblyLoadContext(dllPath);
                var assembly = loadContext.LoadFromAssemblyPath(dllPath);

                // A plugin built for a newer SDK would fail later with MissingMethodException: refuse it clearly.
                if (RequiredSdkVersion(assembly) is { } needed && needed > HostSdkVersion)
                {
                    _logStore.Error($"Plugin '{fileName}' needs Plugin SDK {needed} or newer; this Denon Desktop has {HostSdkVersion}. Update Denon Desktop.");
                    continue;
                }

                var pluginTypes = assembly
                    .GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IDenonPlugin).IsAssignableFrom(t))
                    .ToList();

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
                            keepContext = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logStore.Warning($"Failed to inspect external plugin assembly '{fileName}': {ex.Message}");
            }
            finally
            {
                // Keep a context only when a plugin from it registered. A dependency DLL, a duplicate id or a load
                // error unloads it at once (before, it stayed until exit, and again on every rescan).
                if (loadContext is not null)
                {
                    if (keepContext)
                    {
                        _externalContexts.Add(loadContext);
                    }
                    else
                    {
                        try { loadContext.Unload(); } catch { }
                    }
                }
            }
        }

        return added;
    }

    /// <summary>A LAN address (IPv4 private, link-local or loopback; IPv6 loopback, unique-local or link-local), localhost or a .local name.</summary>
    internal static bool IsLanHost(string? host)
    {
        var name = (host ?? string.Empty).Trim().Trim('[', ']');
        if (name.Length == 0)
        {
            return false;
        }

        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsPrivateLanAddress(name);
    }

    internal static bool IsPrivateLanAddress(string host)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        }

        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254) ||
               (bytes[0] == 127);
    }

    private static readonly Version HostSdkVersion = typeof(IDenonPlugin).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>The Plugin SDK version a plugin assembly was built against, or null when it does not reference it.</summary>
    internal static Version? RequiredSdkVersion(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .FirstOrDefault(static n => string.Equals(n.Name, "Denon.App.PluginSdk", StringComparison.OrdinalIgnoreCase))
            ?.Version;

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
            (statusText, health, details) =>
            {
                entry.StatusText = statusText;
                entry.StatusDetails = details is { Count: > 0 }
                    ? new Dictionary<string, string>(details, StringComparer.Ordinal)
                    : PluginRecordSnapshot.EmptyDetails;
                entry.Health = entry.IsEnabled ? health : PluginHealthState.Disabled;
                NotifyPluginsChanged();
            },
            () => IsStoppingAll,
            (target, action, parameters, ct) => ExecuteActionAsync(target, action, parameters, "plugin", ct));

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
                entry.StatusDetails = PluginRecordSnapshot.EmptyDetails;
            }
        }
        catch (Exception ex)
        {
            entry.LastError = ex.Message;
            entry.Health = PluginHealthState.Error;
            entry.StatusText = $"Init failed: {ex.Message}";
            entry.StatusDetails = PluginRecordSnapshot.EmptyDetails;
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
            entry.StatusDetails = PluginRecordSnapshot.EmptyDetails;
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
                entry.StatusDetails = PluginRecordSnapshot.EmptyDetails;
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
            Interlocked.Increment(ref entry.Run);
            if (!entry.IsEnabled)
            {
                entry.Health = PluginHealthState.Disabled;
                entry.StatusText = "Disabled";
                entry.StatusDetails = PluginRecordSnapshot.EmptyDetails;
            }
        }
    }

    private void OnReceiverSceneActivated(ScenePreset scene)
    {
        // The receiver part of a scene is done here; its plugin chain runs in the background. Keep the task so an
        // automation can wait for the whole scene before its next step (WhenSceneActionsDoneAsync).
        var run = Task.Run(() => NotifySceneActivatedAsync(scene, CancellationToken.None));
        _sceneActionRuns[scene.Id] = run;
    }

    /// <summary>
    /// Completes when the plugin chain of the latest activation of <paramref name="sceneId"/> has run (at once when
    /// there is none). Activation errors are logged by the chain, not thrown here.
    /// </summary>
    public async Task WhenSceneActionsDoneAsync(string sceneId, CancellationToken cancellationToken = default)
    {
        if (_sceneActionRuns.TryGetValue(sceneId, out var run))
        {
            try
            {
                await run.WaitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The chain reports its own failures.
            }
        }
    }

    private void OnReceiverStateChanged(ReceiverStateSnapshot rawSnapshot)
    {
        // Under one lock: the previous/current pair and the order of the events queued for the plugins stay true to
        // the order of the state changes.
        lock (_receiverEventLock)
        {
            OnReceiverStateChangedLocked(rawSnapshot);
        }
    }

    private void OnReceiverStateChangedLocked(ReceiverStateSnapshot rawSnapshot)
    {
        var current = MapSnapshot(rawSnapshot);
        var previous = _lastBroadcastReceiver;
        _lastBroadcastReceiver = current;

        if (previous is null || !current.IsConnected)
        {
            return;
        }

        if (previous.IsConnected && (previous.Volume != current.Volume || previous.IsMuted != current.IsMuted))
        {
            ScheduleVolumeChangedEvent();
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

        foreach (var evt in eventsToDispatch)
        {
            QueueReceiverEvent(evt, skipPluginIds: null, CancellationToken.None);
        }
    }

    /// <summary>
    /// One VolumeChanged event after the volume has rested for <see cref="VolumeEventDelay"/>, with the newest state.
    /// </summary>
    private void ScheduleVolumeChangedEvent()
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _volumeEventCts, cts);
        try { previous?.Cancel(); } catch { /* already done and disposed */ }
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(VolumeEventDelay, token);
                lock (_receiverEventLock)
                {
                    if (!token.IsCancellationRequested && _lastBroadcastReceiver is { IsConnected: true } latest)
                    {
                        QueueReceiverEvent(new PluginReceiverEvent(PluginReceiverEventType.VolumeChanged, latest), skipPluginIds: null, CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // A newer change takes over.
            }
            finally
            {
                Interlocked.CompareExchange(ref _volumeEventCts, null, cts);
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <summary>Waits until every running plugin has handled the event (each plugin in its own queue).</summary>
    private Task BroadcastReceiverEventAsync(
        PluginReceiverEvent evt,
        CancellationToken cancellationToken,
        ISet<string>? skipPluginIds = null) =>
        Task.WhenAll(QueueReceiverEvent(evt, skipPluginIds, cancellationToken)).WaitAsync(cancellationToken);

    /// <summary>
    /// Puts the event in each running plugin's own queue. A plugin handles its events one at a time and in order;
    /// plugins do not wait for each other, so a slow one (the TV wake) does not hold up the rest.
    /// </summary>
    /// <param name="cancellationToken">The caller's token: a cancelled event is skipped, and a running one gets it.</param>
    private List<Task> QueueReceiverEvent(PluginReceiverEvent evt, ISet<string>? skipPluginIds, CancellationToken cancellationToken)
    {
        List<ManagedPluginEntry> activeEntries;
        lock (_syncRoot)
        {
            activeEntries = _entries.Where(e => e.IsEnabled && e.IsStarted).ToList();
        }

        var handled = new List<Task>(activeEntries.Count);
        foreach (var entry in activeEntries)
        {
            if (skipPluginIds is not null && skipPluginIds.Contains(entry.Manifest.Id))
            {
                continue;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!EventQueueOf(entry).Writer.TryWrite((evt, done, cancellationToken, Volatile.Read(ref entry.Run))))
            {
                done.TrySetResult();
            }

            handled.Add(done.Task);
        }

        return handled;
    }

    private Channel<(PluginReceiverEvent Event, TaskCompletionSource Done, CancellationToken Token, int Run)> EventQueueOf(ManagedPluginEntry entry)
    {
        lock (entry)
        {
            if (entry.Events is null)
            {
                entry.Events = Channel.CreateUnbounded<(PluginReceiverEvent, TaskCompletionSource, CancellationToken, int)>(new UnboundedChannelOptions { SingleReader = true });
                var reader = entry.Events.Reader;
                _ = Task.Run(() => PumpReceiverEventsAsync(entry, reader));
            }

            return entry.Events;
        }
    }

    private async Task PumpReceiverEventsAsync(ManagedPluginEntry entry, ChannelReader<(PluginReceiverEvent Event, TaskCompletionSource Done, CancellationToken Token, int Run)> reader)
    {
        await foreach (var (evt, done, token, run) in reader.ReadAllAsync())
        {
            try
            {
                // A cancelled event (a scene that was called off) is skipped, not run late. So is one queued before
                // the plugin was stopped: a restart must not replay automation from its previous run.
                if (entry.IsEnabled && entry.IsStarted && !token.IsCancellationRequested && run == Volatile.Read(ref entry.Run))
                {
                    await entry.Plugin.OnReceiverEventAsync(evt, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The caller cancelled.
            }
            catch (Exception ex)
            {
                _logStore.Warning($"[Plugin:{entry.Manifest.Id}] Receiver event '{evt.EventType}' error: {ex.Message}");
            }
            finally
            {
                done.TrySetResult();
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
            StatusDetails = entry.StatusDetails,
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

        public IReadOnlyDictionary<string, string> StatusDetails { get; set; } = PluginRecordSnapshot.EmptyDetails;

        public string? LastError { get; set; }

        public IReadOnlyList<PluginActionDescriptor> Actions { get; set; } = [];

        /// <summary>Counts the plugin's stops: events queued in an earlier run are skipped.</summary>
        public int Run;

        /// <summary>This plugin's receiver events, handled one at a time in order (created on first use).</summary>
        public Channel<(PluginReceiverEvent Event, TaskCompletionSource Done, CancellationToken Token, int Run)>? Events { get; set; }
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
                // Return the host's SDK explicitly, so the plugin and the host share one copy of the SDK types.
                return typeof(IDenonPlugin).Assembly;
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

    /// <summary>The origin domain a plugin's actions change: tv for the LG TV, lighting for Hue, else plugin:<id>.</summary>
    public static string MapPluginToDomain(string pluginId) => pluginId.Trim().ToLowerInvariant() switch
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
        Action<string, PluginHealthState, IReadOnlyDictionary<string, string>?> reportStatus,
        Func<bool> isAppShuttingDown,
        Func<string, string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<PluginActionResult>> runPluginAction) : IPluginContext
    {
        public string PluginId { get; } = pluginId;

        public Task<PluginActionResult> RunPluginActionAsync(
            string targetPluginId,
            string actionId,
            IReadOnlyDictionary<string, string>? parameters = null,
            CancellationToken cancellationToken = default) =>
            string.Equals(targetPluginId?.Trim(), PluginId, StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(PluginActionResult.Fail("A plugin cannot run its own action through the host."))
                : runPluginAction(targetPluginId?.Trim() ?? string.Empty, actionId, parameters, cancellationToken);

        public bool IsAppShuttingDown => isAppShuttingDown();

        public IPluginLogger Logger { get; } = logger;

        public IPluginStorage Storage { get; } = storage;

        public IPluginReceiverBridge Receiver { get; } = receiver;

        public IPluginReceiverControl? ReceiverControl { get; } = receiverControl;

        public IPluginEventLogger Events { get; } = events;

        public HttpClient CreateHttpClient(bool allowLocalSelfSignedTls = false, TimeSpan? timeout = null)
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(5),
            };

            if (allowLocalSelfSignedTls)
            {
                // A device on the LAN (a Hue bridge, a TV) uses a self-signed certificate: accept it only for a LAN
                // address or a .local name. Anything on the internet still needs a valid certificate.
                handler.SslOptions.RemoteCertificateValidationCallback = (sender, _, _, sslErrors) =>
                    sslErrors == System.Net.Security.SslPolicyErrors.None ||
                    (sender is System.Net.Security.SslStream stream && IsLanHost(stream.TargetHostName));
            }

            return new HttpClient(handler)
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(8),
            };
        }

        public void ReportStatus(string statusText, PluginHealthState health) =>
            reportStatus(statusText, health, null);

        public void ReportStatus(string statusText, PluginHealthState health, IReadOnlyDictionary<string, string>? details) =>
            reportStatus(statusText, health, details);

    }
}
