namespace Denon.Plugin.AppleTv.Protocol.Mrp;

/// <summary>Artwork bytes of the playing item and the file type they are in.</summary>
internal sealed record MrpArtwork(byte[] Data, string Extension)
{
    /// <summary>
    /// Reads the artwork from a SET_STATE reply to PLAYBACK_QUEUE_REQUEST: setState (9) → playbackQueue (3) →
    /// contentItems (2) → artworkData (3). The file type comes from the bytes (JPEG or PNG; other types are
    /// not shown). Null when the reply has no artwork.
    /// </summary>
    public static MrpArtwork? FromQueueReply(ProtoReader reply)
    {
        var items = reply.Message(MrpExtension.SetState)?.Message(3)?.Messages(2);
        if (items is null)
        {
            return null;
        }

        foreach (var item in items)
        {
            if (item.Bytes(3) is { Length: > 0 } data && ExtensionOf(data) is { } extension)
            {
                return new MrpArtwork(data, extension);
            }
        }

        return null;
    }

    public static string? ExtensionOf(byte[] data) => data switch
    {
        [0xFF, 0xD8, 0xFF, ..] => ".jpg",
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        _ => null,
    };
}
