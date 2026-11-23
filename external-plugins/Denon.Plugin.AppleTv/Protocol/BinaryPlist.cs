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

    public static object? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40 || !data[..8].SequenceEqual(Magic))
        {
            throw new FormatException("Not a binary plist.");
        }

        var trailer = data[^32..];
        var offsetSize = trailer[6];
        var refSize = trailer[7];
        var count = checked((int)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]));
        var top = checked((int)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]));
        var tableOffset = checked((int)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]));

        var offsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = checked((int)ReadSized(data.Slice(tableOffset + (i * offsetSize), offsetSize)));
        }

        var reader = new Reader(data.ToArray(), offsets, refSize);
        return reader.ReadObject(top, depth: 0);
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

    private sealed class Reader(byte[] data, int[] offsets, int refSize)
    {
        public object? ReadObject(int index, int depth)
        {
            if (depth > 64 || index < 0 || index >= offsets.Length)
            {
                throw new FormatException("bplist reference is invalid.");
            }

            var pos = offsets[index];
            var marker = data[pos];
            var type = marker >> 4;
            var info = marker & 0x0F;

            switch (type)
            {
                case 0x0:
                    return info switch { 0x08 => false, 0x09 => true, _ => null };
                case 0x1:
                    var intBytes = 1 << info;
                    var span = data.AsSpan(pos + 1, intBytes);
                    return intBytes switch
                    {
                        1 => (long)span[0],
                        2 => (long)BinaryPrimitives.ReadUInt16BigEndian(span),
                        4 => (long)BinaryPrimitives.ReadUInt32BigEndian(span),
                        8 => BinaryPrimitives.ReadInt64BigEndian(span),
                        16 => BinaryPrimitives.ReadInt64BigEndian(span[8..]), // low 64 bits (plistlib big unsigned)
                        _ => throw new FormatException("bplist integer size is invalid."),
                    };
                case 0x2:
                    return info == 2
                        ? BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(pos + 1, 4))
                        : BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(pos + 1, 8));
                case 0x3:
                    var seconds = BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(pos + 1, 8));
                    return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
                case 0x4:
                    var (dataLength, dataStart) = Count(pos, info);
                    return data.AsSpan(dataStart, dataLength).ToArray();
                case 0x5:
                    var (asciiLength, asciiStart) = Count(pos, info);
                    return Encoding.ASCII.GetString(data, asciiStart, asciiLength);
                case 0x6:
                    var (utf16Length, utf16Start) = Count(pos, info);
                    return Encoding.BigEndianUnicode.GetString(data, utf16Start, utf16Length * 2);
                case 0x8:
                    return (long)ReadSized(data.AsSpan(pos + 1, info + 1));
                case 0xA:
                    var (arrayCount, arrayStart) = Count(pos, info);
                    var list = new List<object?>(arrayCount);
                    for (var i = 0; i < arrayCount; i++)
                    {
                        list.Add(ReadObject(Ref(arrayStart, i), depth + 1));
                    }

                    return list;
                case 0xD:
                    var (dictCount, dictStart) = Count(pos, info);
                    var dict = new Dictionary<string, object?>(dictCount);
                    for (var i = 0; i < dictCount; i++)
                    {
                        var key = ReadObject(Ref(dictStart, i), depth + 1)?.ToString() ?? string.Empty;
                        dict[key] = ReadObject(Ref(dictStart, dictCount + i), depth + 1);
                    }

                    return dict;
                default:
                    throw new FormatException($"Unsupported bplist object type 0x{marker:X2}.");
            }
        }

        private int Ref(int start, int i) => (int)ReadSized(data.AsSpan(start + (i * refSize), refSize));

        private (int Count, int Start) Count(int pos, int info)
        {
            if (info != 0x0F)
            {
                return (info, pos + 1);
            }

            var intMarker = data[pos + 1];
            var size = 1 << (intMarker & 0x0F);
            return ((int)ReadSized(data.AsSpan(pos + 2, size)), pos + 2 + size);
        }
    }
}
