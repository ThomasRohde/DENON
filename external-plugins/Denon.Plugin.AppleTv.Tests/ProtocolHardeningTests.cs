using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Denon.Plugin.AppleTv.Protocol;
using Denon.Plugin.AppleTv.Protocol.AirPlay;
using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Hap;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

/// <summary>Malformed and hostile input to the shared Apple codecs (issue #35): each must fail cleanly.</summary>
public sealed class OpackHardeningTests
{
    [Theory]
    [InlineData(0xD1)] // list of one, nested
    [InlineData(0xDF)] // endless list, nested
    [InlineData(0xE1)] // dictionary of one, the key nested
    [InlineData(0xEF)] // endless dictionary, nested
    public void Deep_nesting_is_a_format_error_not_a_stack_overflow(byte tag)
    {
        var data = Enumerable.Repeat(tag, 200_000).ToArray();
        Assert.Throws<FormatException>(() => Opack.Unpack(data));
    }

    [Fact]
    public void Nesting_within_the_cap_still_unpacks()
    {
        byte[] data = [.. Enumerable.Repeat((byte)0xD1, Opack.MaxDepth), 0x08];
        var value = Opack.Unpack(data);
        for (var i = 0; i < Opack.MaxDepth; i++)
        {
            value = Assert.Single(Assert.IsType<List<object?>>(value));
        }

        Assert.Equal(0L, value);
    }

    [Theory]
    [InlineData("C4FFFFFFFF")] // reference 0xFFFFFFFF (negative as int)
    [InlineData("C105")] // reference past an empty table
    [InlineData("A0")] // reference 0 with an empty table
    public void Bad_references_are_format_errors(string hex)
    {
        Assert.Throws<FormatException>(() => Opack.Unpack(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("64FFFFFF7F61")] // string, 4-byte length int.MaxValue
    [InlineData("64FFFFFFFF61")] // string, 4-byte length that is negative as int
    [InlineData("94FFFFFFFFFFFFFFFF00")] // data, 8-byte length
    [InlineData("94000000800000000000")] // data, 8-byte length above int.MaxValue
    [InlineData("93FFFFFF7F00")] // data, 4-byte length int.MaxValue
    public void Huge_lengths_are_format_errors(string hex)
    {
        Assert.Throws<FormatException>(() => Opack.Unpack(Convert.FromHexString(hex)));
    }
}

public sealed class BinaryPlistHardeningTests
{
    /// <summary>Builds a bplist00 from encoded objects (object 0 first), with a trailer that can be bent.</summary>
    private static byte[] Plist(byte[][] objects, int offsetSize = 1, int refSize = 1, ulong? count = null, ulong top = 0, ulong? tableOffset = null)
    {
        var bytes = new List<byte>("bplist00"u8.ToArray());
        var offsets = new List<long>();
        foreach (var o in objects)
        {
            offsets.Add(bytes.Count);
            bytes.AddRange(o);
        }

        var table = (ulong)bytes.Count;
        foreach (var offset in offsets)
        {
            for (var b = offsetSize - 1; b >= 0; b--)
            {
                bytes.Add((byte)(offset >> (8 * b)));
            }
        }

        var trailer = new byte[32];
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), count ?? (ulong)objects.Length);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), top);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), tableOffset ?? table);
        bytes.AddRange(trailer);
        return [.. bytes];
    }

    private static byte[] Double(byte marker, double value)
    {
        var bytes = new byte[9];
        bytes[0] = marker;
        BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(1), value);
        return bytes;
    }

    [Fact]
    public void A_well_formed_minimal_plist_reads()
    {
        Assert.Equal(5L, BinaryPlist.Read(Plist([[0x10, 0x05]])));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(9, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 9)]
    public void Offset_and_reference_sizes_outside_1_to_8_are_rejected(int offsetSize, int refSize)
    {
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist([[0x10, 0x05]], offsetSize, refSize)));
    }

    [Fact]
    public void A_huge_object_count_is_rejected_before_any_allocation()
    {
        // 40 bytes that claim a billion objects (issue #35: this used to allocate gigabytes).
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist([[0x10, 0x05]], count: 0x3FFFFFFF)));
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist([[0x10, 0x05]], count: ulong.MaxValue)));
    }

    [Fact]
    public void A_table_or_root_outside_the_data_is_rejected()
    {
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist([[0x10, 0x05]], tableOffset: 4)));
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist([[0x10, 0x05]], tableOffset: 1_000_000)));
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist([[0x10, 0x05]], tableOffset: ulong.MaxValue)));
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist([[0x10, 0x05]], top: 1)));
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist([[0x10, 0x05]], count: 0)));
    }

    [Fact]
    public void An_offset_into_the_table_is_rejected()
    {
        // One object, but the offset table entry points at the table itself.
        var data = Plist([[0x10, 0x05]]);
        data[^33] = 10; // the single offset (1 byte) now points at the table start
        Assert.Throws<FormatException>(() => BinaryPlist.Read(data));
    }

    [Theory]
    [InlineData(new byte[] { 0x15, 1, 2, 3, 4, 5, 6, 7, 8 })] // integer of 32 bytes
    [InlineData(new byte[] { 0x14, 1, 2, 3 })] // 16-byte integer cut short
    [InlineData(new byte[] { 0x23, 1, 2 })] // double cut short
    [InlineData(new byte[] { 0x21, 1, 2 })] // real of a reserved size
    [InlineData(new byte[] { 0x5F, 0x13, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })] // ASCII, 8-byte count
    [InlineData(new byte[] { 0x6F, 0x12, 0x7F, 0xFF, 0xFF, 0xFF })] // UTF-16, count that overflows when doubled
    [InlineData(new byte[] { 0x4F, 0x22, 0x00 })] // data whose count is not an integer object
    [InlineData(new byte[] { 0xAF, 0x12, 0x7F, 0xFF, 0xFF, 0xFF })] // array of int.MaxValue references
    [InlineData(new byte[] { 0xDF, 0x12, 0x40, 0x00, 0x00, 0x00 })] // dictionary whose references run past the end
    [InlineData(new byte[] { 0xA1, 0x05 })] // array with a reference to a missing object
    [InlineData(new byte[] { 0x70 })] // unsupported type
    public void Malformed_objects_are_format_errors(byte[] root)
    {
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist([root])));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e300)]
    [InlineData(-1e300)]
    public void Dates_that_no_DateTime_can_hold_are_format_errors(double seconds)
    {
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist([Double(0x33, seconds)])));
    }

    [Fact]
    public void A_plausible_date_still_reads()
    {
        var date = Assert.IsType<DateTime>(BinaryPlist.Read(Plist([Double(0x33, 0)])));
        Assert.Equal(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc), date);
    }

    [Fact]
    public void A_self_referencing_array_hits_the_depth_cap()
    {
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist([[0xA1, 0x00]])));
    }

    [Fact]
    public void Shared_references_cannot_expand_into_a_huge_tree()
    {
        // Object i is [i+1, i+1]: 31 small objects that would decode into 2^31 values.
        var objects = Enumerable.Range(0, 30).Select(i => new byte[] { 0xA2, (byte)(i + 1), (byte)(i + 1) }).Append([0x10, 0x01]).ToArray();
        Assert.Throws<FormatException>(() => BinaryPlist.Read(Plist(objects)));
    }
}

