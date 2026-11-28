using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace Denon.Plugin.AppleTv.Protocol;

/// <summary>
/// Minimal binary property list ("bplist00") codec for AirPlay RTSP bodies and data-stream payloads
/// (pyatv uses Python's plistlib). Supported: null, bool, integers, double, string, byte[], Guid (as data),
/// lists and string-keyed dictionaries. Integers are written like plistlib: 1, 2, 4 or 8 bytes by value.
/// Read results: long, double, bool, string, byte[], DateTime, List&lt;object?&gt;, Dictionary&lt;string, object?&gt;.
/// </summary>
internal static class BinaryPlist
{
    private static readonly byte[] Magic = "bplist00"u8.ToArray();

    // ---------------------------------------------------------------- write

    public static byte[] Write(object? root)
    {
        var objects = new List<byte[]>();
        var top = Flatten(root, objects);
        var refSize = objects.Count <= 0xFF ? 1 : objects.Count <= 0xFFFF ? 2 : 4;

        // Second pass: now that the reference size is known, encode containers with real references.
        objects.Clear();
        top = Encode(root, objects, refSize);

        using var ms = new MemoryStream();
        ms.Write(Magic);
        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = ms.Position;
            ms.Write(objects[i]);
        }

        var offsetTable = ms.Position;
        var offsetSize = offsetTable <= 0xFF ? 1 : offsetTable <= 0xFFFF ? 2 : offsetTable <= 0xFFFFFFFF ? 4 : 8;
        foreach (var offset in offsets)
        {
            WriteSized(ms, (ulong)offset, offsetSize);
        }

