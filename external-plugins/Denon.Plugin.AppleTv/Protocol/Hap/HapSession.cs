using System.Buffers.Binary;

namespace Denon.Plugin.AppleTv.Protocol.Hap;

/// <summary>
/// HAP stream encryption for AirPlay control, event and data channels (pyatv: auth/hap_session.HAPSession).
/// Data is cut into blocks of at most 1024 bytes; each block = 2-byte little-endian length (also the AAD) +
/// ciphertext + 16-byte tag. Nonce = 4 zero bytes + 8-byte little-endian counter (one counter per direction).
/// Note: this differs from Companion, whose nonce is the counter in the first 8 bytes.
/// </summary>
internal sealed class HapSession(byte[] outputKey, byte[] inputKey)
{
    public const int FrameLength = 1024;

    private readonly MemoryStream _pending = new();
    private ulong _outCounter;
    private ulong _inCounter;

    public static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length + ((data.Length / FrameLength) + 1) * 18);
        var pos = 0;
        while (pos < data.Length)
        {
            var size = Math.Min(FrameLength, data.Length - pos);
            byte[] length = [(byte)size, (byte)(size >> 8)];
            output.Write(length);
            output.Write(HapCrypto.Encrypt(outputKey, Nonce(_outCounter++), data.Slice(pos, size), length));
            pos += size;
        }

        return output.ToArray();
    }

    /// <summary>Adds received bytes and returns all plaintext from complete blocks (may be empty).</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        _pending.Write(data);
        var buffer = _pending.GetBuffer().AsSpan(0, (int)_pending.Length);
        using var output = new MemoryStream();
        var pos = 0;
        while (buffer.Length - pos >= 2)
        {
            var blockLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[pos..]) + HapCrypto.TagLength;
            if (buffer.Length - pos - 2 < blockLength)
            {
                break;
            }

            var aad = buffer.Slice(pos, 2).ToArray();
            output.Write(HapCrypto.Decrypt(inputKey, Nonce(_inCounter++), buffer.Slice(pos + 2, blockLength), aad));
            pos += 2 + blockLength;
        }

        var rest = buffer[pos..].ToArray();
        _pending.SetLength(0);
        _pending.Write(rest);
        return output.ToArray();
    }
}
