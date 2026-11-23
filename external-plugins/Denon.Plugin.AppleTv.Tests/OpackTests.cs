using Denon.Plugin.AppleTv.Protocol;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

/// <summary>Byte vectors from pyatv's tests/support/test_opack.py (MIT).</summary>
public sealed class OpackTests
{
    private static byte[] B(params int[] bytes) => bytes.Select(b => (byte)b).ToArray();

    private static byte[] Repeat(int value, int count) => Enumerable.Repeat((byte)value, count).ToArray();

    [Fact]
    public void Packs_scalars()
    {
        Assert.Equal(B(0x01), Opack.Pack(true));
        Assert.Equal(B(0x02), Opack.Pack(false));
        Assert.Equal(B(0x04), Opack.Pack(null));
        Assert.Equal(B(0x05, 0x12, 0x34, 0x56, 0x78, 0x12, 0x34, 0x56, 0x78, 0x12, 0x34, 0x56, 0x78, 0x12, 0x34, 0x56, 0x78),
            Opack.Pack(Guid.Parse("12345678-1234-5678-1234-567812345678")));
        Assert.Equal(B(0x36, 0, 0, 0, 0, 0, 0, 0xF0, 0x3F), Opack.Pack(1.0));
    }

    [Fact]
    public void Packs_integers_by_size()
    {
        Assert.Equal(B(0x08), Opack.Pack(0));
        Assert.Equal(B(0x17), Opack.Pack(0xF));
        Assert.Equal(B(0x2F), Opack.Pack(0x27));
        Assert.Equal(B(0x30, 0x28), Opack.Pack(0x28));
        Assert.Equal(B(0x31, 0xFF, 0x01), Opack.Pack(0x1FF));
        Assert.Equal(B(0x32, 0xFF, 0xFF, 0xFF, 0x01), Opack.Pack(0x1FFFFFF));
        Assert.Equal(B(0x33, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01), Opack.Pack(0x1FFFFFFFFFFFFFFL));
        Assert.Throws<NotSupportedException>(() => Opack.Pack(-1));
    }

    [Fact]
    public void Packs_strings_and_bytes()
    {
        Assert.Equal(B(0x41, 0x61), Opack.Pack("a"));
        Assert.Equal(B(0x43, 0x61, 0x62, 0x63), Opack.Pack("abc"));
        Assert.Equal([0x60, .. Repeat(0x61, 0x20)], Opack.Pack(new string('a', 0x20)));
        Assert.Equal([0x61, 0x21, .. Repeat(0x61, 33)], Opack.Pack(new string('a', 33)));
        Assert.Equal([0x62, 0x00, 0x01, .. Repeat(0x61, 256)], Opack.Pack(new string('a', 256)));

        Assert.Equal(B(0x71, 0xAC), Opack.Pack(B(0xAC)));
        Assert.Equal(B(0x73, 0x12, 0x34, 0x56), Opack.Pack(B(0x12, 0x34, 0x56)));
        Assert.Equal([0x90, .. Repeat(0xAD, 0x20)], Opack.Pack(Repeat(0xAD, 0x20)));
        Assert.Equal([0x91, 0x21, .. Repeat(0x61, 33)], Opack.Pack(Repeat(0x61, 33)));
        Assert.Equal([0x92, 0x00, 0x01, .. Repeat(0x61, 256)], Opack.Pack(Repeat(0x61, 256)));
        Assert.Equal([0x93, 0x00, 0x00, 0x01, 0x00, .. Repeat(0x61, 65536)], Opack.Pack(Repeat(0x61, 65536)));
    }

    [Fact]
    public void Packs_containers_and_references()
    {
        Assert.Equal(B(0xD0), Opack.Pack(new List<object?>()));
        Assert.Equal(B(0xD3, 0x09, 0x44, 0x74, 0x65, 0x73, 0x74, 0x02), Opack.Pack(new List<object?> { 1, "test", false }));
        Assert.Equal(B(0xD1, 0xD1, 0x01), Opack.Pack(new List<object?> { new List<object?> { true } }));
        Assert.Equal([0xDF, 0x41, 0x61, .. Repeat(0xA0, 14), 0x03], Opack.Pack(Enumerable.Repeat<object?>("a", 15).ToList()));

        Assert.Equal(B(0xE0), Opack.Pack(new Dictionary<object, object?>()));
        Assert.Equal(B(0xE2, 0x41, 0x61, 0x14, 0x02, 0x04), Opack.Pack(new Dictionary<object, object?> { ["a"] = 12, [false] = null }));
        Assert.Equal(B(0xE1, 0x01, 0xE1, 0x41, 0x61, 0x0A),
            Opack.Pack(new Dictionary<object, object?> { [true] = new Dictionary<object, object?> { ["a"] = 2 } }));

        Assert.Equal(B(0xD2, 0x41, 0x61, 0xA0), Opack.Pack(new List<object?> { "a", "a" }));
        Assert.Equal(B(0xD4, 0x43, 0x66, 0x6F, 0x6F, 0x43, 0x62, 0x61, 0x72, 0xA0, 0xA1),
            Opack.Pack(new List<object?> { "foo", "bar", "foo", "bar" }));
        Assert.Equal(B(0xE3, 0x41, 0x61, 0x41, 0x62, 0x41, 0x63, 0xE1, 0x41, 0x64, 0xA0, 0xA3, 0x01),
            Opack.Pack(new Dictionary<object, object?> { ["a"] = "b", ["c"] = new Dictionary<object, object?> { ["d"] = "a" }, ["d"] = true }));
    }

