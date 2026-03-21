namespace Denon.App.Core.Infrastructure;

public static class AppDataPaths
{
    public static string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DenonDesktop");

    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");

    public static string SettingsFilePath { get; } = Path.Combine(RootDirectory, "settings.json");

    public static string LogFilePath { get; } = Path.Combine(LogsDirectory, "app.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
