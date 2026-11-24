using System.IO;
using System.Reflection;

namespace Denon.App.Desktop.Infrastructure;

/// <summary>The app's version and install, for the status bar and the splash screen.</summary>
public static class AppInfo
{
    /// <summary>The release version, for example <c>2.1.0</c> (the informational version without the commit).</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>"Version 2.1.0", for the status bar.</summary>
    public static string VersionLabel { get; } = "Version " + Version;

    /// <summary>
    /// Identifies this install: the version and the time the exe was written. An upgrade changes the version; a
    /// reinstall of the same version changes the time.
    /// </summary>
    public static string InstallStamp { get; } = ReadInstallStamp();

    private static string ReadVersion()
    {
        var informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational;
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }

    private static string ReadInstallStamp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            var written = exe is not null && File.Exists(exe) ? File.GetLastWriteTimeUtc(exe).Ticks : 0;
            return $"{Version}|{written}";
        }
        catch (Exception)
        {
            return Version;
        }
    }
}
