using System.Security.Cryptography;
using Denon.Plugin.AppleTv.Protocol;
using Denon.Plugin.AppleTv.Protocol.AirPlay;
using Denon.Plugin.AppleTv.Protocol.Hap;
using Denon.Plugin.AppleTv.Protocol.Mrp;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

public sealed class AirPlayTunnelTests
{
    /// <summary>Written by Python's plistlib (FMT_BINARY) from a dict like the RTSP SETUP replies.</summary>
    private const string PythonPlistHex =
        "62706c6973743030d80102030405060708090a0b0c0d0e0f1053626967546461746155656d707479596576656e74506f7274546e616d65536e656754726174655773747265616d7313000000010000000043010203a011c82266004b00f8006b006b0065006e13fffffffffffffffd233ff8000000000000a111d41213141516171819596368616e6e656c4944547365656454747970655f101477616e7473446564696361746564536f636b6574574142432d4445461300000b3a73ce2ff210820908191d222832373b404851555659666f787a838d9297aeb6bfc10000000000000101000000000000001a000000000000000000000000000000c2";

    [Fact]
    public void Reads_a_plist_written_by_python_plistlib()
    {
        var dict = BinaryPlist.ReadDictionary(Convert.FromHexString(PythonPlistHex));

        Assert.Equal(51234L, dict["eventPort"]);
        Assert.Equal("Køkken", dict["name"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, dict["data"]);
        Assert.Equal(1.5, dict["rate"]);
        Assert.Equal(-3L, dict["neg"]);
        Assert.Equal(4294967296L, dict["big"]);
        Assert.Empty(Assert.IsType<List<object?>>(dict["empty"]));
        var stream = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<List<object?>>(dict["streams"])[0]);
        Assert.Equal(130L, stream["type"]);
        Assert.Equal(12345678901234L, stream["seed"]);
        Assert.Equal(true, stream["wantsDedicatedSocket"]);
    }

