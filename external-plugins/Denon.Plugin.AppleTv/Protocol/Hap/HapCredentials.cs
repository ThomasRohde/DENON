namespace Denon.Plugin.AppleTv.Protocol.Hap;

/// <summary>
/// Long-term pairing result (pyatv: hap_pairing.HapCredentials). The string form "ltpk:ltsk:atvId:clientId"
/// (hex) is compatible with pyatv's Companion credentials.
/// </summary>
/// <param name="AtvPublicKey">The Apple TV's long-term Ed25519 public key (LTPK).</param>
/// <param name="ClientSeed">Our long-term Ed25519 private seed (LTSK). Secret.</param>
/// <param name="AtvId">The Apple TV's pairing identifier.</param>
/// <param name="ClientId">Our pairing identifier (an ASCII UUID).</param>
internal sealed record HapCredentials(byte[] AtvPublicKey, byte[] ClientSeed, byte[] AtvId, byte[] ClientId)
{
    public override string ToString() =>
        string.Join(':', Convert.ToHexStringLower(AtvPublicKey), Convert.ToHexStringLower(ClientSeed),
            Convert.ToHexStringLower(AtvId), Convert.ToHexStringLower(ClientId));

    public static HapCredentials Parse(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length != 4 || parts.Any(string.IsNullOrEmpty))
        {
            throw new FormatException("Credentials must be four hex fields: ltpk:ltsk:atvId:clientId.");
        }

        return new HapCredentials(
            Convert.FromHexString(parts[0]),
            Convert.FromHexString(parts[1]),
            Convert.FromHexString(parts[2]),
            Convert.FromHexString(parts[3]));
    }

    /// <summary>Hides the secret seed in logs.</summary>
    public string ToSafeString() => $"atvId={System.Text.Encoding.ASCII.GetString(AtvId)}, clientId={System.Text.Encoding.ASCII.GetString(ClientId)}";
}
