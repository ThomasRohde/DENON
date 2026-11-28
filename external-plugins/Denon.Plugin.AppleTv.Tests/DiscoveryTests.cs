using System.Net;
using System.Text;
using Denon.Plugin.AppleTv.Discovery;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

/// <summary>
/// Discovery tests. Fixtures copy the record layout an Apple TV 4K (tvOS 26) announces, with made-up
/// identifiers (no real MACs, keys or ids).
/// </summary>
public sealed class DiscoveryTests
{
    private static readonly IPAddress AppleTvIp = IPAddress.Parse("192.168.50.2");
    private static readonly IPAddress HomePodIp = IPAddress.Parse("192.168.50.7");

    [Fact]
    public void Query_asks_for_every_service_type_as_ptr()
    {
        var query = MdnsBrowser.BuildPtrQuery(AppleServiceTypes.All);
        Assert.Equal(AppleServiceTypes.All.Count, (query[4] << 8) | query[5]);
        Assert.Contains("_companion-link", Encoding.UTF8.GetString(query));
        Assert.Equal(12, query[^3]); // last question: type PTR
    }

    [Fact]
    public void Response_packet_yields_service_with_srv_txt_and_a_records()
    {
        var packet = new DnsPacketBuilder()
            .Ptr(AppleServiceTypes.Companion, "Living Room." + AppleServiceTypes.Companion)
            .Srv("Living Room." + AppleServiceTypes.Companion, "Living-Room.local", 49153)
            .Txt("Living Room." + AppleServiceTypes.Companion, "rpMd=AppleTV6,2", "rpVr=715.2", "rpMac=2")
            .A("Living-Room.local", AppleTvIp)
            .Build();

        var records = new MdnsBrowser.RecordSet(AppleServiceTypes.All);
        MdnsBrowser.ParsePacket(packet, IPAddress.Parse("192.168.50.99"), records);
        var service = Assert.Single(records.Build());

        Assert.Equal(AppleServiceTypes.Companion, service.Type);
        Assert.Equal("Living Room", service.InstanceLabel);
        Assert.Equal(49153, service.Port);
        Assert.Equal(AppleTvIp, service.Address); // A record wins over the sender address
        Assert.Equal("AppleTV6,2", service.Txt["rpMd"]);
    }

    [Fact]
    public void Unwanted_service_types_and_public_addresses_are_ignored()
    {
        var packet = new DnsPacketBuilder()
            .Ptr("_googlecast._tcp.local", "Chromecast._googlecast._tcp.local")
            .Ptr(AppleServiceTypes.AirPlay, "Far Away." + AppleServiceTypes.AirPlay)
            .Srv("Far Away." + AppleServiceTypes.AirPlay, "far.local", 7000)
            .A("far.local", IPAddress.Parse("8.8.8.8"))
            .Build();

        var records = new MdnsBrowser.RecordSet(AppleServiceTypes.All);
        MdnsBrowser.ParsePacket(packet, IPAddress.Parse("192.168.50.99"), records);
        Assert.Empty(records.Build());
    }

    [Fact]
    public void Txt_flag_keys_without_value_are_kept()
    {
        var data = new DnsPacketBuilder().TxtRdata("da=true", "flag", "osvers=26.6.1");
        var txt = MdnsBrowser.ParseTxt(data, 0, data.Length);
        Assert.Equal("true", txt["da"]);
        Assert.Equal(string.Empty, txt["flag"]);
        Assert.Equal("26.6.1", txt["osvers"]);
    }

    [Fact]
    public void Txt_empty_strings_do_not_hide_the_entries_after_them()
    {
        // RFC 6763: a zero-length string is valid; parsing must go on past it.
        var data = new DnsPacketBuilder().TxtRdata("da=true", "", "osvers=26.6.1");
        var txt = MdnsBrowser.ParseTxt(data, 0, data.Length);
        Assert.Equal("true", txt["da"]);
        Assert.Equal("26.6.1", txt["osvers"]);
    }

    [Theory]
    [InlineData(0x40)]
    [InlineData(0x80)]
    [InlineData(0x7F)]
    public void Reserved_label_types_fail_the_packet_instead_of_drifting(byte label)
    {
        var packet = new DnsPacketBuilder()
            .Ptr(AppleServiceTypes.Companion, "Living Room." + AppleServiceTypes.Companion)
            .Build();
        packet[12] = label; // the first answer's name now starts with a reserved label type

        var records = new MdnsBrowser.RecordSet(AppleServiceTypes.All);
        Assert.Throws<FormatException>(() => MdnsBrowser.ParsePacket(packet, IPAddress.Parse("192.168.50.99"), records));
        Assert.Empty(records.Build());
    }

    [Fact]
    public void Services_group_into_one_apple_tv_with_model_os_and_id()
    {
        var device = Assert.Single(AppleDeviceDiscovery.Group(AppleTvServices()));

        Assert.Equal("Living Room", device.Name);
        Assert.Equal(AppleDeviceKind.AppleTv, device.Kind);
        Assert.Equal("AppleTV6,2", device.ModelId);
        Assert.Equal("Apple TV 4K (1st generation)", device.ModelName);
        Assert.Equal("26.6.1", device.OsVersion);
        Assert.Equal("AA:BB:CC:00:11:22", device.DeviceId);
        Assert.True(device.HasCompanion);
        Assert.True(device.HasAirPlay);
    }