public sealed class FramingHardeningTests
{
    private static byte[] Header(uint size)
    {
        var header = new byte[DataStreamChannel.HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, size);
        return header;
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(31u)]
    [InlineData((uint)DataStreamChannel.MaxFrameBytes + 1)]
    [InlineData(0x80000000u)]
    [InlineData(uint.MaxValue)]
    public void Data_stream_sizes_that_cannot_be_consumed_fail_the_stream(uint size)
    {
        Assert.Throws<InvalidDataException>(() => DataStreamChannel.CompleteFrameSize(Header(size)));
    }

    [Fact]
    public void Data_stream_frames_wait_for_all_their_bytes()
    {
        Assert.Equal(0, DataStreamChannel.CompleteFrameSize(new byte[10])); // header not complete
        Assert.Equal(0, DataStreamChannel.CompleteFrameSize(Header(40))); // body not complete
        Assert.Equal(32, DataStreamChannel.CompleteFrameSize(Header(32)));
        var frame = DataStreamChannel.EncodeFrame("sync", "comm", 1, [1, 2, 3]);
        Assert.Equal(frame.Length, DataStreamChannel.CompleteFrameSize([.. frame, 0xAA]));
    }

    [Fact]
    public void Protobuf_split_ignores_a_length_past_the_end()
    {
        // Length 0x7FFFFFFF (pos + length used to overflow and throw an ArgumentOutOfRangeException).
        Assert.Empty(DataStreamChannel.SplitProtobufs([0xFF, 0xFF, 0xFF, 0xFF, 0x07, 1, 2, 3]));
        Assert.Empty(DataStreamChannel.SplitProtobufs([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 1]));
    }

