namespace Denon.Plugin.AppleTv.Protocol.Mrp;

/// <summary>ProtocolMessage.Type values used here (pyatv: mrp/protobuf/ProtocolMessage.proto).</summary>
internal static class MrpType
{
    public const int SendCommand = 1;
    public const int SendCommandResult = 2;
    public const int SetState = 4;
    public const int SetArtwork = 5;
    public const int DeviceInfo = 15;
    public const int ClientUpdatesConfig = 16;
    public const int GetKeyboardSession = 24;
    public const int PlaybackQueueRequest = 32;
    public const int SetConnectionState = 38;
    public const int GenericMessage = 42;
    public const int SetNowPlayingClient = 46;
    public const int SetNowPlayingPlayer = 47;
    public const int UpdateClient = 55;
    public const int UpdateContentItem = 56;
    public const int UpdatePlayer = 58;
    public const int ConfigureConnection = 120;
}

/// <summary>Extension field numbers on ProtocolMessage for the inner messages.</summary>
internal static class MrpExtension
{
    public const int SendCommand = 6;
    public const int SendCommandResult = 7;
    public const int SetState = 9;
    public const int DeviceInfo = 20;
    public const int ClientUpdatesConfig = 21;
    public const int SetConnectionState = 42;
    public const int SetNowPlayingClient = 50;
    public const int SetNowPlayingPlayer = 51;
    public const int UpdateContentItem = 60;
}

/// <summary>Remote commands (pyatv: CommandInfo.proto Command).</summary>
internal enum MrpCommand
{
    Play = 1,
    Pause = 2,
    TogglePlayPause = 3,
    Stop = 4,
    NextTrack = 5,
    PreviousTrack = 6,
}

/// <summary>Playback state (pyatv: Common.proto PlaybackState).</summary>
internal enum MrpPlaybackState
{
    Unknown = 0,
    Playing = 1,
    Paused = 2,
    Stopped = 3,
    Interrupted = 4,
    Seeking = 5,
}

/// <summary>Builds outgoing MRP messages (pyatv: mrp/messages.py).</summary>
internal static class MrpMessages
{
    /// <summary>ProtocolMessage: type (1), identifier (2), errorCode (4), the inner message, uniqueIdentifier (85).</summary>
    public static byte[] Create(int type, string? identifier = null, (int Field, ProtoWriter Inner)? inner = null)
    {
        var message = new ProtoWriter().Uint(1, (ulong)type);
        if (identifier is not null)
        {
            message.String(2, identifier);
        }

        message.Uint(4, 0); // errorCode = NoError (pyatv sets it explicitly)
        if (inner is { } ext)
        {
            message.Message(ext.Field, ext.Inner);
        }

        message.String(85, Guid.NewGuid().ToString().ToUpperInvariant());
        return message.ToArray();
    }

    public static byte[] DeviceInformation(string identifier, string uniqueIdentifier, string name) =>
        Create(MrpType.DeviceInfo, identifier, (MrpExtension.DeviceInfo, new ProtoWriter()
            .String(1, uniqueIdentifier)
            .String(2, name)
            .String(3, "iPhone")
            .String(4, "18G82")
            .String(5, "com.apple.TVRemote")
            .String(6, "344.28")
            .Int(7, 1)
            .Uint(8, 108)
            .Bool(9, true)
            .Bool(10, true)
            .String(12, "com.apple.TVMusic")
            .Bool(13, true)
            .Bool(14, true)
            .Bool(15, true)
            .Uint(17, 2)
            .Uint(21, 1) // DeviceClass.iPhone
            .Uint(22, 1)));

    public static byte[] SetConnectionState() =>
        Create(MrpType.SetConnectionState, inner: (MrpExtension.SetConnectionState, new ProtoWriter().Uint(1, 2))); // Connected

    /// <summary>pyatv defaults: artwork, volume, keyboard and output-device updates on; now-playing updates off.</summary>
    public static byte[] ClientUpdatesConfig(string identifier) =>
        Create(MrpType.ClientUpdatesConfig, identifier, (MrpExtension.ClientUpdatesConfig, new ProtoWriter()
            .Bool(1, true)
            .Bool(2, false)
            .Bool(3, true)
            .Bool(4, true)
            .Bool(5, true)));

    public static byte[] GetKeyboardSession(string identifier) => Create(MrpType.GetKeyboardSession, identifier);

    /// <summary>SEND_COMMAND (pyatv: messages.command): SendCommandMessage { command = 1 }.</summary>
    public static byte[] SendCommand(string identifier, MrpCommand command) =>
        Create(MrpType.SendCommand, identifier, (MrpExtension.SendCommand, new ProtoWriter().Uint(1, (ulong)command)));

    public static byte[] Heartbeat(string identifier) => Create(MrpType.GenericMessage, identifier);
}
