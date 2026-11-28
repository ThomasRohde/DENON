using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;
using Microsoft.Win32;

namespace Denon.App.Desktop.Services;

/// <summary>A downloaded installer whose SHA-256 matched the release's checksums.</summary>
public sealed record VerifiedInstaller(string Path, string Sha256);

/// <summary>An app release on GitHub: its version, notes (the CHANGELOG section) and download links.</summary>
public sealed record AppRelease(Version Version, string Tag, string Notes, string PageUrl, string? InstallerUrl, string? ChecksumsUrl)
{
    public string Label => Version.ToString(3);
}

/// <summary>An external plugin release on GitHub: its plugin id, version, notes, release page and zip download link.</summary>
public sealed record PluginRelease(
    string PluginId,
    Version Version,
    string Tag,
    string Notes,
    string PageUrl,
    string? ZipUrl,
    string? ChecksumsUrl)
{
    public string Label => Version.ToString(3);
}

/// <summary>An available update for an installed external plugin.</summary>
public sealed record PluginUpdateInfo(
    string PluginId,
    string PluginName,
    string CurrentVersion,
    Version LatestVersion,
    string Tag,
    string ReleaseNotes,
    string ReleaseUrl,
    string? ZipUrl,
    string? ChecksumsUrl)
{
    public string LatestVersionLabel => LatestVersion.ToString(3);
}

/// <summary>Combined result of checking GitHub for both app and installed external plugin updates.</summary>
public sealed record UpdateCheckResult(
    AppRelease? AppUpdate,
    IReadOnlyList<PluginUpdateInfo> PluginUpdates)
{
    public bool HasAnyUpdate => AppUpdate is not null || PluginUpdates.Count > 0;
}

/// <summary>
/// In-app updates: finds a newer app release on GitHub, downloads the installer, checks it against the release's
/// SHA256SUMS.txt and starts it silently; the installer closes the app and starts the new version
/// (installer/DenonDesktop.iss, /LAUNCHAFTER=1). Nothing installs without the user's click.
/// </summary>
public sealed partial class UpdateService : IDisposable
{
    /// <summary>GitHub's "latest" release: always the app's (the plugin release workflows set make_latest: false).</summary>
    public const string LatestReleaseApi = "https://api.github.com/repos/ThomasRohde/DENON/releases/latest";

    /// <summary>The release list, read when "latest" is not an app release.</summary>
    public const string ReleasesApi = "https://api.github.com/repos/ThomasRohde/DENON/releases?per_page=100";

    /// <summary>The releases page, opened when a release has no trusted page address of its own.</summary>
    public const string ReleasesPageUrl = "https://github.com/ThomasRohde/DENON/releases";

    private const string RepositoryPath = "/ThomasRohde/DENON/";

    /// <summary>The installer's AppId (installer/DenonDesktop.iss) and the uninstall key Inno Setup writes for it.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B48C17F1-4D83-4C5C-9E3A-9D5E6B2C140A}_is1";

    /// <summary>A download that receives nothing for this long has stalled.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where installers are downloaded, each in a new folder of its own; emptied at the next start (Setup has exited
    /// by then). Under the app's own data folder, not the shared temp folder.
    /// </summary>
    public static string DownloadFolder => Path.Combine(AppDataPaths.RootDirectory, "updates");
    public const string InstallerName = "DenonDesktop-Setup-win-x64.exe";
    public const string ChecksumsName = "SHA256SUMS.txt";

    private readonly HttpClient _http;
    private readonly AppLogStore _log;

    public UpdateService(AppLogStore log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"DenonDesktop/{AppInfo.Version} (https://github.com/ThomasRohde/DENON)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>
    /// True when the running exe is the one Denon Desktop's installer registered (its install folder in the
    /// uninstall key, per user or for all users). The portable exe, or a copy elsewhere, cannot be updated in place,
    /// so it gets the release page instead.
    /// </summary>
    public static bool IsInstalled
    {
        get
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = root.OpenSubKey(UninstallKey);
                    if (IsSameFolder(exeDir, key?.GetValue("InstallLocation") as string))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // No access: not this install.
                }
            }

