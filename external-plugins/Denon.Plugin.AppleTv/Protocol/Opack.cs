using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace Denon.Plugin.AppleTv.Protocol;

/// <summary>
/// Apple's OPACK serialization (pyatv: support/opack.py, ported byte for byte, including the back-reference
/// table that pack and unpack both keep). One deliberate difference: pack does not enter containers in the
/// reference table, so pack and unpack agree on reference indices (pyatv's pack and unpack do not).
/// <list type="bullet">
/// <item>.NET types: null, bool, integers (non-negative), double/float, string, byte[], Guid,
/// lists (<see cref="IList"/>), dictionaries (<see cref="IDictionary"/>).</item>
/// <item>Unpack returns long for integers, double for floats, string, byte[], Guid, List&lt;object?&gt; and
/// Dictionary&lt;object, object?&gt; (insertion order kept). Absolute time (0x06) unpacks as long.</item>
/// </list>
/// </summary>
internal static class Opack
{
    public static byte[] Pack(object? data) => Pack(data, []);

    public static object? Unpack(ReadOnlySpan<byte> data) => Unpack(data, out _);

    public static object? Unpack(ReadOnlySpan<byte> data, out int consumed)
    {
        var objects = new List<object?>();
        var pos = 0;
        var value = Unpack(data, ref pos, objects);
        consumed = pos;
        return value;
    }

    /// <summary>Unpacks a top-level dictionary (the shape of every Companion message).</summary>
    public static Dictionary<object, object?> UnpackDictionary(ReadOnlySpan<byte> data) =>
        Unpack(data) as Dictionary<object, object?> ?? throw new FormatException("OPACK payload is not a dictionary.");

    // ---------------------------------------------------------------- pack

    private static byte[] Pack(object? data, List<byte[]> objects)
    {
        byte[] packed;
        switch (data)
        {
            case null:
                packed = [0x04];
                break;
            case bool b:
                packed = [(byte)(b ? 0x01 : 0x02)];
                break;
            case Guid g:
                packed = [0x05, .. g.ToByteArray(bigEndian: true)];
                break;
            case DateTime or DateTimeOffset:
                throw new NotSupportedException("OPACK absolute time is not supported.");
            case float f:
                packed = PackDouble(f);
                break;
            case double d:
                packed = PackDouble(d);
                break;
            case string s:
                packed = PackLengthPrefixed(Encoding.UTF8.GetBytes(s), inlineBase: 0x40, inlineMax: 0x20, sizedBase: 0x60, isString: true);
                break;
            case byte[] bytes:
                packed = PackLengthPrefixed(bytes, inlineBase: 0x70, inlineMax: 0x20, sizedBase: 0x90, isString: false);
                break;
            // Containers are never entered in (or taken from) the reference table: that matches unpack, which
            // parses real Apple data. (pyatv's pack does add containers, so its own round trip can break.)
            case IDictionary dict:
                return PackContainer(0xE0, dict.Count, Pairs(dict).SelectMany(kv => new[] { Pack(kv.Key, objects), Pack(kv.Value, objects) }));
            case IList list:
                return PackContainer(0xD0, list.Count, list.Cast<object?>().Select(x => Pack(x, objects)));
            default:
                packed = data is sbyte or byte or short or ushort or int or uint or long or ulong
                    ? PackInteger(data)
                    : throw new NotSupportedException($"OPACK cannot pack {data.GetType().Name}.");
                break;
        }

        // Reuse an earlier identical value by reference, else remember it (single bytes are never referenced).
        var index = objects.FindIndex(o => o.AsSpan().SequenceEqual(packed));
        if (index >= 0)
        {
            packed = index switch
            {
                < 0x21 => [(byte)(0xA0 + index)],
                <= 0xFF => [0xC1, (byte)index],
                <= 0xFFFF => [0xC2, (byte)index, (byte)(index >> 8)],
                _ => [0xC3, (byte)index, (byte)(index >> 8), (byte)(index >> 16), (byte)(index >> 24)],
            };
        }
        else if (packed.Length > 1)
        {
            objects.Add(packed);
        }

        return packed;
    }

