using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Denon.Plugin.AppleTv.Protocol.Hap;

/// <summary>
/// Crypto primitives for HAP (pyatv: hap_srp.hkdf_expand, support/chacha20). ChaCha20-Poly1305 and HKDF come
/// from .NET; Ed25519 and X25519 from BouncyCastle. No primitive is implemented by hand.
/// </summary>
internal static class HapCrypto
{
    public const int TagLength = 16;

    /// <summary>HKDF-SHA512 with ASCII salt and info, 32-byte output.</summary>
    public static byte[] HkdfExpand(string salt, string info, byte[] secret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, secret, 32, Encoding.ASCII.GetBytes(salt), Encoding.ASCII.GetBytes(info));

    /// <summary>
    /// Nonce for the pairing messages: an 8-byte ASCII label ("PS-Msg05", "PV-Msg02", …) padded on the left to
    /// 12 bytes with zeros.
    /// </summary>
    public static byte[] LabelNonce(string label)
    {
        var bytes = Encoding.ASCII.GetBytes(label);
        if (bytes.Length > 12)
        {
            throw new ArgumentException("Nonce label is longer than 12 bytes.", nameof(label));
        }

        var nonce = new byte[12];
        bytes.CopyTo(nonce, 12 - bytes.Length);
        return nonce;
    }

    /// <summary>Encrypts and returns ciphertext followed by the 16-byte tag.</summary>
    public static byte[] Encrypt(byte[] key, byte[] nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad = default)
    {
        using var chacha = new ChaCha20Poly1305(key);
        var output = new byte[plaintext.Length + TagLength];
        chacha.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), aad);
        return output;
    }

    /// <summary>Decrypts ciphertext followed by the 16-byte tag. Throws <see cref="AuthenticationTagMismatchException"/> on tampering.</summary>
    public static byte[] Decrypt(byte[] key, byte[] nonce, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad = default)
    {
        if (ciphertextAndTag.Length < TagLength)
        {
            throw new CryptographicException("Encrypted data is shorter than the authentication tag.");
        }

        using var chacha = new ChaCha20Poly1305(key);
        var length = ciphertextAndTag.Length - TagLength;
        var output = new byte[length];
        chacha.Decrypt(nonce, ciphertextAndTag[..length], ciphertextAndTag[length..], output, aad);
        return output;
    }

    // ---------------------------------------------------------------- Ed25519 (long-term keys)

    public static byte[] NewEd25519Seed() => RandomNumberGenerator.GetBytes(32);

    public static byte[] Ed25519PublicKey(byte[] seed) =>
        new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded();

    public static byte[] Ed25519Sign(byte[] seed, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed, 0));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    public static bool Ed25519Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
        {
            return false;
        }

        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (ArgumentException)
        {
            return false; // not a valid curve point
        }
    }

    // ---------------------------------------------------------------- X25519 (session keys)

    public sealed class X25519KeyPair
    {
        private readonly X25519PrivateKeyParameters _private;

        public X25519KeyPair()
        {
            _private = new X25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
            PublicKey = _private.GeneratePublicKey().GetEncoded();
        }

        public byte[] PublicKey { get; }

        public byte[] SharedSecret(byte[] peerPublicKey)
        {
            if (peerPublicKey.Length != 32)
            {
                throw new HapPairingException("The Apple TV session key has the wrong length.");
            }

            var agreement = new X25519Agreement();
            agreement.Init(_private);
            var shared = new byte[agreement.AgreementSize];
            try
            {
                agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublicKey, 0), shared, 0);
            }
            catch (InvalidOperationException)
            {
                // BouncyCastle refuses a low-order peer key itself; report it like the check below.
                throw new HapPairingException("The Apple TV session key is invalid.");
            }

            // RFC 7748: a low-order peer key gives an all-zero secret, which an attacker could predict.
            if (shared.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                throw new HapPairingException("The Apple TV session key is invalid.");
            }

            return shared;
        }
    }
}