    [Fact]
    public void HomePods_and_other_airplay_receivers_are_not_apple_tvs()
    {
        var homePod = new MdnsService(AppleServiceTypes.AirPlay, "Kitchen." + AppleServiceTypes.AirPlay, "k.local", 7000,
            new Dictionary<string, string> { ["model"] = "AudioAccessory5,1" }, HomePodIp);
        var receiver = new MdnsService(AppleServiceTypes.AirPlay, "AVR." + AppleServiceTypes.AirPlay, "avr.local", 7000,
            new Dictionary<string, string> { ["model"] = "Denon AVR-X2400H", ["manufacturer"] = "Denon" }, IPAddress.Parse("192.168.50.3"));

        var devices = AppleDeviceDiscovery.Group([.. AppleTvServices(), homePod, receiver]);

        Assert.Equal(AppleDeviceKind.AppleTv, devices[0].Kind); // Apple TVs sort first
        Assert.Contains(devices, d => d.Kind == AppleDeviceKind.HomePod && d.ModelName == "HomePod mini");
        Assert.Contains(devices, d => d.Kind == AppleDeviceKind.Other && d.Name == "AVR");
    }

    [Fact]
    public void Raop_only_device_takes_its_name_after_the_at_sign()
    {
        var raop = new MdnsService(AppleServiceTypes.Raop, "AABBCC001122@Den." + AppleServiceTypes.Raop, "den.local", 7000,
            new Dictionary<string, string> { ["am"] = "AppleTV14,1" }, AppleTvIp);
        var device = Assert.Single(AppleDeviceDiscovery.Group([raop]));
        Assert.Equal("Den", device.Name);
        Assert.Equal("Apple TV 4K (3rd generation)", device.ModelName);
    }

    [Theory]
    [InlineData("AppleTV99,1", "Apple TV (AppleTV99,1)", "AppleTv")]
    [InlineData(null, "Unknown Apple device", "Other")]
    [InlineData("MacBookPro18,3", "MacBookPro18,3", "Other")]
    public void Unknown_models_still_classify(string? modelId, string name, string kind)
    {
        Assert.Equal(name, AppleModels.FriendlyName(modelId));
        Assert.Equal(kind, AppleModels.KindOf(modelId).ToString());
    }

    private static IReadOnlyList<MdnsService> AppleTvServices() =>
    [
        new(AppleServiceTypes.Companion, "Living Room." + AppleServiceTypes.Companion, "Living-Room.local", 49153,
            new Dictionary<string, string> { ["rpMd"] = "AppleTV6,2", ["rpVr"] = "715.2" }, AppleTvIp),
        new(AppleServiceTypes.AirPlay, "Living Room." + AppleServiceTypes.AirPlay, "Living-Room.local", 7000,
            new Dictionary<string, string>
            {
                ["model"] = "AppleTV6,2",
                ["osvers"] = "26.6.1",
                ["deviceid"] = "AA:BB:CC:00:11:22",
                ["acl"] = "0",
                ["features"] = "0x4A7FDFD5,0x3C175FDE",
            },
            AppleTvIp),
        new(AppleServiceTypes.Raop, "AABBCC001122@Living Room." + AppleServiceTypes.Raop, "Living-Room.local", 7000,
            new Dictionary<string, string> { ["am"] = "AppleTV6,2", ["ov"] = "26.6.1" }, AppleTvIp),
    ];

    /// <summary>Builds DNS response packets (uncompressed names) for parser tests.</summary>
    private sealed class DnsPacketBuilder
    {
        private readonly List<byte[]> _records = [];

        public DnsPacketBuilder Ptr(string name, string instance) => Add(name, 12, Name(instance));

        public DnsPacketBuilder Srv(string name, string target, int port) =>
            Add(name, 33, [0, 0, 0, 0, (byte)(port >> 8), (byte)port, .. Name(target)]);

        public DnsPacketBuilder Txt(string name, params string[] entries) => Add(name, 16, TxtRdata(entries));

        public DnsPacketBuilder A(string name, IPAddress address) => Add(name, 1, address.GetAddressBytes());

        public byte[] TxtRdata(params string[] entries)
        {
            var bytes = new List<byte>();
            foreach (var e in entries)
            {
                var b = Encoding.UTF8.GetBytes(e);
                bytes.Add((byte)b.Length);
                bytes.AddRange(b);
            }

            return [.. bytes];
        }

        public byte[] Build()
        {
            var packet = new List<byte> { 0, 0, 0x84, 0, 0, 0, (byte)(_records.Count >> 8), (byte)_records.Count, 0, 0, 0, 0 };
            foreach (var r in _records)
            {
                packet.AddRange(r);
            }

            return [.. packet];
        }

        private DnsPacketBuilder Add(string name, int type, byte[] rdata)
        {
            _records.Add([.. Name(name), 0, (byte)type, 0x80, 1, 0, 0, 0x11, 0x94, (byte)(rdata.Length >> 8), (byte)rdata.Length, .. rdata]);
            return this;
        }

        private static byte[] Name(string name)
        {
            var bytes = new List<byte>();
            foreach (var label in name.Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                bytes.Add((byte)b.Length);
                bytes.AddRange(b);
            }

            bytes.Add(0);
            return [.. bytes];
        }
    }
}
