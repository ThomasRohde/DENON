using Denon.App.Core.Infrastructure;

namespace Denon.App.Core.Services;

/// <summary>
/// The saved station this app started last, kept in <c>last-station.txt</c>. HEOS reports the app's own stream only as
/// "Url Stream" with no address, so after a restart (an update, a reboot) the app could not tell which station plays.
/// With this file it still shows the station's name and logo.
/// </summary>
public sealed class LastStationFile
{
    private readonly string _path;
    private readonly object _sync = new();

    public LastStationFile()
        : this(Path.Combine(AppDataPaths.RootDirectory, "last-station.txt"))
    {
    }

    public LastStationFile(string path)
    {
        _path = path;
    }

    /// <summary>The saved alias, or null when there is none or the file cannot be read.</summary>
    public string? Load()
    {
        try
        {
            lock (_sync)
            {
                if (!File.Exists(_path))
                {
                    return null;
                }

                var alias = File.ReadAllText(_path).Trim();
                return alias.Length == 0 ? null : alias;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves the alias, or deletes the file for null. Best effort: a failed write only loses the hint.</summary>
    public void Save(string? alias)
    {
        try
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    File.Delete(_path);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, alias.Trim());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
