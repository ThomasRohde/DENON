using System.Text.Json;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

public sealed class AppSettingsStore
{
    public const string RedactedSecretPlaceholder = "REDACTED";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppLogStore _logStore;
    private readonly string _settingsFilePath;
    private AppSettings _current = AppSettings.CreateDefault();

    public AppSettingsStore(AppLogStore logStore)
        : this(logStore, AppDataPaths.SettingsFilePath)
    {
    }

    public AppSettingsStore(AppLogStore logStore, string settingsFilePath)
    {
        _logStore = logStore;
        _settingsFilePath = settingsFilePath;
        var dir = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public event Action<AppSettings>? SettingsChanged;

    public AppSettings Current => _current.Clone();

    /// <summary>The HEOS sync offset without cloning the settings (read by the audio paths many times a second).</summary>
    public int CinemaSyncOffsetMs => Volatile.Read(ref _current).Media?.CinemaSyncOffsetMs ?? 1800;

    public string? LastLoadWarning { get; private set; }

    /// <summary>True when this run created the settings file (a first start on this PC).</summary>
    public bool CreatedThisRun { get; private set; }

    public async Task<AppSettings> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LastLoadWarning = null;
            if (!File.Exists(_settingsFilePath))
            {
                _current = Normalize(AppSettings.CreateDefault(), seedDefaultsIfEmpty: true);
                await SaveInternalAsync(_current, cancellationToken);
                CreatedThisRun = true;
                _logStore.Info("Created default settings file.");
            }
            else
            {
                var (loaded, needsPersist) = await LoadFromDiskAsync(cancellationToken);
                _current = loaded;
                if (needsPersist)
                {
                    await SaveInternalAsync(_current, cancellationToken);
                    _logStore.Info("Persisted newly initialized security tokens to settings file.");
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return _current.Clone();
    }

    public string SettingsFilePath => _settingsFilePath;

    /// <summary>
    /// Replaces the settings (MCP, plugins, automations). While the settings file is being recovered
    /// (<see cref="LastLoadWarning"/> is set) the change stays in memory: only the user's Save &amp; Sync
    /// (<see cref="SaveEditorAsync"/>) or an import writes the recovered settings and ends the recovery.
    /// </summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await CommitAsync(Normalize(settings, seedDefaultsIfEmpty: false), endsRecovery: false, cancellationToken);
        _logStore.Info("Settings saved.");
        SettingsChanged?.Invoke(_current.Clone());
    }

    /// <summary>
    /// Writes <paramref name="next"/> and publishes it, or during a recovery keeps it in memory unless
    /// <paramref name="endsRecovery"/> (the user's own save of the whole settings). Call without holding the lock.
    /// </summary>
    private async Task CommitAsync(AppSettings next, bool endsRecovery, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CommitLockedAsync(next, endsRecovery, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CommitLockedAsync(AppSettings next, bool endsRecovery, CancellationToken cancellationToken)
    {
        if (endsRecovery || LastLoadWarning is null)
        {
            // Persist to disk FIRST; only publish in-memory state if replacement succeeds.
            await SaveInternalAsync(next, cancellationToken);
            LastLoadWarning = null;
        }

        _current = next;
    }

    /// <summary>
    /// Saves UI state that the app or the Appearance chips set (window placement, splash marker, theme, skin)
    /// without <see cref="SettingsChanged"/>: listeners such as the settings editor and the HEOS connection must not
    /// react to a moved window or a new theme.
    /// While the store runs on recovered settings (<see cref="LastLoadWarning"/> is set) the change stays in memory
    /// and the file on disk is not touched: it is written with the user's next explicit save. Returns whether the
    /// change was written.
    /// </summary>
    public async Task<bool> SaveUiStateAsync(Action<UiSettings> mutator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var draft = _current.Clone();
            mutator(draft.Ui);
            if (LastLoadWarning is not null)
            {
                _current = draft;
                return false;
            }

            await SaveInternalAsync(draft, cancellationToken);
            _current = draft;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Atomically applies a targeted field-level mutation (such as Phone Remote toggles/token rotations or
    /// calibrated lip-sync offsets) without raising <see cref="SettingsChanged"/>, so overlapping saves never
    /// reload the settings editor (dropping unsaved edits) or reconnect HEOS events.
    /// Normalizes the updated snapshot and preserves recovery safety when <see cref="LastLoadWarning"/> is set.
    /// </summary>
    public async Task<(AppSettings Settings, bool SavedToDisk)> UpdateQuietAsync(
        Action<AppSettings> mutator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var draft = _current.Clone();
            mutator(draft);
            var normalized = Normalize(draft, seedDefaultsIfEmpty: false);
            if (LastLoadWarning is not null)
            {
                _current = normalized;
                return (_current.Clone(), false);
            }

            await SaveInternalAsync(normalized, cancellationToken);
            _current = normalized;
            return (_current.Clone(), true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Atomically applies a targeted field-level mutation to the latest persisted settings inside
    /// the store lock so concurrent/independent writers (discovery, volume unit, remote toggles,
    /// calibration) never clobber each other's changes. While the settings file is being recovered the change stays
    /// in memory (see <see cref="SaveAsync"/>); <see cref="SettingsChanged"/> is raised either way.
    /// </summary>
    public Task<AppSettings> UpdateAsync(Action<AppSettings> mutator, CancellationToken cancellationToken = default) =>
        UpdateCoreAsync(mutator, endsRecovery: false, cancellationToken);

    /// <summary>
    /// The user's Save &amp; Sync: like <see cref="UpdateAsync"/>, and it writes the recovered settings and ends a
    /// settings recovery.
    /// </summary>
    public Task<AppSettings> SaveEditorAsync(Action<AppSettings> mutator, CancellationToken cancellationToken = default) =>
        UpdateCoreAsync(mutator, endsRecovery: true, cancellationToken);

    private async Task<AppSettings> UpdateCoreAsync(Action<AppSettings> mutator, bool endsRecovery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var draft = _current.Clone();
            mutator(draft);
            await CommitLockedAsync(Normalize(draft, seedDefaultsIfEmpty: false), endsRecovery, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        _logStore.Info("Settings updated.");
        var snapshot = _current.Clone();
        SettingsChanged?.Invoke(snapshot);
        return snapshot;
    }

    public string ExportJson()
    {
        var sanitized = _current.Clone();
        sanitized.Mcp.BearerToken = RedactedSecretPlaceholder;
        sanitized.Remote.OwnerToken = RedactedSecretPlaceholder;
        sanitized.Remote.GuestToken = RedactedSecretPlaceholder;
        return JsonSerializer.Serialize(sanitized, JsonOptions);
    }

    public async Task<AppSettings> ImportJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
            ?? throw new InvalidOperationException("Imported JSON did not contain valid settings.");

        // Preserve existing local secrets if the imported export redacted them.
        var existing = _current;
        parsed.Mcp ??= new McpServerSettings();
        if (IsRedactedOrEmpty(parsed.Mcp.BearerToken))
        {
            parsed.Mcp.BearerToken = existing.Mcp.BearerToken;
        }

        parsed.Remote ??= new RemoteAccessSettings();
        if (IsRedactedOrEmpty(parsed.Remote.OwnerToken))
        {
            parsed.Remote.OwnerToken = existing.Remote.OwnerToken;
        }

        if (IsRedactedOrEmpty(parsed.Remote.GuestToken))
        {
            parsed.Remote.GuestToken = existing.Remote.GuestToken;
        }

        // An import replaces the whole settings on purpose: it is written and ends a recovery.
        await CommitAsync(Normalize(parsed, seedDefaultsIfEmpty: false), endsRecovery: true, cancellationToken);
        _logStore.Info("Settings imported.");
        SettingsChanged?.Invoke(_current.Clone());
        return _current.Clone();
    }

    private async Task<(AppSettings Settings, bool NeedsPersist)> LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rawJson = await File.ReadAllTextAsync(_settingsFilePath, cancellationToken);
            var hasPersistedMcpToken = rawJson.Contains("\"bearerToken\"", StringComparison.OrdinalIgnoreCase);
            var hasPersistedRemoteTokens = rawJson.Contains("\"ownerToken\"", StringComparison.OrdinalIgnoreCase) &&
                                           rawJson.Contains("\"guestToken\"", StringComparison.OrdinalIgnoreCase);
            var settings = JsonSerializer.Deserialize<AppSettings>(rawJson, JsonOptions);
            if (settings is null)
            {
                throw new JsonException("Settings file deserialized to null.");
            }

            var normalized = Normalize(settings, seedDefaultsIfEmpty: false);
            return (normalized, !hasPersistedMcpToken || !hasPersistedRemoteTokens);
        }
        catch (Exception exception)
        {
            // Never overwrite a corrupt or transiently unreadable settings file with defaults!
            TryBackupCorruptFile();

            var backupPath = _settingsFilePath + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    var backupJson = await File.ReadAllTextAsync(backupPath, cancellationToken);
                    var recovered = JsonSerializer.Deserialize<AppSettings>(backupJson, JsonOptions);
                    if (recovered is not null)
                    {
                        LastLoadWarning = $"Could not read settings.json ({exception.Message}). Recovered last-good backup.";
                        _logStore.Warning(LastLoadWarning);
                        return (Normalize(recovered, seedDefaultsIfEmpty: false), false);
                    }
                }
                catch
                {
                    // Fall through to in-memory defaults without overwriting disk.
                }
            }

            LastLoadWarning = $"Could not read settings.json ({exception.Message}). Running with in-memory defaults; original file preserved.";
            _logStore.Warning(LastLoadWarning);
            return (Normalize(AppSettings.CreateDefault(), seedDefaultsIfEmpty: true), false);
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var corruptCopy = $"{_settingsFilePath}.corrupt-{stamp}";
                if (!File.Exists(corruptCopy))
                {
                    File.Copy(_settingsFilePath, corruptCopy, overwrite: false);
                }
            }
        }
        catch
        {
            // Best-effort backup only.
        }
    }

    private async Task SaveInternalAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = $"{_settingsFilePath}.{Guid.NewGuid():N}.tmp";
        var backupPath = _settingsFilePath + ".bak";

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            // Retain a last-good backup ONLY when the existing primary parses cleanly as valid settings,
            // so saving after recovery from a corrupt primary NEVER overwrites the valid .bak file!
            if (File.Exists(_settingsFilePath) && await IsValidSettingsFileAsync(_settingsFilePath, cancellationToken))
            {
                try
                {
                    File.Copy(_settingsFilePath, backupPath, overwrite: true);
                }
                catch
                {
                    // Do not fail save if backup copy is briefly locked.
                }
            }

            File.Move(tempPath, _settingsFilePath, overwrite: true);

            // If no valid backup exists yet, seed .bak from the newly written valid primary.
            if (!File.Exists(backupPath))
            {
                try { File.Copy(_settingsFilePath, backupPath, overwrite: true); } catch { }
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }

            throw;
        }
    }

