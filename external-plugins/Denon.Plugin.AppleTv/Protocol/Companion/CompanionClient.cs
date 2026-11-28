using System.Security.Cryptography;
using Denon.Plugin.AppleTv.Protocol.Hap;

namespace Denon.Plugin.AppleTv.Protocol.Companion;

/// <summary>HID buttons (pyatv: companion/api.HidCommand).</summary>
internal enum HidCommand
{
    Up = 1,
    Down = 2,
    Left = 3,
    Right = 4,
    Menu = 5,
    Select = 6,
    Home = 7,
    VolumeUp = 8,
    VolumeDown = 9,
    Siri = 10,
    Screensaver = 11,
    Sleep = 12,
    Wake = 13,
    PlayPause = 14,
    ChannelIncrement = 15,
    ChannelDecrement = 16,
    Guide = 17,
    PageUp = 18,
    PageDown = 19,
}

/// <summary>Power/attention state (pyatv: companion/api.SystemStatus).</summary>
internal enum AttentionState
{
    Unknown = 0,
    Asleep = 1,
    Screensaver = 2,
    Awake = 3,
    Idle = 4,
}

/// <summary>
/// How this controller presents itself in "_systemInfo". Keep it stable per installation (store it with the
/// credentials): <see cref="RpId"/> must not be empty, or tvOS stops pushing power-state events.
/// </summary>
internal sealed record CompanionIdentity(string Name, string Model, string DeviceId, string RpId)
{
    public static CompanionIdentity Create(string name)
    {
        var mac = RandomNumberGenerator.GetBytes(6);
        mac[0] = (byte)((mac[0] | 0x02) & 0xFE); // locally administered, unicast
        return new CompanionIdentity(
            name,
            "iPhone10,6",
            string.Join(':', mac.Select(b => b.ToString("X2"))),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    }
}

/// <summary>
/// Paired Companion session (pyatv: companion/protocol.CompanionProtocol + api.CompanionAPI): pair-verify,
/// encryption, system info, session start, then power state, keys, apps and events.
/// </summary>
internal sealed class CompanionClient : IAsyncDisposable
{
    private const string PairingDataKey = "_pd";

    private readonly CompanionConnection _connection;
    private readonly HapCredentials _credentials;
    private readonly CompanionIdentity _identity;
    private long _sessionId;

    private CompanionClient(CompanionConnection connection, HapCredentials credentials, CompanionIdentity identity)
    {
        _connection = connection;
        _credentials = credentials;
        _identity = identity;
        _connection.EventReceived += (name, content) => EventReceived?.Invoke(name, content);
        _connection.Closed += ex => Closed?.Invoke(ex);
    }

    public event Action<string, Dictionary<object, object?>>? EventReceived;

    public event Action<Exception?>? Closed;

    public bool IsConnected => _connection.IsConnected;