    [Theory]
    [InlineData("0A808080808020")] // field 1, length 2^40
    [InlineData("0AFFFFFFFF0701")] // field 1, length int.MaxValue, 1 byte present
    [InlineData("0AFFFFFFFFFFFFFFFFFF01")] // field 1, length ulong.MaxValue
    [InlineData("0A05010203")] // field 1, length 5, 3 bytes present
    [InlineData("08FFFFFFFFFFFFFFFFFFFF01")] // varint longer than 64 bits
    public void Protobuf_lengths_past_the_end_are_format_errors_not_overflows(string hex)
    {
        Assert.Throws<FormatException>(() => new ProtoReader(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("99999999999")]
    [InlineData("12abc")]
    [InlineData("16777217")] // one byte over the body cap
    public void Http_content_lengths_that_are_negative_or_huge_fail(string length)
    {
        var raw = Encoding.ASCII.GetBytes($"RTSP/1.0 200 OK\r\nContent-Length: {length}\r\n\r\n");
        Assert.Throws<InvalidDataException>(() => HttpMessage.TryParse(raw, out _));
    }

    [Fact]
    public void Http_headers_without_an_end_fail_once_they_pass_the_cap()
    {
        var endless = Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nX: " + new string('a', HttpMessage.MaxHeaderBytes));
        Assert.Throws<InvalidDataException>(() => HttpMessage.TryParse(endless, out _));
        Assert.Null(HttpMessage.TryParse(Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nCSeq: 1\r\n"), out _));
    }

    [Fact]
    public void Http_message_with_a_body_still_parses()
    {
        var raw = Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nCSeq: 3\r\nContent-Length: 2\r\n\r\nhi+");
        var message = HttpMessage.TryParse(raw, out var consumed);
        Assert.NotNull(message);
        Assert.Equal(200, message.StatusCode);
        Assert.Equal("hi"u8.ToArray(), message.Body);
        Assert.Equal(raw.Length - 1, consumed);
        Assert.Null(HttpMessage.TryParse(raw.AsSpan(0, raw.Length - 2), out _)); // body not complete
    }
}

public sealed class HapHardeningTests
{
    [Fact]
    public void Pair_setup_fails_when_the_m6_signature_does_not_verify()
    {
        var accessory = new FakeAccessory("1234") { CorruptM6Signature = true };
        var setup = new HapPairSetup("Denon Desktop");
        setup.ReadM2(accessory.HandleSetup(HapPairSetup.BuildM1()));
        setup.ReadM4(accessory.HandleSetup(setup.BuildM3("1234")));

        // No credentials come back, so there is nothing a caller could save.
        var ex = Assert.Throws<HapPairingException>(() => setup.ReadM6(accessory.HandleSetup(setup.BuildM5())));
        Assert.Contains("signature", ex.Message);
    }

    [Fact]
    public void Ed25519_verify_is_false_for_a_key_that_is_not_a_curve_point()
    {
        var notAPoint = Enumerable.Repeat((byte)0xFF, 32).ToArray();
        Assert.False(HapCrypto.Ed25519Verify(notAPoint, [1, 2, 3], new byte[64]));
    }

    [Fact]
    public void An_all_zero_x25519_secret_is_rejected()
    {
        // The zero point has low order: every private key gives an all-zero (predictable) secret with it.
        var pair = new HapCrypto.X25519KeyPair();
        Assert.Throws<HapPairingException>(() => pair.SharedSecret(new byte[32]));
    }

    [Fact]
    public void A_normal_x25519_exchange_still_agrees()
    {
        var a = new HapCrypto.X25519KeyPair();
        var b = new HapCrypto.X25519KeyPair();
        Assert.Equal(a.SharedSecret(b.PublicKey), b.SharedSecret(a.PublicKey));
    }
}

/// <summary>Channel close handling over loopback (issues #35 and #50).</summary>
public sealed class ChannelCloseTests
{
    [Fact]
    public async Task A_data_stream_frame_of_size_zero_closes_the_channel_instead_of_stalling()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var outKey = RandomNumberGenerator.GetBytes(32);
        var inKey = RandomNumberGenerator.GetBytes(32);
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(cts.Token);
            await subscribed.Task.WaitAsync(cts.Token);
            var session = new HapSession(inKey, outKey); // the peer's output is our input
            await peer.GetStream().WriteAsync(session.Encrypt(new byte[DataStreamChannel.HeaderLength]), cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ContinueWith(_ => { });
        });

        await using var channel = await DataStreamChannel.ConnectAsync("127.0.0.1", port, outKey, inKey, cts.Token);
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Closed += ex => closed.TrySetResult(ex);
        subscribed.SetResult();

        var reason = await closed.Task.WaitAsync(cts.Token);
        Assert.IsType<InvalidDataException>(reason);
        Assert.True(channel.IsClosed);
        cts.Cancel();
        listener.Stop();
        await server.ContinueWith(_ => { });
    }

    [Fact]
    public async Task A_close_before_subscribing_is_visible_through_IsClosed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(cts.Token); // accept, then close at once
        });

        await using var channel = await DataStreamChannel.ConnectAsync("127.0.0.1", port, new byte[32], new byte[32], cts.Token);
        while (!channel.IsClosed)
        {
            await Task.Delay(20, cts.Token);
        }

        // The event already fired; a late subscriber must rely on IsClosed (what MrpClient and the clients do).
        var fired = false;
        channel.Closed += _ => fired = true;
        Assert.True(channel.IsClosed);
        Assert.False(fired);
        listener.Stop();
        await server;
    }

    [Fact]
    public async Task A_companion_frame_over_24_bits_is_refused()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync(cts.Token).AsTask();

        await using var connection = await CompanionConnection.ConnectAsync("127.0.0.1", port, cts.Token);
        using var peer = await accept;
        await Assert.ThrowsAsync<CompanionProtocolException>(() =>
            connection.SendFrameAsync(CompanionFrameType.UOpack, new byte[CompanionConnection.MaxFrameLength + 1], cts.Token));
        await connection.SendFrameAsync(CompanionFrameType.NoOp, [], cts.Token); // the stream is still usable
        listener.Stop();
    }
}
