namespace Denon.App.Core.Media;

/// <summary>Cover art found in an audio file: the image bytes and the image MIME type (for example "image/jpeg").</summary>
public sealed record AudioCoverArt(byte[] Data, string MimeType);

/// <summary>
/// The tags that <see cref="AudioTagReader"/> found in an audio file. A field is null when the file does not have
/// it or the reader cannot read it.
/// </summary>
public sealed record AudioTags(
    string? Title,
    string? Artist,
    string? Album,
    int? TrackNumber,
    TimeSpan? Duration,
    AudioCoverArt? CoverArt)
{
    /// <summary>No tags: the file is missing, unknown or unreadable.</summary>
    public static AudioTags Empty { get; } = new(null, null, null, null, null, null);
}
