using System.Text.Json;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppLogStore _logStore;
    private AppSettings _current = AppSettings.CreateDefault();

    public AppSettingsStore(AppLogStore logStore)
    {
        _logStore = logStore;
        AppDataPaths.EnsureCreated();
    }

    public event Action<AppSettings>? SettingsChanged;

    public AppSettings Current => _current.Clone();

    public async Task<AppSettings> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(AppDataPaths.SettingsFilePath))
            {
                _current = Normalize(AppSettings.CreateDefault());
                await SaveInternalAsync(_current, cancellationToken);
                _logStore.Info("Created default settings file.");
            }
            else
            {
                var (loaded, needsPersist) = await LoadFromDiskAsync(cancellationToken);
                _current = loaded;
                if (needsPersist)
                {
                    await SaveInternalAsync(_current, cancellationToken);
                    _logStore.Info("Persisted newly initialized settings (including MCP Bearer token) to disk.");
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return _current.Clone();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _current = normalized;
            await SaveInternalAsync(_current, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        _logStore.Info("Settings saved.");
        SettingsChanged?.Invoke(_current.Clone());
    }

    public string ExportJson() => JsonSerializer.Serialize(_current, JsonOptions);

    public async Task<AppSettings> ImportJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
            ?? throw new InvalidOperationException("Imported JSON did not contain valid settings.");

        await SaveAsync(parsed, cancellationToken);
        return _current.Clone();
    }

    private async Task<(AppSettings Settings, bool NeedsPersist)> LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rawJson = await File.ReadAllTextAsync(AppDataPaths.SettingsFilePath, cancellationToken);
            var hasPersistedMcpToken = rawJson.Contains("\"bearerToken\"", StringComparison.OrdinalIgnoreCase);
            var settings = JsonSerializer.Deserialize<AppSettings>(rawJson, JsonOptions);
            return (Normalize(settings ?? AppSettings.CreateDefault()), !hasPersistedMcpToken);
        }
        catch (Exception exception)
        {
            _logStore.Warning($"Failed to parse settings. Falling back to defaults. {exception.Message}");
            return (Normalize(AppSettings.CreateDefault()), true);
        }
    }

    private static async Task SaveInternalAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var tempPath = AppDataPaths.SettingsFilePath + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, AppDataPaths.SettingsFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            await using var stream = File.Create(AppDataPaths.SettingsFilePath);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        }
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        var normalized = settings.Clone();
        normalized.Receiver.Host = NormalizeHost(normalized.Receiver.Host);
        normalized.Receiver.Port = NormalizePort(normalized.Receiver.Port);
        normalized.Receiver.PlayerName = normalized.Receiver.PlayerName?.Trim() ?? string.Empty;
        normalized.Receiver.DefaultLowVolume = Math.Clamp(normalized.Receiver.DefaultLowVolume, 0, 98);
        normalized.Receiver.PollIntervalSeconds = Math.Clamp(normalized.Receiver.PollIntervalSeconds, 2, 60);
        normalized.Receiver.VolumePresets = NormalizeVolumePresets(normalized.Receiver.VolumePresets);
        normalized.Receiver.Stations = NormalizeStations(normalized.Receiver.Stations);
        normalized.Receiver.Scenes = NormalizeScenes(normalized.Receiver.Scenes);
        normalized.Media ??= new MediaRelaySettings();
        normalized.Media.RelayPort = normalized.Media.RelayPort is >= 1024 and <= 65535
            ? normalized.Media.RelayPort
            : 18925;
        normalized.Media.FfmpegPath = normalized.Media.FfmpegPath?.Trim() ?? string.Empty;
        normalized.Media.CinemaSyncOffsetMs = Math.Clamp(normalized.Media.CinemaSyncOffsetMs, 0, 8000);
        normalized.Shortcuts ??= new ShortcutSettings();
        normalized.Shortcuts.Bindings = NormalizeShortcuts(normalized.Shortcuts.Bindings);
        normalized.Mcp ??= new McpServerSettings();
        normalized.Mcp.Port = normalized.Mcp.Port is >= 1024 and <= 65535 ? normalized.Mcp.Port : 18926;
        normalized.Mcp.MaxSafeVolume = Math.Clamp(normalized.Mcp.MaxSafeVolume, 20, 95);
        if (string.IsNullOrWhiteSpace(normalized.Mcp.BearerToken))
        {
            normalized.Mcp.BearerToken = McpServerSettings.GenerateSecureToken();
        }

        return normalized;
    }

    private static List<ShortcutBinding> NormalizeShortcuts(IEnumerable<ShortcutBinding>? existing)
    {
        var byId = (existing ?? [])
            .Where(static b => !string.IsNullOrWhiteSpace(b.ActionId))
            .GroupBy(static b => b.ActionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);

        var defaults = ShortcutSettings.CreateDefaults();
        var result = new List<ShortcutBinding>(defaults.Count);

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

        return result;
    }

    private static List<ScenePreset> NormalizeScenes(IEnumerable<ScenePreset>? scenes)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<ScenePreset>();

        foreach (var scene in scenes ?? [])
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

        return normalized.Count == 0 ? ScenePreset.CreateDefaultScenes() : normalized;
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

    private static List<StationPreset> NormalizeStations(IEnumerable<StationPreset>? stations)
    {
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<StationPreset>();

        foreach (var station in stations ?? [])
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

        return normalized.Count == 0 ? ReceiverSettings.CreateDefaultStations() : normalized;
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