    public static async Task<CompanionClient> ConnectAsync(
        string host, int port, HapCredentials credentials, CompanionIdentity identity, CancellationToken cancellationToken)
    {
        var connection = await CompanionConnection.ConnectAsync(host, port, cancellationToken);
        var client = new CompanionClient(connection, credentials, identity);
        try
        {
            await client.VerifyAsync(cancellationToken);
            await client.StartSessionAsync(cancellationToken);
            return client;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Power state: Asleep, Screensaver, Awake or Idle.</summary>
    public async Task<AttentionState> FetchAttentionStateAsync(CancellationToken cancellationToken)
    {
        var content = Content(await RequestAsync("FetchAttentionState", [], cancellationToken));
        return content.GetValueOrDefault("state") is long state && Enum.IsDefined(typeof(AttentionState), (int)state)
            ? (AttentionState)state
            : AttentionState.Unknown;
    }

    /// <summary>Launchable apps: bundle id → name.</summary>
    public async Task<IReadOnlyDictionary<string, string>> FetchAppsAsync(CancellationToken cancellationToken)
    {
        var content = Content(await RequestAsync("FetchLaunchableApplicationsEvent", [], cancellationToken));
        return content
            .Where(kv => kv.Key is string)
            .ToDictionary(kv => (string)kv.Key, kv => kv.Value?.ToString() ?? (string)kv.Key, StringComparer.Ordinal);
    }

    public Task LaunchAppAsync(string bundleIdOrUrl, CancellationToken cancellationToken)
    {
        var key = Uri.TryCreate(bundleIdOrUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme) && bundleIdOrUrl.Contains(':')
            ? "_urlS"
            : "_bundleID";
        return RequestAsync("_launchApp", new() { [key] = bundleIdOrUrl }, cancellationToken);
    }

    /// <summary>Presses and releases a button.</summary>
    public async Task PressAsync(HidCommand command, CancellationToken cancellationToken)
    {
        await RequestAsync("_hidC", new() { ["_hBtS"] = 1, ["_hidC"] = (int)command }, cancellationToken);
        await RequestAsync("_hidC", new() { ["_hBtS"] = 2, ["_hidC"] = (int)command }, cancellationToken);
    }

    /// <summary>Media control (pyatv MediaControlCommand): 1 Play, 2 Pause, 3 Next, 4 Previous.</summary>
    public Task MediaControlAsync(int command, CancellationToken cancellationToken) =>
        RequestAsync("_mcc", new() { ["_mcc"] = command }, cancellationToken);

    /// <summary>Registers for events (for example "_iMC" media control or "TVSystemStatus").</summary>
    public Task SubscribeAsync(string eventName, CancellationToken cancellationToken) =>
        SendEventAsync("_interest", new() { ["_regEvents"] = new List<object?> { eventName } }, cancellationToken);

    public async Task<Dictionary<object, object?>> RequestAsync(string identifier, Dictionary<string, object?> content, CancellationToken cancellationToken) =>
        await _connection.ExchangeOpackAsync(
            CompanionFrameType.EOpack,
            new Dictionary<string, object?> { ["_i"] = identifier, ["_t"] = (int)CompanionMessageType.Request, ["_c"] = content },
            cancellationToken);

    public Task SendEventAsync(string identifier, Dictionary<string, object?> content, CancellationToken cancellationToken) =>
        _connection.SendOpackAsync(
            CompanionFrameType.EOpack,
            new Dictionary<string, object?> { ["_i"] = identifier, ["_t"] = (int)CompanionMessageType.Event, ["_c"] = content },
            cancellationToken);

    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var verify = new HapPairVerify(_credentials);
        var m2 = await _connection.ExchangeAuthAsync(
            CompanionFrameType.PvStart,
            new() { [PairingDataKey] = verify.BuildM1(), ["_auTy"] = 4 },
            cancellationToken);

        var m4 = await _connection.ExchangeAuthAsync(
            CompanionFrameType.PvNext,
            new() { [PairingDataKey] = verify.ReadM2BuildM3(PairingData(m2)) },
            cancellationToken);

        if (m4.GetValueOrDefault(PairingDataKey) is byte[] m4Data)
        {
            HapPairVerify.ReadM4(m4Data);
        }

        var (output, input) = verify.DeriveKeys("", "ClientEncrypt-main", "ServerEncrypt-main");
        _connection.EnableEncryption(output, input);
    }

    private async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        await RequestAsync("_systemInfo", new()
        {
            ["_bf"] = 0,
            ["_cf"] = 512,
            ["_clFl"] = 128,
            ["_i"] = _identity.RpId,
            ["_idsID"] = _credentials.ClientId,
            ["_pubID"] = _identity.DeviceId,
            ["_sf"] = 256,
            ["_sv"] = "170.18",
            ["model"] = _identity.Model,
            ["name"] = _identity.Name,
        }, cancellationToken);

        var localSid = (long)(uint)Random.Shared.NextInt64(0, uint.MaxValue);
        var response = await RequestAsync("_sessionStart", new() { ["_srvT"] = "com.apple.tvremoteservices", ["_sid"] = localSid }, cancellationToken);
        if (Content(response).GetValueOrDefault("_sid") is long remoteSid)
        {
            _sessionId = (remoteSid << 32) | localSid;
        }

        // tvOS answers FetchAttentionState only after a TV Remote Client session exists (pyatv comment).
        try
        {
            await RequestAsync("TVRCSessionStart", new() { ["ProtocolVersionKey"] = "1.2" }, cancellationToken);
        }
        catch (CompanionProtocolException)
        {
        }

        await SubscribeAsync("_iMC", cancellationToken);
    }

    private static byte[] PairingData(Dictionary<object, object?> message) =>
        message.GetValueOrDefault(PairingDataKey) as byte[] ?? throw new HapPairingException("The Apple TV reply has no pairing data.");

    private static Dictionary<object, object?> Content(Dictionary<object, object?> response) =>
        response.GetValueOrDefault("_c") as Dictionary<object, object?> ?? [];

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsConnected && _sessionId != 0)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await RequestAsync("_sessionStop", new() { ["_srvT"] = "com.apple.tvremoteservices", ["_sid"] = _sessionId }, timeout.Token);
            }
            catch
            {
                // Best effort, as in pyatv.
            }
        }

        await _connection.DisposeAsync();
    }
}

/// <summary>
/// Companion pair-setup: <see cref="StartAsync"/> makes the Apple TV show a PIN; <see cref="FinishAsync"/>
/// sends the PIN and returns the credentials (pyatv: companion/auth.CompanionPairSetupProcedure).
/// </summary>
internal sealed class CompanionPairing : IAsyncDisposable
{
    private readonly CompanionConnection _connection;
    private readonly HapPairSetup _setup;

    private CompanionPairing(CompanionConnection connection, HapPairSetup setup)
    {
        _connection = connection;
        _setup = setup;
    }

    public static async Task<CompanionPairing> StartAsync(string host, int port, string displayName, CancellationToken cancellationToken)
    {
        var connection = await CompanionConnection.ConnectAsync(host, port, cancellationToken);
        try
        {
            var setup = new HapPairSetup(displayName);
            var m2 = await connection.ExchangeAuthAsync(
                CompanionFrameType.PsStart,
                new() { ["_pd"] = HapPairSetup.BuildM1(), ["_pwTy"] = 1 },
                cancellationToken);
            setup.ReadM2(m2.GetValueOrDefault("_pd") as byte[] ?? throw new HapPairingException("The Apple TV did not start pairing."));
            return new CompanionPairing(connection, setup);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<HapCredentials> FinishAsync(string pin, CancellationToken cancellationToken)
    {
        var m4 = await _connection.ExchangeAuthAsync(
            CompanionFrameType.PsNext,
            new() { ["_pd"] = _setup.BuildM3(pin.Trim()), ["_pwTy"] = 1 },
            cancellationToken);
        _setup.ReadM4(m4.GetValueOrDefault("_pd") as byte[] ?? throw new HapPairingException("No reply to the PIN."));

        var m6 = await _connection.ExchangeAuthAsync(
            CompanionFrameType.PsNext,
            new() { ["_pd"] = _setup.BuildM5(), ["_pwTy"] = 1 },
            cancellationToken);
        return _setup.ReadM6(m6.GetValueOrDefault("_pd") as byte[] ?? throw new HapPairingException("No final pairing reply."));
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
