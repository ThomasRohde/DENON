using Denon.App.Core.Plugins;
using Denon.App.Desktop.Services;
using Denon.App.PluginSdk;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>In-app updates: which GitHub release counts as the newest app version, and how its files are checked.</summary>
public sealed class UpdateServiceTests
{
    private const string Releases = """
        [
          { "tag_name": "chromecast-v0.9.0", "draft": false, "prerelease": false, "body": "plugin", "html_url": "https://github.com/ThomasRohde/DENON/cc", "assets": [] },
          { "tag_name": "v9.9.9", "draft": true, "prerelease": false, "body": "draft", "html_url": "https://github.com/ThomasRohde/DENON/draft", "assets": [] },
          { "tag_name": "v3.0.0-beta", "draft": false, "prerelease": true, "body": "beta", "html_url": "https://github.com/ThomasRohde/DENON/beta", "assets": [] },
          { "tag_name": "v2.9.0", "draft": false, "prerelease": false, "body": "older", "html_url": "https://github.com/ThomasRohde/DENON/290", "assets": [] },
          { "tag_name": "v2.10.0", "draft": false, "prerelease": false, "body": "### Added\n- **New** thing", "html_url": "https://github.com/ThomasRohde/DENON/2100",
            "assets": [
              { "name": "DenonDesktop-Setup-win-x64.exe", "browser_download_url": "https://github.com/ThomasRohde/DENON/setup.exe" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/ThomasRohde/DENON/sums.txt" }
            ] },
          { "tag_name": "appletv-v0.4.0", "draft": false, "prerelease": false, "body": "plugin", "html_url": "https://github.com/ThomasRohde/DENON/atv", "assets": [] }
        ]
        """;

    [Fact]
    public void The_newest_published_app_release_wins()
    {
        var latest = UpdateService.PickLatest(Releases);

        Assert.NotNull(latest);
        Assert.Equal("v2.10.0", latest.Tag); // numeric order: 2.10 is newer than 2.9; plugins, drafts and betas are skipped
        Assert.Equal("2.10.0", latest.Label);
        Assert.Equal("https://github.com/ThomasRohde/DENON/setup.exe", latest.InstallerUrl);
        Assert.Equal("https://github.com/ThomasRohde/DENON/sums.txt", latest.ChecksumsUrl);
        Assert.Equal("https://github.com/ThomasRohde/DENON/2100", latest.PageUrl);
    }

    [Fact]
    public void No_app_release_gives_null()
    {
        Assert.Null(UpdateService.PickLatest("""[{ "tag_name": "chromecast-v1.0.0", "draft": false, "prerelease": false }]"""));
        Assert.Null(UpdateService.PickLatest("[]"));
    }

    [Theory]
    [InlineData("2.2.3", "2.2.2", true)]
    [InlineData("2.3.0", "2.2.9", true)]
    [InlineData("2.2.2", "2.2.2", false)]
    [InlineData("2.2.1", "2.2.2", false)]
    [InlineData("2.10.0", "2.9.0", true)]
    [InlineData("9.0.0", "not a version", false)]
    public void Only_a_newer_version_is_an_update(string candidate, string running, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(Version.Parse(candidate), running));

