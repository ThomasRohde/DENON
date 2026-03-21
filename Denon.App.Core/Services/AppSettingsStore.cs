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
                _current = await LoadFromDiskAsync(cancellationToken);
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

    private async Task<AppSettings> LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(AppDataPaths.SettingsFilePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
            return Normalize(settings ?? AppSettings.CreateDefault());
        }
        catch (Exception exception)
        {
            _logStore.Warning($"Failed to parse settings. Falling back to defaults. {exception.Message}");
            return Normalize(AppSettings.CreateDefault());
        }
    }

    private static async Task SaveInternalAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(AppDataPaths.SettingsFilePath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        var normalized = settings.Clone();
        normalized.Receiver.Host = NormalizeHost(normalized.Receiver.Host);
        normalized.Receiver.Port = NormalizePort(normalized.Receiver.Port);
        normalized.Receiver.PlayerName = string.IsNullOrWhiteSpace(normalized.Receiver.PlayerName)
            ? "Denon AVR-X2400H"
            : normalized.Receiver.PlayerName.Trim();
        normalized.Receiver.DefaultLowVolume = Math.Clamp(normalized.Receiver.DefaultLowVolume, 0, 100);
        normalized.Receiver.PollIntervalSeconds = Math.Clamp(normalized.Receiver.PollIntervalSeconds, 2, 60);
        normalized.Receiver.Stations = NormalizeStations(normalized.Receiver.Stations);
        return normalized;
    }

    private static string NormalizeHost(string value) =>
        string.IsNullOrWhiteSpace(value) ? "192.168.1.3" : value.Trim();

    private static int NormalizePort(int value) => value is > 0 and <= 65535 ? value : 1255;

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
