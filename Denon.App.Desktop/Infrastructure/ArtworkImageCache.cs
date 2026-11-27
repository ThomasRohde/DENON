using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Denon.App.Core.Infrastructure;

namespace Denon.App.Desktop.Infrastructure;

/// <summary>
/// Pictures from the web (station logos, album art) for the now-playing views. Each address is downloaded once: the
/// picture stays in memory for the session and in <c>art-cache</c> on disk, so the next start shows it at once.
/// Use on the UI thread only.
/// </summary>
public static class ArtworkImageCache
{
    private const int MaxInMemory = 64;
    private const int MaxFilesOnDisk = 300;
    private const int KeepFilesOnPrune = 200;

    private static readonly Dictionary<string, ImageSource> Memory = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Downloading = new(StringComparer.Ordinal);
    private static readonly HttpClient Http = CreateHttpClient();
    private static bool _pruned;

    private static string Folder => Path.Combine(AppDataPaths.RootDirectory, "art-cache");

    /// <summary>The picture for a web address, from memory, the disk or a new download. Null for a bad address.</summary>
    public static ImageSource? Get(Uri uri)
    {
        var key = uri.AbsoluteUri;
        if (Memory.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var image = TryLoadFile(key) ?? LoadFromWeb(uri);
        if (image is null)
        {
            return null;
        }

        if (Memory.Count >= MaxInMemory)
        {
            Memory.Clear();
        }

        Memory[key] = image;
        return image;
    }

    private static ImageSource? TryLoadFile(string key)
    {
        var file = FileFor(key);
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(File.ReadAllBytes(file));
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // A broken file: forget it and download again.
            try { File.Delete(file); } catch { }
            return null;
        }
    }

    private static ImageSource? LoadFromWeb(Uri uri)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                _ = SaveToDiskAsync(uri);
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Downloads the bytes once more in the background and keeps them for the next start.</summary>
    private static async Task SaveToDiskAsync(Uri uri)
    {
        var key = uri.AbsoluteUri;
        if (!Downloading.Add(key))
        {
            return;
        }

        try
        {
            var bytes = await Http.GetByteArrayAsync(uri).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024)
            {
                return;
            }

            Directory.CreateDirectory(Folder);
            PruneOnce();
            await File.WriteAllBytesAsync(FileFor(key), bytes).ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the picture shows from the web anyway.
        }
    }

    private static void PruneOnce()
    {
        if (_pruned)
        {
            return;
        }

        _pruned = true;
        try
        {
            var files = new DirectoryInfo(Folder).GetFiles("*.img");
            if (files.Length <= MaxFilesOnDisk)
            {
                return;
            }

            foreach (var old in files.OrderByDescending(static f => f.LastWriteTimeUtc).Skip(KeepFilesOnPrune))
            {
                old.Delete();
            }
        }
        catch
        {
        }
    }

    private static string FileFor(string key) =>
        Path.Combine(Folder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))) + ".img");

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DenonDesktop/2.0");
        return client;
    }
}
