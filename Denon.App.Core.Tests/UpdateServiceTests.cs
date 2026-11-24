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
          { "tag_name": "chromecast-v0.9.0", "draft": false, "prerelease": false, "body": "plugin", "html_url": "https://x/cc", "assets": [] },
          { "tag_name": "v9.9.9", "draft": true, "prerelease": false, "body": "draft", "html_url": "https://x/draft", "assets": [] },
          { "tag_name": "v3.0.0-beta", "draft": false, "prerelease": true, "body": "beta", "html_url": "https://x/beta", "assets": [] },
          { "tag_name": "v2.9.0", "draft": false, "prerelease": false, "body": "older", "html_url": "https://x/290", "assets": [] },
          { "tag_name": "v2.10.0", "draft": false, "prerelease": false, "body": "### Added\n- **New** thing", "html_url": "https://x/2100",
            "assets": [
              { "name": "DenonDesktop-Setup-win-x64.exe", "browser_download_url": "https://x/setup.exe" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://x/sums.txt" }
            ] },
          { "tag_name": "appletv-v0.4.0", "draft": false, "prerelease": false, "body": "plugin", "html_url": "https://x/atv", "assets": [] }
        ]
        """;

    [Fact]
    public void The_newest_published_app_release_wins()
    {
        var latest = UpdateService.PickLatest(Releases);

        Assert.NotNull(latest);
        Assert.Equal("v2.10.0", latest.Tag); // numeric order: 2.10 is newer than 2.9; plugins, drafts and betas are skipped
        Assert.Equal("2.10.0", latest.Label);
        Assert.Equal("https://x/setup.exe", latest.InstallerUrl);
        Assert.Equal("https://x/sums.txt", latest.ChecksumsUrl);
        Assert.Equal("https://x/2100", latest.PageUrl);
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
          { "tag_name": "chromecast-v0.10.0", "draft": false, "prerelease": false, "body": "### Added\n- More Cast apps", "html_url": "https://x/cc-010",
            "assets": [
              { "name": "Denon.Plugin.Chromecast-0.10.0.zip", "browser_download_url": "https://x/cc-0.10.0.zip" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://x/cc-sums.txt" }
            ] },
          { "tag_name": "chromecast-v0.9.0", "draft": false, "prerelease": false, "body": "older", "html_url": "https://x/cc-09", "assets": [] },
          { "tag_name": "chromecast-v9.0.0", "draft": true, "prerelease": false, "body": "draft", "html_url": "https://x/cc-draft", "assets": [] },
          { "tag_name": "chromecast-v8.0.0", "draft": false, "prerelease": true, "body": "beta", "html_url": "https://x/cc-beta", "assets": [] },
          { "tag_name": "appletv-v0.5.0", "draft": false, "prerelease": false, "body": "### Fixed\n- Grace period", "html_url": "https://x/atv-05",
            "assets": [
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://x/atv-sums.txt" },
              { "name": "Denon.Plugin.AppleTv-0.5.0.zip", "browser_download_url": "https://x/atv-0.5.0.zip" }
            ] },
          { "tag_name": "appletv-v0.4.0", "draft": false, "prerelease": false, "body": "older", "html_url": "https://x/atv-04", "assets": [] },
          { "tag_name": "v2.10.0", "draft": false, "prerelease": false, "body": "app release", "html_url": "https://x/app", "assets": [] }
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
        Assert.Equal("https://x/cc-0.10.0.zip", cc.ZipUrl);
        Assert.Equal("https://x/cc-sums.txt", cc.ChecksumsUrl);
        Assert.Equal("https://x/cc-010", cc.PageUrl);

        var atv = latest["apple-tv"];
        Assert.Equal("appletv-v0.5.0", atv.Tag);
        Assert.Equal("0.5.0", atv.Label);
        Assert.Equal("https://x/atv-0.5.0.zip", atv.ZipUrl);
        Assert.Equal("https://x/atv-sums.txt", atv.ChecksumsUrl);
        Assert.Equal("https://x/atv-05", atv.PageUrl);
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
        Assert.Equal("https://x/cc-010", update.ReleaseUrl);
        Assert.Equal("https://x/cc-0.10.0.zip", update.ZipUrl);
        Assert.Equal("https://x/cc-sums.txt", update.ChecksumsUrl);
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
