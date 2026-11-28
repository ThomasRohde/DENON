using System.Text;

namespace Denon.App.Core.Media;

/// <summary>MP3: ID3v2.2/2.3/2.4 tags, the ID3v1 tag, and the duration from the Xing/Info or VBRI header or the bitrate.</summary>
public static partial class AudioTagReader
{
    /// <summary>The first MPEG frame must start within this many bytes after the ID3v2 tag.</summary>
    private const int MpegFrameSearchBytes = 64 * 1024;

    private static readonly int[] BitratesV1L1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static readonly int[] BitratesV1L2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] BitratesV1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] BitratesV2L1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static readonly int[] BitratesV2L23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] SampleRatesV1 = [44100, 48000, 32000];

    /// <summary>One MPEG audio frame header. <see cref="Version"/> is 1 for MPEG-1, else MPEG-2 or 2.5.</summary>
    private readonly record struct MpegFrame(
        int Version, int Bitrate, int SampleRate, bool IsMono, int FrameLength, int SamplesPerFrame);

    /// <summary>
    /// Reads the ID3v2 tag at <paramref name="offset"/>. Returns the position after the tag, or 0 when the header
    /// is not valid.
    /// </summary>
    private static long ReadId3v2(Stream stream, long offset, TagBuilder tags)
    {
        stream.Position = offset;
        var header = ReadUpTo(stream, 10);
        if (header.Length < 10 || !header.AsSpan().StartsWith("ID3"u8)
            || !TryReadSyncsafe(header.AsSpan(6, 4), out var size))
        {
            return 0;
        }

        int major = header[3];
        var flags = header[5];
        var end = offset + 10 + size + (major == 4 && (flags & 0x10) != 0 ? 10 : 0);
        if (major is < 2 or > 4 || (major == 2 && (flags & 0x40) != 0))
        {
            return end; // unknown version or a compressed ID3v2.2 tag: skip it
        }

        var body = ReadUpTo(stream, Math.Min(size, MaxTagBytes));
        var tagUnsynchronised = (flags & 0x80) != 0;
        if (tagUnsynchronised && major < 4)
        {
            body = RemoveUnsynchronisation(body);
        }

        var position = 0L;
        if (major >= 3 && (flags & 0x40) != 0)
        {
            if (body.Length < 4)
            {
                return end;
            }

            if (major == 3)
            {
                position = 4L + Be32(body); // the size does not count its own 4 bytes
            }
            else if (TryReadSyncsafe(body.AsSpan(0, 4), out var extendedSize))
            {
                position = extendedSize;
            }
            else
            {
                return end;
            }
        }

        var frameHeaderLength = major == 2 ? 6 : 10;
        while (position + frameHeaderLength <= body.Length)
        {
            var frame = body.AsSpan((int)position);
            if (frame[0] == 0)
            {
                break; // padding
            }

            var idLength = major == 2 ? 3 : 4;
            if (!IsFrameId(frame[..idLength]))
            {
                break;
            }

            var id = Encoding.ASCII.GetString(frame[..idLength]);
            long frameSize;
            byte formatFlags = 0;
            if (major == 2)
            {
                frameSize = (frame[3] << 16) | (frame[4] << 8) | frame[5];
            }
            else if (major == 3)
            {
                frameSize = Be32(frame, 4);
                formatFlags = frame[9];
            }
            else if (TryReadSyncsafe(frame.Slice(4, 4), out var syncsafeSize))
            {
                frameSize = syncsafeSize;
                formatFlags = frame[9];
            }
            else
            {
                break;
            }

            if (frameSize > frame.Length - frameHeaderLength)
            {
                break; // the frame is cut off
            }

            var data = frame.Slice(frameHeaderLength, (int)frameSize);
            position += frameHeaderLength + frameSize;
            if (TryUnwrapId3Frame(major, formatFlags, tagUnsynchronised, data, out var payload))
            {
                ReadId3Frame(id, payload, major == 2, tags);
            }
        }

        return end;
    }

    private static bool IsFrameId(ReadOnlySpan<byte> id)
    {
        foreach (var b in id)
        {
            if (b is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Removes the frame's extra bytes (group id, data length) and its unsynchronisation. Compressed or encrypted frames give false.</summary>
    private static bool TryUnwrapId3Frame(
        int major, byte formatFlags, bool tagUnsynchronised, ReadOnlySpan<byte> data, out ReadOnlySpan<byte> payload)
    {
        payload = data;
        if (major == 3)
        {
            if ((formatFlags & 0xC0) != 0)
            {
                return false;
            }

            if ((formatFlags & 0x20) != 0)
            {
                if (payload.IsEmpty)
                {
                    return false;
                }

                payload = payload[1..];
            }
        }
        else if (major == 4)
        {
            if ((formatFlags & 0x0C) != 0)
            {
                return false;
            }

            var skip = ((formatFlags & 0x40) != 0 ? 1 : 0) + ((formatFlags & 0x01) != 0 ? 4 : 0);
            if (payload.Length < skip)
            {
                return false;
            }

            payload = payload[skip..];
            if ((formatFlags & 0x02) != 0 || tagUnsynchronised)
            {
                payload = RemoveUnsynchronisation(payload);
            }
        }

        return true;
    }

    private static void ReadId3Frame(string id, ReadOnlySpan<byte> payload, bool isV22, TagBuilder tags)
    {
        switch (id)
        {
            case "TIT2" or "TT2":
                tags.OfferTitle(DecodeId3Text(payload));
                break;
            case "TPE1" or "TP1":
                tags.OfferArtist(DecodeId3Text(payload));
                break;
            case "TPE2" or "TP2":
                tags.OfferAlbumArtist(DecodeId3Text(payload));
                break;
            case "TALB" or "TAL":
                tags.OfferAlbum(DecodeId3Text(payload));
                break;
            case "TRCK" or "TRK":
                tags.OfferTrackNumber(DecodeId3Text(payload));
                break;
            case "APIC" or "PIC" when tags.IncludeCoverArt:
                ReadId3Picture(payload, isV22, tags);
                break;
        }
    }

    /// <summary>APIC (v2.3/2.4): encoding, MIME, picture type, description, data. PIC (v2.2) has a 3-letter format instead of the MIME.</summary>
    private static void ReadId3Picture(ReadOnlySpan<byte> payload, bool isV22, TagBuilder tags)
    {
        if (payload.Length < 2)
        {
            return;
        }

        var encoding = payload[0];
        string? mimeType;
        int position;
        if (isV22)
        {
            if (payload.Length < 5)
            {
                return;
            }

            mimeType = Encoding.ASCII.GetString(payload.Slice(1, 3)).ToUpperInvariant() switch
            {
                "JPG" => "image/jpeg",
                "PNG" => "image/png",
                _ => null,
            };
            position = 4;
        }
        else
        {
            var mimeEnd = payload[1..].IndexOf((byte)0);
            if (mimeEnd < 0)
            {
                return;
            }

            mimeType = Encoding.Latin1.GetString(payload.Slice(1, mimeEnd));
            position = 1 + mimeEnd + 1;
        }

        if (position >= payload.Length)
        {
            return;
        }

        var pictureType = payload[position++];
        var dataStart = FindEndOfString(payload, position, isWide: encoding is 1 or 2);
        if (dataStart < 0 || dataStart >= payload.Length)
        {
            return;
        }

        tags.OfferCoverArt(payload[dataStart..], mimeType, pictureType == FrontCoverPictureType);
    }

    /// <summary>The position after the string's terminator (one zero byte, or two aligned for UTF-16), or -1.</summary>
    private static int FindEndOfString(ReadOnlySpan<byte> bytes, int start, bool isWide)
    {
        if (!isWide)
        {
            var zero = bytes[start..].IndexOf((byte)0);
            return zero < 0 ? -1 : start + zero + 1;
        }

        for (var i = start; i + 1 < bytes.Length; i += 2)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0)
            {
                return i + 2;
            }
        }

        return -1;
    }

    /// <summary>A text frame: one encoding byte, then the text. Only the first value of a v2.4 list is kept.</summary>
    private static string? DecodeId3Text(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return null;
        }

        var text = DecodeId3String(payload[0], payload[1..]);
        var zero = text.IndexOf('\0');
        return zero >= 0 ? text[..zero] : text;
    }

    private static string DecodeId3String(byte encoding, ReadOnlySpan<byte> bytes)
    {
        switch (encoding)
        {
            case 1 when bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF:
                return Encoding.BigEndianUnicode.GetString(bytes[2..]);
            case 1 when bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE:
                return Encoding.Unicode.GetString(bytes[2..]);
            case 1:
                return Encoding.Unicode.GetString(bytes);
            case 2:
                return Encoding.BigEndianUnicode.GetString(bytes);
            case 3:
                return Encoding.UTF8.GetString(bytes);
            default:
                return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>ID3 unsynchronisation puts a zero byte after each 0xFF that could look like a frame sync. This removes them.</summary>
    private static byte[] RemoveUnsynchronisation(ReadOnlySpan<byte> data)
    {
        var result = new byte[data.Length];
        var length = 0;
        for (var i = 0; i < data.Length; i++)
        {
            result[length++] = data[i];
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0)
            {
                i++;
            }
        }

        return length == result.Length ? result : result[..length];
    }

    private static bool TryReadSyncsafe(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        foreach (var b in bytes)
        {
            if ((b & 0x80) != 0)
            {
                return false;
            }

            value = (value << 7) | b;
        }

        return true;
    }

    /// <summary>Reads the 128-byte ID3v1 tag at the end of the file. It fills only fields that the ID3v2 tag did not give.</summary>
    private static bool ReadId3v1(Stream stream, TagBuilder tags)
    {
        if (stream.Length < 128)
        {
            return false;
        }

        stream.Position = stream.Length - 128;
        var tag = ReadUpTo(stream, 128);
        if (tag.Length < 128 || !tag.AsSpan().StartsWith("TAG"u8))
        {
            return false;
        }

        tags.OfferTitle(ReadId3v1Text(tag.AsSpan(3, 30)));
        tags.OfferArtist(ReadId3v1Text(tag.AsSpan(33, 30)));
        tags.OfferAlbum(ReadId3v1Text(tag.AsSpan(63, 30)));
        if (tag[125] == 0 && tag[126] != 0)
        {
            tags.OfferTrackNumber(tag[126]); // ID3v1.1
        }

        return true;
    }

    private static string ReadId3v1Text(ReadOnlySpan<byte> field)
    {
        var zero = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(zero >= 0 ? field[..zero] : field);
    }

    /// <summary>
    /// Finds the first MPEG frame after <paramref name="audioStart"/>. The duration comes from its Xing/Info or VBRI
    /// header (frame count), else from its bitrate and the audio size (exact only for constant bitrate).
    /// </summary>
    private static void ReadMpegDuration(Stream stream, long audioStart, long audioEnd, TagBuilder tags)
    {
        stream.Position = audioStart;
        var buffer = ReadUpTo(stream, MpegFrameSearchBytes);
        for (var i = 0; i + 4 <= buffer.Length; i++)
        {
            if (!TryParseMpegHeader(buffer, i, out var frame))
            {
                continue;
            }

            // A real frame is followed by another frame header (when it is in the buffer).
            var next = i + frame.FrameLength;
            if (next + 4 <= buffer.Length && !TryParseMpegHeader(buffer, next, out _))
            {
                continue;
            }

            var duration = ReadVbrHeaderDuration(buffer, i, frame);
            if (duration is null)
            {
                var audioBytes = audioEnd - (audioStart + i);
                duration = audioBytes > 0 ? Seconds(audioBytes * 8.0 / frame.Bitrate) : null;
            }

            tags.OfferDuration(duration);
            return;
        }
    }

    private static TimeSpan? ReadVbrHeaderDuration(ReadOnlySpan<byte> buffer, int frameStart, MpegFrame frame)
    {
        var sideInfoLength = frame.Version == 1 ? (frame.IsMono ? 17 : 32) : (frame.IsMono ? 9 : 17);
        var xing = frameStart + 4 + sideInfoLength;
        if (xing + 12 <= buffer.Length
            && (buffer.Slice(xing, 4).SequenceEqual("Xing"u8) || buffer.Slice(xing, 4).SequenceEqual("Info"u8)))
        {
            var hasFrameCount = (Be32(buffer, xing + 4) & 0x1) != 0;
            var frames = hasFrameCount ? Be32(buffer, xing + 8) : 0;
            return frames > 0 ? Seconds(frames * (double)frame.SamplesPerFrame / frame.SampleRate) : null;
        }

        var vbri = frameStart + 4 + 32;
        if (vbri + 18 <= buffer.Length && buffer.Slice(vbri, 4).SequenceEqual("VBRI"u8))
        {
            var frames = Be32(buffer, vbri + 14);
            return frames > 0 ? Seconds(frames * (double)frame.SamplesPerFrame / frame.SampleRate) : null;
        }

        return null;
    }

    private static bool TryParseMpegHeader(ReadOnlySpan<byte> bytes, int offset, out MpegFrame frame)
    {
        frame = default;
        if (offset < 0 || offset + 4 > bytes.Length || bytes[offset] != 0xFF || (bytes[offset + 1] & 0xE0) != 0xE0)
        {
            return false;
        }

        var versionBits = (bytes[offset + 1] >> 3) & 0x3; // 0 = MPEG-2.5, 1 = reserved, 2 = MPEG-2, 3 = MPEG-1
        var layerBits = (bytes[offset + 1] >> 1) & 0x3; // 1 = Layer III, 2 = Layer II, 3 = Layer I
        var bitrateIndex = bytes[offset + 2] >> 4;
        var sampleRateIndex = (bytes[offset + 2] >> 2) & 0x3;
        var padding = (bytes[offset + 2] >> 1) & 0x1;
        var isMono = (bytes[offset + 3] >> 6) == 0x3;
        if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
        {
            return false;
        }

        var isV1 = versionBits == 3;
        var layer = 4 - layerBits;
        var table = (isV1, layer) switch
        {
            (true, 1) => BitratesV1L1,
            (true, 2) => BitratesV1L2,
            (true, _) => BitratesV1L3,
            (false, 1) => BitratesV2L1,
            _ => BitratesV2L23,
        };
        var bitrate = table[bitrateIndex] * 1000;
        var sampleRate = SampleRatesV1[sampleRateIndex] >> (isV1 ? 0 : versionBits == 2 ? 1 : 2);
        var samplesPerFrame = layer switch
        {
            1 => 384,
            2 => 1152,
            _ => isV1 ? 1152 : 576,
        };
        var frameLength = layer == 1
            ? ((12 * bitrate / sampleRate) + padding) * 4
            : (samplesPerFrame / 8 * bitrate / sampleRate) + padding;
        frame = new MpegFrame(isV1 ? 1 : 2, bitrate, sampleRate, isMono, frameLength, samplesPerFrame);
        return true;
    }
}
