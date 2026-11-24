namespace Denon.Plugin.AppleTv.Protocol.Hap;

/// <summary>Session keys from a HAP handshake: HKDF over its shared secret (pair-verify or transient pair-setup).</summary>
internal interface IHapSessionKeys
{
    (byte[] Output, byte[] Input) DeriveKeys(string salt, string outputInfo, string inputInfo);
}

/// <summary>
/// Transient HAP pair-setup (pyatv: airplay/auth/hap_transient.py), for devices without a screen such as the
/// HomePod: M1 with the transient flag, then M2–M4 with the fixed PIN 3939. There is no M5/M6 and nothing is
/// stored: the SRP session key is the shared secret for this connection's keys. Each connection pairs again.
/// </summary>
internal sealed class HapTransientPairSetup : IHapSessionKeys
{
    public const string TransientPin = "3939";

    /// <summary>kTLVType_Flags: kPairingFlag_Transient.</summary>
    private const byte TransientFlag = 0x10;

    private byte[]? _salt;
    private byte[]? _serverPublic;
    private SrpClient? _srp;
    private byte[]? _sessionKey;

    /// <summary>M1: pair-setup with the transient flag.</summary>
    public static byte[] BuildM1() => Tlv8.Write((TlvTag.Method, [0x00]), (TlvTag.SeqNo, [0x01]), (TlvTag.Flags, [TransientFlag]));

    /// <summary>M2: salt and the device's SRP public key.</summary>
    public void ReadM2(byte[] tlvBytes)
    {
        var tlv = Tlv8.ReadChecked(tlvBytes);
        _salt = Tlv8.Require(tlv, TlvTag.Salt, "salt");
        _serverPublic = Tlv8.Require(tlv, TlvTag.PublicKey, "SRP public key");
    }

    /// <summary>M3: our SRP public key and proof for the fixed PIN.</summary>
    public byte[] BuildM3()
    {
        if (_salt is null || _serverPublic is null)
        {
            throw new InvalidOperationException("M2 was not received.");
        }

        _srp = new SrpClient(TransientPin);
        var proof = _srp.Process(_serverPublic, _salt);
        return Tlv8.Write((TlvTag.SeqNo, [0x03]), (TlvTag.PublicKey, _srp.PublicKey), (TlvTag.Proof, proof));
    }

    /// <summary>M4: the device's proof. After it, <see cref="DeriveKeys"/> works.</summary>
    public void ReadM4(byte[] tlvBytes)
    {
        var tlv = Tlv8.ReadChecked(tlvBytes);
        var proof = Tlv8.Require(tlv, TlvTag.Proof, "SRP proof");
        if (_srp is null || !_srp.VerifyServerProof(proof))
        {
            throw new HapPairingException("The device's SRP proof does not match in transient pairing.");
        }

        _sessionKey = _srp.SessionKey;
    }

    public (byte[] Output, byte[] Input) DeriveKeys(string salt, string outputInfo, string inputInfo)
    {
        var shared = _sessionKey ?? throw new InvalidOperationException("Transient pairing did not finish.");
        return (HapCrypto.HkdfExpand(salt, outputInfo, shared), HapCrypto.HkdfExpand(salt, inputInfo, shared));
    }
}
