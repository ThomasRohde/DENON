using System.Buffers.Binary;
using System.Text;

namespace Denon.App.Core.Media;

/// <summary>FLAC: STREAMINFO (duration), VORBIS_COMMENT (text tags) and PICTURE (cover art) metadata blocks.</summary>
public static partial class AudioTagReader
{
    private const int FlacStreamInfo = 0;
    private const int FlacVorbisComment = 4;
    private const int FlacPicture = 6;
    private const int FlacInvalidBlock = 127;
    private const int FrontCoverPictureType = 3;

    /// <summary>Reads the metadata blocks that start at <paramref name="position"/> (just after "fLaC").</summary>
    private static void ReadFlac(Stream stream, long position, TagBuilder tags)
    {
        for (var block = 0; block < 1024; block++)
        {
            stream.Position = position;
            var header = ReadUpTo(stream, 4);
            if (header.Length < 4)
            {
                return;
            }

            var isLast = (header[0] & 0x80) != 0;
            var type = header[0] & 0x7F;
            var length = (header[1] << 16) | (header[2] << 8) | header[3];
            switch (type)
            {
                case FlacStreamInfo:
                    ParseFlacStreamInfo(ReadUpTo(stream, Math.Min(length, 34)), tags);
                    break;
                case FlacVorbisComment:
                    ParseVorbisComments(ReadUpTo(stream, length), tags);
                    break;
                case FlacPicture when tags.IncludeCoverArt:
                    var picture = ReadUpTo(stream, length);
                    if (picture.Length == length)
                    {
                        ParseFlacPicture(picture, tags);
                    }

                    break;
                case FlacInvalidBlock:
                    return;
            }

            if (isLast)
            {
                return;
            }

            position += 4 + length;
        }
    }

    private static void ParseFlacStreamInfo(ReadOnlySpan<byte> info, TagBuilder tags)
    {
        if (info.Length < 18)
        {
            return;
        }

        var sampleRate = (info[10] << 12) | (info[11] << 4) | (info[12] >> 4);
        var totalSamples = ((long)(info[13] & 0x0F) << 32) | Be32(info, 14);
        if (sampleRate > 0 && totalSamples > 0)
        {
            tags.OfferDuration(Seconds(totalSamples / (double)sampleRate));
        }
    }

    /// <summary>A Vorbis comment block: vendor string, then "KEY=value" strings (little-endian lengths, UTF-8).</summary>
    private static void ParseVorbisComments(ReadOnlySpan<byte> block, TagBuilder tags)
    {
        var position = 0;
        if (!TryReadLe32(block, ref position, out var vendorLength) || vendorLength > (uint)(block.Length - position))
        {
            return;
        }

        position += (int)vendorLength;
        if (!TryReadLe32(block, ref position, out var count))
        {
            return;
        }

        for (uint i = 0; i < count; i++)
        {
            if (!TryReadLe32(block, ref position, out var length) || length > (uint)(block.Length - position))
            {
                return;
            }

            var comment = block.Slice(position, (int)length);
            position += (int)length;
            var equals = comment.IndexOf((byte)'=');
            if (equals <= 0)
            {
                continue;
            }

            var value = Encoding.UTF8.GetString(comment[(equals + 1)..]);
            switch (Encoding.ASCII.GetString(comment[..equals]).ToUpperInvariant())
            {
                case "TITLE":
                    tags.OfferTitle(value);
                    break;
                case "ARTIST":
                    tags.OfferArtist(value);
                    break;
                case "ALBUMARTIST" or "ALBUM ARTIST":
                    tags.OfferAlbumArtist(value);
                    break;
                case "ALBUM":
                    tags.OfferAlbum(value);
                    break;
                case "TRACKNUMBER":
                    tags.OfferTrackNumber(value);
                    break;
            }
        }
    }

    /// <summary>A FLAC PICTURE block: type, MIME, description, size fields, image data (big-endian lengths).</summary>
    private static void ParseFlacPicture(ReadOnlySpan<byte> block, TagBuilder tags)
    {
        var position = 0;
        if (!TryReadBe32(block, ref position, out var pictureType)
            || !TryReadBe32Block(block, ref position, out var mime)
            || !TryReadBe32Block(block, ref position, out _)
            || block.Length - position < 16)
        {
            return;
        }

        position += 16; // width, height, colour depth, palette size
        if (!TryReadBe32Block(block, ref position, out var data))
        {
            return;
        }

        var mimeType = Encoding.ASCII.GetString(mime);
        if (mimeType == "-->")
        {
            return; // the "image" is a URL
        }

        tags.OfferCoverArt(data, mimeType, pictureType == FrontCoverPictureType);
    }

    private static bool TryReadLe32(ReadOnlySpan<byte> bytes, ref int position, out uint value)
    {
        value = 0;
        if (bytes.Length - position < 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position, 4));
        position += 4;
        return true;
    }

    private static bool TryReadBe32(ReadOnlySpan<byte> bytes, ref int position, out uint value)
    {
        value = 0;
        if (bytes.Length - position < 4)
        {
            return false;
        }

        value = Be32(bytes, position);
        position += 4;
        return true;
    }

    /// <summary>A big-endian 32-bit length, then that many bytes.</summary>
    private static bool TryReadBe32Block(ReadOnlySpan<byte> bytes, ref int position, out ReadOnlySpan<byte> block)
    {
        block = default;
        if (!TryReadBe32(bytes, ref position, out var length) || length > (uint)(bytes.Length - position))
        {
            return false;
        }

        block = bytes.Slice(position, (int)length);
        position += (int)length;
        return true;
    }
}
