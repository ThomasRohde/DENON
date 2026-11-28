using System.IO;
using System.Net;
using System.Text;
using Denon.Plugin.Chromecast.Cast;
using Xunit;

namespace Denon.Plugin.Chromecast.Tests;

public sealed class MdnsCastDiscoveryTests
{
    private const string Service = "_googlecast._tcp.local";
    private const string Instance = "Chromecast-abc123._googlecast._tcp.local";
    private const string Target = "abc123.local";
    private static readonly IPAddress Sender = IPAddress.Parse("192.168.1.30");

    [Fact]
    public void A_full_answer_gives_one_device()
    {
        var packet = new DnsPacket()
            .Answer(DnsPacket.Name(Service), 12, DnsPacket.Name(Instance))
            .Answer(DnsPacket.Name(Instance), 33, DnsPacket.Srv(8009, Target))
            .Answer(DnsPacket.Name(Instance), 16, DnsPacket.Txt("id=abc123", "fn=Stuen 2", "md=Chromecast"))
            .Answer(DnsPacket.Name(Target), 1, [192, 168, 1, 20])
            .Build();

        var device = Assert.Single(Parse(packet));

        Assert.Equal(new DiscoveredCastDevice("abc123", "Stuen 2", "Chromecast", "192.168.1.20", 8009), device);
    }

    [Fact]
    public void A_zero_length_txt_string_is_skipped_and_the_rest_is_read()
    {
        var packet = new DnsPacket()
            .Answer(DnsPacket.Name(Service), 12, DnsPacket.Name(Instance))
            .Answer(DnsPacket.Name(Instance), 16, DnsPacket.Txt("", "id=abc123", "", "fn=Køkken"))
            .Build();

        var device = Assert.Single(Parse(packet));

        Assert.Equal("abc123", device.Id);
        Assert.Equal("Køkken", device.Name);
        Assert.Equal("192.168.1.30", device.Host); // No SRV/A record: the sender's address.
        Assert.Equal(8009, device.Port);
    }

    [Fact]
    public void A_txt_string_past_the_record_end_stops_the_txt_but_keeps_earlier_keys()
    {
        var txt = DnsPacket.Txt("id=abc123").Concat(new byte[] { 40, (byte)'f', (byte)'n' }).ToArray();
        var packet = new DnsPacket()
            .Answer(DnsPacket.Name(Service), 12, DnsPacket.Name(Instance))
            .Answer(DnsPacket.Name(Instance), 16, txt)
            .Build();

        var device = Assert.Single(Parse(packet));

        Assert.Equal("abc123", device.Id);
        Assert.Equal("Chromecast-abc123", device.Name); // No fn: the first label of the instance.
    }

    [Fact]
    public void Compressed_names_are_followed()
    {
        // The PTR target points back at the instance name written in the SRV record before it.
        var builder = new DnsPacket();
        var instanceOffset = builder.NextNameOffset;
        builder.Answer(DnsPacket.Name(Instance), 33, DnsPacket.Srv(8010, Target));
        builder.Answer(DnsPacket.Name(Service), 12, DnsPacket.Pointer(instanceOffset));

        var device = Assert.Single(Parse(builder.Build()));

        Assert.Equal(Instance, device.Id);
        Assert.Equal(8010, device.Port);
    }

    [Fact]
    public void ReadName_reads_labels_and_moves_past_the_name()
    {
        var data = DnsPacket.Name("a.bc.local").Concat(new byte[] { 0xEE }).ToArray();
        var pos = 0;

        Assert.Equal("a.bc.local", MdnsCastDiscovery.ReadName(data, ref pos));
        Assert.Equal(data.Length - 1, pos);
    }

    [Fact]
    public void ReadName_after_a_pointer_continues_after_the_pointer()
    {
        byte[] data = [5, .. "local"u8, 0, 3, .. "abc"u8, 0xC0, 0x00, 0xEE];
        var pos = 7;

        Assert.Equal("abc.local", MdnsCastDiscovery.ReadName(data, ref pos));
        Assert.Equal(13, pos);
    }

    [Fact]
    public void ReadName_stops_on_a_pointer_loop()
    {
        byte[] data = [0xC0, 0x00];
        var pos = 0;

        Assert.Equal(string.Empty, MdnsCastDiscovery.ReadName(data, ref pos));
        Assert.Equal(2, pos);
    }

    [Theory]
    [InlineData(0x40)]
    [InlineData(0x80)]
    [InlineData(0xBF)]
    public void ReadName_refuses_reserved_label_types(byte lengthByte)
    {
        byte[] data = [lengthByte, (byte)'a', (byte)'b', 0];
        var pos = 0;

        Assert.Throws<InvalidDataException>(() => MdnsCastDiscovery.ReadName(data, ref pos));
    }

