namespace Denon.App.Core.Models;

public sealed record ReceiverPlayerInfo(
    string Name,
    int PlayerId,
    string Model,
    string Version,
    string IpAddress,
    string Network,
    int LineOut,
    string Serial);

public sealed record NowPlayingMedia(
    string Type,
    string Song,
    string Album,
    string Artist,
    string ImageUrl,
    string AlbumId,
    string MediaId,
    int? QueueId,
    int? SourceId);

public sealed record ReceiverStatus(
    ReceiverPlayerInfo Player,
    string PlayState,
    int Volume,
    bool IsMuted,
    NowPlayingMedia NowPlaying,
    DateTimeOffset RetrievedAt,
    bool IsPoweredOn = false,
    bool IsMainZoneOn = false,
    string InputSource = "");

public sealed record AvrDeviceStatus(
    bool IsPoweredOn,
    bool IsMainZoneOn,
    int Volume,
    bool IsMuted,
    string InputSource);
