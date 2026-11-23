using System.Collections.Concurrent;
using Denon.Plugin.AppleTv.Protocol.AirPlay;
using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Hap;

namespace Denon.Plugin.AppleTv.Protocol.Mrp;

/// <summary>
/// MRP over the AirPlay remote-control tunnel (pyatv: mrp/protocol.MrpProtocol.start + airplay/mrp_connection).
/// Start: DEVICE_INFO (wait) → SET_CONNECTION_STATE Connected → CLIENT_UPDATES_CONFIG (wait) →
/// GET_KEYBOARD_SESSION (wait). Replies are matched by identifier (field 2). Everything else updates the
/// now-playing tracker.
/// </summary>
internal sealed class MrpClient : IAsyncDisposable
{
    private readonly RemoteControlSession _session;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProtoReader>> _pending = new();
    private readonly SortedSet<int> _seenTypes = [];

    /// <summary>All MRP message types received so far, including during start-up (diagnostics).</summary>
    public IReadOnlyCollection<int> SeenTypes
    {
        get
        {
            lock (_seenTypes)
            {
                return [.. _seenTypes];
            }
        }
    }

    private MrpClient(RemoteControlSession session)
    {
        _session = session;
        _session.DataChannel.ProtobufReceived += OnProtobuf;
        _session.Closed += ex => Closed?.Invoke(ex);
    }

    public MrpNowPlayingTracker NowPlaying { get; } = new();

    /// <summary>The now-playing state may have changed.</summary>
    public event Action<NowPlayingSnapshot>? NowPlayingChanged;

    /// <summary>Every received message type (for diagnostics).</summary>
    public event Action<int>? MessageReceived;

    public event Action<Exception?>? Closed;

    /// <summary>Every received message, parsed (diagnostics).</summary>
    public event Action<ProtoReader>? RawMessageReceived;

    /// <summary>The Apple TV's own DEVICE_INFO reply (name, model, OS build).</summary>
    public ProtoReader? DeviceInfo { get; private set; }

    public static async Task<MrpClient> ConnectAsync(
        string host, int airPlayPort, HapCredentials credentials, CompanionIdentity identity, CancellationToken cancellationToken,
        Action<ProtoReader>? onRawMessage = null)
    {
        var session = await RemoteControlSession.ConnectAsync(host, airPlayPort, credentials, identity, cancellationToken);
        var client = new MrpClient(session);
        if (onRawMessage is not null)
        {
            client.RawMessageReceived += onRawMessage; // before start: state arrives during start-up
        }

        try
        {
            await client.StartAsync(identity, cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private async Task StartAsync(CompanionIdentity identity, CancellationToken cancellationToken)
    {
        var deviceInfo = await SendAndReceiveAsync(
            id => MrpMessages.DeviceInformation(id, Guid.NewGuid().ToString().ToUpperInvariant(), identity.Name), cancellationToken);
        DeviceInfo = deviceInfo.Message(MrpExtension.DeviceInfo);

        await _session.DataChannel.SendProtobufAsync(MrpMessages.SetConnectionState(), cancellationToken);
        await SendAndReceiveAsync(MrpMessages.ClientUpdatesConfig, cancellationToken);
        await SendAndReceiveAsync(MrpMessages.GetKeyboardSession, cancellationToken);
    }

    /// <summary>
    /// Sends a remote command to the app that plays and waits for SEND_COMMAND_RESULT. Throws when the
    /// Apple TV reports an error (sendError, field 1) or the app refuses it (handlerReturnStatus, field 2).
    /// </summary>
    public async Task SendCommandAsync(MrpCommand command, CancellationToken cancellationToken)
    {
        var reply = await SendAndReceiveAsync(id => MrpMessages.SendCommand(id, command), cancellationToken);
        var result = reply.Message(MrpExtension.SendCommandResult);
        var sendError = result?.Uint(1) ?? 0;
        var handlerStatus = result?.Uint(2) ?? 0;
        if (sendError != 0 || handlerStatus != 0)
        {
            throw new CompanionProtocolException(
                $"The app refused {command}{StatusHint(handlerStatus)} (status {sendError}/{handlerStatus}).");
        }
    }

    private static string StatusHint(ulong status) => status switch
    {
        1 => ": no such content",
        2 => " for this program (live programs often cannot pause)",
        10 => ": nothing plays",
        20 => ": device not found",
        _ => string.Empty,
    };

    public async Task<ProtoReader> SendAndReceiveAsync(Func<string, byte[]> build, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var identifier = Guid.NewGuid().ToString().ToUpperInvariant();
        var tcs = new TaskCompletionSource<ProtoReader>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[identifier] = tcs;
        try
        {
            await _session.DataChannel.SendProtobufAsync(build(identifier), cancellationToken);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new CompanionProtocolException("The Apple TV did not answer an MRP request.");
        }
        finally
        {
            _pending.TryRemove(identifier, out _);
        }
    }

    private void OnProtobuf(byte[] raw)
    {
        ProtoReader message;
        try
        {
            message = new ProtoReader(raw);
        }
        catch (FormatException)
        {
            return;
        }

        var type = (int)(message.Uint(1) ?? 0);
        lock (_seenTypes)
        {
            _seenTypes.Add(type);
        }

        MessageReceived?.Invoke(type);
        try
        {
            RawMessageReceived?.Invoke(message);
        }
        catch
        {
        }

        if (message.String(2) is { } identifier && _pending.TryRemove(identifier, out var waiter))
        {
            waiter.TrySetResult(message);
            return;
        }

        bool changed;
        try
        {
            changed = NowPlaying.Apply(message);
        }
        catch (FormatException)
        {
            return;
        }

        if (changed)
        {
            try
            {
                NowPlayingChanged?.Invoke(NowPlaying.Current);
            }
            catch
            {
            }
        }
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
