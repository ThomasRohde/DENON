using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Denon.App.Core.Media;

/// <summary>
/// Reads title, artist, album, track number, duration and cover art from FLAC, MP3 and M4A/MP4 files, with no
/// third-party library. It reads only the header region: the FLAC metadata blocks, the ID3v2 tag and the first MPEG
/// frame (plus the ID3v1 tag at the end), or the MP4 <c>moov</c> box. It seeks past the audio data.
/// It never throws: a bad, truncated or unknown file gives null fields, and the fields read before a bad part stay.
/// </summary>
public static partial class AudioTagReader
{
    /// <summary>Cover art larger than this is ignored.</summary>
    public const int MaxCoverArtBytes = 16 * 1024 * 1024;

    /// <summary>The reader reads at most this much of one tag (an ID3v2 tag or a Vorbis comment block).</summary>
    public const int MaxTagBytes = 32 * 1024 * 1024;

    /// <summary>An MP4 text item larger than this is ignored.</summary>
    private const int MaxTextBytes = 64 * 1024;

    /// <summary>A duration longer than this is a bad header, not a real track.</summary>
    private const double MaxDurationSeconds = 7 * 24 * 3600;

    /// <summary>Reads the tags of the file at <paramref name="path"/>. A missing or locked file gives <see cref="AudioTags.Empty"/>.</summary>
    /// <param name="includeCoverArt">False skips the cover art, which is faster for a long track list.</param>
    public static AudioTags Read(string path, bool includeCoverArt = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return AudioTags.Empty;
        }

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return Read(stream, includeCoverArt, Path.GetExtension(path));
        }
        catch (Exception)
        {
            // A missing, locked or unreadable file has no tags.
            return AudioTags.Empty;
        }
    }

    /// <summary>
    /// Reads the tags from a seekable stream. The format comes from the first bytes; <paramref name="extension"/>
    /// (".mp3") helps only for an MP3 file that has no ID3v2 tag and does not start with an MPEG frame.
    /// </summary>
    public static AudioTags Read(Stream stream, bool includeCoverArt = true, string? extension = null)
    {
        var tags = new TagBuilder(includeCoverArt);
        try
        {
            Parse(stream, extension, tags);
        }
        catch (Exception)
        {
            // A parser that meets data it does not expect stops here. The fields read before it stay.
        }

        return tags.Build();
    }

    /// <summary>The same as <see cref="Read(Stream, bool, string?)"/>, but parser errors are not caught (for tests).</summary>
    internal static AudioTags ReadUnguarded(Stream stream, bool includeCoverArt = true, string? extension = null)
    {
        var tags = new TagBuilder(includeCoverArt);
        Parse(stream, extension, tags);
        return tags.Build();
    }

    private static void Parse(Stream stream, string? extension, TagBuilder tags)
    {
        if (stream is null || !stream.CanRead || !stream.CanSeek)
        {
            return;
        }

        stream.Position = 0;
        var head = ReadUpTo(stream, 12);
        long audioStart = 0;
        var hasId3v2 = false;
        if (head.AsSpan().StartsWith("ID3"u8))
        {
            var tagEnd = ReadId3v2(stream, 0, tags);
            if (tagEnd > 0)
            {
                hasId3v2 = true;
                audioStart = tagEnd;
                stream.Position = tagEnd;
                head = ReadUpTo(stream, 12);
            }
        }

        if (head.AsSpan().StartsWith("fLaC"u8))
        {
            ReadFlac(stream, audioStart + 4, tags);
            return;
        }

        if (!hasId3v2 && head.Length >= 8 && head.AsSpan(4, 4).SequenceEqual("ftyp"u8))
        {
            ReadMp4(stream, tags);
            return;
        }

        if (hasId3v2
            || string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase)
            || TryParseMpegHeader(head, 0, out _))
        {
            var audioEnd = ReadId3v1(stream, tags) ? stream.Length - 128 : stream.Length;
            ReadMpegDuration(stream, audioStart, audioEnd, tags);
        }
    }

    /// <summary>Reads up to <paramref name="count"/> bytes from the current position. It never reads past the end.</summary>
    private static byte[] ReadUpTo(Stream stream, long count)
    {
        var remaining = stream.Length - stream.Position;
        var length = (int)Math.Clamp(Math.Min(count, remaining), 0, int.MaxValue);
        if (length == 0)
        {
            return [];
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = stream.Read(buffer, read, length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        return read == length ? buffer : buffer[..read];
    }

    private static uint Be32(ReadOnlySpan<byte> bytes, int offset = 0) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));

    private static TimeSpan? Seconds(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 && seconds <= MaxDurationSeconds ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>Trims a tag value and removes control characters. An empty value is null.</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsControl(c) && c != '﻿')
            {
                builder.Append(c);
            }
        }

        var text = builder.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>"3", "03" and "3/12" give 3. Anything else gives null.</summary>
    private static int? ParseTrackNumber(string? value)
    {
        var text = Clean(value);
        if (text is null)
        {
            return null;
        }

        var slash = text.IndexOf('/');
        if (slash >= 0)
        {
            text = text[..slash].Trim();
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;
    }

    /// <summary>The image type from the first bytes, else from the given MIME type. Not an image gives null.</summary>
    private static string? ImageMimeType(string? declared, ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]))
        {
            return "image/png";
        }

        if (data.StartsWith("GIF8"u8))
        {
            return "image/gif";
        }

        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (data.StartsWith("BM"u8))
        {
            return "image/bmp";
        }

        var mime = Clean(declared)?.ToLowerInvariant();
        return mime switch
        {
            "image/jpg" => "image/jpeg",
            not null when mime.StartsWith("image/", StringComparison.Ordinal) && mime.Length > 6 => mime,
            _ => null,
        };
    }

    /// <summary>Collects the fields. The first value of a field wins; a front cover wins over other pictures.</summary>
    private sealed class TagBuilder(bool includeCoverArt)
    {
        private string? _title;
        private string? _artist;
        private string? _albumArtist;
        private string? _album;
        private int? _trackNumber;
        private TimeSpan? _duration;
        private AudioCoverArt? _coverArt;
        private bool _coverIsFront;

        public bool IncludeCoverArt => includeCoverArt;

        public void OfferTitle(string? value) => _title ??= Clean(value);

        public void OfferArtist(string? value) => _artist ??= Clean(value);

        public void OfferAlbumArtist(string? value) => _albumArtist ??= Clean(value);

        public void OfferAlbum(string? value) => _album ??= Clean(value);

        public void OfferTrackNumber(string? value) => _trackNumber ??= ParseTrackNumber(value);

        public void OfferTrackNumber(int value)
        {
            if (value > 0)
            {
                _trackNumber ??= value;
            }
        }

        public void OfferDuration(TimeSpan? value)
        {
            if (value is { } duration && duration > TimeSpan.Zero)
            {
                _duration ??= duration;
            }
        }

        public void OfferCoverArt(ReadOnlySpan<byte> data, string? declaredMimeType, bool isFrontCover)
        {
            if (!includeCoverArt || data.IsEmpty || data.Length > MaxCoverArtBytes)
            {
                return;
            }

            if (_coverArt is not null && (_coverIsFront || !isFrontCover))
            {
                return;
            }

            var mimeType = ImageMimeType(declaredMimeType, data);
            if (mimeType is null)
            {
                return;
            }

            _coverArt = new AudioCoverArt(data.ToArray(), mimeType);
            _coverIsFront = isFrontCover;
        }

        public AudioTags Build() =>
            new(_title, _artist ?? _albumArtist, _album, _trackNumber, _duration, _coverArt);
    }
}
