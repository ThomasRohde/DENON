using System.Text;

namespace Denon.Plugin.AppleTv.Protocol.Hap;

/// <summary>
/// HAP pair-setup (M1–M6), transport-neutral: each method builds the TLV8 bytes to send or reads the reply
/// (pyatv: hap_srp.SRPAuthHandler.initialize/step1–step4 and companion/auth.CompanionPairSetupProcedure).
/// </summary>
internal sealed class HapPairSetup
{
    private readonly byte[] _seed = HapCrypto.NewEd25519Seed();
    private readonly byte[] _publicKey;
    private readonly string? _displayName;
    private byte[]? _salt;
    private byte[]? _serverPublic;
    private SrpClient? _srp;
    private byte[]? _encryptKey;

    public HapPairSetup(string? displayName = null, byte[]? pairingId = null)
    {
        _displayName = displayName;
        _publicKey = HapCrypto.Ed25519PublicKey(_seed);

        // pyatv: str(uuid.uuid4()).encode() — a lowercase UUID string as ASCII bytes.
        PairingId = pairingId ?? Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("D"));
    }

    public byte[] PairingId { get; }

    /// <summary>M1: start pair-setup (the Apple TV then shows a PIN).</summary>
    public static byte[] BuildM1() => Tlv8.Write((TlvTag.Method, [0x00]), (TlvTag.SeqNo, [0x01]));

    /// <summary>M2: salt and the Apple TV's SRP public key.</summary>
    public void ReadM2(byte[] tlvBytes)
    {
        var tlv = Tlv8.ReadChecked(tlvBytes);
        _salt = Tlv8.Require(tlv, TlvTag.Salt, "salt");
        _serverPublic = Tlv8.Require(tlv, TlvTag.PublicKey, "SRP public key");
    }

    /// <summary>M3: our SRP public key and proof for the PIN.</summary>
    public byte[] BuildM3(string pin)
    {
        if (_salt is null || _serverPublic is null)
        {
            throw new InvalidOperationException("M2 was not received.");
        }

        _srp = new SrpClient(pin);
        var proof = _srp.Process(_serverPublic, _salt);
        return Tlv8.Write((TlvTag.SeqNo, [0x03]), (TlvTag.PublicKey, _srp.PublicKey), (TlvTag.Proof, proof));
    }

    /// <summary>M4: the Apple TV's proof (a wrong PIN arrives here as an Authentication error).</summary>
    public void ReadM4(byte[] tlvBytes)
    {
        var tlv = Tlv8.ReadChecked(tlvBytes);
        var proof = Tlv8.Require(tlv, TlvTag.Proof, "SRP proof");
        if (_srp is null || !_srp.VerifyServerProof(proof))
        {
            throw new HapPairingException("The Apple TV's SRP proof does not match. Start pairing again (a rare key-encoding edge case is fixed by a retry).");
        }
    }

    /// <summary>M5: our long-term identity, signed and encrypted with the SRP session key.</summary>
    public byte[] BuildM5()
    {
        var sessionKey = _srp?.SessionKey ?? throw new InvalidOperationException("M4 was not processed.");
        var iosDeviceX = HapCrypto.HkdfExpand("Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info", sessionKey);
        _encryptKey = HapCrypto.HkdfExpand("Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info", sessionKey);

        byte[] deviceInfo = [.. iosDeviceX, .. PairingId, .. _publicKey];
        var signature = HapCrypto.Ed25519Sign(_seed, deviceInfo);

        var items = new List<(byte, byte[])>
        {
            (TlvTag.Identifier, PairingId),
            (TlvTag.PublicKey, _publicKey),
            (TlvTag.Signature, signature),
        };

        if (!string.IsNullOrWhiteSpace(_displayName))
        {
            items.Add((TlvTag.Name, Opack.Pack(new Dictionary<string, object?> { ["name"] = _displayName })));
        }

        var encrypted = HapCrypto.Encrypt(_encryptKey, HapCrypto.LabelNonce("PS-Msg05"), Tlv8.Write([.. items]));
        return Tlv8.Write((TlvTag.SeqNo, [0x05]), (TlvTag.EncryptedData, encrypted));
    }

    /// <summary>
    /// M6: the Apple TV's long-term identity. Returns the credentials to store. Throws
    /// <see cref="HapPairingException"/> when the Apple TV's signature does not verify (pyatv does not check it),
    /// so nothing is saved for a device that cannot prove it holds the key.
    /// </summary>
    public HapCredentials ReadM6(byte[] tlvBytes)
    {
        var tlv = Tlv8.ReadChecked(tlvBytes);
        var encrypted = Tlv8.Require(tlv, TlvTag.EncryptedData, "encrypted data");
        var key = _encryptKey ?? throw new InvalidOperationException("M5 was not sent.");

        byte[] decrypted;
        try
        {
            decrypted = HapCrypto.Decrypt(key, HapCrypto.LabelNonce("PS-Msg06"), encrypted);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new HapPairingException($"Could not decrypt the Apple TV's final pairing message: {ex.Message}");
        }

        var inner = Tlv8.Read(decrypted);
        var atvId = Tlv8.Require(inner, TlvTag.Identifier, "identifier");
        var atvPublic = Tlv8.Require(inner, TlvTag.PublicKey, "long-term public key");
        var atvSignature = Tlv8.Require(inner, TlvTag.Signature, "signature");

        // HAP: the accessory signs AccessoryX | AccessoryPairingID | AccessoryLTPK.
        var accessoryX = HapCrypto.HkdfExpand("Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info", _srp!.SessionKey);
        if (!HapCrypto.Ed25519Verify(atvPublic, [.. accessoryX, .. atvId, .. atvPublic], atvSignature))
        {
            throw new HapPairingException("The Apple TV's pairing signature does not verify. Pairing was not saved; start pairing again.");
        }

        return new HapCredentials(atvPublic, _seed, atvId, PairingId);
    }
}

