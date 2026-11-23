namespace Denon.Plugin.Chromecast.Cast;

/// <summary>
/// Clean app names for the habit log and dashboard. Some Cast receivers report a technical
/// display name (DR TV says "AXIS 10 PROD - www.drtv-platform.dr.dk"). Unknown apps keep their own name.
/// </summary>
internal static class CastAppNames
{
    private static readonly Dictionary<string, string> ByAppId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CC1AD845"] = "Default Media Receiver",
        ["E8C28D3C"] = "Backdrop",
        ["233637DE"] = "YouTube",
        ["CA5E8412"] = "Netflix",
        ["CC32E753"] = "Spotify",
        ["C3DE6BC2"] = "Disney+",
        ["A0181CF1"] = "DR TV",
    };

    public static string? Friendly(string? appId, string? displayName) =>
        appId is not null && ByAppId.TryGetValue(appId, out var name) ? name : displayName;
}