    [Fact]
    public void Packs_endless_dictionary()
    {
        var dict = new Dictionary<object, object?>();
        for (var x = 97; x < 127; x += 2)
        {
            dict[((char)x).ToString()] = ((char)(x + 1)).ToString();
        }

        var expected = new List<byte> { 0xEF };
        for (var x = 97; x < 127; x++)
        {
            expected.Add(0x41);
            expected.Add((byte)x);
        }

        expected.Add(0x03);
        Assert.Equal(expected.ToArray(), Opack.Pack(dict));
    }

    [Fact]
    public void Uses_one_byte_uid_after_33_references()
    {
        // 257 distinct one-byte values, then the same again: references 0..32 are 0xA0..0xC0, then 0xC1 <uid>.
        var items = Enumerable.Range(0, 257).Select(x => (object?)System.Text.Encoding.UTF8.GetBytes(((char)x).ToString())).ToList();
        var packed = Opack.Pack(items.Concat(items).ToList());
        var unpacked = Assert.IsType<List<object?>>(Opack.Unpack(packed));
        Assert.Equal(514, unpacked.Count);
        Assert.Equal(items[256], unpacked[513]);
        Assert.Contains((byte)0xC1, packed);
        Assert.Equal(B(0xC2, 0x00, 0x01, 0x03), packed[^4..]);
    }

    [Fact]
    public void Unpacks_scalars_and_sized_values()
    {
        Assert.Equal(true, Opack.Unpack(B(0x01)));
        Assert.Equal(false, Opack.Unpack(B(0x02)));
        Assert.Null(Opack.Unpack(B(0x04)));
        Assert.Equal(1L, Opack.Unpack(B(0x06, 0x01, 0, 0, 0, 0, 0, 0, 0)));
        Assert.Equal(0x27L, Opack.Unpack(B(0x2F)));
        Assert.Equal(0x1FFL, Opack.Unpack(B(0x31, 0xFF, 0x01)));
        Assert.Equal(1.0, Opack.Unpack(B(0x35, 0x00, 0x00, 0x80, 0x3F)));
        Assert.Equal(1.0, Opack.Unpack(B(0x36, 0, 0, 0, 0, 0, 0, 0xF0, 0x3F)));
        Assert.Equal(new string('a', 33), Opack.Unpack([0x61, 0x21, .. Repeat(0x61, 33)]));
        Assert.Equal(Repeat(0x61, 256), Opack.Unpack([0x92, 0x00, 0x01, .. Repeat(0x61, 256)]));
        Assert.Throws<FormatException>(() => Opack.Unpack(B(0x00)));
    }

    [Fact]
    public void Unpacks_endless_lists_and_uids()
    {
        byte[] list1 = [0xDF, 0x41, 0x61, .. Repeat(0xA0, 15), 0x03];
        byte[] list2 = [0xDF, 0x41, 0x62, .. Repeat(0xA1, 15), 0x03];
        var both = Assert.IsType<List<object?>>(Opack.Unpack([0xD2, .. list1, .. list2]));
        Assert.All(Assert.IsType<List<object?>>(both[0]), v => Assert.Equal("a", v));
        Assert.All(Assert.IsType<List<object?>>(both[1]), v => Assert.Equal("b", v));
        Assert.Equal(16, ((List<object?>)both[1]!).Count);

        foreach (var uid in new[] { B(0xC1, 0x01), B(0xC2, 0x01, 0x00), B(0xC3, 0x01, 0x00, 0x00), B(0xC4, 0x01, 0x00, 0x00, 0x00) })
        {
            var list = Assert.IsType<List<object?>>(Opack.Unpack([0xDF, 0x30, 0x01, 0x30, 0x02, .. uid, 0x03]));
            Assert.Equal(new object?[] { 1L, 2L, 2L }, list);
        }
    }

    [Fact]
    public void Companion_message_round_trips()
    {
        var message = new Dictionary<string, object?>
        {
            ["_i"] = "_systemInfo",
            ["_x"] = 1254122577,
            ["_btHP"] = false,
            ["_c"] = new Dictionary<string, object?>
            {
                ["_pubID"] = "AA:BB:CC:DD:EE:FF",
                ["_sv"] = "230.1",
                ["_siriInfo"] = new Dictionary<string, object?> { ["collectorElectionVersion"] = 1.0, ["sharedDataProtoBuf"] = Repeat(0x08, 512) },
                ["_stA"] = new List<object?> { "com.apple.LiveAudio", "com.apple.siri.wakeup", "com.apple.LiveAudio" },
                ["_hkUID"] = new List<object?> { Guid.Parse("17ed160a-81f8-4488-962c-6b1a83eb0081") },
                ["model"] = "iPhone10,6",
            },
            ["_t"] = 2,
        };

        var back = Opack.UnpackDictionary(Opack.Pack(message));
        Assert.Equal("_systemInfo", back["_i"]);
        Assert.Equal(1254122577L, back["_x"]);
        var content = Assert.IsType<Dictionary<object, object?>>(back["_c"]);
        Assert.Equal(new object?[] { "com.apple.LiveAudio", "com.apple.siri.wakeup", "com.apple.LiveAudio" }, Assert.IsType<List<object?>>(content["_stA"]));
        Assert.Equal(Guid.Parse("17ed160a-81f8-4488-962c-6b1a83eb0081"), Assert.IsType<List<object?>>(content["_hkUID"])[0]);
        Assert.Equal(Repeat(0x08, 512), Assert.IsType<Dictionary<object, object?>>(content["_siriInfo"])["sharedDataProtoBuf"]);
    }
}
