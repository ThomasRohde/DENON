using Denon.Plugin.AppleTv.Protocol;
using Denon.Plugin.AppleTv.Protocol.Mrp;
using Xunit;

namespace Denon.Plugin.AppleTv.Tests;

public sealed class MrpPlaybackQueueTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static ProtoWriter Path(string bundle, string player) =>
        new ProtoWriter().Message(2, new ProtoWriter().String(2, bundle)).Message(3, new ProtoWriter().String(1, player));

    private static ProtoReader Message(int type, int extension, ProtoWriter inner, string? identifier = null) =>
        new(MrpMessages.Create(type, identifier, (extension, inner)));

    private static ProtoReader ClearClient() =>
        Message(MrpType.SetNowPlayingClient, MrpExtension.SetNowPlayingClient, new ProtoWriter().Message(1, new ProtoWriter().String(7, "")));

    private static ProtoReader SetClient(string bundle) =>
        Message(MrpType.SetNowPlayingClient, MrpExtension.SetNowPlayingClient, new ProtoWriter().Message(1, new ProtoWriter().String(2, bundle)));

    private static ProtoWriter State(string itemId, string title, MrpPlaybackState? playback, ProtoWriter? path, byte[]? artwork = null)
    {
        var item = new ProtoWriter().String(1, itemId).Message(2, new ProtoWriter().String(1, title).Bool(19, true).String(80, "art-" + itemId));
        if (artwork is not null)
        {
            item.Bytes(3, artwork);
        }

        var state = new ProtoWriter().Message(3, new ProtoWriter().Int(1, 0).Message(2, item));
        if (playback is { } p)
        {
            state.Uint(6, (ulong)p);
        }

        if (path is not null)
        {
            state.Message(9, path);
        }

        return state;
    }

    [Fact]
    public void Playback_queue_request_asks_for_one_item_with_artwork()
    {
        var message = new ProtoReader(MrpMessages.PlaybackQueueRequest("REQ-7", 2));
        Assert.Equal((ulong)MrpType.PlaybackQueueRequest, message.Uint(1));
        Assert.Equal("REQ-7", message.String(2));
        var request = message.Message(MrpExtension.PlaybackQueueRequest)!;
        Assert.Equal(2, request.Int(1));
        Assert.Equal(1, request.Int(2));
        Assert.Equal(-1, request.Double(4));
        Assert.Equal(400, request.Double(5));
        Assert.True(request.Bool(13));
    }

    [Fact]
    public void Artwork_is_read_from_the_queue_reply_and_typed_by_its_bytes()
    {
        var reply = Message(MrpType.SetState, MrpExtension.SetState, State("item-1", "Straffen", null, null, Jpeg), "REQ-7");
        var artwork = MrpArtwork.FromQueueReply(reply);
        Assert.NotNull(artwork);
        Assert.Equal(".jpg", artwork.Extension);
        Assert.Equal(Jpeg, artwork.Data);

        var unknown = Message(MrpType.SetState, MrpExtension.SetState, State("item-1", "Straffen", null, null, [1, 2, 3, 4]));
        Assert.Null(MrpArtwork.FromQueueReply(unknown));
        Assert.Null(MrpArtwork.FromQueueReply(Message(MrpType.SetState, MrpExtension.SetState, State("item-1", "Straffen", null, null))));
    }

    [Fact]
    public void Snapshot_names_its_artwork_by_the_artwork_identifier()
    {
        var tracker = new MrpNowPlayingTracker();
        tracker.Apply(SetClient("dk.dr.tv"));
        tracker.Apply(Message(MrpType.SetState, MrpExtension.SetState, State("item-1", "Straffen", MrpPlaybackState.Playing, Path("dk.dr.tv", "avkit-1"))));

        var now = tracker.Current;
        Assert.True(now.ArtworkAvailable);
        Assert.Equal("dk.dr.tv|art-item-1", now.ArtworkKey);
        Assert.Equal("item-1", now.ItemIdentifier);
    }

    [Fact]
    public void Without_a_client_a_player_that_starts_playing_is_shown_but_older_players_are_not()
    {
        var tracker = new MrpNowPlayingTracker();
        tracker.Apply(SetClient("dk.dr.tv"));
        tracker.Apply(Message(MrpType.SetState, MrpExtension.SetState, State("item-1", "Løftet", MrpPlaybackState.Playing, Path("dk.dr.tv", "avkit-1"))));

        // The episode ends: tvOS names no app; the old player still says Playing.
        tracker.Apply(ClearClient());
        Assert.False(tracker.Current.HasMedia);

        // The next episode's player reports state, but tvOS names no app (DRTV autoplay).
        Thread.Sleep(2);
        tracker.Apply(Message(MrpType.SetState, MrpExtension.SetState, State("item-2", "Straffen", MrpPlaybackState.Playing, Path("dk.dr.tv", "avkit-2"))));
        Assert.Equal("Straffen", tracker.Current.Title);
        Assert.Equal("dk.dr.tv", tracker.Current.AppBundleId);
    }

    [Fact]
    public void A_playing_queue_reply_without_a_path_belongs_to_the_last_app()
    {
        var tracker = new MrpNowPlayingTracker();
        tracker.Apply(SetClient("dk.dr.tv"));
        tracker.Apply(ClearClient());
        Assert.False(tracker.Current.HasMedia);

        // A paused reply does not make an app active.
        var paused = Message(MrpType.SetState, MrpExtension.SetState, State("item-2", "Straffen", MrpPlaybackState.Paused, null), "REQ-1");
        Assert.True(tracker.ApplyQueueReply(paused));
        Assert.False(tracker.Current.HasMedia);

        var playing = Message(MrpType.SetState, MrpExtension.SetState, State("item-2", "Straffen", MrpPlaybackState.Playing, null), "REQ-2");
        Assert.True(tracker.ApplyQueueReply(playing));
        Assert.Equal("Straffen", tracker.Current.Title);
        Assert.Equal(MrpPlaybackState.Playing, tracker.Current.State);
        Assert.Equal("dk.dr.tv", tracker.Current.AppBundleId);
    }

    [Fact]
    public void A_queue_reply_before_any_app_is_ignored()
    {
        var tracker = new MrpNowPlayingTracker();
        var reply = Message(MrpType.SetState, MrpExtension.SetState, State("item-1", "Straffen", MrpPlaybackState.Playing, null), "REQ-1");
        Assert.False(tracker.ApplyQueueReply(reply));
        Assert.False(tracker.Current.HasMedia);
    }
}
