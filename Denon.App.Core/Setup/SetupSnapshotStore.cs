using System.Text.Json;

namespace Denon.App.Core.Setup;

/// <summary>
/// Keeps the old values of the last apply on disk (one file), so an undo works after the app restarts and after a
/// crash in the middle of an apply. A new apply replaces it.
/// </summary>
public sealed class SetupSnapshotStore(string directory)
{
    private const string FileName = "latest.json";
    private readonly object _sync = new();

    public string FilePath { get; } = Path.Combine(directory, FileName);

    public void Save(SetupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            Directory.CreateDirectory(directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, ProfileJson.Options));
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    /// <summary>The saved snapshot, or null when there is none or the file cannot be read.</summary>
    public SetupSnapshot? Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }

                var snapshot = JsonSerializer.Deserialize<SetupSnapshot>(File.ReadAllText(FilePath), ProfileJson.Options);
                return snapshot is { HasWrittenEntries: true } ? snapshot : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            try
            {
                File.Delete(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be deleted stays; the next apply replaces it.
            }
        }
    }
}