        var trailer = new byte[32];
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)objects.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), (ulong)top);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)offsetTable);
        ms.Write(trailer);
        return ms.ToArray();
    }

    /// <summary>Counts objects (first pass) with a provisional 4-byte reference size.</summary>
    private static int Flatten(object? value, List<byte[]> objects) => Encode(value, objects, 4);

    private static int Encode(object? value, List<byte[]> objects, int refSize)
    {
        var index = objects.Count;
        objects.Add([]); // reserve the slot so containers come before their children (like plistlib)

        byte[] encoded;
        switch (value)
        {
            case null:
                encoded = [0x00];
                break;
            case bool b:
                encoded = [(byte)(b ? 0x09 : 0x08)];
                break;
            case float or double:
                encoded = new byte[9];
                encoded[0] = 0x23;
                BinaryPrimitives.WriteDoubleBigEndian(encoded.AsSpan(1), Convert.ToDouble(value));
                break;
            case string s:
                encoded = EncodeString(s);
                break;
            case byte[] data:
                encoded = [.. Marker(0x40, data.Length), .. data];
                break;
            case Guid g:
                var guidBytes = g.ToByteArray(bigEndian: true);
                encoded = [.. Marker(0x40, guidBytes.Length), .. guidBytes];
                break;
            case IDictionary dict:
                var keys = new List<int>();
                var values = new List<int>();
                foreach (DictionaryEntry entry in dict)
                {
                    keys.Add(Encode(entry.Key.ToString(), objects, refSize));
                }

                foreach (DictionaryEntry entry in dict)
                {
                    values.Add(Encode(entry.Value, objects, refSize));
                }

                encoded = [.. Marker(0xD0, dict.Count), .. Refs(keys, refSize), .. Refs(values, refSize)];
                break;
            case IList list:
                var items = new List<int>();
                foreach (var item in list)
                {
                    items.Add(Encode(item, objects, refSize));
                }

                encoded = [.. Marker(0xA0, list.Count), .. Refs(items, refSize)];
                break;
            default:
                encoded = value is sbyte or byte or short or ushort or int or uint or long or ulong
                    ? EncodeInteger(value)
                    : throw new NotSupportedException($"bplist cannot write {value.GetType().Name}.");
                break;
        }

        objects[index] = encoded;
        return index;
    }

    private static byte[] EncodeInteger(object value)
    {
        if (value is ulong big && big > long.MaxValue)
        {
            throw new NotSupportedException("bplist integers above 2^63-1 are not supported.");
        }

        var v = Convert.ToInt64(value);
        if (v < 0)
        {
            var neg = new byte[9];
            neg[0] = 0x13;
            BinaryPrimitives.WriteInt64BigEndian(neg.AsSpan(1), v);
            return neg;
        }

        if (v <= 0xFF)
        {
            return [0x10, (byte)v];
        }

        if (v <= 0xFFFF)
        {
            var b = new byte[3];
            b[0] = 0x11;
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), (ushort)v);
            return b;
        }

        if (v <= 0xFFFFFFFF)
        {
            var b = new byte[5];
            b[0] = 0x12;
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(1), (uint)v);
            return b;
        }

        var b8 = new byte[9];
        b8[0] = 0x13;
        BinaryPrimitives.WriteInt64BigEndian(b8.AsSpan(1), v);
        return b8;
    }

    private static byte[] EncodeString(string s)
    {
        if (s.All(c => c < 0x80))
        {
            return [.. Marker(0x50, s.Length), .. Encoding.ASCII.GetBytes(s)];
        }

        var utf16 = Encoding.BigEndianUnicode.GetBytes(s);
        return [.. Marker(0x60, utf16.Length / 2), .. utf16];
    }

    private static byte[] Marker(byte type, int count)
    {
        if (count < 15)
        {
            return [(byte)(type | count)];
        }

        return [(byte)(type | 0x0F), .. EncodeInteger(count)];
    }

    private static byte[] Refs(List<int> refs, int refSize)
    {
        var bytes = new byte[refs.Count * refSize];
        for (var i = 0; i < refs.Count; i++)
        {
            var r = (ulong)refs[i];
            for (var b = 0; b < refSize; b++)
            {
                bytes[(i * refSize) + b] = (byte)(r >> (8 * (refSize - 1 - b)));
            }
        }

        return bytes;
    }

    private static void WriteSized(Stream stream, ulong value, int size)
    {
        for (var b = size - 1; b >= 0; b--)
        {
            stream.WriteByte((byte)(value >> (8 * b)));
        }
    }

    // ---------------------------------------------------------------- read

    /// <summary>Most objects one read may decode. References can share objects, so a small file could
    /// otherwise expand into a huge tree.</summary>
    public const int MaxDecodedObjects = 100_000;

    private const int MaxDepth = 64;
    private const int TrailerLength = 32;

    /// <summary>Reads a binary plist. Any malformed input throws <see cref="FormatException"/>.</summary>
    public static object? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < Magic.Length + TrailerLength || !data[..Magic.Length].SequenceEqual(Magic))
        {
            throw new FormatException("Not a binary plist.");
        }

        var trailer = data[^TrailerLength..];
        int offsetSize = trailer[6];
        int refSize = trailer[7];
        if (offsetSize is < 1 or > 8 || refSize is < 1 or > 8)
        {
            throw new FormatException("bplist offset or reference size is invalid.");
        }

        var count = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        var top = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        var tableOffset = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);

        // Objects sit between the magic and the offset table; the table must end before the trailer.
        var tableEnd = (ulong)(data.Length - TrailerLength);
        if (count == 0 || top >= count || tableOffset < (ulong)Magic.Length || tableOffset > tableEnd
            || count > (tableEnd - tableOffset) / (ulong)offsetSize)
        {
            throw new FormatException("bplist trailer is invalid.");
        }

        var offsets = new int[(int)count];
        for (var i = 0; i < offsets.Length; i++)
        {
            var offset = ReadSized(data.Slice((int)tableOffset + (i * offsetSize), offsetSize));
            if (offset < (ulong)Magic.Length || offset >= tableOffset)
            {
                throw new FormatException("bplist object offset is out of range.");
            }

            offsets[i] = (int)offset;
        }

        var reader = new Reader(data[..(int)tableOffset].ToArray(), offsets, refSize);
        try
        {
            return reader.ReadObject(top, depth: 0);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            // Backstop: the expected failures are checked, but no malformed input may escape as another type.
            throw new FormatException("bplist data is malformed.", ex);
        }
    }

    public static Dictionary<string, object?> ReadDictionary(ReadOnlySpan<byte> data) =>
        Read(data) as Dictionary<string, object?> ?? throw new FormatException("bplist root is not a dictionary.");

    private static ulong ReadSized(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    /// <summary>Reads objects from the object area (everything before the offset table).</summary>
    private sealed class Reader(byte[] data, int[] offsets, int refSize)
    {
        private static readonly DateTime Epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly double MinSeconds = (DateTime.MinValue - Epoch).TotalSeconds;
        private static readonly double MaxSeconds = (DateTime.MaxValue - Epoch).TotalSeconds;

        private int _decoded;

        public object? ReadObject(ulong index, int depth)
        {
            if (depth > MaxDepth || index >= (ulong)offsets.Length)
            {
                throw new FormatException("bplist reference is invalid.");
            }

            if (++_decoded > MaxDecodedObjects)
            {
                throw new FormatException("bplist has too many objects.");
            }

            var pos = offsets[(int)index];
            var marker = data[pos];
            var type = marker >> 4;
            var info = marker & 0x0F;

            switch (type)
            {
                case 0x0:
                    return info switch { 0x08 => false, 0x09 => true, _ => null };
                case 0x1:
                    return ReadInteger(pos, info);
                case 0x2:
                    return info switch
                    {
                        2 => BinaryPrimitives.ReadSingleBigEndian(Bytes(pos + 1, 4)),
                        3 => BinaryPrimitives.ReadDoubleBigEndian(Bytes(pos + 1, 8)),
                        _ => throw new FormatException("bplist real size is invalid."),
                    };
                case 0x3:
                    var seconds = BinaryPrimitives.ReadDoubleBigEndian(Bytes(pos + 1, 8));
                    if (!double.IsFinite(seconds) || seconds <= MinSeconds || seconds >= MaxSeconds)
                    {
                        throw new FormatException("bplist date is out of range.");
                    }

                    return Epoch.AddSeconds(seconds);
                case 0x4:
                    var (dataLength, dataStart) = Count(pos, info);
                    return Bytes(dataStart, dataLength).ToArray();
                case 0x5:
                    var (asciiLength, asciiStart) = Count(pos, info);
                    return Encoding.ASCII.GetString(Bytes(asciiStart, asciiLength));
                case 0x6:
                    var (utf16Length, utf16Start) = Count(pos, info);
                    return Encoding.BigEndianUnicode.GetString(Bytes(utf16Start, utf16Length * 2));
                case 0x8:
                    return (long)ReadSized(Bytes(pos + 1, info + 1));
                case 0xA:
                    var (arrayCount, arrayStart) = Count(pos, info);
                    var arrayRefs = Bytes(arrayStart, arrayCount * refSize);
                    var list = new List<object?>((int)Math.Min(arrayCount, 256));
                    for (var i = 0; i < arrayCount; i++)
                    {
                        list.Add(ReadObject(Ref(arrayRefs, i), depth + 1));
                    }

                    return list;
                case 0xD:
                    var (dictCount, dictStart) = Count(pos, info);
                    var dictRefs = Bytes(dictStart, dictCount * 2 * refSize);
                    var dict = new Dictionary<string, object?>((int)Math.Min(dictCount, 256));
                    for (var i = 0; i < dictCount; i++)
                    {
                        var key = ReadObject(Ref(dictRefs, i), depth + 1)?.ToString() ?? string.Empty;
                        dict[key] = ReadObject(Ref(dictRefs, (int)dictCount + i), depth + 1);
                    }

                    return dict;
                default:
                    throw new FormatException($"Unsupported bplist object type 0x{marker:X2}.");
            }
        }

        private long ReadInteger(int pos, int info)
        {
            // Sizes are 1, 2, 4, 8 or 16 bytes (info 0 to 4); anything larger is not a valid integer.
            if (info > 4)
            {
                throw new FormatException("bplist integer size is invalid.");
            }

            var span = Bytes(pos + 1, 1 << info);
            return info switch
            {
                0 => span[0],
                1 => BinaryPrimitives.ReadUInt16BigEndian(span),
                2 => BinaryPrimitives.ReadUInt32BigEndian(span),
                3 => BinaryPrimitives.ReadInt64BigEndian(span),
                _ => BinaryPrimitives.ReadInt64BigEndian(span[8..]), // low 64 bits (plistlib big unsigned)
            };
        }

        private ulong Ref(ReadOnlySpan<byte> refs, int i) => ReadSized(refs.Slice(i * refSize, refSize));

        /// <summary>A checked slice of the object area. Lengths are long so a hostile size cannot wrap.</summary>
        private ReadOnlySpan<byte> Bytes(long start, long length)
        {
            if (start < 0 || length < 0 || start > data.Length || length > data.Length - start)
            {
                throw new FormatException("bplist object runs past the end of the data.");
            }

            return data.AsSpan((int)start, (int)length);
        }

        /// <summary>A container or string length: the low nibble, or a following integer object when it is 0xF.</summary>
        private (long Count, int Start) Count(int pos, int info)
        {
            if (info != 0x0F)
            {
                return (info, pos + 1);
            }

            var intMarker = Bytes(pos + 1, 1)[0];
            var sizeInfo = intMarker & 0x0F;
            if (intMarker >> 4 != 0x1 || sizeInfo > 3)
            {
                throw new FormatException("bplist count is invalid.");
            }

            var size = 1 << sizeInfo;
            var value = ReadSized(Bytes(pos + 2, size));
            if (value > int.MaxValue)
            {
                throw new FormatException("bplist count is too large.");
            }

            return ((long)value, pos + 2 + size);
        }
    }
}
