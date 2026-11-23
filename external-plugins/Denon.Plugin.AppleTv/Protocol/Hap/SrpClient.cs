using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement.Srp;

namespace Denon.Plugin.AppleTv.Protocol.Hap;

/// <summary>
/// SRP-6a client for HAP pair-setup: RFC 5054 3072-bit group, generator 5, SHA-512, username "Pair-Setup",
/// password = PIN (pyatv: hap_srp.SRPAuthHandler.step1/step2 via srptools).
/// <para>
/// Formulas (as srptools): k = H(N | PAD(g)), x = H(s | H(I ":" P)), u = H(PAD(A) | PAD(B)),
/// S = (B − k·g^x)^(a + u·x) mod N, K = H(S), M1 = H(H(N) xor H(g) | H(I) | s | A | B | K), M2 = H(A | M1 | K).
/// H(g) hashes g as the single byte 0x05 (minimal), exactly like srptools. A, B and S are padded to the size
/// of N and hashes are full 64-byte digests, as HomeKit accessories expect from iOS. This equals srptools'
/// minimal encoding unless a value starts with a zero byte (about 1 in 256); then a retry with a new random
/// key works.
/// </para>
/// </summary>
internal sealed class SrpClient
{
    public const string Username = "Pair-Setup";

    private static readonly BigInteger N = ToBig(Srp6StandardGroups.rfc5054_3072.N.ToByteArrayUnsigned());
    private static readonly BigInteger G = ToBig(Srp6StandardGroups.rfc5054_3072.G.ToByteArrayUnsigned());
    private static readonly int NLength = Srp6StandardGroups.rfc5054_3072.N.ToByteArrayUnsigned().Length; // 384

    private readonly string _password;
    private readonly BigInteger _a;
    private byte[]? _expectedServerProof;

    public SrpClient(string pin, byte[]? privateKey = null)
    {
        _password = pin;
        var a = privateKey ?? RandomNumberGenerator.GetBytes(32);
        _a = ToBig(a);
        PublicKey = Pad(BigInteger.ModPow(G, _a, N));
    }

    /// <summary>A, padded to the size of N.</summary>
    public byte[] PublicKey { get; }

    /// <summary>K (64 bytes) after <see cref="Process"/>.</summary>
    public byte[] SessionKey { get; private set; } = [];

    /// <summary>Computes the session key and the client proof M1 from the server's B and salt.</summary>
    public byte[] Process(byte[] serverPublic, byte[] salt)
    {
        var b = ToBig(serverPublic);
        if (b % N == 0)
        {
            throw new HapPairingException("The Apple TV sent an invalid SRP public key.");
        }

        var paddedB = Pad(b);
        var k = ToBig(H(Pad(N), Pad(G)));
        var x = ToBig(H(salt, H(Encoding.UTF8.GetBytes($"{Username}:{_password}"))));
        var u = ToBig(H(PublicKey, paddedB));
        if (u.IsZero)
        {
            throw new HapPairingException("SRP scrambling parameter is zero.");
        }

        var v = BigInteger.ModPow(G, x, N);
        var baseValue = Mod(b - (k * v), N);
        var s = BigInteger.ModPow(baseValue, _a + (u * x), N);
        SessionKey = H(Pad(s));

        // H(N) and H(g) use the minimal encodings: N is already 384 bytes, g is the single byte 0x05
        // (srptools hashes ints with int_to_bytes; only k pads g).
        var hn = H(N.ToByteArray(isUnsigned: true, isBigEndian: true));
        var hg = H(G.ToByteArray(isUnsigned: true, isBigEndian: true));
        var xor = new byte[hn.Length];
        for (var i = 0; i < xor.Length; i++)
        {
            xor[i] = (byte)(hn[i] ^ hg[i]);
        }

        var m1 = H(xor, H(Encoding.UTF8.GetBytes(Username)), salt, PublicKey, paddedB, SessionKey);
        _expectedServerProof = H(PublicKey, m1, SessionKey);
        return m1;
    }

    /// <summary>True when the server proof M2 matches (the PIN was right on both sides).</summary>
    public bool VerifyServerProof(byte[] serverProof) =>
        _expectedServerProof is not null && CryptographicOperations.FixedTimeEquals(serverProof, _expectedServerProof);

    // ---------------------------------------------------------------- helpers (also used by the test server)

    internal static BigInteger GroupPrime => N;

    internal static BigInteger Generator => G;

    internal static byte[] H(params byte[][] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var part in parts)
        {
            sha.AppendData(part);
        }

        return sha.GetHashAndReset();
    }

    internal static BigInteger ToBig(ReadOnlySpan<byte> bigEndian) => new(bigEndian, isUnsigned: true, isBigEndian: true);

    internal static byte[] Pad(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length >= NLength)
        {
            return bytes;
        }

        var padded = new byte[NLength];
        bytes.CopyTo(padded, NLength - bytes.Length);
        return padded;
    }

    internal static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        var r = value % modulus;
        return r.Sign < 0 ? r + modulus : r;
    }
}