    [Fact]
    public void Plist_round_trips_setup_bodies()
    {
        var body = new Dictionary<string, object?>
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?> { ["controlType"] = 2, ["seed"] = (ulong)long.MaxValue, ["type"] = 130, ["wantsDedicatedSocket"] = true },
            },
            ["name"] = "Denon Desktop – Stue",
            ["params"] = new Dictionary<string, object?> { ["data"] = RandomNumberGenerator.GetBytes(300) },
            ["isRemoteControlOnly"] = true,
        };

        var back = BinaryPlist.ReadDictionary(BinaryPlist.Write(body));
        Assert.Equal("Denon Desktop – Stue", back["name"]);
        Assert.Equal(true, back["isRemoteControlOnly"]);
        var stream = (Dictionary<string, object?>)((List<object?>)back["streams"]!)[0]!;
        Assert.Equal(long.MaxValue, stream["seed"]);
        Assert.Equal(((Dictionary<string, object?>)body["params"]!)["data"], ((Dictionary<string, object?>)back["params"]!)["data"]);
    }

    [Fact]
    public void Hap_session_encrypts_in_1024_byte_blocks_and_decrypts_split_input()
    {
        var a = RandomNumberGenerator.GetBytes(32);
        var b = RandomNumberGenerator.GetBytes(32);
        var sender = new HapSession(a, b);
        var receiver = new HapSession(b, a);
        var message = RandomNumberGenerator.GetBytes(3000);

        var wire = sender.Encrypt(message);
        Assert.Equal(3000 + (3 * (2 + 16)), wire.Length); // 1024 + 1024 + 952

        var output = new List<byte>();
        foreach (var piece in wire.Chunk(7))
        {
            output.AddRange(receiver.Decrypt(piece));
        }

        Assert.Equal(message, output.ToArray());
        Assert.Equal([0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0, 0], HapSession.Nonce(5)); // counter after 4 zero bytes
    }

    [Fact]
    public void Data_stream_frame_and_protobuf_split()
    {
        var frame = DataStreamChannel.EncodeFrame("sync", "comm", 0x1_2345_6789UL, [9, 9]);
        Assert.Equal(34, frame.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 34 }, frame[..4]);
        Assert.Equal("sync"u8.ToArray(), frame[4..8]);
        Assert.Equal("comm"u8.ToArray(), frame[16..20]);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x23, 0x45, 0x67, 0x89 }, frame[20..28]);

        var m1 = MrpMessages.Create(MrpType.SetConnectionState);
        var m2 = MrpMessages.Create(MrpType.GenericMessage, "ID");
        byte[] joined = [.. ProtoWriter.Varint((ulong)m1.Length), .. m1, .. ProtoWriter.Varint((ulong)m2.Length), .. m2];
        var split = DataStreamChannel.SplitProtobufs(joined);
        Assert.Equal(2, split.Count);
        Assert.Equal(m2, split[1]);

        // A raw message (starts with 0x08 = field 1 varint) is taken as one unprefixed message.
        Assert.Single(DataStreamChannel.SplitProtobufs(m1));
    }

    [Fact]
    public void Device_info_message_has_type_identifier_and_inner_fields()
    {
        var message = new ProtoReader(MrpMessages.DeviceInformation("REQ-1", "UID-1", "Denon Desktop"));
        Assert.Equal((ulong)MrpType.DeviceInfo, message.Uint(1));
        Assert.Equal("REQ-1", message.String(2));
        Assert.Equal(0UL, message.Uint(4));
        Assert.NotNull(message.String(85));
        var info = message.Message(MrpExtension.DeviceInfo)!;
        Assert.Equal("Denon Desktop", info.String(2));
        Assert.Equal("com.apple.TVRemote", info.String(5));
        Assert.Equal(108UL, info.Uint(8));
    }

    [Fact]
    public void Tracker_follows_the_active_player_not_the_empty_default_player()
    {
        // Real sequence from DRTV on tvOS 26: avkit player plays, the default player is paused and empty.
        var tracker = new MrpNowPlayingTracker();
        static ProtoWriter Path(string bundle, string player) =>
            new ProtoWriter().Message(2, new ProtoWriter().String(2, bundle)).Message(3, new ProtoWriter().String(1, player));

        tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.SetNowPlayingClient,
            inner: (MrpExtension.SetNowPlayingClient, new ProtoWriter().Message(1, new ProtoWriter().String(2, "dk.dr.tv").String(7, ""))))));
        tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.SetNowPlayingPlayer,
            inner: (MrpExtension.SetNowPlayingPlayer, new ProtoWriter().Message(1, Path("dk.dr.tv", "avkit-1"))))));

        var item = new ProtoWriter().String(1, "com.apple.avkit.1").Message(2, new ProtoWriter().String(1, "Verdenssituationen").Double(14, 1800));
        var playing = new ProtoWriter().Message(3, new ProtoWriter().Int(1, 0).Message(2, item)).String(5, "DRTV")
            .Uint(6, (ulong)MrpPlaybackState.Playing).Message(9, Path("dk.dr.tv", "avkit-1"));
        tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.SetState, inner: (MrpExtension.SetState, playing))));

        var defaultPaused = new ProtoWriter().Uint(6, (ulong)MrpPlaybackState.Paused).Message(9, Path("dk.dr.tv", MrpNowPlayingTracker.DefaultPlayerId));
        tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.SetState, inner: (MrpExtension.SetState, defaultPaused))));

        var now = tracker.Current;
        Assert.Equal(MrpPlaybackState.Playing, now.State);
        Assert.Equal("Verdenssituationen", now.Title);
        Assert.Equal("DRTV", now.AppName);
        Assert.Equal(1800, now.DurationSeconds);

        // Live sequence when the user leaves the stream: no client, default player, the avkit player paused.
        tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.SetNowPlayingClient,
            inner: (MrpExtension.SetNowPlayingClient, new ProtoWriter().Message(1, new ProtoWriter().String(7, ""))))));
        Assert.False(tracker.Current.HasMedia);

        // Re-enter: a NEW avkit player is named before its state arrives → unknown, not the old paused player.
        tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.SetNowPlayingPlayer,
            inner: (MrpExtension.SetNowPlayingPlayer, new ProtoWriter().Message(1, Path("dk.dr.tv", "avkit-2"))))));
        tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.SetNowPlayingClient,
            inner: (MrpExtension.SetNowPlayingClient, new ProtoWriter().Message(1, new ProtoWriter().String(2, "dk.dr.tv").String(7, ""))))));
        Assert.Equal(MrpPlaybackState.Unknown, tracker.Current.State);
        Assert.Null(tracker.Current.Title);

        var item2 = new ProtoWriter().String(1, "com.apple.avkit.2").Message(2, new ProtoWriter().String(1, "Danmarkshistorien"));
        tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.SetState, inner: (MrpExtension.SetState,
            new ProtoWriter().Message(3, new ProtoWriter().Int(1, 0).Message(2, item2)).Uint(6, (ulong)MrpPlaybackState.Playing).Message(9, Path("dk.dr.tv", "avkit-2"))))));
        Assert.Equal(MrpPlaybackState.Playing, tracker.Current.State);
        Assert.Equal("Danmarkshistorien", tracker.Current.Title);
        Assert.Equal("DRTV", tracker.Current.AppName); // name remembered per app
    }

    [Fact]
    public void Tracker_merges_state_queue_and_content_updates()
    {
        var tracker = new MrpNowPlayingTracker();

        var client = new ProtoWriter().String(2, "dk.dr.tv").String(7, "DRTV");
        tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.SetNowPlayingClient, inner: (MrpExtension.SetNowPlayingClient, new ProtoWriter().Message(1, client)))));

        var metadata = new ProtoWriter().String(1, "Min familie og andre dyr").String(63, "Min familie og andre dyr").Int(10, 2).Int(11, 4)
            .Double(14, 2700).Double(35, 125.5).Bool(19, true);
        var item = new ProtoWriter().String(1, "item-1").Message(2, metadata);
        var queue = new ProtoWriter().Int(1, 0).Message(2, item);
        var path = new ProtoWriter().Message(2, new ProtoWriter().String(2, "dk.dr.tv"));
        var setState = new ProtoWriter().Message(3, queue).Uint(6, (ulong)MrpPlaybackState.Playing).Message(9, path);
        Assert.True(tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.SetState, inner: (MrpExtension.SetState, setState)))));

        var now = tracker.Current;
        Assert.Equal("dk.dr.tv", now.AppBundleId);
        Assert.Equal("DRTV", now.AppName);
        Assert.Equal(MrpPlaybackState.Playing, now.State);
        Assert.Equal("Min familie og andre dyr", now.Title);
        Assert.Equal(2, now.Season);
        Assert.Equal(4, now.Episode);
        Assert.Equal(2700, now.DurationSeconds);
        Assert.True(now.ArtworkAvailable);

        // A later UPDATE_CONTENT_ITEM for the same item changes the elapsed time only.
        var updateItem = new ProtoWriter().String(1, "item-1").Message(2, new ProtoWriter().Double(35, 300));
        var update = new ProtoWriter().Message(1, updateItem).Message(2, path);
        tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.UpdateContentItem, inner: (MrpExtension.UpdateContentItem, update))));
        Assert.Equal(300, tracker.Current.ElapsedSeconds);
        Assert.Equal("Min familie og andre dyr", tracker.Current.Title);

        // Pause.
        var paused = new ProtoWriter().Uint(6, (ulong)MrpPlaybackState.Paused).Message(9, path);
        tracker.Apply(new ProtoReader(MrpMessages.Create(MrpType.SetState, inner: (MrpExtension.SetState, paused))));
        Assert.Equal(MrpPlaybackState.Paused, tracker.Current.State);
    }
}