/// <summary>
/// HAP pair-verify (M1–M4) with stored credentials; yields the shared secret for session keys
/// (pyatv: hap_srp.SRPAuthHandler.verify1/verify2 and companion/auth.CompanionPairVerifyProcedure).
/// </summary>
internal sealed class HapPairVerify(HapCredentials credentials) : IHapSessionKeys
{
    private readonly HapCrypto.X25519KeyPair _ephemeral = new();
    private byte[]? _shared;

    /// <summary>M1: our ephemeral X25519 public key.</summary>
    public byte[] BuildM1() => Tlv8.Write((TlvTag.SeqNo, [0x01]), (TlvTag.PublicKey, _ephemeral.PublicKey));

    /// <summary>Reads M2 (the Apple TV proves its identity) and builds M3 (we prove ours).</summary>
    public byte[] ReadM2BuildM3(byte[] tlvBytes)
    {
        var tlv = Tlv8.ReadChecked(tlvBytes);
        var atvSessionPublic = Tlv8.Require(tlv, TlvTag.PublicKey, "session public key");
        var encrypted = Tlv8.Require(tlv, TlvTag.EncryptedData, "encrypted data");

        _shared = _ephemeral.SharedSecret(atvSessionPublic);
        var sessionKey = HapCrypto.HkdfExpand("Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info", _shared);

        byte[] decrypted;
        try
        {
            decrypted = HapCrypto.Decrypt(sessionKey, HapCrypto.LabelNonce("PV-Msg02"), encrypted);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new HapPairingException("Pair-verify failed: could not decrypt the Apple TV's reply.");
        }

        var inner = Tlv8.Read(decrypted);
        var identifier = Tlv8.Require(inner, TlvTag.Identifier, "identifier");
        var signature = Tlv8.Require(inner, TlvTag.Signature, "signature");

        if (!identifier.AsSpan().SequenceEqual(credentials.AtvId))
        {
            throw new HapPairingException("Pair-verify failed: this is not the paired Apple TV (identifier differs). Pair again.");
        }

        if (!HapCrypto.Ed25519Verify(credentials.AtvPublicKey, [.. atvSessionPublic, .. identifier, .. _ephemeral.PublicKey], signature))
        {
            throw new HapPairingException("Pair-verify failed: the Apple TV's signature is wrong. Pair again.");
        }

        byte[] deviceInfo = [.. _ephemeral.PublicKey, .. credentials.ClientId, .. atvSessionPublic];
        var ourSignature = HapCrypto.Ed25519Sign(credentials.ClientSeed, deviceInfo);
        var innerOut = Tlv8.Write((TlvTag.Identifier, credentials.ClientId), (TlvTag.Signature, ourSignature));
        var encryptedOut = HapCrypto.Encrypt(sessionKey, HapCrypto.LabelNonce("PV-Msg03"), innerOut);
        return Tlv8.Write((TlvTag.SeqNo, [0x03]), (TlvTag.EncryptedData, encryptedOut));
    }

    /// <summary>M4: success, or an error TLV (for example when the Apple TV removed our pairing).</summary>
    public static void ReadM4(byte[] tlvBytes) => Tlv8.ReadChecked(tlvBytes);

    /// <summary>Session keys after a successful verify (Companion: salt "", "ClientEncrypt-main" / "ServerEncrypt-main").</summary>
    public (byte[] Output, byte[] Input) DeriveKeys(string salt, string outputInfo, string inputInfo)
    {
        var shared = _shared ?? throw new InvalidOperationException("Pair-verify did not run.");
        return (HapCrypto.HkdfExpand(salt, outputInfo, shared), HapCrypto.HkdfExpand(salt, inputInfo, shared));
    }
}
