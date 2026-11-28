using System.Buffers.Binary;
using System.Text;

namespace Denon.Plugin.AppleTv.Protocol;

/// <summary>Minimal protobuf (proto2) writer: fields are written in the order they are added.</summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    public static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>(10);
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);

        return [.. bytes];
    }

    public ProtoWriter Uint(int field, ulong value)
    {
        Tag(field, 0);
        _ms.Write(Varint(value));
        return this;
    }

    public ProtoWriter Int(int field, long value) => Uint(field, unchecked((ulong)value)); // int32/int64: two's complement

    public ProtoWriter Bool(int field, bool value) => Uint(field, value ? 1UL : 0UL);

    public ProtoWriter String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    public ProtoWriter Bytes(int field, byte[] value)
    {
        Tag(field, 2);
        _ms.Write(Varint((ulong)value.Length));
        _ms.Write(value);
        return this;
    }

    public ProtoWriter Message(int field, ProtoWriter message) => Bytes(field, message.ToArray());

    public ProtoWriter Double(int field, double value)
    {
        Tag(field, 1);
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, value);
        _ms.Write(b);
        return this;
    }

    public byte[] ToArray() => _ms.ToArray();

    private void Tag(int field, int wireType) => _ms.Write(Varint(((ulong)field << 3) | (uint)wireType));
}

/// <summary>
/// Minimal protobuf reader: parses one message into its fields (unknown fields kept, never fatal).
/// Varints come back as ulong, length-delimited as byte[], fixed64 as ulong bits, fixed32 as uint bits.
/// </summary>
internal sealed class ProtoReader
{
    private readonly List<(int Field, int WireType, object Value)> _fields = [];

    public ProtoReader(ReadOnlySpan<byte> data)
    {
        var bytes = data.ToArray();
        var pos = 0;
        while (pos < bytes.Length)
        {
            var key = ReadVarint(bytes, ref pos);
            var field = (int)(key >> 3);
            var wireType = (int)(key & 7);
            switch (wireType)
            {
                case 0:
                    _fields.Add((field, 0, ReadVarint(bytes, ref pos)));
                    break;
                case 1:
                    Ensure(bytes, pos, 8);
                    _fields.Add((field, 1, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(pos))));
                    pos += 8;
                    break;
                case 2:
                    var rawLength = ReadVarint(bytes, ref pos);
                    Ensure(bytes, pos, rawLength);
                    var length = (int)rawLength;
                    _fields.Add((field, 2, bytes[pos..(pos + length)]));
                    pos += length;
                    break;
                case 5:
                    Ensure(bytes, pos, 4);
                    _fields.Add((field, 5, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos))));
                    pos += 4;
                    break;
                default:
                    throw new FormatException($"Unsupported protobuf wire type {wireType}.");
            }
        }
    }

    public bool Has(int field) => _fields.Any(f => f.Field == field);

    public ulong? Uint(int field) => Last(field, 0) is ulong v ? v : null;

    public long? Int(int field) => Uint(field) is { } v ? unchecked((long)v) : null;

    public bool? Bool(int field) => Uint(field) is { } v ? v != 0 : null;

    public string? String(int field) => Last(field, 2) is byte[] b ? Encoding.UTF8.GetString(b) : null;

    public byte[]? Bytes(int field) => Last(field, 2) as byte[];

    public ProtoReader? Message(int field) => Last(field, 2) is byte[] b ? new ProtoReader(b) : null;

    public IEnumerable<ProtoReader> Messages(int field) =>
        _fields.Where(f => f.Field == field && f.WireType == 2).Select(f => new ProtoReader((byte[])f.Value));

    public double? Double(int field) => Last(field, 1) is ulong bits ? BitConverter.UInt64BitsToDouble(bits) : null;

    public float? Float(int field) => Last(field, 5) is uint bits ? BitConverter.UInt32BitsToSingle(bits) : null;

    public static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (pos >= data.Length)
            {
                throw new FormatException("Protobuf varint ended early.");
            }

            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new FormatException("Protobuf varint is too long.");
    }

    private object? Last(int field, int wireType)
    {
        for (var i = _fields.Count - 1; i >= 0; i--)
        {
            if (_fields[i].Field == field && _fields[i].WireType == wireType)
            {
                return _fields[i].Value;
            }
        }

        return null;
    }

    /// <summary>Throws unless <paramref name="length"/> bytes remain. Compares against what is left, so a
    /// huge length cannot overflow.</summary>
    private static void Ensure(byte[] data, int pos, ulong length)
    {
        if ((uint)pos > (uint)data.Length || length > (ulong)(data.Length - pos))
        {
            throw new FormatException("Protobuf field runs past the end of the data.");
        }
    }
}