            return false;
        }
    }

    /// <summary>True when both paths name the same folder (case and a trailing separator do not matter).</summary>
    public static bool IsSameFolder(string? a, string? b)
    {
        static string? Norm(string? p) => string.IsNullOrWhiteSpace(p)
            ? null
            : Path.GetFullPath(p.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var (x, y) = (Norm(a), Norm(b));
        return x is not null && y is not null && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes installers left from earlier updates (and partial downloads). Best effort.</summary>
    public static void CleanDownloads()
    {
        try
        {
            if (Directory.Exists(DownloadFolder))
            {
                foreach (var folder in Directory.EnumerateDirectories(DownloadFolder))
                {
                    try
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The newest app release when it is newer than this app (or than <paramref name="asVersion"/>, for screenshot
    /// mode checks), else null.
    /// </summary>
    public async Task<AppRelease?> FindUpdateAsync(string? asVersion = null, CancellationToken cancellationToken = default)
    {
        // "latest" first (one small request); the full list only if it is not an app release.
        var latest = PickLatest($"[{await _http.GetStringAsync(LatestReleaseApi, cancellationToken)}]")
                     ?? PickLatest(await _http.GetStringAsync(ReleasesApi, cancellationToken));
        return latest is not null && IsNewer(latest.Version, asVersion ?? AppInfo.Version) ? latest : null;
    }

    /// <summary>
    /// Checks GitHub for both a newer app release and newer external plugin releases (chromecast-v*, appletv-v*, homepod-v*)
    /// matching <paramref name="installedPlugins"/>, reusing a single releases request when external plugins are installed.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        IEnumerable<PluginRecordSnapshot>? installedPlugins = null,
        string? asAppVersion = null,
        CancellationToken cancellationToken = default)
    {
        var plugins = installedPlugins?.Where(p => IsTrackablePlugin(p.Id)).ToList() ?? [];
        if (plugins.Count == 0)
        {
            var appOnly = await FindUpdateAsync(asAppVersion, cancellationToken);
            return new UpdateCheckResult(appOnly, []);
        }

        var (releasesJson, latestPlugins) = await FetchPluginReleasesAsync(plugins, cancellationToken);
        var latestApp = PickLatest(releasesJson)
                        ?? PickLatest($"[{await _http.GetStringAsync(LatestReleaseApi, cancellationToken)}]");
        var appUpdate = latestApp is not null && IsNewer(latestApp.Version, asAppVersion ?? AppInfo.Version)
            ? latestApp
            : null;
        var pluginUpdates = MatchPluginUpdates(latestPlugins, plugins);
        return new UpdateCheckResult(appUpdate, pluginUpdates);
    }

    /// <summary>
    /// Checks GitHub for newer releases of any installed external plugins (chromecast-v*, appletv-v*, homepod-v*).
    /// Makes no network request when no trackable external plugin is installed.
    /// </summary>
    public async Task<IReadOnlyList<PluginUpdateInfo>> FindPluginUpdatesAsync(
        IEnumerable<PluginRecordSnapshot> installedPlugins,
        CancellationToken cancellationToken = default)
    {
        var plugins = installedPlugins.Where(p => IsTrackablePlugin(p.Id)).ToList();
        if (plugins.Count == 0)
        {
            return [];
        }

        var (_, latestPlugins) = await FetchPluginReleasesAsync(plugins, cancellationToken);
        return MatchPluginUpdates(latestPlugins, plugins);
    }

    /// <summary>Most release pages read to find an installed plugin's newest release (100 releases a page).</summary>
    public const int MaxReleasePages = 5;

    /// <summary>
    /// The first releases page, and the newest release per plugin. The app is released more often than the
    /// plugins, so a plugin's newest release can be past the first 100: later pages are read while an installed
    /// plugin is still missing and the page was full.
    /// </summary>
    private async Task<(string FirstPage, IReadOnlyDictionary<string, PluginRelease> LatestPlugins)> FetchPluginReleasesAsync(
        IReadOnlyCollection<PluginRecordSnapshot> plugins,
        CancellationToken cancellationToken)
    {
        var firstPage = await _http.GetStringAsync(ReleasesApi, cancellationToken);
        var latest = new Dictionary<string, PluginRelease>(PickLatestPlugins(firstPage), StringComparer.OrdinalIgnoreCase);
        var pageJson = firstPage;
        for (var page = 2;
             page <= MaxReleasePages && ReleaseCount(pageJson) >= 100 && plugins.Any(p => !latest.ContainsKey(p.Id));
             page++)
        {
            pageJson = await _http.GetStringAsync($"{ReleasesApi}&page={page}", cancellationToken);
            foreach (var (id, release) in PickLatestPlugins(pageJson))
            {
                if (!latest.TryGetValue(id, out var have) || release.Version > have.Version)
                {
                    latest[id] = release;
                }
            }
        }

        return (firstPage, latest);
    }

    private static int ReleaseCount(string releasesJson)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
    }

    /// <summary>True when <paramref name="pluginId"/> corresponds to an external plugin published in this repository's GitHub releases.</summary>
    public static bool IsTrackablePlugin(string? pluginId) =>
        string.Equals(pluginId, NowPlayingResolver.ChromecastPluginId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pluginId, NowPlayingResolver.AppleTvPluginId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pluginId, NowPlayingResolver.HomePodPluginId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The newest published app release in a GitHub releases list: tags vX.Y.Z only (the plugins have their own
    /// tags), no drafts and no pre-releases.
    /// </summary>
    public static AppRelease? PickLatest(string releasesJson)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        AppRelease? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
            if (Bool(release, "draft") || Bool(release, "prerelease") || AppTag().Match(tag) is not { Success: true } match)
            {
                continue;
            }

            var version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
            if (best is not null && version <= best.Version)
            {
                continue;
            }

            best = new AppRelease(
                version,
                tag,
                release.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                PageUrl(release),
                AssetUrl(release, InstallerName),
                AssetUrl(release, ChecksumsName));
        }

        return best;
    }

    /// <summary>
    /// The newest published release per external plugin in a GitHub releases list: tags <c>chromecast-vX.Y.Z</c>
    /// <c>appletv-vX.Y.Z</c> and <c>homepod-vX.Y.Z</c>, skipping drafts and pre-releases.
    /// </summary>
    public static IReadOnlyDictionary<string, PluginRelease> PickLatestPlugins(string releasesJson)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        var bestByPlugin = new Dictionary<string, PluginRelease>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tag = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
            if (Bool(release, "draft") || Bool(release, "prerelease") || !TryParsePluginTag(tag, out var pluginId, out var version))
            {
                continue;
            }

            if (bestByPlugin.TryGetValue(pluginId, out var existing) && version <= existing.Version)
            {
                continue;
            }

            bestByPlugin[pluginId] = new PluginRelease(
                pluginId,
                version,
                tag,
                release.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                PageUrl(release),
                AssetUrlEndingWith(release, ".zip"),
                AssetUrl(release, ChecksumsName));
        }

        return bestByPlugin;
    }

    /// <summary>
    /// Compares the latest plugin releases in <paramref name="releasesJson"/> against <paramref name="installedPlugins"/>
    /// and returns every installed external plugin that has a strictly newer published release.
    /// </summary>
    public static IReadOnlyList<PluginUpdateInfo> MatchPluginUpdates(
        string releasesJson,
        IEnumerable<PluginRecordSnapshot> installedPlugins) =>
        MatchPluginUpdates(PickLatestPlugins(releasesJson), installedPlugins);

    /// <summary>Every installed external plugin with a strictly newer release in <paramref name="latestByPlugin"/>.</summary>
    public static IReadOnlyList<PluginUpdateInfo> MatchPluginUpdates(
        IReadOnlyDictionary<string, PluginRelease> latestByPlugin,
        IEnumerable<PluginRecordSnapshot> installedPlugins)
    {
        if (latestByPlugin.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updates = new List<PluginUpdateInfo>();
        foreach (var plugin in installedPlugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Id) || !seen.Add(plugin.Id))
            {
                continue;
            }

            if (!latestByPlugin.TryGetValue(plugin.Id, out var release) || !IsNewer(release.Version, plugin.Version))
            {
                continue;
            }

            updates.Add(new PluginUpdateInfo(
                PluginId: plugin.Id,
                PluginName: string.IsNullOrWhiteSpace(plugin.Name) ? plugin.Id : plugin.Name,
                CurrentVersion: plugin.Version,
                LatestVersion: release.Version,
                Tag: release.Tag,
                ReleaseNotes: release.Notes,
                ReleaseUrl: release.PageUrl,
                ZipUrl: release.ZipUrl,
                ChecksumsUrl: release.ChecksumsUrl));
        }

        updates.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.PluginName, b.PluginName));
        return updates;
    }

    /// <summary>True when <paramref name="candidate"/> is newer than the running version (for example "2.2.2").</summary>
    public static bool IsNewer(Version candidate, string current) =>
        Version.TryParse(current, out var running) && candidate > Normalize(running);

    /// <summary>The SHA-256 for <paramref name="fileName"/> in a SHA256SUMS.txt ("hash  name" or "hash *name" lines).</summary>
    public static string? ChecksumFor(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var m = SumLine().Match(line.Trim());
            if (m.Success && string.Equals(m.Groups[2].Value, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return m.Groups[1].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// True for an https address on github.com in this app's repository: the only place release pages and files
    /// come from. Anything else in the release feed is ignored (never downloaded, never opened).
    /// </summary>
    public static bool IsTrustedGitHubUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith(RepositoryPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Release notes as plain text: Markdown marks removed, headings and bullets kept readable. The release page's
    /// download list and checksums (after a "---" line) are left out: the update window has its own buttons.
    /// </summary>
    public static string PlainNotes(string markdown)
    {
        var all = markdown.Replace("\r\n", "\n").Split('\n');
        var end = Array.FindIndex(all, line => line.Trim() == "---");
        var lines = (end >= 0 ? all[..end] : all).Select(line =>
        {
            var text = line.TrimEnd();
            text = Heading().Replace(text, string.Empty);
            text = Bold().Replace(text, "$1");
            text = text.Replace("`", string.Empty);
            text = Link().Replace(text, "$1");
            return text.StartsWith("- ", StringComparison.Ordinal) ? "• " + text[2..] : text;
        });
        return Regex.Replace(string.Join("\n", lines).Trim(), "\n{3,}", "\n\n");
    }

    /// <summary>
    /// Downloads the release's installer to a new folder and checks its SHA-256 against the release's
    /// SHA256SUMS.txt. Returns the installer and its checksum; throws when a file is missing or the check fails.
    /// </summary>
    public async Task<VerifiedInstaller> DownloadVerifiedAsync(AppRelease release, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (release.InstallerUrl is null || release.ChecksumsUrl is null)
        {
            throw new InvalidOperationException($"Release {release.Label} has no installer or no checksums.");
        }

        if (!IsTrustedGitHubUrl(release.InstallerUrl) || !IsTrustedGitHubUrl(release.ChecksumsUrl))
        {
            throw new InvalidOperationException($"Release {release.Label} links to files outside the app's GitHub releases.");
        }

        var sums = await _http.GetStringAsync(release.ChecksumsUrl, cancellationToken);
        var expected = ChecksumFor(sums, InstallerName)
            ?? throw new InvalidOperationException($"{ChecksumsName} of release {release.Label} has no line for {InstallerName}.");

        // A new folder per download: nothing can be waiting at a known path.
        var folder = Directory.CreateDirectory(Path.Combine(DownloadFolder, Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(folder, $"DenonDesktop-Setup-{release.Label}.exe");

        // The HTTP timeout ends with the headers; after that, a read that gets nothing for StallTimeout stops the download.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using var response = await _http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
            {
                stall.CancelAfter(StallTimeout);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (total is > 0)
                {
                    progress?.Report((double)done / total.Value);
                }
            }
        }
        catch (Exception exception)
        {
            TryDelete(path); // never leave a partial installer behind
            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The download stopped: nothing arrived for {StallTimeout.TotalSeconds:0} seconds.", exception);
            }

            throw;
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            TryDelete(path);
            throw new InvalidOperationException("The downloaded installer does not match the release's checksum, so it was deleted.");
        }

        _log.Info($"Update {release.Label} downloaded and verified (SHA-256 {actual}).");
        return new VerifiedInstaller(path, actual);
    }

    /// <summary>
    /// Starts the installer with a progress window and no questions. It closes this app (Restart Manager), installs
    /// over it, and starts the new version (/LAUNCHAFTER=1). The caller then exits the app.
    /// The file is checked again right before the start, and held open so it cannot be changed or replaced until
    /// Setup runs.
    /// </summary>
    public void StartInstaller(VerifiedInstaller installer)
    {
        using (var hold = new FileStream(installer.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var actual = Convert.ToHexString(SHA256.HashData(hold)).ToLowerInvariant();
            if (!string.Equals(actual, installer.Sha256, StringComparison.Ordinal))
            {
                hold.Dispose();
                TryDelete(installer.Path);
                throw new InvalidOperationException("The installer changed after its check, so it was deleted.");
            }

            var log = Path.Combine(Path.GetDirectoryName(installer.Path)!, "install.log");
            Process.Start(new ProcessStartInfo(installer.Path)
            {
                Arguments = $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /LAUNCHAFTER=1 \"/LOG={log}\"",
                UseShellExecute = true,
            });
        }

        _log.Info($"Update installer started: {installer.Path}");
    }

    public void Dispose() => _http.Dispose();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string PageUrl(JsonElement release) =>
        release.TryGetProperty("html_url", out var page) && page.GetString() is { } url && IsTrustedGitHubUrl(url)
            ? url
            : string.Empty;

    private static string? AssetUrl(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind == JsonValueKind.Object &&
                asset.TryGetProperty("name", out var n) &&
                string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase) &&
                asset.TryGetProperty("browser_download_url", out var url))
            {
                return IsTrustedGitHubUrl(url.GetString()) ? url.GetString() : null;
            }
        }

        return null;
    }

    private static string? AssetUrlEndingWith(JsonElement release, string suffix)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind == JsonValueKind.Object &&
                asset.TryGetProperty("name", out var n) &&
                n.GetString() is { } fileName &&
                fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                asset.TryGetProperty("browser_download_url", out var url))
            {
                return IsTrustedGitHubUrl(url.GetString()) ? url.GetString() : null;
            }
        }

        return null;
    }

    private static bool TryParsePluginTag(string tag, out string pluginId, out Version version)
    {
        if (PluginTag().Match(tag) is { Success: true } match)
        {
            pluginId = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "chromecast" => NowPlayingResolver.ChromecastPluginId,
                "appletv" => NowPlayingResolver.AppleTvPluginId,
                "homepod" => NowPlayingResolver.HomePodPluginId,
                _ => string.Empty,
            };
            if (pluginId.Length > 0)
            {
                version = new Version(
                    int.Parse(match.Groups[2].Value),
                    int.Parse(match.Groups[3].Value),
                    int.Parse(match.Groups[4].Value));
                return true;
            }
        }

        pluginId = string.Empty;
        version = new Version(0, 0, 0);
        return false;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    [GeneratedRegex(@"^v(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex AppTag();

    [GeneratedRegex(@"^(chromecast|appletv|homepod)-v(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex PluginTag();

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+)$")]
    private static partial Regex SumLine();

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();
}
