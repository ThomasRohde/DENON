namespace Denon.App.Core.Media;

/// <summary>
/// Cover art found in an audio file: the image bytes (read-only) and the image MIME type (for example "image/jpeg").
/// Two values are equal when the MIME type and the bytes are the same.
/// </summary>
public sealed record AudioCoverArt(ReadOnlyMemory<byte> Data, string MimeType)
{
    public bool Equals(AudioCoverArt? other) =>
        other is not null
        && string.Equals(MimeType, other.MimeType, StringComparison.Ordinal)
        && Data.Span.SequenceEqual(other.Data.Span);

    public override int GetHashCode() => HashCode.Combine(MimeType, Data.Length);
}

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
