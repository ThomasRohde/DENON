using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Denon.App.Core.Plugins;
using Denon.App.PluginSdk;

namespace Denon.App.Desktop.Services;

/// <summary>
/// Extracts vibrant dominant and secondary colors from the currently playing track's album artwork
/// to tint the OLED Now Playing deck and drive Philips Hue Album-Art Ambient Sync.
/// </summary>
public sealed class AlbumArtPaletteService : IDisposable
{
    private readonly PluginHostService _pluginHost;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, PluginAlbumArtPalette> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastRequestedUrl;

    public AlbumArtPaletteService(PluginHostService pluginHost)
    {
        _pluginHost = pluginHost;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DenonDesktop/1.2");
    }

    public event Action<PluginAlbumArtPalette>? PaletteExtracted;

    public async Task ProcessArtworkAsync(string? imageUrl, string trackTitle, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) ||
            (!imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
             !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (string.Equals(_lastRequestedUrl, imageUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastRequestedUrl = imageUrl;

        try
        {
            if (!_cache.TryGetValue(imageUrl, out var palette))
            {
                var bytes = await _http.GetByteArrayAsync(imageUrl, cancellationToken);
                var (dominant, secondary) = await Task.Run(() => ExtractVibrantColors(bytes), cancellationToken);
                palette = new PluginAlbumArtPalette(dominant, secondary, trackTitle, imageUrl);
                _cache[imageUrl] = palette;
            }
            else
            {
                palette = palette with { TrackTitle = trackTitle };
            }

            PaletteExtracted?.Invoke(palette);
            await _pluginHost.PublishAlbumArtPaletteAsync(palette, cancellationToken);
        }
        catch
        {
            // Artwork extraction is best-effort; ignore broken or unreachable thumbnail URLs
        }
    }

    public void Dispose() => _http.Dispose();

    private static (string DominantHex, string SecondaryHex) ExtractVibrantColors(byte[] imageBytes)
    {
        using var ms = new MemoryStream(imageBytes);
        var frame = new BitmapImage();
        frame.BeginInit();
        frame.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        frame.CacheOption = BitmapCacheOption.OnLoad;
        frame.DecodePixelWidth = 96; // Enough for the sample grid; a full 1500 px cover took tens of ms.
        frame.StreamSource = ms;
        frame.EndInit();
        frame.Freeze();

        // Convert to standard Bgra32 and sample a 24x24 grid
        var formatted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var width = formatted.PixelWidth;
        var height = formatted.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return ("#00D4FF", "#F59E0B");
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];
        formatted.CopyPixels(pixels, stride, 0);

        const int bucketCount = 12;
        var bucketWeights = new double[bucketCount];
        var bucketR = new double[bucketCount];
        var bucketG = new double[bucketCount];
        var bucketB = new double[bucketCount];

        var stepX = Math.Max(1, width / 24);
        var stepY = Math.Max(1, height / 24);

        for (var y = 0; y < height; y += stepY)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < width; x += stepX)
            {
                var idx = rowOffset + (x * 4);
                var b = pixels[idx];
                var g = pixels[idx + 1];
                var r = pixels[idx + 2];
                var a = pixels[idx + 3];
                if (a < 128)
                {
                    continue;
                }

                RgbToHsv(r, g, b, out var hue, out var sat, out var val);
                if (val < 0.14 || val > 0.96 || sat < 0.18)
                {
                    continue;
                }

                // Favor rich, saturated, mid-to-bright tones that look great on OLED and Hue bulbs
                var valueWeight = 1.0 - Math.Abs(val - 0.68);
                var weight = Math.Pow(sat, 1.55) * Math.Max(0.25, valueWeight);
                var bucket = ((int)Math.Floor(hue / 30.0)) % bucketCount;
                if (bucket < 0) bucket += bucketCount;

                bucketWeights[bucket] += weight;
                bucketR[bucket] += r * weight;
                bucketG[bucket] += g * weight;
                bucketB[bucket] += b * weight;
            }
        }

        var ranked = Enumerable.Range(0, bucketCount)
            .Where(i => bucketWeights[i] > 0.01)
            .OrderByDescending(i => bucketWeights[i])
            .ToList();

        if (ranked.Count == 0)
        {
            return ("#00D4FF", "#F59E0B");
        }

        var primaryIdx = ranked[0];
        var secondaryIdx = ranked.FirstOrDefault(i => Math.Abs(i - primaryIdx) >= 2 && Math.Abs(i - primaryIdx) <= 10);
        if (secondaryIdx == primaryIdx && ranked.Count > 1)
        {
            secondaryIdx = ranked[1];
        }

        var domHex = BucketToHex(primaryIdx, bucketWeights, bucketR, bucketG, bucketB);
        var secHex = secondaryIdx != primaryIdx
            ? BucketToHex(secondaryIdx, bucketWeights, bucketR, bucketG, bucketB)
            : "#00D4FF";

        return (domHex, secHex);
    }

    private static string BucketToHex(int idx, double[] weights, double[] sumR, double[] sumG, double[] sumB)
    {
        var w = Math.Max(0.0001, weights[idx]);
        var r = (byte)Math.Clamp((int)Math.Round(sumR[idx] / w), 0, 255);
        var g = (byte)Math.Clamp((int)Math.Round(sumG[idx] / w), 0, 255);
        var b = (byte)Math.Clamp((int)Math.Round(sumB[idx] / w), 0, 255);

        // Boost slightly if too dim so Hue & OLED stay luminous
        RgbToHsv(r, g, b, out var h, out var s, out var v);
        if (v < 0.55)
        {
            HsvToRgb(h, Math.Min(1.0, s * 1.1), 0.72, out r, out g, out b);
        }

        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static void RgbToHsv(byte r8, byte g8, byte b8, out double hue, out double sat, out double val)
    {
        var r = r8 / 255.0;
        var g = g8 / 255.0;
        var b = b8 / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        val = max;
        sat = max <= 0.0001 ? 0 : delta / max;

        if (delta <= 0.0001)
        {
            hue = 0;
            return;
        }

        if (max == r)
        {
            hue = 60.0 * (((g - b) / delta) % 6.0);
        }
        else if (max == g)
        {
            hue = 60.0 * (((b - r) / delta) + 2.0);
        }
        else
        {
            hue = 60.0 * (((r - g) / delta) + 4.0);
        }

        if (hue < 0)
        {
            hue += 360.0;
        }
    }

    private static void HsvToRgb(double hue, double sat, double val, out byte r, out byte g, out byte b)
    {
        var c = val * sat;
        var x = c * (1.0 - Math.Abs(((hue / 60.0) % 2.0) - 1.0));
        var m = val - c;

        var (rp, gp, bp) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        r = (byte)Math.Clamp((int)Math.Round((rp + m) * 255.0), 0, 255);
        g = (byte)Math.Clamp((int)Math.Round((gp + m) * 255.0), 0, 255);
        b = (byte)Math.Clamp((int)Math.Round((bp + m) * 255.0), 0, 255);
    }
}
