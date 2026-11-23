using Denon.Plugin.AppleTv.Protocol.Hap;

namespace Denon.Plugin.AppleTv.Protocol.AirPlay;

/// <summary>
/// HAP pairing over AirPlay HTTP (pyatv: airplay/auth/hap.py + auth/__init__.verify_connection).
/// Pair-setup: POST /pair-pin-start (the Apple TV shows a PIN), then /pair-setup M1–M6.
/// Pair-verify: POST /pair-verify M1–M4, then the control connection is encrypted with
/// HKDF("Control-Salt", "Control-Write-Encryption-Key" / "Control-Read-Encryption-Key").
/// </summary>
internal static class AirPlayAuth
{
    public const string ControlSalt = "Control-Salt";
    public const string ControlOutputInfo = "Control-Write-Encryption-Key";
    public const string ControlInputInfo = "Control-Read-Encryption-Key";

    private static IEnumerable<KeyValuePair<string, string>> Headers() =>
    [
        new("User-Agent", "AirPlay/320.20"),
        new("Connection", "keep-alive"),
        new("X-Apple-HKP", "3"),
        new("Content-Type", "application/octet-stream"),
    ];

    /// <summary>Starts pair-setup: the Apple TV shows a PIN. Finish with <see cref="FinishPairingAsync"/>.</summary>
    public static async Task<HapPairSetup> StartPairingAsync(AirPlayConnection connection, string displayName, CancellationToken cancellationToken)
    {
        await connection.SendAsync("POST", "/pair-pin-start", Headers(), null, cancellationToken);
        var setup = new HapPairSetup(displayName);
        var m2 = await connection.SendAsync("POST", "/pair-setup", Headers(), HapPairSetup.BuildM1(), cancellationToken);
        setup.ReadM2(m2.Body);
        return setup;
    }

    public static async Task<HapCredentials> FinishPairingAsync(
        AirPlayConnection connection, HapPairSetup setup, string pin, CancellationToken cancellationToken)
    {
        var m4 = await connection.SendAsync("POST", "/pair-setup", Headers(), setup.BuildM3(pin.Trim()), cancellationToken);
        setup.ReadM4(m4.Body);
        var m6 = await connection.SendAsync("POST", "/pair-setup", Headers(), setup.BuildM5(), cancellationToken);
        return setup.ReadM6(m6.Body);
    }

    /// <summary>Pair-verify and encrypt the control connection. Returns the verifier for channel keys.</summary>
    public static async Task<HapPairVerify> VerifyAsync(AirPlayConnection connection, HapCredentials credentials, CancellationToken cancellationToken)
    {
        var verify = new HapPairVerify(credentials);
        var m2 = await connection.SendAsync("POST", "/pair-verify", Headers(), verify.BuildM1(), cancellationToken);
        var m4 = await connection.SendAsync("POST", "/pair-verify", Headers(), verify.ReadM2BuildM3(m2.Body), cancellationToken);
        if (m4.Body.Length > 0)
        {
            HapPairVerify.ReadM4(m4.Body);
        }

        var (output, input) = verify.DeriveKeys(ControlSalt, ControlOutputInfo, ControlInputInfo);
        connection.EnableEncryption(output, input);
        return verify;
    }
}