    [Fact]
    public void The_checksum_line_for_the_installer_is_found()
    {
        var hash = new string('a', 64);
        var other = new string('b', 64);
        var sums = $"{other}  Denon.App.Desktop-win-x64.exe\r\n{hash.ToUpperInvariant()} *DenonDesktop-Setup-win-x64.exe\r\n";

        Assert.Equal(hash, UpdateService.ChecksumFor(sums, "DenonDesktop-Setup-win-x64.exe"));
        Assert.Equal(other, UpdateService.ChecksumFor(sums, "Denon.App.Desktop-win-x64.exe"));
        Assert.Null(UpdateService.ChecksumFor(sums, "missing.exe"));
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Denon Desktop", @"C:\Users\x\AppData\Local\Programs\Denon Desktop\", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Denon Desktop", @"c:\users\x\appdata\local\programs\denon desktop", true)]
    [InlineData(@"C:\Users\x\Downloads", @"C:\Users\x\AppData\Local\Programs\Denon Desktop\", false)]
    [InlineData(@"C:\Users\x\Downloads", null, false)]
    [InlineData(null, @"C:\Users\x\Downloads", false)]
    public void Only_the_registered_install_folder_updates_in_place(string? exeFolder, string? installLocation, bool same) =>
        Assert.Equal(same, UpdateService.IsSameFolder(exeFolder, installLocation));

    [Fact]
    public void Release_notes_read_as_plain_text()
    {
        var notes = UpdateService.PlainNotes("### Added\r\n- **Website** at `site/`, see [the guide](https://x).\r\n\r\n\r\n\r\n### Fixed\r\n- A fix.");

        Assert.Equal("Added\n• Website at site/, see the guide.\n\nFixed\n• A fix.", notes);
    }

    [Fact]
    public void Release_notes_leave_out_the_download_list()
    {
        var notes = UpdateService.PlainNotes("### Fixed\n- A fix.\n\n---\n\n### Downloads\n- Setup.exe\n\n### SHA-256 Checksums");

        Assert.Equal("Fixed\n• A fix.", notes);
    }

    [Theory]
    [InlineData("https://github.com/ThomasRohde/DENON/releases/download/v2.10.0/DenonDesktop-Setup-win-x64.exe", true)]
    [InlineData("https://GitHub.com/thomasrohde/denon/releases/tag/v2.10.0", true)]
    [InlineData("http://github.com/ThomasRohde/DENON/releases/download/v2.10.0/x.exe", false)]
    [InlineData("https://github.com:8443/ThomasRohde/DENON/releases", false)]
    [InlineData("https://user@github.com/ThomasRohde/DENON/releases", false)]
    [InlineData("https://github.com/someone-else/DENON/releases/download/v2.10.0/x.exe", false)]
    [InlineData("https://github.com.example.net/ThomasRohde/DENON/releases", false)]
    [InlineData("https://example.net/ThomasRohde/DENON/releases", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_apps_own_github_addresses_are_trusted(string? url, bool trusted) =>
        Assert.Equal(trusted, UpdateService.IsTrustedGitHubUrl(url));

    [Fact]
    public void Release_links_outside_the_apps_github_are_ignored()
    {
        const string json = """
            [
              { "tag_name": "v3.0.0", "draft": false, "prerelease": false, "body": "x", "html_url": "http://example.net/page",
                "assets": [
                  { "name": "DenonDesktop-Setup-win-x64.exe", "browser_download_url": "https://example.net/setup.exe" },
                  { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/ThomasRohde/DENON/releases/download/v3.0.0/SHA256SUMS.txt" }
                ] }
            ]
            """;

        var latest = UpdateService.PickLatest(json);

        Assert.NotNull(latest);
        Assert.Null(latest.InstallerUrl); // no installer: the app offers the release page instead
        Assert.Equal(string.Empty, latest.PageUrl);
        Assert.Equal("https://github.com/ThomasRohde/DENON/releases/download/v3.0.0/SHA256SUMS.txt", latest.ChecksumsUrl);
    }

    [Fact]
    public void A_homepod_release_is_found_as_a_plugin_update()
    {
        const string json = """
            [
              { "tag_name": "homepod-v0.3.0", "draft": false, "prerelease": false, "body": "pod", "html_url": "https://github.com/ThomasRohde/DENON/releases/tag/homepod-v0.3.0",
                "assets": [
                  { "name": "Denon.Plugin.HomePod-0.3.0.zip", "browser_download_url": "https://github.com/ThomasRohde/DENON/releases/download/homepod-v0.3.0/Denon.Plugin.HomePod-0.3.0.zip" }
                ] },
              { "tag_name": "v2.10.0", "draft": false, "prerelease": false, "body": "app", "html_url": "https://github.com/ThomasRohde/DENON/releases/tag/v2.10.0", "assets": [] }
            ]
            """;

        var update = Assert.Single(UpdateService.MatchPluginUpdates(json, [ExternalPlugin("homepod", "HomePod", "0.2.0")]));

        Assert.Equal("homepod", update.PluginId);
        Assert.Equal("0.3.0", update.LatestVersionLabel);
        Assert.Equal("v2.10.0", UpdateService.PickLatest(json)?.Tag); // the plugin tag is never the app's release
    }

    private static PluginRecordSnapshot ExternalPlugin(string id, string name, string version) => new()
    {
        Id = id,
        Name = name,
        Version = version,
        Author = "test",
        Description = "",
        Category = "Streaming Sources",
        AccentHex = "#000000",
        IconGlyph = "P",
        Capabilities = PluginCapabilities.Actions,
        Origin = PluginOrigin.ExternalDll,
        AssemblyPath = null,
        IsEnabled = true,
        IsStarted = true,
        HasDashboardView = true,
        Health = PluginHealthState.Connected,
        StatusText = "",
        LastError = null,
        Actions = [],
    };

    private const string PluginReleases = """
        [
          { "tag_name": "chromecast-v0.10.0", "draft": false, "prerelease": false, "body": "### Added\n- More Cast apps", "html_url": "https://github.com/ThomasRohde/DENON/cc-010",
            "assets": [
              { "name": "Denon.Plugin.Chromecast-0.10.0.zip", "browser_download_url": "https://github.com/ThomasRohde/DENON/cc-0.10.0.zip" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/ThomasRohde/DENON/cc-sums.txt" }
            ] },
          { "tag_name": "chromecast-v0.9.0", "draft": false, "prerelease": false, "body": "older", "html_url": "https://github.com/ThomasRohde/DENON/cc-09", "assets": [] },
          { "tag_name": "chromecast-v9.0.0", "draft": true, "prerelease": false, "body": "draft", "html_url": "https://github.com/ThomasRohde/DENON/cc-draft", "assets": [] },
          { "tag_name": "chromecast-v8.0.0", "draft": false, "prerelease": true, "body": "beta", "html_url": "https://github.com/ThomasRohde/DENON/cc-beta", "assets": [] },
          { "tag_name": "appletv-v0.5.0", "draft": false, "prerelease": false, "body": "### Fixed\n- Grace period", "html_url": "https://github.com/ThomasRohde/DENON/atv-05",
            "assets": [
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/ThomasRohde/DENON/atv-sums.txt" },
              { "name": "Denon.Plugin.AppleTv-0.5.0.zip", "browser_download_url": "https://github.com/ThomasRohde/DENON/atv-0.5.0.zip" }
            ] },
          { "tag_name": "appletv-v0.4.0", "draft": false, "prerelease": false, "body": "older", "html_url": "https://github.com/ThomasRohde/DENON/atv-04", "assets": [] },
          { "tag_name": "v2.10.0", "draft": false, "prerelease": false, "body": "app release", "html_url": "https://github.com/ThomasRohde/DENON/app", "assets": [] }
        ]
        """;

    [Fact]
    public void The_newest_published_release_per_plugin_is_picked()
    {
        var latest = UpdateService.PickLatestPlugins(PluginReleases);

        Assert.Equal(2, latest.Count);

        var cc = latest["chromecast"];
        Assert.Equal("chromecast-v0.10.0", cc.Tag);
        Assert.Equal("0.10.0", cc.Label);
        Assert.Equal("https://github.com/ThomasRohde/DENON/cc-0.10.0.zip", cc.ZipUrl);
        Assert.Equal("https://github.com/ThomasRohde/DENON/cc-sums.txt", cc.ChecksumsUrl);
        Assert.Equal("https://github.com/ThomasRohde/DENON/cc-010", cc.PageUrl);

        var atv = latest["apple-tv"];
        Assert.Equal("appletv-v0.5.0", atv.Tag);
        Assert.Equal("0.5.0", atv.Label);
        Assert.Equal("https://github.com/ThomasRohde/DENON/atv-0.5.0.zip", atv.ZipUrl);
        Assert.Equal("https://github.com/ThomasRohde/DENON/atv-sums.txt", atv.ChecksumsUrl);
        Assert.Equal("https://github.com/ThomasRohde/DENON/atv-05", atv.PageUrl);
    }

    [Fact]
    public void Only_installed_plugins_with_a_newer_release_are_reported()
    {
        var installed = new[]
        {
            ExternalPlugin("chromecast", "Chromecast & Google Cast", "0.5.0"),
            ExternalPlugin("apple-tv", "Apple TV", "0.5.0"), // already current
            ExternalPlugin("smart-tv", "LG Smart TV", "1.0.0"), // built-in / not in feed
        };

        var updates = UpdateService.MatchPluginUpdates(PluginReleases, installed);

        var update = Assert.Single(updates);
        Assert.Equal("chromecast", update.PluginId);
        Assert.Equal("Chromecast & Google Cast", update.PluginName);
        Assert.Equal("0.5.0", update.CurrentVersion);
        Assert.Equal(new Version(0, 10, 0), update.LatestVersion);
        Assert.Equal("0.10.0", update.LatestVersionLabel);
        Assert.Equal("chromecast-v0.10.0", update.Tag);
        Assert.Equal("### Added\n- More Cast apps", update.ReleaseNotes);
        Assert.Equal("https://github.com/ThomasRohde/DENON/cc-010", update.ReleaseUrl);
        Assert.Equal("https://github.com/ThomasRohde/DENON/cc-0.10.0.zip", update.ZipUrl);
        Assert.Equal("https://github.com/ThomasRohde/DENON/cc-sums.txt", update.ChecksumsUrl);
    }

    [Fact]
    public void Multiple_plugin_updates_are_sorted_by_name_and_deduplicated()
    {
        var installed = new[]
        {
            ExternalPlugin("chromecast", "Chromecast & Google Cast", "0.5.0"),
            ExternalPlugin("apple-tv", "Apple TV", "0.4.0"),
            ExternalPlugin("apple-tv", "Apple TV", "0.4.0"), // duplicate ignored
        };

        var updates = UpdateService.MatchPluginUpdates(PluginReleases, installed);

        Assert.Equal(2, updates.Count);
        Assert.Equal("apple-tv", updates[0].PluginId);
        Assert.Equal("0.5.0", updates[0].LatestVersionLabel);
        Assert.Equal("chromecast", updates[1].PluginId);
        Assert.Equal("0.10.0", updates[1].LatestVersionLabel);
    }

    [Theory]
    [InlineData("chromecast", true)]
    [InlineData("APPLE-TV", true)]
    [InlineData("hue", false)]
    [InlineData("smart-tv", false)]
    [InlineData(null, false)]
    public void Only_known_external_plugins_are_trackable(string? id, bool expected) =>
        Assert.Equal(expected, UpdateService.IsTrackablePlugin(id));
}
