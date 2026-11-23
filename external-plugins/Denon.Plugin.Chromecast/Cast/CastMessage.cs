using System.IO;
using System.Text;

namespace Denon.Plugin.Chromecast.Cast;

/// <summary>
/// Cast v2 "CastMessage" protobuf (cast_channel.proto), string payloads only.
/// Fields: 1 protocol_version, 2 source_id, 3 destination_id, 4 namespace, 5 payload_type, 6 payload_utf8.
/// </summary>
internal sealed record CastMessage(string SourceId, string DestinationId, string Namespace, string PayloadUtf8)
{
    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        WriteVarintField(ms, 1, 0); // CASTV2_1_0
        WriteStringField(ms, 2, SourceId);
        WriteStringField(ms, 3, DestinationId);
        WriteStringField(ms, 4, Namespace);
        WriteVarintField(ms, 5, 0); // STRING
        WriteStringField(ms, 6, PayloadUtf8);
        return ms.ToArray();
    }

    public static CastMessage Decode(ReadOnlySpan<byte> data)
    {
        string source = string.Empty, destination = string.Empty, ns = string.Empty, payload = string.Empty;
        var pos = 0;
        while (pos < data.Length)
        {
            var key = ReadVarint(data, ref pos);
            var field = (int)(key >> 3);
            var wireType = (int)(key & 7);
            switch (wireType)
            {
                case 0:
                    ReadVarint(data, ref pos);
                    break;
                case 1:
                    pos += 8;
                    break;
                case 5:
                    pos += 4;
                    break;
                case 2:
                    var length = ReadVarint(data, ref pos);
                    if (length > (ulong)(data.Length - pos))
                    {
                        throw new InvalidDataException("Truncated Cast message.");
                    }

                    var slice = data.Slice(pos, (int)length);
                    pos += (int)length;
                    switch (field)
                    {
                        case 2: source = Encoding.UTF8.GetString(slice); break;
                        case 3: destination = Encoding.UTF8.GetString(slice); break;
                        case 4: ns = Encoding.UTF8.GetString(slice); break;
                        case 6: payload = Encoding.UTF8.GetString(slice); break;
                    }

                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wireType}.");
            }
        }

        if (pos > data.Length)
        {
            throw new InvalidDataException("Truncated Cast message.");
        }

        return new CastMessage(source, destination, ns, payload);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (pos >= data.Length)
            {
                throw new InvalidDataException("Truncated varint.");
            }

            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("Varint is too long.");
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteVarintField(Stream stream, int field, ulong value)
    {
        WriteVarint(stream, (ulong)(field << 3));
        WriteVarint(stream, value);
    }

    private static void WriteStringField(Stream stream, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(stream, (ulong)((field << 3) | 2));
        WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }
}