    [Fact]
    public void A_reserved_label_drops_the_rest_of_the_packet_without_an_exception()
    {
        var packet = new DnsPacket()
            .Answer(DnsPacket.Name(Service), 12, DnsPacket.Name(Instance))
            .Answer([0x41, (byte)'x', 0], 16, DnsPacket.Txt("id=other"))
            .Build();
        var records = new MdnsCastDiscovery.RecordSet();

        MdnsCastDiscovery.ParsePacket(packet, Sender, records);

        Assert.Contains(Instance, records.Instances); // Read before the bad name.
        Assert.Empty(records.Texts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(40)]
    public void A_truncated_packet_does_not_throw(int length)
    {
        var packet = new DnsPacket()
            .Answer(DnsPacket.Name(Service), 12, DnsPacket.Name(Instance))
            .Answer(DnsPacket.Name(Instance), 33, DnsPacket.Srv(8009, Target))
            .Build();

        var records = new MdnsCastDiscovery.RecordSet();
        MdnsCastDiscovery.ParsePacket(packet[..Math.Min(length, packet.Length)], Sender, records);

        Assert.Empty(records.Services);
    }

    [Fact]
    public void A_record_length_past_the_packet_end_is_ignored()
    {
        var packet = new DnsPacket()
            .Answer(DnsPacket.Name(Service), 12, DnsPacket.Name(Instance))
            .Build();
        packet[^(DnsPacket.Name(Instance).Length + 1)] = 0xFF; // Low byte of the PTR's data length.

        var records = new MdnsCastDiscovery.RecordSet();
        MdnsCastDiscovery.ParsePacket(packet, Sender, records);

        Assert.Empty(records.Instances);
    }

    [Fact]
    public void A_speaker_group_is_left_out()
    {
        var packet = new DnsPacket()
            .Answer(DnsPacket.Name(Service), 12, DnsPacket.Name(Instance))
            .Answer(DnsPacket.Name(Instance), 16, DnsPacket.Txt("id=grp", "md=Google Cast Group"))
            .Build();

        Assert.Empty(Parse(packet));
    }

    [Fact]
    public void A_device_with_a_public_address_is_left_out()
    {
        var packet = new DnsPacket()
            .Answer(DnsPacket.Name(Service), 12, DnsPacket.Name(Instance))
            .Answer(DnsPacket.Name(Instance), 33, DnsPacket.Srv(8009, Target))
            .Answer(DnsPacket.Name(Target), 1, [8, 8, 8, 8])
            .Build();

        Assert.Empty(Parse(packet));
    }

    [Fact]
    public void A_ptr_for_another_service_is_not_a_cast_device()
    {
        var packet = new DnsPacket()
            .Answer(DnsPacket.Name("_airplay._tcp.local"), 12, DnsPacket.Name("TV._airplay._tcp.local"))
            .Build();

        Assert.Empty(Parse(packet));
    }

    [Fact]
    public void BuildPtrQuery_writes_one_ptr_question()
    {
        var query = MdnsCastDiscovery.BuildPtrQuery(Service);

        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, query[..12]);
        var pos = 12;
        Assert.Equal(Service, MdnsCastDiscovery.ReadName(query, ref pos));
        Assert.Equal(new byte[] { 0, 12, 0, 1 }, query[pos..]);
    }

    private static IReadOnlyList<DiscoveredCastDevice> Parse(byte[] packet)
    {
        var records = new MdnsCastDiscovery.RecordSet();
        MdnsCastDiscovery.ParsePacket(packet, Sender, records);
        return records.Build();
    }

    /// <summary>Builds a DNS response with answer records only.</summary>
    private sealed class DnsPacket
    {
        private readonly List<byte> _body = [];
        private int _answers;

        /// <summary>Offset in the finished packet where the next record's name starts.</summary>
        public int NextNameOffset => 12 + _body.Count;

        public static byte[] Name(string name)
        {
            var bytes = new List<byte>();
            foreach (var label in name.Split('.'))
            {
                var data = Encoding.UTF8.GetBytes(label);
                bytes.Add((byte)data.Length);
                bytes.AddRange(data);
            }

            bytes.Add(0);
            return [.. bytes];
        }

        public static byte[] Pointer(int offset) => [(byte)(0xC0 | (offset >> 8)), (byte)offset];

        public static byte[] Srv(int port, string target) =>
            [0, 0, 0, 0, (byte)(port >> 8), (byte)port, .. Name(target)];

        public static byte[] Txt(params string[] entries)
        {
            var bytes = new List<byte>();
            foreach (var entry in entries)
            {
                var data = Encoding.UTF8.GetBytes(entry);
                bytes.Add((byte)data.Length);
                bytes.AddRange(data);
            }

            return [.. bytes];
        }

        public DnsPacket Answer(byte[] name, int type, byte[] data)
        {
            _body.AddRange(name);
            _body.AddRange([(byte)(type >> 8), (byte)type, 0x80, 0x01, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length]);
            _body.AddRange(data);
            _answers++;
            return this;
        }

        public byte[] Build() =>
            [0, 0, 0x84, 0x00, 0, 0, (byte)(_answers >> 8), (byte)_answers, 0, 0, 0, 0, .. _body];
    }
}
