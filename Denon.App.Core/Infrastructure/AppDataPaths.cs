namespace Denon.App.Core.Infrastructure;

public static class AppDataPaths
{
    private static readonly string DefaultRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DenonDesktop");

    /// <summary>
    /// Root of settings, logs and plugins. <c>%LocalAppData%\DenonDesktop</c> unless <see cref="UseRootDirectory"/>
    /// moved it (screenshot mode uses an empty folder, so README screenshots never show the user's own setup).
    /// </summary>
    public static string RootDirectory { get; private set; } = DefaultRoot;

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static string EventsLogsDirectory => Path.Combine(LogsDirectory, "events");

    public static string PluginEventsLogsDirectory => Path.Combine(EventsLogsDirectory, "plugins");

    public static string PluginsDirectory => Path.Combine(RootDirectory, "plugins");

    public static string ExternalPluginsDirectory => Path.Combine(PluginsDirectory, "external");

    public static string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    public static string RoomHabitsFilePath => Path.Combine(RootDirectory, "room-habits.json");

    public static string LogFilePath => Path.Combine(LogsDirectory, "app.log");

    /// <summary>Moves all app data to <paramref name="rootDirectory"/>. Call before any service starts.</summary>
    public static void UseRootDirectory(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A root directory is required.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public static string GetPluginDataDirectory(string pluginId)
    {
        var safeId = SlugNormalizer.Normalize(pluginId);
        if (string.IsNullOrWhiteSpace(safeId))
        {
            safeId = "plugin";
        }

        return Path.Combine(PluginsDirectory, "data", safeId);
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(EventsLogsDirectory);
        Directory.CreateDirectory(PluginEventsLogsDirectory);
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(ExternalPluginsDirectory);
    }
}
