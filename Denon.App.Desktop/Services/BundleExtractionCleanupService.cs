using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Denon.App.Desktop.Services;

/// <summary>
/// Deletes old native-library extraction folders of this single-file bundle. The .NET host extracts the native DLLs
/// (wpfgfx, PresentationNative, WebView2Loader…) to <c>&lt;base&gt;\&lt;exe name&gt;\&lt;bundle id&gt;\</c>, one folder per
/// build, and never deletes one. <c>&lt;base&gt;</c> is <c>DOTNET_BUNDLE_EXTRACT_BASE_DIR</c> when set, else
/// <c>%TEMP%\.net</c>. Best effort: a folder that is in use, too new, or cannot be moved is left alone.
/// </summary>
internal static class BundleExtractionCleanupService
{
    internal const string BaseDirVariable = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";

    /// <summary>A folder younger than this can belong to a build that is starting now (the host still extracts).</summary>
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(10);

    internal sealed record CleanupResult(int Deleted, int Skipped, long FreedBytes)
    {
        public static readonly CleanupResult None = new(0, 0, 0);
    }

    /// <summary>Cleans up for the running process. Does nothing when the app does not run as an extracted bundle.</summary>
    public static CleanupResult RunForCurrentProcess()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return CleanupResult.None;
        }

        var appName = Path.GetFileNameWithoutExtension(processPath);
        var appRoot = GetExtractionAppRoot(Environment.GetEnvironmentVariable(BaseDirVariable), Path.GetTempPath(), appName);
        if (appRoot is null)
        {
            return CleanupResult.None;
        }

        // The host puts the extraction folder on the native DLL search path; the loaded DLLs confirm it.
        var ownPaths = new List<string>();
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string searchDirectories)
        {
            ownPaths.AddRange(searchDirectories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        using (var self = Process.GetCurrentProcess())
        {
            ownPaths.AddRange(ModulePaths(self));
        }

        var keep = FindExtractionFolders(ownPaths, appRoot);
        if (keep.Count == 0)
        {
            return CleanupResult.None; // not a single-file bundle with extracted files (a dev build, for example)
        }

        // Another instance or an older version can run from its own folder. Renaming does not fail for a folder
        // with loaded DLLs, so look at what those processes have loaded.
        var others = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(appName))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                List<string> paths;
                try
                {
                    paths = ModulePaths(process);
                }
                catch (InvalidOperationException)
                {
                    continue; // it exited
                }
                catch (Win32Exception)
                {
                    return CleanupResult.None; // elevated, or exiting while read: try again at the next start
                }

                var folders = FindExtractionFolders(paths, appRoot);
                if (folders.Count == 0)
                {
                    // Still starting: its host can be about to use an old folder that it has not loaded from yet.
                    // (A dev build with this exe name also stops the cleanup while it runs.)
                    return CleanupResult.None;
                }

                keep.UnionWith(folders);
                others.Add(process.Id);
            }
        }

        // A copy that starts after the list above can be about to use an old folder. Each folder is renamed before
        // it is deleted, so a copy that starts after the rename extracts a new one; a copy that started before it
        // is in a new list taken after the rename, and the folder is then moved back.
        return DeleteOldFolders(appRoot, keep, DateTime.UtcNow, MinimumAge, () => NoNewInstance(appName, others));
    }

    private static bool NoNewInstance(string appName, IReadOnlySet<int> known)
    {
        var processes = Process.GetProcessesByName(appName);
        try
        {
            return processes.All(p => p.Id == Environment.ProcessId || known.Contains(p.Id));
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// The folder that holds one extraction folder per build: <c>&lt;base&gt;\&lt;app name&gt;</c>. Null when
    /// <c>DOTNET_BUNDLE_EXTRACT_BASE_DIR</c> is set but is not a full path: the host resolves such a value against
    /// its own working folder, so the app does not guess where that is and deletes nothing.
    /// </summary>
    internal static string? GetExtractionAppRoot(string? baseDirOverride, string tempPath, string appName)
    {
        if (string.IsNullOrWhiteSpace(baseDirOverride))
        {
            return Normalize(Path.Combine(tempPath, ".net", appName));
        }

        return Path.IsPathFullyQualified(baseDirOverride) ? Normalize(Path.Combine(baseDirOverride, appName)) : null;
    }

    /// <summary>The extraction folders (direct children of <paramref name="appRoot"/>) that the paths are in.</summary>
    internal static HashSet<string> FindExtractionFolders(IEnumerable<string> paths, string appRoot)
    {
        var root = Normalize(appRoot) + Path.DirectorySeparatorChar;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            string full;
            try
            {
                full = Normalize(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = full[root.Length..];
            var separator = rest.IndexOf(Path.DirectorySeparatorChar);
            var child = separator < 0 ? rest : rest[..separator];
            if (child.Length > 0)
            {
                folders.Add(root + child);
            }
        }

        return folders;
    }

    /// <summary>
    /// Deletes every folder in <paramref name="appRoot"/> that is not in <paramref name="keep"/> and is older than
    /// <paramref name="minimumAge"/>. Each folder is first renamed (fails when a file in it is open), then deleted.
    /// IO errors skip that folder. When <paramref name="stillSafe"/> says no after a rename, the folder is moved back
    /// and the cleanup stops.
    /// </summary>
    internal static CleanupResult DeleteOldFolders(
        string appRoot, IReadOnlySet<string> keep, DateTime nowUtc, TimeSpan minimumAge, Func<bool>? stillSafe = null)
    {
        var keepFull = new HashSet<string>(keep.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        string[] folders;
        try
        {
            folders = Directory.GetDirectories(appRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CleanupResult.None; // no extraction folder yet, or not readable
        }

        int deleted = 0, skipped = 0;
        long freed = 0;
        foreach (var folder in folders)
        {
            var full = Normalize(folder);
            if (keepFull.Contains(full))
            {
                continue;
            }

            try
            {
                var info = new DirectoryInfo(full);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped++; // a link the host did not make
                    continue;
                }

                var newest = info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
                if (nowUtc - newest < minimumAge)
                {
                    skipped++;
                    continue;
                }

                var size = info.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                var doomed = full + ".old-" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(full, doomed);
                if (stillSafe is not null && !stillSafe())
                {
                    Directory.Move(doomed, full); // a new copy started: it can use this folder
                    return new CleanupResult(deleted, skipped + 1, freed);
                }

                Directory.Delete(doomed, recursive: true);
                deleted++;
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++; // in use or locked: try again at the next start
            }
        }

        return new CleanupResult(deleted, skipped, freed);
    }

    private static List<string> ModulePaths(Process process)
    {
        var paths = new List<string>();
        foreach (ProcessModule module in process.Modules)
        {
            using (module)
            {
                if (!string.IsNullOrEmpty(module.FileName))
                {
                    paths.Add(module.FileName);
                }
            }
        }

        return paths;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
