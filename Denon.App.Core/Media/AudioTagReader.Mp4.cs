using System.Buffers.Binary;
using System.Text;

namespace Denon.App.Core.Media;

/// <summary>M4A/MP4: the duration from <c>moov/mvhd</c> and the tags from <c>moov/udta/meta/ilst</c> (iTunes items).</summary>
public static partial class AudioTagReader
{
    private const int MaxBoxesPerLevel = 4096;

    /// <summary>One MP4 box. <see cref="Truncated"/> is true when the file ends before the box does.</summary>
    private readonly record struct Mp4Box(string Type, long DataStart, long End, bool Truncated)
    {
        public long DataLength => End - DataStart;
    }

    private static void ReadMp4(Stream stream, TagBuilder tags)
    {
        foreach (var box in ReadMp4Boxes(stream, 0, stream.Length))
        {
            if (box.Type == "moov")
            {
                ReadMp4Moov(stream, box, tags);
                return;
            }
        }
    }

    private static void ReadMp4Moov(Stream stream, Mp4Box moov, TagBuilder tags)
    {
        foreach (var child in ReadMp4Boxes(stream, moov.DataStart, moov.End))
        {
            switch (child.Type)
            {
                case "mvhd":
                    stream.Position = child.DataStart;
                    ParseMp4MovieHeader(ReadUpTo(stream, Math.Min(child.DataLength, 32)), tags);
                    break;
                case "udta":
                    foreach (var item in ReadMp4Boxes(stream, child.DataStart, child.End))
                    {
                        if (item.Type == "meta")
                        {
                            ReadMp4Meta(stream, item, tags);
                        }
                    }

                    break;
                case "meta":
                    ReadMp4Meta(stream, child, tags);
                    break;
            }
        }
    }

    /// <summary>mvhd: version 0 has 32-bit times, version 1 has 64-bit times. The duration is in timescale units.</summary>
    private static void ParseMp4MovieHeader(ReadOnlySpan<byte> header, TagBuilder tags)
    {
        if (header.Length < 20)
        {
            return;
        }

        uint timescale;
        ulong duration;
        if (header[0] == 1)
        {
            if (header.Length < 32)
            {
                return;
            }

            timescale = Be32(header, 20);
            duration = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(24, 8));
            if (duration == ulong.MaxValue)
            {
                return; // unknown
            }
        }
        else
        {
            timescale = Be32(header, 12);
            duration = Be32(header, 16);
            if (duration == uint.MaxValue)
            {
                return; // unknown
            }
        }

        if (timescale > 0 && duration > 0)
        {
            tags.OfferDuration(Seconds(duration / (double)timescale));
        }
    }

    /// <summary>meta is a full box (4 bytes of version and flags) in MP4, but not in some QuickTime files.</summary>
    private static void ReadMp4Meta(Stream stream, Mp4Box meta, TagBuilder tags)
    {
        stream.Position = meta.DataStart;
        var peek = ReadUpTo(stream, 8);
        var childrenStart = peek.Length >= 8 && peek.AsSpan(4, 4).SequenceEqual("hdlr"u8)
            ? meta.DataStart
            : meta.DataStart + 4;
        foreach (var box in ReadMp4Boxes(stream, childrenStart, meta.End))
        {
            if (box.Type == "ilst")
            {
                ReadMp4ItemList(stream, box, tags);
            }
        }
    }

    /// <summary>Each ilst item holds a "data" box: a 4-byte type (1 = UTF-8, 13 = JPEG, 14 = PNG), 4-byte locale, then the value.</summary>
    private static void ReadMp4ItemList(Stream stream, Mp4Box ilst, TagBuilder tags)
    {
        foreach (var item in ReadMp4Boxes(stream, ilst.DataStart, ilst.End))
        {
            var isCover = item.Type == "covr";
            if (item.Type is not ("©nam" or "©ART" or "aART" or "©alb" or "trkn" or "covr")
                || (isCover && !tags.IncludeCoverArt))
            {
                continue;
            }

            foreach (var data in ReadMp4Boxes(stream, item.DataStart, item.End))
            {
                if (data.Type != "data")
                {
                    continue;
                }

                if (data.Truncated || data.DataLength < 8 || data.DataLength - 8 > (isCover ? MaxCoverArtBytes : MaxTextBytes))
                {
                    break;
                }

                stream.Position = data.DataStart;
                var bytes = ReadUpTo(stream, data.DataLength);
                if (bytes.Length != data.DataLength)
                {
                    break;
                }

                var dataType = Be32(bytes) & 0x00FFFFFF;
                var value = bytes.AsSpan(8);
                switch (item.Type)
                {
                    case "©nam":
                        tags.OfferTitle(DecodeMp4Text(dataType, value));
                        break;
                    case "©ART":
                        tags.OfferArtist(DecodeMp4Text(dataType, value));
                        break;
                    case "aART":
                        tags.OfferAlbumArtist(DecodeMp4Text(dataType, value));
                        break;
                    case "©alb":
                        tags.OfferAlbum(DecodeMp4Text(dataType, value));
                        break;
                    case "trkn" when value.Length >= 4:
                        tags.OfferTrackNumber(BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2)));
                        break;
                    case "covr":
                        var mimeType = dataType switch
                        {
                            13 => "image/jpeg",
                            14 => "image/png",
                            27 => "image/bmp",
                            _ => null,
                        };
                        tags.OfferCoverArt(value, mimeType, isFrontCover: true);
                        break;
                }

                break; // the first data box is the value
            }
        }
    }

    /// <summary>Well-known data types: 1 is UTF-8 and 2 is UTF-16 big-endian. Other types are read as UTF-8.</summary>
    private static string DecodeMp4Text(uint dataType, ReadOnlySpan<byte> value) =>
        dataType == 2 ? Encoding.BigEndianUnicode.GetString(value) : Encoding.UTF8.GetString(value);

    /// <summary>
    /// The boxes between <paramref name="start"/> and <paramref name="end"/>. A box that runs past the end is
    /// returned once, cut at the end and marked truncated. It reads only the 8- or 16-byte box headers.
    /// </summary>
    private static List<Mp4Box> ReadMp4Boxes(Stream stream, long start, long end)
    {
        var boxes = new List<Mp4Box>();
        var position = start;
        while (position + 8 <= end && boxes.Count < MaxBoxesPerLevel)
        {
            stream.Position = position;
            var header = ReadUpTo(stream, 8);
            if (header.Length < 8)
            {
                break;
            }

            long size = Be32(header);
            var headerLength = 8;
            var type = Encoding.Latin1.GetString(header, 4, 4);
            if (size == 1)
            {
                var largeSize = ReadUpTo(stream, 8);
                if (largeSize.Length < 8)
                {
                    break;
                }

                var value = BinaryPrimitives.ReadUInt64BigEndian(largeSize);
                if (value > long.MaxValue)
                {
                    break;
                }

                size = (long)value;
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = end - position; // the box runs to the end of the file
            }

            if (size < headerLength || position + headerLength > end)
            {
                break;
            }

            var truncated = size > end - position;
            var boxEnd = truncated ? end : position + size;
            boxes.Add(new Mp4Box(type, position + headerLength, boxEnd, truncated));
            if (truncated)
            {
                break;
            }

            position = boxEnd;
        }

        return boxes;
    }
}