    private static async Task<bool> IsValidSettingsFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await File.ReadAllTextAsync(path, cancellationToken);
            var parsed = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions);
            return parsed is not null;
        }
        catch
        {
            return false;
        }
    }

    public static AppSettings Normalize(AppSettings settings, bool seedDefaultsIfEmpty = false)
    {
        var normalized = settings.Clone();
        normalized.Receiver ??= new ReceiverSettings();
        normalized.Receiver.Host = NormalizeHost(normalized.Receiver.Host);
        normalized.Receiver.Port = NormalizePort(normalized.Receiver.Port);
        normalized.Receiver.PlayerName = normalized.Receiver.PlayerName?.Trim() ?? string.Empty;
        normalized.Receiver.DefaultLowVolume = Math.Clamp(normalized.Receiver.DefaultLowVolume, 0, 98);
        normalized.Receiver.PollIntervalSeconds = Math.Clamp(normalized.Receiver.PollIntervalSeconds, 2, 60);
        normalized.Receiver.VolumePresets = NormalizeVolumePresets(normalized.Receiver.VolumePresets);
        normalized.Receiver.Stations = NormalizeStations(normalized.Receiver.Stations, seedDefaultsIfEmpty);
        normalized.Receiver.Scenes = NormalizeScenes(normalized.Receiver.Scenes, seedDefaultsIfEmpty);
        normalized.Ui ??= new UiSettings();
        normalized.Ui.MeterMicDeviceId = normalized.Ui.MeterMicDeviceId?.Trim() ?? string.Empty;
        normalized.Media ??= new MediaRelaySettings();
        normalized.Media.RelayPort = normalized.Media.RelayPort is >= 1024 and <= 65535
            ? normalized.Media.RelayPort
            : 18925;
        normalized.Media.FfmpegPath = normalized.Media.FfmpegPath?.Trim() ?? string.Empty;
        normalized.Media.CinemaSyncOffsetMs = Math.Clamp(normalized.Media.CinemaSyncOffsetMs, 0, 8000);
        normalized.Shortcuts ??= new ShortcutSettings();
        normalized.Shortcuts.Bindings = NormalizeShortcuts(normalized.Shortcuts.Bindings);
        normalized.AutomationGraphs = normalized.AutomationGraphs is null
            ? AutomationGraph.CreateDefaults()
            : normalized.AutomationGraphs.Where(static g => g is not null).ToList();
        normalized.Mcp ??= new McpServerSettings();
        normalized.Mcp.Port = normalized.Mcp.Port is >= 1024 and <= 65535 ? normalized.Mcp.Port : 18926;
        normalized.Mcp.MaxSafeVolume = Math.Clamp(normalized.Mcp.MaxSafeVolume, 20, 95);
        if (IsRedactedOrEmpty(normalized.Mcp.BearerToken))
        {
            normalized.Mcp.BearerToken = McpServerSettings.GenerateSecureToken();
        }

        normalized.Remote ??= new RemoteAccessSettings();
        if (IsRedactedOrEmpty(normalized.Remote.OwnerToken))
        {
            normalized.Remote.OwnerToken = RemoteAccessSettings.GeneratePairingToken("own");
        }

        if (IsRedactedOrEmpty(normalized.Remote.GuestToken))
        {
            normalized.Remote.GuestToken = RemoteAccessSettings.GeneratePairingToken("gst");
        }

        normalized.Telemetry ??= new TelemetrySettings();
        normalized.Telemetry.RetentionDays = Math.Clamp(normalized.Telemetry.RetentionDays, 7, 365);
        normalized.Telemetry.MediaDwellSeconds = Math.Clamp(normalized.Telemetry.MediaDwellSeconds, 5, 120);
        normalized.Telemetry.VolumeSettleMs = Math.Clamp(normalized.Telemetry.VolumeSettleMs, 1000, 10000);
        normalized.Telemetry.ActiveProbeIntervalSeconds = Math.Clamp(normalized.Telemetry.ActiveProbeIntervalSeconds, 10, 120);
        normalized.Telemetry.StandbyProbeIntervalSeconds = Math.Clamp(normalized.Telemetry.StandbyProbeIntervalSeconds, 30, 300);
        normalized.Telemetry.MaxTotalSizeMb = Math.Clamp(normalized.Telemetry.MaxTotalSizeMb, 5, 500);

        return normalized;
    }

    private static bool IsRedactedOrEmpty(string? token) =>
        string.IsNullOrWhiteSpace(token) ||
        string.Equals(token.Trim(), RedactedSecretPlaceholder, StringComparison.OrdinalIgnoreCase);

    private static List<ShortcutBinding> NormalizeShortcuts(IEnumerable<ShortcutBinding>? existing)
    {
        var savedList = (existing ?? [])
            .Where(static b => !string.IsNullOrWhiteSpace(b.ActionId))
            .GroupBy(static b => b.ActionId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
            .ToList();

        var byId = savedList.ToDictionary(static b => b.ActionId.Trim(), StringComparer.OrdinalIgnoreCase);
        var defaults = ShortcutSettings.CreateDefaults();
        var defaultIds = new HashSet<string>(defaults.Select(static d => d.ActionId), StringComparer.OrdinalIgnoreCase);
        var result = new List<ShortcutBinding>(defaults.Count + 4);

        foreach (var def in defaults)
        {
            if (byId.TryGetValue(def.ActionId, out var saved))
            {
                result.Add(new ShortcutBinding(def.ActionId, def.Label, def.IsGlobal, saved.Gesture?.Trim() ?? string.Empty));
            }
            else
            {
                result.Add(def.Clone());
            }
        }

        // Preserve validated dynamic custom scene & plugin shortcuts alongside built-in defaults!
        foreach (var custom in savedList)
        {
            var id = custom.ActionId.Trim();
            if (defaultIds.Contains(id))
            {
                continue;
            }

            if (id.StartsWith(ShortcutActionIds.ScenePrefix, StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(ShortcutActionIds.PluginPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var label = string.IsNullOrWhiteSpace(custom.Label) ? id : custom.Label.Trim();
                result.Add(new ShortcutBinding(id, label, custom.IsGlobal, custom.Gesture?.Trim() ?? string.Empty));
            }
        }

        return result;
    }

    private static List<ScenePreset> NormalizeScenes(IEnumerable<ScenePreset>? scenes, bool seedDefaultsIfEmpty)
    {
        if (scenes is null)
        {
            return seedDefaultsIfEmpty ? ScenePreset.CreateDefaultScenes() : [];
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<ScenePreset>();

        foreach (var scene in scenes)
        {
            if (string.IsNullOrWhiteSpace(scene.Name))
            {
                continue;
            }

            var clone = scene.Clone();
            clone.Name = clone.Name.Trim();
            clone.Description = clone.Description?.Trim() ?? string.Empty;
            clone.Badge = string.IsNullOrWhiteSpace(clone.Badge)
                ? clone.Name[..Math.Min(4, clone.Name.Length)].ToUpperInvariant()
                : clone.Badge.Trim().ToUpperInvariant()[..Math.Min(5, clone.Badge.Trim().Length)];
            clone.AccentHex = string.IsNullOrWhiteSpace(clone.AccentHex) ? "#00D4FF" : clone.AccentHex.Trim();
            clone.Id = EnsureUniqueAlias(
                SlugNormalizer.Normalize(string.IsNullOrWhiteSpace(clone.Id) ? clone.Name : clone.Id),
                seenIds);

            if (clone.TargetVolume.HasValue)
            {
                clone.TargetVolume = Math.Clamp(clone.TargetVolume.Value, 0, 98);
            }

            if (clone.SubwooferTrimDb.HasValue)
            {
                clone.SubwooferTrimDb = Math.Clamp(clone.SubwooferTrimDb.Value, -12, 12);
            }

            if (clone.DialogTrimDb.HasValue)
            {
                clone.DialogTrimDb = Math.Clamp(clone.DialogTrimDb.Value, -12, 12);
            }

            if (clone.SleepTimerMinutes.HasValue)
            {
                clone.SleepTimerMinutes = Math.Clamp(clone.SleepTimerMinutes.Value, 0, 120);
            }

            normalized.Add(clone);
        }

        return normalized.Count == 0 && seedDefaultsIfEmpty ? ScenePreset.CreateDefaultScenes() : normalized;
    }

    private static string NormalizeHost(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static int NormalizePort(int value) => value is > 0 and <= 65535 ? value : 1255;

    private static List<int> NormalizeVolumePresets(IEnumerable<int>? presets)
    {
        var cleaned = (presets ?? [])
            .Select(static v => Math.Clamp(v, 0, 98))
            .Distinct()
            .OrderBy(static v => v)
            .Take(6)
            .ToList();

        return cleaned.Count == 0 ? [15, 28, 42, 55] : cleaned;
    }

    private static List<StationPreset> NormalizeStations(IEnumerable<StationPreset>? stations, bool seedDefaultsIfEmpty)
    {
        if (stations is null)
        {
            return seedDefaultsIfEmpty ? ReceiverSettings.CreateDefaultStations() : [];
        }

        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<StationPreset>();

        foreach (var station in stations)
        {
            var displayName = string.IsNullOrWhiteSpace(station.DisplayName) ? station.Alias : station.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(station.Url))
            {
                continue;
            }

            var alias = SlugNormalizer.Normalize(string.IsNullOrWhiteSpace(station.Alias) ? displayName : station.Alias);
            alias = EnsureUniqueAlias(alias, seenAliases);

            normalized.Add(new StationPreset(alias, displayName.Trim(), station.Url.Trim()));
        }

        return normalized.Count == 0 && seedDefaultsIfEmpty ? ReceiverSettings.CreateDefaultStations() : normalized;
    }

    private static string EnsureUniqueAlias(string alias, ISet<string> seenAliases)
    {
        var baseAlias = string.IsNullOrWhiteSpace(alias) ? "station" : alias;
        var candidate = baseAlias;
        var suffix = 2;

        while (!seenAliases.Add(candidate))
        {
            candidate = $"{baseAlias}-{suffix++}";
        }

        return candidate;
    }
}
