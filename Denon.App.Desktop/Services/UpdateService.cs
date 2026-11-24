using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;
using Microsoft.Win32;

namespace Denon.App.Desktop.Services;

/// <summary>An app release on GitHub: its version, notes (the CHANGELOG section) and download links.</summary>
public sealed record AppRelease(Version Version, string Tag, string Notes, string PageUrl, string? InstallerUrl, string? ChecksumsUrl)
{
    public string Label => Version.ToString(3);
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

    /// <summary>The installer's AppId (installer/DenonDesktop.iss) and the uninstall key Inno Setup writes for it.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B48C17F1-4D83-4C5C-9E3A-9D5E6B2C140A}_is1";

    /// <summary>A download that receives nothing for this long has stalled.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Where installers are downloaded; emptied at the next start (Setup has exited by then).</summary>
    public static string DownloadFolder => Path.Combine(Path.GetTempPath(), "DenonDesktop-Update");
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
                foreach (var file in Directory.EnumerateFiles(DownloadFolder, "*.exe"))
                {
                    try
                    {
                        File.Delete(file);
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

            string? Asset(string name) => release.TryGetProperty("assets", out var assets)
                ? assets.EnumerateArray()
                    .FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                    is { ValueKind: JsonValueKind.Object } a ? a.GetProperty("browser_download_url").GetString() : null
                : null;

            best = new AppRelease(
                version,
                tag,
                release.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                release.TryGetProperty("html_url", out var page) ? page.GetString() ?? string.Empty : string.Empty,
                Asset(InstallerName),
                Asset(ChecksumsName));
        }

        return best;
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

    /// <summary>Release notes as plain text: Markdown marks removed, headings and bullets kept readable.</summary>
    public static string PlainNotes(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
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
    /// Downloads the release's installer to a temporary folder and checks its SHA-256 against the release's
    /// SHA256SUMS.txt. Returns the installer path; throws when a file is missing or the check fails.
    /// </summary>
    public async Task<string> DownloadVerifiedAsync(AppRelease release, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (release.InstallerUrl is null || release.ChecksumsUrl is null)
        {
            throw new InvalidOperationException($"Release {release.Label} has no installer or no checksums.");
        }

        var sums = await _http.GetStringAsync(release.ChecksumsUrl, cancellationToken);
        var expected = ChecksumFor(sums, InstallerName)
            ?? throw new InvalidOperationException($"{ChecksumsName} of release {release.Label} has no line for {InstallerName}.");

        Directory.CreateDirectory(DownloadFolder);
        var path = Path.Combine(DownloadFolder, $"DenonDesktop-Setup-{release.Label}.exe");

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
        return path;
    }

    /// <summary>
    /// Starts the installer with a progress window and no questions. It closes this app (Restart Manager), installs
    /// over it, and starts the new version (/LAUNCHAFTER=1). The caller then exits the app.
    /// </summary>
    public void StartInstaller(string installerPath)
    {
        var log = Path.Combine(Path.GetDirectoryName(installerPath)!, "install.log");
        Process.Start(new ProcessStartInfo(installerPath)
        {
            Arguments = $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /LAUNCHAFTER=1 \"/LOG={log}\"",
            UseShellExecute = true,
        });
        _log.Info($"Update installer started: {installerPath}");
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

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    [GeneratedRegex(@"^v(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex AppTag();

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+)$")]
    private static partial Regex SumLine();

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();
}
