namespace Denon.App.Core.Models;

public enum PcCastFormat
{
    Mp3_320k,
    Wav_Lossless,
}

public sealed record AudioMeterSample(double Left, double Right, bool IsLiveSignal);

public sealed record RadioBrowserStation(
    string StationUuid,
    string Name,
    string UrlResolved,
    string Favicon,
    string Country,
    string Tags,
    string Codec,
    int Bitrate,
    int Votes)
{
    public string CodecBadge
    {
        get
        {
            var c = string.IsNullOrWhiteSpace(Codec) ? "HTTP" : Codec.Trim().ToUpperInvariant();
            return Bitrate > 0 ? $"{c} {Bitrate}k" : c;
        }
    }

    public string SubtitleLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Country))
            {
                parts.Add(Country.Trim());
            }

            if (!string.IsNullOrWhiteSpace(Tags))
            {
                var firstTags = string.Join(", ", Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
                if (!string.IsNullOrWhiteSpace(firstTags))
                {
                    parts.Add(firstTags);
                }
            }

            return parts.Count > 0 ? string.Join("  •  ", parts) : UrlResolved;
        }
    }
}

public sealed record LocalAudioTrackItem(
    string TrackId,
    string FilePath,
    string FileName,
    string Title,
    string Artist,
    string Extension,
    long FileSizeBytes,
    string? Album = null,
    TimeSpan? Duration = null,
    int? TrackNumber = null,
    string? CoverArtPath = null,
    bool TagsRead = false,
    DateTime? FileModifiedUtc = null)
{
    public string CodecBadge => Extension.TrimStart('.').ToUpperInvariant();

    /// <summary>"Artist • Album", or the artist alone without an album tag.</summary>
    public string ArtistAlbumDisplay => string.IsNullOrWhiteSpace(Album) ? Artist : $"{Artist} • {Album}";

    /// <summary>"4:05" (or "1:02:03"), or empty when the length is not known.</summary>
    public string DurationDisplay => Duration is { } d && d > TimeSpan.Zero
        ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture) : d.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture))
        : string.Empty;

    public string SizeDisplay => FileSizeBytes >= 1024 * 1024
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{FileSizeBytes / (1024.0 * 1024.0):0.1} MB")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Max(1, FileSizeBytes / 1024.0):0} KB");

    public bool IsNativeHeosFormat =>
        Extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
        Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
        Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
        Extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
        Extension.Equals(".aac", StringComparison.OrdinalIgnoreCase);

    public LocalAudioTrackItem WithNewTrackId() =>
        this with { TrackId = Guid.NewGuid().ToString("N")[..10] };
}
