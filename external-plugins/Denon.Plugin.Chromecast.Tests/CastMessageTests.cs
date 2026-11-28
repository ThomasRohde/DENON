using System.IO;
using Denon.Plugin.Chromecast.Cast;
using Xunit;

namespace Denon.Plugin.Chromecast.Tests;

public sealed class CastMessageTests
{
    [Fact]
    public void Encode_then_decode_gives_the_same_message()
    {
        var message = new CastMessage("sender-denon", "receiver-0", CastNamespaces.Receiver, "{\"type\":\"GET_STATUS\",\"requestId\":1}");

        var decoded = CastMessage.Decode(message.Encode());

        Assert.Equal(message, decoded);
    }

    [Fact]
    public void Encode_writes_the_cast_v2_fields_in_order()
    {
        var bytes = new CastMessage("a", "b", "c", "d").Encode();

        // 1: version 0, 2: "a", 3: "b", 4: "c", 5: payload type 0 (STRING), 6: "d".
        Assert.Equal(
            new byte[] { 0x08, 0, 0x12, 1, (byte)'a', 0x1A, 1, (byte)'b', 0x22, 1, (byte)'c', 0x28, 0, 0x32, 1, (byte)'d' },
            bytes);
    }

    [Fact]
    public void A_long_payload_uses_a_multi_byte_length()
    {
        var payload = new string('x', 300);

        var bytes = new CastMessage("s", "d", "n", payload).Encode();

        Assert.Equal(payload, CastMessage.Decode(bytes).PayloadUtf8);
        Assert.Contains(bytes.Skip(1).Zip(bytes.Skip(2)), pair => pair is (0xAC, 0x02)); // 300 as a varint.
    }

    [Fact]
    public void Decode_reads_utf8_text()
    {
        var message = new CastMessage("s", "d", "n", "{\"title\":\"Søren på TV 2 – ♫\"}");

        Assert.Equal(message.PayloadUtf8, CastMessage.Decode(message.Encode()).PayloadUtf8);
    }

    [Fact]
    public void Decode_skips_unknown_fields_of_every_supported_wire_type()
    {
        byte[] data =
        [
            0x38, 0x96, 0x01, // field 7, varint 150
            0x41, 1, 2, 3, 4, 5, 6, 7, 8, // field 8, fixed64
            0x4D, 1, 2, 3, 4, // field 9, fixed32
            0x52, 2, (byte)'z', (byte)'z', // field 10, bytes
            0x22, 1, (byte)'n', // field 4: namespace
            0x32, 2, (byte)'{', (byte)'}', // field 6: payload
        ];

        var message = CastMessage.Decode(data);

        Assert.Equal("n", message.Namespace);
        Assert.Equal("{}", message.PayloadUtf8);
        Assert.Equal(string.Empty, message.SourceId);
    }

    [Fact]
    public void Decode_of_an_empty_frame_gives_empty_fields()
    {
        Assert.Equal(new CastMessage("", "", "", ""), CastMessage.Decode([]));
    }

    [Theory]
    [InlineData(new byte[] { 0x32, 5, (byte)'a' })] // length past the end
    [InlineData(new byte[] { 0x32 })] // length missing
    [InlineData(new byte[] { 0x08, 0x80 })] // varint cut off
    [InlineData(new byte[] { 0x41, 1, 2, 3 })] // fixed64 cut off
    [InlineData(new byte[] { 0x4D, 1 })] // fixed32 cut off
    [InlineData(new byte[] { 0x0B })] // wire type 3 (group) is not supported
    [InlineData(new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })] // varint too long
    public void Decode_refuses_malformed_frames(byte[] data)
    {
        Assert.Throws<InvalidDataException>(() => CastMessage.Decode(data));
    }
}
