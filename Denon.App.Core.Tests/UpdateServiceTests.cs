using Denon.App.Desktop.Services;
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
}
