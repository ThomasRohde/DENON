namespace Denon.Plugin.AppleTv.Protocol.Hap;

/// <summary>TLV8 tags used by HAP pairing (pyatv: hap_tlv8.TlvValue).</summary>
internal static class TlvTag
{
    public const byte Method = 0x00;
    public const byte Identifier = 0x01;
    public const byte Salt = 0x02;
    public const byte PublicKey = 0x03;
    public const byte Proof = 0x04;
    public const byte EncryptedData = 0x05;
    public const byte SeqNo = 0x06;
    public const byte Error = 0x07;
    public const byte BackOff = 0x08;
    public const byte Certificate = 0x09;
    public const byte Signature = 0x0A;
    public const byte Permissions = 0x0B;
    public const byte FragmentData = 0x0C;
    public const byte FragmentLast = 0x0D;
    public const byte Name = 0x11;
    public const byte Flags = 0x13;
}

/// <summary>HAP pairing error codes (TLV tag 0x07).</summary>
internal static class TlvError
{
    public static string Describe(byte code) => code switch
    {
        0x01 => "Unknown",
        0x02 => "Authentication (wrong PIN?)",
        0x03 => "BackOff (too many attempts; wait and try again)",
        0x04 => "MaxPeers (too many paired devices)",
        0x05 => "MaxTries",
        0x06 => "Unavailable (already pairing, or pairing is not allowed)",
        0x07 => "Busy",
        _ => $"0x{code:X2}",
    };
}

/// <summary>
/// TLV8 encoding (HomeKit). Values longer than 255 bytes are split into chunks with the same tag; reading
/// joins chunks of the same tag again. Empty values are not written (as in pyatv).
/// </summary>
internal static class Tlv8
{
    public static byte[] Write(params (byte Tag, byte[] Value)[] items)
    {
        using var ms = new MemoryStream();
        foreach (var (tag, value) in items)
        {
            var pos = 0;
            while (pos < value.Length)
            {
                var size = Math.Min(255, value.Length - pos);
                ms.WriteByte(tag);
                ms.WriteByte((byte)size);
                ms.Write(value, pos, size);
                pos += size;
            }
        }

        return ms.ToArray();
    }

    public static Dictionary<byte, byte[]> Read(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<byte, byte[]>();
        var pos = 0;
        while (pos + 2 <= data.Length)
        {
            var tag = data[pos];
            var length = data[pos + 1];
            if (pos + 2 + length > data.Length)
            {
                throw new FormatException("TLV8 value runs past the end of the data.");
            }

            var value = data.Slice(pos + 2, length).ToArray();
            result[tag] = result.TryGetValue(tag, out var existing) ? [.. existing, .. value] : value;
            pos += 2 + length;
        }

        return result;
    }

    /// <summary>Throws a <see cref="HapPairingException"/> when the TLV holds an error tag.</summary>
    public static Dictionary<byte, byte[]> ReadChecked(ReadOnlySpan<byte> data)
    {
        var tlv = Read(data);
        if (tlv.TryGetValue(TlvTag.Error, out var error) && error.Length > 0)
        {
            var detail = TlvError.Describe(error[0]);
            if (tlv.TryGetValue(TlvTag.BackOff, out var backoff) && backoff.Length > 0)
            {
                long seconds = 0;
                for (var i = 0; i < Math.Min(backoff.Length, 8); i++)
                {
                    seconds |= (long)backoff[i] << (8 * i); // little-endian
                }

                detail += $", retry in {seconds} s";
            }

            throw new HapPairingException($"The Apple TV refused: {detail}.", error[0]);
        }

        return tlv;
    }

    public static byte[] Require(Dictionary<byte, byte[]> tlv, byte tag, string what) =>
        tlv.TryGetValue(tag, out var value) && value.Length > 0
            ? value
            : throw new HapPairingException($"The Apple TV response has no {what}.");
}

internal sealed class HapPairingException(string message, byte? errorCode = null) : Exception(message)
{
    public byte? ErrorCode { get; } = errorCode;
}
