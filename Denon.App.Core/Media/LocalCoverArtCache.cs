using System.Security.Cryptography;

namespace Denon.App.Core.Media;

/// <summary>
/// Keeps the cover art of local audio files as image files in one folder, so Now Playing and the Hue album-art
/// colours can load a picture by its path. The file name is a hash of the image bytes: all tracks of an album share
/// one file. Pictures larger than <see cref="MaxCoverBytes"/> are not kept. Call it off the UI thread.
/// </summary>
public sealed class LocalCoverArtCache
{
    /// <summary>A larger embedded picture is not kept (a cover for a screen needs much less).</summary>
    public const int MaxCoverBytes = 4 * 1024 * 1024;

    /// <summary>When the folder has more files than this at start, the oldest are deleted.</summary>
    internal const int MaxFiles = 1000;

    private const int KeepFilesOnPrune = 800;

    private readonly object _sync = new();
    private bool _pruned;

    public LocalCoverArtCache(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A directory is required.", nameof(directory));
        }

        Directory = Path.GetFullPath(directory);
    }

    public string Directory { get; }

    /// <summary>Writes the picture (once) and gives its full path. Null when it is too large or the write fails.</summary>
    public string? Store(AudioCoverArt? coverArt)
    {
        if (coverArt is null || coverArt.Data.IsEmpty || coverArt.Data.Length > MaxCoverBytes)
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(coverArt.Data.Span))[..32].ToLowerInvariant();
        var path = Path.Combine(Directory, hash + ExtensionFor(coverArt.MimeType));
        try
        {
            lock (_sync)
            {
                System.IO.Directory.CreateDirectory(Directory);
                PruneOnceLocked();
                if (File.Exists(path))
                {
                    // Mark it as used, so a prune keeps it.
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    return path;
                }

                var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                File.WriteAllBytes(temp, coverArt.Data.ToArray());
                File.Move(temp, path, overwrite: true);
                return path;
            }
        }
        catch (Exception)
        {
            // Best effort: without a cached picture, Now Playing shows no cover.
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> is a picture in this cache that still exists.</summary>
    public bool Contains(string? path) => IsInCacheFolder(path) && File.Exists(path);

    /// <summary>
    /// True when <paramref name="path"/> is a full local path to a file directly in this cache folder. It reads
    /// nothing from the disk. A saved queue can only point to pictures here (never to a network share).
    /// </summary>
    public bool IsInCacheFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return string.Equals(full, path, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetDirectoryName(full), Directory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string ExtensionFor(string? mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".jpg",
    };

    private void PruneOnceLocked()
    {
        if (_pruned)
        {
            return;
        }

        _pruned = true;
        try
        {
            var files = new DirectoryInfo(Directory).GetFiles();
            if (files.Length <= MaxFiles)
            {
                return;
            }

            foreach (var old in files.OrderByDescending(static f => f.LastWriteTimeUtc).Skip(KeepFilesOnPrune))
            {
                old.Delete();
            }
        }
        catch (Exception)
        {
        }
    }
}
