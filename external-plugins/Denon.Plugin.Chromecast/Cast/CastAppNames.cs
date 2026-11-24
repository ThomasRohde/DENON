namespace Denon.Plugin.Chromecast.Cast;

/// <summary>
/// Clean app names for the habit log and dashboard. Some Cast receivers report a technical
/// display name (DR TV says "AXIS 10 PROD - www.drtv-platform.dr.dk"). Unknown apps keep their own name.
/// </summary>
internal static class CastAppNames
{
    private static readonly Dictionary<string, string> ByAppId = new(StringComparer.OrdinalIgnoreCase)
    {
        // Built-in & Google
        ["CC1AD845"] = "Default Media Receiver",
        ["E8C28D3C"] = "Backdrop",
        ["233637DE"] = "YouTube",
        ["2DB7CC49"] = "YouTube Music",

        // Major streaming & music
        ["CA5E8412"] = "Netflix",
        ["CC32E753"] = "Spotify",
        ["C3DE6BC2"] = "Disney+",
        ["17608BC8"] = "Prime Video",
        ["01CE20A3"] = "HBO GO",
        ["B3DCF968"] = "Twitch",
        ["9AC194DC"] = "Plex",
        ["06ee44ee-e7e3-4249-83b6-f5d0b6f07f34_1"] = "Plex",
        ["F007D354"] = "Jellyfin",
        ["B143C57E"] = "SoundCloud",
        ["B88B034A"] = "Dailymotion",

        // Nordic & European broadcasters / streaming
        ["A0181CF1"] = "DR TV",
        ["6313CF39"] = "Viaplay",
        ["3F70D486"] = "SVT Play",
        ["3AEDF8D1"] = "NRK TV",
        ["A49874B1"] = "NRK Radio",
        ["A9BCCB7C"] = "Yle Areena",
        ["5E81F6DB"] = "BBC iPlayer",
        ["2BA92214"] = "BBC iPlayer",
        ["03977A48"] = "BBC Sounds",

        // Media & smart-home receivers
        ["3927FA74"] = "BubbleUPnP",
        ["07AEE832"] = "Shaka Player",
        ["A078F6B0"] = "Home Assistant",
        ["B45F4572"] = "Home Assistant",
    };

    // Fallback cleanup for services that use dynamic/regional Cast app IDs or report a technical receiver string.
    private static readonly (string Marker, string FriendlyName)[] TechnicalDisplayNameMarkers =
    [
        ("drtv-platform.dr.dk", "DR TV"),
        ("play.tv2.dk", "TV 2 Play"),
        ("tv2play", "TV 2 Play"),
        ("tv2 play", "TV 2 Play"),
        ("primevideo", "Prime Video"),
        ("amazon prime video", "Prime Video"),
        ("viaplay", "Viaplay"),
        ("hbomax", "Max"),
        ("hbo max", "Max"),
        ("max.com", "Max"),
        ("skyshowtime", "SkyShowtime"),
        ("disneyplus", "Disney+"),
        ("tv.apple.com", "Apple TV+"),
        ("appletv", "Apple TV+"),
        ("svtplay", "SVT Play"),
        ("nrk.no", "NRK TV"),
    ];

    public static string? Friendly(string? appId, string? displayName)
    {
        if (appId is not null && ByAppId.TryGetValue(appId, out var name))
        {
            return name;
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            foreach (var (marker, friendlyName) in TechnicalDisplayNameMarkers)
            {
                if (displayName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return friendlyName;
                }
            }
        }

        return displayName;
    }
}
