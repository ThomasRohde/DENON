using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Denon.Plugin.AppleTv.Protocol;
using Denon.Plugin.AppleTv.Protocol.Hap;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

public sealed class HapPairingTests
{
    [Fact]
    public void Tlv8_splits_long_values_and_joins_them_again()
    {
        var big = RandomNumberGenerator.GetBytes(600);
        var bytes = Tlv8.Write((TlvTag.PublicKey, big), (TlvTag.SeqNo, [0x02]));

        Assert.Equal(600 + (3 * 2) + 3, bytes.Length); // three chunks of the key + one seq item
        var tlv = Tlv8.Read(bytes);
        Assert.Equal(big, tlv[TlvTag.PublicKey]);
        Assert.Equal([0x02], tlv[TlvTag.SeqNo]);
    }

    [Fact]
    public void Tlv8_error_becomes_a_readable_exception()
    {
        var ex = Assert.Throws<HapPairingException>(() => Tlv8.ReadChecked(Tlv8.Write((TlvTag.SeqNo, [0x04]), (TlvTag.Error, [0x02]))));
        Assert.Contains("wrong PIN", ex.Message);
        Assert.Equal((byte)0x02, ex.ErrorCode);

        var backoff = Assert.Throws<HapPairingException>(() =>
            Tlv8.ReadChecked(Tlv8.Write((TlvTag.Error, [0x03]), (TlvTag.BackOff, [0x2C, 0x01]))));
        Assert.Contains("retry in 300 s", backoff.Message);
    }

    [Fact]
    public void Label_nonce_is_left_padded_to_12_bytes()
    {
        Assert.Equal([0, 0, 0, 0, .. Encoding.ASCII.GetBytes("PS-Msg05")], HapCrypto.LabelNonce("PS-Msg05"));
    }

    [Fact]
    public void Credentials_round_trip_in_pyatv_format()
    {
        var creds = new HapCredentials([1, 2], [3, 4], Encoding.ASCII.GetBytes("atv"), Encoding.ASCII.GetBytes("client"));
        Assert.Equal("0102:0304:617476:636c69656e74", creds.ToString());
        var parsed = HapCredentials.Parse(creds.ToString());
        Assert.Equal(creds.AtvId, parsed.AtvId);
        Assert.Equal(creds.ClientSeed, parsed.ClientSeed);
        Assert.Throws<FormatException>(() => HapCredentials.Parse("aa:bb"));
    }

    [Fact]
    public void Pair_setup_and_pair_verify_work_against_a_hap_accessory()
    {
        var accessory = new FakeAccessory("1234");
        var setup = new HapPairSetup("Denon Desktop");

        setup.ReadM2(accessory.HandleSetup(HapPairSetup.BuildM1()));
        setup.ReadM4(accessory.HandleSetup(setup.BuildM3("1234")));
        var credentials = setup.ReadM6(accessory.HandleSetup(setup.BuildM5()));

        Assert.True(setup.AccessorySignatureVerified);
        Assert.Equal("Denon Desktop", accessory.ControllerName);
        Assert.Equal(accessory.Id, credentials.AtvId);
        Assert.Equal(setup.PairingId, credentials.ClientId);

        // Later session: verify with the stored credentials, then both sides derive the same keys.
        var restored = HapCredentials.Parse(credentials.ToString());
        var verify = new HapPairVerify(restored);
        var m3 = verify.ReadM2BuildM3(accessory.HandleVerify(verify.BuildM1()));
        HapPairVerify.ReadM4(accessory.HandleVerify(m3));

        var (output, input) = verify.DeriveKeys("", "ClientEncrypt-main", "ServerEncrypt-main");
        Assert.Equal(accessory.Key("ClientEncrypt-main"), output);
        Assert.Equal(accessory.Key("ServerEncrypt-main"), input);
    }

    [Fact]
    public void Wrong_pin_is_reported_by_the_accessory()
    {
        var accessory = new FakeAccessory("1234");
        var setup = new HapPairSetup();
        setup.ReadM2(accessory.HandleSetup(HapPairSetup.BuildM1()));

        var ex = Assert.Throws<HapPairingException>(() => setup.ReadM4(accessory.HandleSetup(setup.BuildM3("9999"))));
        Assert.Equal((byte)0x02, ex.ErrorCode);
    }

    [Fact]
    public void Verify_rejects_a_different_apple_tv()
    {
        var paired = new FakeAccessory("1111");
        var setup = new HapPairSetup();
        setup.ReadM2(paired.HandleSetup(HapPairSetup.BuildM1()));
        setup.ReadM4(paired.HandleSetup(setup.BuildM3("1111")));
        var credentials = setup.ReadM6(paired.HandleSetup(setup.BuildM5()));

        var impostor = new FakeAccessory("1111");
        var verify = new HapPairVerify(credentials);
        Assert.Throws<HapPairingException>(() => verify.ReadM2BuildM3(impostor.HandleVerify(verify.BuildM1())));
    }
}
