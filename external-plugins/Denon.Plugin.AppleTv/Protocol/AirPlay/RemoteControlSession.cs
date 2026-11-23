using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Hap;

namespace Denon.Plugin.AppleTv.Protocol.AirPlay;

/// <summary>
/// AirPlay 2 remote-control session that carries MRP (pyatv: airplay/ap2_session.AP2Session + support/rtsp).
/// Control connection (port 7000, pair-verified and encrypted) → RTSP SETUP (event channel) → RECORD →
/// RTSP SETUP (data stream type 130) → encrypted data channel. A POST /feedback every 2 s keeps it alive.
/// </summary>
internal sealed class RemoteControlSession : IAsyncDisposable
{
    public const string EventsSalt = "Events-Salt";
    public const string EventsWriteInfo = "Events-Write-Encryption-Key";
    public const string EventsReadInfo = "Events-Read-Encryption-Key";
    public const string DataStreamSalt = "DataStream-Salt"; // + seed (decimal)
    public const string DataStreamOutputInfo = "DataStream-Output-Encryption-Key";
    public const string DataStreamInputInfo = "DataStream-Input-Encryption-Key";

    private readonly AirPlayConnection _control;
    private readonly HapPairVerify _verifier;
    private readonly CompanionIdentity _identity;
    private readonly uint _sessionId = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
    private readonly string _dacpId = Convert.ToHexString(BitConverter.GetBytes(Random.Shared.NextInt64())).TrimStart('0');
    private readonly uint _activeRemote = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private int _cseq;
    private EventChannel? _events;
    private Task? _feedbackLoop;

    private RemoteControlSession(AirPlayConnection control, HapPairVerify verifier, CompanionIdentity identity)
    {
        _control = control;
        _verifier = verifier;
        _identity = identity;
    }

    public DataStreamChannel DataChannel { get; private set; } = null!;

    /// <summary>The session failed (feedback or a channel closed).</summary>
    public event Action<Exception?>? Closed;

    public static async Task<RemoteControlSession> ConnectAsync(
        string host, int port, HapCredentials credentials, CompanionIdentity identity, CancellationToken cancellationToken)
    {
        var control = await AirPlayConnection.ConnectAsync(host, port, cancellationToken);
        try
        {
            var verifier = await AirPlayAuth.VerifyAsync(control, credentials, cancellationToken);
            var session = new RemoteControlSession(control, verifier, identity);
            await session.SetupAsync(host, cancellationToken);
            return session;
        }
        catch
        {
            await control.DisposeAsync();
            throw;
        }
    }

    private string Uri => $"rtsp://{_control.LocalAddress}/{_sessionId}";

    private async Task SetupAsync(string host, CancellationToken cancellationToken)
    {
        // 1. Event channel (not used, but must exist). Read/write info are swapped: the receiver opened it.
        var eventSetup = await RtspAsync("SETUP", Uri, new Dictionary<string, object?>
        {
            ["isRemoteControlOnly"] = true,
            ["osName"] = "iPhone OS",
            ["sourceVersion"] = "550.10",
            ["timingProtocol"] = "None",
            ["model"] = _identity.Model,
            ["deviceID"] = _identity.DeviceId,
            ["osVersion"] = "14.7.1",
            ["osBuildVersion"] = "18G82",
            ["macAddress"] = _identity.DeviceId,
            ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
            ["name"] = _identity.Name,
        }, cancellationToken);

        var eventPort = Convert.ToInt32(eventSetup.GetValueOrDefault("eventPort") ?? throw new AirPlayHttpException("SETUP gave no eventPort.", 0));
        var (eventOut, eventIn) = _verifier.DeriveKeys(EventsSalt, EventsReadInfo, EventsWriteInfo);
        _events = await EventChannel.ConnectAsync(host, eventPort, eventOut, eventIn, cancellationToken);

        // 2. RECORD.
        await RtspAsync("RECORD", Uri, null, cancellationToken);

        // 3. Data stream (type 130 = remote control / MRP) with a 64-bit seed in the key salt.
        var seed = (ulong)Random.Shared.NextInt64(0, long.MaxValue);
        var dataSetup = await RtspAsync("SETUP", Uri, new Dictionary<string, object?>
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["controlType"] = 2,
                    ["channelID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                    ["seed"] = seed,
                    ["clientUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                    ["type"] = 130,
                    ["wantsDedicatedSocket"] = true,
                    ["clientTypeUUID"] = "1910A70F-DBC0-4242-AF95-115DB30604E1",
                },
            },
        }, cancellationToken);

        var stream = (dataSetup.GetValueOrDefault("streams") as List<object?>)?.FirstOrDefault() as Dictionary<string, object?>
                     ?? throw new AirPlayHttpException("SETUP gave no data stream.", 0);
        var dataPort = Convert.ToInt32(stream.GetValueOrDefault("dataPort") ?? throw new AirPlayHttpException("SETUP gave no dataPort.", 0));
        var (dataOut, dataIn) = _verifier.DeriveKeys(DataStreamSalt + seed, DataStreamOutputInfo, DataStreamInputInfo);
        DataChannel = await DataStreamChannel.ConnectAsync(host, dataPort, dataOut, dataIn, cancellationToken);
        DataChannel.Closed += ex => Closed?.Invoke(ex);

        _feedbackLoop = Task.Run(FeedbackLoopAsync);
    }

    private async Task<Dictionary<string, object?>> RtspAsync(
        string method, string uri, Dictionary<string, object?>? body, CancellationToken cancellationToken)
    {
        var cseq = _cseq++;
        var headers = new List<KeyValuePair<string, string>>
        {
            new("User-Agent", "AirPlay/550.10"),
            new("CSeq", cseq.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("DACP-ID", _dacpId),
            new("Active-Remote", _activeRemote.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("Client-Instance", _dacpId),
        };

        byte[]? payload = null;
        if (body is not null)
        {
            headers.Add(new("Content-Type", "application/x-apple-binary-plist"));
            payload = BinaryPlist.Write(body);
        }

        var response = await _control.SendAsync(method, uri, headers, payload, cancellationToken, protocol: "RTSP/1.0");
        return response.Body.Length > 0 && response.Body.AsSpan().StartsWith("bplist"u8)
            ? BinaryPlist.ReadDictionary(response.Body)
            : [];
    }

    private async Task FeedbackLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
                await RtspAsync("POST", "/feedback", null, _cts.Token);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Closed?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_feedbackLoop is not null)
        {
            try
            {
                await _feedbackLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        if (DataChannel is not null)
        {
            await DataChannel.DisposeAsync();
        }

        if (_events is not null)
        {
            await _events.DisposeAsync();
        }

        await _control.DisposeAsync();
        _cts.Dispose();
    }
}