    private static IEnumerable<DictionaryEntry> Pairs(IDictionary dict)
    {
        foreach (DictionaryEntry entry in dict)
        {
            yield return entry;
        }
    }

    private static byte[] PackInteger(object data)
    {
        ulong value = data switch
        {
            ulong u => u,
            _ when Convert.ToInt64(data) is var l && l >= 0 => (ulong)l,
            _ => throw new NotSupportedException("OPACK cannot pack negative integers."),
        };

        if (value < 0x28)
        {
            return [(byte)(value + 8)];
        }

        if (value <= 0xFF)
        {
            return [0x30, (byte)value];
        }

        if (value <= 0xFFFF)
        {
            var b = new byte[3];
            b[0] = 0x31;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), (ushort)value);
            return b;
        }

        if (value <= 0xFFFFFFFF)
        {
            var b = new byte[5];
            b[0] = 0x32;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), (uint)value);
            return b;
        }

        var b8 = new byte[9];
        b8[0] = 0x33;
        BinaryPrimitives.WriteUInt64LittleEndian(b8.AsSpan(1), value);
        return b8;
    }

    private static byte[] PackDouble(double d)
    {
        var b = new byte[9];
        b[0] = 0x36;
        BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(1), d);
        return b;
    }

    private static byte[] PackLengthPrefixed(byte[] payload, byte inlineBase, int inlineMax, byte sizedBase, bool isString)
    {
        if (payload.Length <= inlineMax)
        {
            return [(byte)(inlineBase + payload.Length), .. payload];
        }

        // Strings: 0x61..0x64 = 1..4 length bytes. Data: 0x91..0x94 = 1, 2, 4, 8 length bytes.
        int lengthBytes;
        byte marker;
        if (payload.Length <= 0xFF)
        {
            (lengthBytes, marker) = (1, (byte)(sizedBase + 1));
        }
        else if (payload.Length <= 0xFFFF)
        {
            (lengthBytes, marker) = (2, (byte)(sizedBase + 2));
        }
        else if (isString && payload.Length <= 0xFFFFFF)
        {
            (lengthBytes, marker) = (3, (byte)(sizedBase + 3));
        }
        else
        {
            (lengthBytes, marker) = (4, isString ? (byte)(sizedBase + 4) : (byte)(sizedBase + 3));
        }

        var result = new byte[1 + lengthBytes + payload.Length];
        result[0] = marker;
        var length = (uint)payload.Length;
        for (var i = 0; i < lengthBytes; i++)
        {
            result[1 + i] = (byte)(length >> (8 * i));
        }

        payload.CopyTo(result, 1 + lengthBytes);
        return result;
    }

    private static byte[] PackContainer(byte baseMarker, int count, IEnumerable<byte[]> items)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(baseMarker + Math.Min(count, 0xF)));
        foreach (var item in items)
        {
            ms.Write(item);
        }

        if (count >= 0xF)
        {
            ms.WriteByte(0x03); // terminator for "endless" containers
        }

        return ms.ToArray();
    }

    // ---------------------------------------------------------------- unpack

    private static object? Unpack(ReadOnlySpan<byte> data, ref int pos, List<object?> objects)
    {
        if (pos >= data.Length)
        {
            throw new FormatException("OPACK data ended early.");
        }

        var tag = data[pos++];
        object? value;
        var remember = true;

        switch (tag)
        {
            case 0x01:
                value = true;
                remember = false;
                break;
            case 0x02:
                value = false;
                remember = false;
                break;
            case 0x04:
                value = null;
                remember = false;
                break;
            case 0x05:
                value = new Guid(Take(data, ref pos, 16), bigEndian: true);
                break;
            case 0x06:
                value = (long)BinaryPrimitives.ReadUInt64LittleEndian(Take(data, ref pos, 8));
                break;
            case >= 0x08 and <= 0x2F:
                value = (long)(tag - 8);
                remember = false;
                break;
            case 0x35:
                value = (double)BinaryPrimitives.ReadSingleLittleEndian(Take(data, ref pos, 4));
                break;
            case 0x36:
                value = BinaryPrimitives.ReadDoubleLittleEndian(Take(data, ref pos, 8));
                break;
            case >= 0x30 and <= 0x33:
                var size = 1 << (tag & 0xF);
                value = (long)ReadLittleEndian(Take(data, ref pos, size));
                break;
            case >= 0x40 and <= 0x60:
                value = Encoding.UTF8.GetString(Take(data, ref pos, tag - 0x40));
                break;
            case >= 0x61 and <= 0x64:
                var strLength = (int)ReadLittleEndian(Take(data, ref pos, tag & 0xF));
                value = Encoding.UTF8.GetString(Take(data, ref pos, strLength));
                break;
            case >= 0x70 and <= 0x90:
                value = Take(data, ref pos, tag - 0x70).ToArray();
                break;
            case >= 0x91 and <= 0x94:
                var dataLength = (int)ReadLittleEndian(Take(data, ref pos, 1 << ((tag & 0xF) - 1)));
                value = Take(data, ref pos, dataLength).ToArray();
                break;
            case >= 0xD0 and <= 0xDF:
                var list = new List<object?>();
                var listCount = tag & 0xF;
                if (listCount == 0xF)
                {
                    while (Peek(data, pos) != 0x03)
                    {
                        list.Add(Unpack(data, ref pos, objects));
                    }

                    pos++;
                }
                else
                {
                    for (var i = 0; i < listCount; i++)
                    {
                        list.Add(Unpack(data, ref pos, objects));
                    }
                }

                value = list;
                remember = false;
                break;
            case >= 0xE0 and <= 0xEF:
                var dict = new Dictionary<object, object?>();
                var dictCount = tag & 0xF;
                if (dictCount == 0xF)
                {
                    while (Peek(data, pos) != 0x03)
                    {
                        var key = Unpack(data, ref pos, objects) ?? throw new FormatException("OPACK dictionary key is null.");
                        dict[key] = Unpack(data, ref pos, objects);
                    }

                    pos++;
                }
                else
                {
                    for (var i = 0; i < dictCount; i++)
                    {
                        var key = Unpack(data, ref pos, objects) ?? throw new FormatException("OPACK dictionary key is null.");
                        dict[key] = Unpack(data, ref pos, objects);
                    }
                }

                value = dict;
                remember = false;
                break;
            case >= 0xA0 and <= 0xC0:
                value = Reference(objects, tag - 0xA0);
                break;
            case >= 0xC1 and <= 0xC4:
                value = Reference(objects, (int)ReadLittleEndian(Take(data, ref pos, tag - 0xC0)));
                break;
            default:
                throw new FormatException($"Unsupported OPACK tag 0x{tag:X2}.");
        }

        if (remember && !objects.Any(o => SameValue(o, value)))
        {
            objects.Add(value);
        }

        return value;
    }

    private static object? Reference(List<object?> objects, int index) =>
        index < objects.Count ? objects[index] : throw new FormatException($"OPACK reference {index} is out of range.");

    private static byte Peek(ReadOnlySpan<byte> data, int pos) =>
        pos < data.Length ? data[pos] : throw new FormatException("OPACK container has no terminator.");

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int pos, int count)
    {
        if (count < 0 || pos + count > data.Length)
        {
            throw new FormatException("OPACK data ended early.");
        }

        var slice = data.Slice(pos, count);
        pos += count;
        return slice;
    }

    private static ulong ReadLittleEndian(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (var i = 0; i < bytes.Length && i < 8; i++)
        {
            value |= (ulong)bytes[i] << (8 * i);
        }

        return value;
    }

    /// <summary>Python-style equality for the back-reference table (bytes by content, numbers by value).</summary>
    private static bool SameValue(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        (long x, double y) => x == y,
        (double x, long y) => x == y,
        _ => Equals(a, b),
    };
}
