using Denon.App.Desktop.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>Old single-file extraction folders in %TEMP%\.net\&lt;app&gt;: which ones go, which ones stay.</summary>
public sealed class BundleExtractionCleanupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DenonExtractTest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void The_app_root_follows_the_base_dir_variable_else_temp_dot_net()
    {
        Assert.Equal(Path.Combine(@"C:\t", ".net", "App"), BundleExtractionCleanupService.GetExtractionAppRoot(null, @"C:\t\", "App"));
        Assert.Equal(Path.Combine(@"C:\t", ".net", "App"), BundleExtractionCleanupService.GetExtractionAppRoot("  ", @"C:\t", "App"));
        Assert.Equal(Path.Combine(@"D:\x", "App"), BundleExtractionCleanupService.GetExtractionAppRoot(@"D:\x\", @"C:\t", "App"));
    }

    [Theory]
    [InlineData(@"extract")]
    [InlineData(@"..\extract")]
    [InlineData(@"\extract")]
    [InlineData(@"C:extract")]
    public void A_base_dir_variable_that_is_not_a_full_path_turns_cleanup_off(string baseDir)
    {
        Assert.Null(BundleExtractionCleanupService.GetExtractionAppRoot(baseDir, @"C:\t", "App"));
    }

    [Fact]
    public void Extraction_folders_come_from_paths_inside_the_app_root_only()
    {
        var appRoot = Path.Combine(_root, "App");
        var found = BundleExtractionCleanupService.FindExtractionFolders(
            [
                Path.Combine(appRoot, "ID1") + Path.DirectorySeparatorChar, // native search dir, with a trailing slash
                Path.Combine(appRoot, "id1", "wpfgfx_cor3.dll"),             // same folder, other case
                Path.Combine(appRoot, "ID2", "PresentationNative_cor3.dll"),
                Path.Combine(_root, "AppOther", "ID3", "x.dll"),             // a sibling app with a longer name
                Path.Combine(_root, "elsewhere.dll"),
                appRoot,
                "",
            ],
            appRoot);

        Assert.Equal(2, found.Count);
        Assert.Contains(Path.Combine(appRoot, "ID1"), found);
        Assert.Contains(Path.Combine(appRoot, "ID2"), found);
    }

    [Fact]
    public void Old_folders_go_and_the_kept_and_new_ones_stay()
    {
        var current = MakeFolder("current", Now.AddDays(-30));
        var running = MakeFolder("running", Now.AddDays(-30));
        var old1 = MakeFolder("old1", Now.AddDays(-30));
        var old2 = MakeFolder("old2", Now.AddHours(-1));
        var young = MakeFolder("young", Now.AddMinutes(-2)); // can be a build that extracts right now

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current, running.ToUpperInvariant() };
        var result = BundleExtractionCleanupService.DeleteOldFolders(_root, keep, Now, TimeSpan.FromMinutes(10));

        Assert.Equal(2, result.Deleted);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(2 * 3 * 1024, result.FreedBytes);
        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(running));
        Assert.True(Directory.Exists(young));
        Assert.False(Directory.Exists(old1));
        Assert.False(Directory.Exists(old2));
        Assert.Equal(3, Directory.GetDirectories(_root).Length); // nothing renamed is left behind
    }

    [Fact]
    public void A_folder_with_an_open_file_is_left_whole()
    {
        var busy = MakeFolder("busy", Now.AddDays(-30));
        var old = MakeFolder("old", Now.AddDays(-30));

        using (new FileStream(Path.Combine(busy, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = BundleExtractionCleanupService.DeleteOldFolders(_root, new HashSet<string>(), Now, TimeSpan.FromMinutes(10));

            Assert.Equal(1, result.Deleted);
            Assert.Equal(1, result.Skipped);
        }

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(busy));
        Assert.Equal(3, Directory.GetFiles(busy).Length); // the rename failed, so no file in it was deleted
    }

    [Fact]
    public void A_new_copy_seen_after_the_rename_moves_the_folder_back_and_stops()
    {
        var first = MakeFolder("a-first", Now.AddDays(-30));
        var second = MakeFolder("b-second", Now.AddDays(-30));
        var checks = 0;

        var result = BundleExtractionCleanupService.DeleteOldFolders(
            _root, new HashSet<string>(), Now, TimeSpan.FromMinutes(10), () => ++checks == 1);

        Assert.Equal(1, result.Deleted); // the first folder went; then a new copy showed up
        Assert.Equal(1, result.Skipped);
        Assert.Equal(2, checks);         // no folder after that was touched
        Assert.False(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
        Assert.Equal(3, Directory.GetFiles(second).Length);
        Assert.Single(Directory.GetDirectories(_root)); // moved back under its own name
    }

    [Fact]
    public void A_missing_app_root_does_nothing()
    {
        var result = BundleExtractionCleanupService.DeleteOldFolders(Path.Combine(_root, "none"), new HashSet<string>(), Now, TimeSpan.Zero);

        Assert.Equal(BundleExtractionCleanupService.CleanupResult.None, result);
    }

    [Fact]
    public void A_dev_build_is_not_a_bundle_and_nothing_is_deleted()
    {
        // The test host is not an extracted single-file bundle, so no extraction folder is found and nothing runs.
        Assert.Equal(BundleExtractionCleanupService.CleanupResult.None, BundleExtractionCleanupService.RunForCurrentProcess());
    }

    private string MakeFolder(string name, DateTime timeUtc)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var file in new[] { "a.dll", "b.dll", "c.dll" })
        {
            File.WriteAllBytes(Path.Combine(folder, file), new byte[1024]);
        }

        Directory.SetCreationTimeUtc(folder, timeUtc);
        Directory.SetLastWriteTimeUtc(folder, timeUtc);
        return folder;
    }
}
