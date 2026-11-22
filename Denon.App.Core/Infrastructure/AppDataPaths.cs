namespace Denon.App.Core.Infrastructure;

public static class AppDataPaths
{
    public static string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DenonDesktop");

    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");

    public static string PluginsDirectory { get; } = Path.Combine(RootDirectory, "plugins");

    public static string ExternalPluginsDirectory { get; } = Path.Combine(PluginsDirectory, "external");

    public static string SettingsFilePath { get; } = Path.Combine(RootDirectory, "settings.json");

    public static string RoomHabitsFilePath { get; } = Path.Combine(RootDirectory, "room-habits.json");

    public static string LogFilePath { get; } = Path.Combine(LogsDirectory, "app.log");

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
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(ExternalPluginsDirectory);
    }
}
