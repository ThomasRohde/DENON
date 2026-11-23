using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Denon.Plugin.AppleTv.Protocol;
using Denon.Plugin.AppleTv.Protocol.Hap;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

/// <summary>
/// Server side of HAP pair-setup and pair-verify, written from the HAP specification (test only).
/// It checks the client's math, signatures and nonces; it does not prove Apple compatibility.
/// </summary>
internal sealed class FakeAccessory(string pin)
{
    private readonly byte[] _seed = HapCrypto.NewEd25519Seed();
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly BigInteger _b = SrpClient.ToBig(RandomNumberGenerator.GetBytes(32));
    private byte[] _paddedB = [];
    private BigInteger _v;
    private byte[] _sessionKey = [];
    private byte[]? _controllerId;
    private byte[]? _controllerPublic;
    private HapCrypto.X25519KeyPair? _ephemeral;
    private byte[]? _controllerEphemeral;
    private byte[]? _shared;

    public byte[] Id { get; } = Encoding.ASCII.GetBytes("AA:BB:CC:DD:EE:FF");

    public string? ControllerName { get; private set; }

    public int HidPresses { get; set; }

    public byte[] Key(string info) => HapCrypto.HkdfExpand("", info, _shared!);

    public byte[] HandleSetup(byte[] request)
    {
        var tlv = Tlv8.Read(request);
        switch (tlv[TlvTag.SeqNo][0])
        {
            case 1:
                var x = SrpClient.ToBig(SrpClient.H(_salt, SrpClient.H(Encoding.UTF8.GetBytes($"Pair-Setup:{pin}"))));
                var n = SrpClient.GroupPrime;
                var g = SrpClient.Generator;
                _v = BigInteger.ModPow(g, x, n);
                var k = SrpClient.ToBig(SrpClient.H(SrpClient.Pad(n), SrpClient.Pad(g)));
                _paddedB = SrpClient.Pad(SrpClient.Mod((k * _v) + BigInteger.ModPow(g, _b, n), n));
                return Tlv8.Write((TlvTag.SeqNo, [2]), (TlvTag.Salt, _salt), (TlvTag.PublicKey, _paddedB));

            case 3:
                var a = tlv[TlvTag.PublicKey];
                var clientProof = tlv[TlvTag.Proof];
                var u = SrpClient.ToBig(SrpClient.H(SrpClient.Pad(SrpClient.ToBig(a)), _paddedB));
                var s = BigInteger.ModPow(SrpClient.ToBig(a) * BigInteger.ModPow(_v, u, SrpClient.GroupPrime), _b, SrpClient.GroupPrime);
                _sessionKey = SrpClient.H(SrpClient.Pad(s));

                var hn = SrpClient.H(SrpClient.GroupPrime.ToByteArray(true, true));
                var hg = SrpClient.H([0x05]);
                var xor = hn.Zip(hg, (p, q) => (byte)(p ^ q)).ToArray();
                var expected = SrpClient.H(xor, SrpClient.H(Encoding.UTF8.GetBytes("Pair-Setup")), _salt, a, _paddedB, _sessionKey);
                if (!expected.AsSpan().SequenceEqual(clientProof))
                {
                    return Tlv8.Write((TlvTag.SeqNo, [4]), (TlvTag.Error, [0x02]));
                }

                return Tlv8.Write((TlvTag.SeqNo, [4]), (TlvTag.Proof, SrpClient.H(a, clientProof, _sessionKey)));

            case 5:
                var encryptKey = HapCrypto.HkdfExpand("Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info", _sessionKey);
                var inner = Tlv8.Read(HapCrypto.Decrypt(encryptKey, HapCrypto.LabelNonce("PS-Msg05"), tlv[TlvTag.EncryptedData]));
                _controllerId = inner[TlvTag.Identifier];
                _controllerPublic = inner[TlvTag.PublicKey];
                var iosX = HapCrypto.HkdfExpand("Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info", _sessionKey);
                Assert.True(HapCrypto.Ed25519Verify(_controllerPublic, [.. iosX, .. _controllerId, .. _controllerPublic], inner[TlvTag.Signature]));
                if (inner.TryGetValue(TlvTag.Name, out var name))
                {
                    ControllerName = Opack.UnpackDictionary(name)["name"] as string;
                }

                var accX = HapCrypto.HkdfExpand("Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info", _sessionKey);
                var ltpk = HapCrypto.Ed25519PublicKey(_seed);
                var signature = HapCrypto.Ed25519Sign(_seed, [.. accX, .. Id, .. ltpk]);
                var payload = Tlv8.Write((TlvTag.Identifier, Id), (TlvTag.PublicKey, ltpk), (TlvTag.Signature, signature));
                return Tlv8.Write((TlvTag.SeqNo, [6]), (TlvTag.EncryptedData, HapCrypto.Encrypt(encryptKey, HapCrypto.LabelNonce("PS-Msg06"), payload)));

            default:
                throw new InvalidOperationException("Unexpected setup step.");
        }
    }

    public byte[] HandleVerify(byte[] request)
    {
        var tlv = Tlv8.Read(request);
        switch (tlv[TlvTag.SeqNo][0])
        {
            case 1:
                _controllerEphemeral = tlv[TlvTag.PublicKey];
                _ephemeral = new HapCrypto.X25519KeyPair();
                _shared = _ephemeral.SharedSecret(_controllerEphemeral);
                var key = HapCrypto.HkdfExpand("Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info", _shared);
                var signature = HapCrypto.Ed25519Sign(_seed, [.. _ephemeral.PublicKey, .. Id, .. _controllerEphemeral]);
                var inner = Tlv8.Write((TlvTag.Identifier, Id), (TlvTag.Signature, signature));
                return Tlv8.Write((TlvTag.SeqNo, [2]), (TlvTag.PublicKey, _ephemeral.PublicKey),
                    (TlvTag.EncryptedData, HapCrypto.Encrypt(key, HapCrypto.LabelNonce("PV-Msg02"), inner)));

            case 3:
                var verifyKey = HapCrypto.HkdfExpand("Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info", _shared!);
                var decrypted = Tlv8.Read(HapCrypto.Decrypt(verifyKey, HapCrypto.LabelNonce("PV-Msg03"), tlv[TlvTag.EncryptedData]));
                Assert.Equal(_controllerId, decrypted[TlvTag.Identifier]);
                Assert.True(HapCrypto.Ed25519Verify(_controllerPublic!,
                    [.. _controllerEphemeral!, .. _controllerId!, .. _ephemeral!.PublicKey], decrypted[TlvTag.Signature]));
                return Tlv8.Write((TlvTag.SeqNo, [4]));

            default:
                throw new InvalidOperationException("Unexpected verify step.");
        }
    }
}
