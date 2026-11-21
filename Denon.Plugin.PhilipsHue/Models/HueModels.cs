namespace Denon.Plugin.PhilipsHue.Models;

public enum HueMusicSyncMode
{
    BassPulse = 0,
    SpectrumSplit = 1,
    AlbumArtAmbient = 2,
    CinemaSubtle = 3,
}

public sealed class HueMusicSyncConfig
{
    public bool Enabled { get; set; }

    public HueMusicSyncMode Mode { get; set; } = HueMusicSyncMode.BassPulse;

    public double Sensitivity { get; set; } = 1.25;

    public int MinBrightness { get; set; } = 16;

    public int MaxBrightness { get; set; } = 95;

    public string SelectedEntertainmentAreaId { get; set; } = string.Empty;
}

public sealed record HueEntertainmentArea(
    string Id,
    string Name,
    int ChannelCount);

public sealed class HuePluginConfig
{
    public string BridgeIp { get; set; } = string.Empty;

    public string BridgeId { get; set; } = string.Empty;

    public string ApplicationKey { get; set; } = string.Empty;

    public string ClientKey { get; set; } = string.Empty;

    public bool UseHttps { get; set; } = true;

    public bool DemoMode { get; set; }

    public string SelectedRoomId { get; set; } = string.Empty;

    public HueAutomationRules Automation { get; set; } = new();

    public HueMusicSyncConfig MusicSync { get; set; } = new();
}

public sealed class HueAutomationRules
{
    public bool SyncWithSmartScenes { get; set; } = true;

    public bool TurnOnWithReceiverPower { get; set; } = false;

    public bool TurnOffWithReceiverStandby { get; set; } = false;

    public bool CinemaPauseLights { get; set; } = false;

    public string TargetRoomId { get; set; } = string.Empty;

    public int PauseBrightenPercent { get; set; } = 55;

    public int ResumeDimPercent { get; set; } = 18;
}

public sealed record HueDiscoveredBridge(
    string Id,
    string IpAddress,
    string Source);

public sealed record HueLightState(
    string Id,
    string Name,
    string TypeName,
    string ProductName,
    bool IsOn,
    bool IsReachable,
    int BrightnessPercent,
    int? ColorTempMirek,
    bool SupportsColor,
    bool SupportsColorTemp,
    double? X,
    double? Y,
    string PreviewHex);

public sealed record HueRoomGroup(
    string Id,
    string Name,
    string GroupType,
    string RoomClass,
    bool AnyOn,
    bool AllOn,
    int BrightnessPercent,
    IReadOnlyList<string> LightIds);

public sealed record HueSceneItem(
    string Id,
    string Name,
    string GroupId);

public sealed record HueColorPreset(
    string Id,
    string Name,
    string HexColor,
    int BrightnessPercent,
    int? ColorTempMirek = null,
    double? X = null,
    double? Y = null)
{
    public static IReadOnlyList<HueColorPreset> Defaults { get; } =
    [
        new("cinema", "Cinema Dim", "#D97706", 15, ColorTempMirek: 454),
        new("candle", "Candlelight", "#F59E0B", 24, ColorTempMirek: 485),
        new("sunset", "Sunset", "#FB7185", 52, X: 0.561, Y: 0.328),
        new("cyan", "Hi-Fi Cyan", "#00D4FF", 58, X: 0.168, Y: 0.340),
        new("indigo", "Deep Indigo", "#818CF8", 45, X: 0.195, Y: 0.115),
        new("emerald", "Emerald", "#10B981", 55, X: 0.214, Y: 0.655),
        new("warm-white", "Warm White", "#FDE68A", 78, ColorTempMirek: 366),
        new("daylight", "Daylight", "#E0F2FE", 100, ColorTempMirek: 185),
    ];
}

public sealed record HuePairingResult(
    bool Success,
    bool LinkButtonNotPressed,
    string? ApplicationKey,
    string? ClientKey,
    string Message);

public static class HueColorConverter
{
    /// <summary>
    /// Converts a hex RGB color (#RRGGBB) to CIE 1931 XY coordinates using the Wide RGB D65 gamut
    /// recommended by the Philips Hue developer documentation.
    /// </summary>
    public static (double X, double Y) HexToXy(string hex)
    {
        var (r8, g8, b8) = ParseHex(hex);
        var r = GammaCorrect(r8 / 255.0);
        var g = GammaCorrect(g8 / 255.0);
        var b = GammaCorrect(b8 / 255.0);

        var bigX = r * 0.664511 + g * 0.154324 + b * 0.162028;
        var bigY = r * 0.283881 + g * 0.668433 + b * 0.047685;
        var bigZ = r * 0.000088 + g * 0.072310 + b * 0.986039;

        var sum = bigX + bigY + bigZ;
        if (sum <= 0.0001)
        {
            return (0.3127, 0.3290); // D65 white
        }

        return (Math.Round(Math.Clamp(bigX / sum, 0.0, 1.0), 4), Math.Round(Math.Clamp(bigY / sum, 0.0, 1.0), 4));
    }

    public static string XyOrCtToHex(double? x, double? y, int? mirek, bool isOn)
    {
        if (!isOn)
        {
            return "#334155";
        }

        if (x is > 0 && y is > 0)
        {
            var z = 1.0 - x.Value - y.Value;
            const double Y = 1.0;
            var X = (Y / y.Value) * x.Value;
            var Z = (Y / y.Value) * z;

            var r = X * 1.656492 - Y * 0.354851 - Z * 0.255038;
            var g = -X * 0.707196 + Y * 1.655397 + Z * 0.036152;
            var b = X * 0.051713 - Y * 0.121364 + Z * 1.011530;

            var max = Math.Max(r, Math.Max(g, b));
            if (max > 1.0)
            {
                r /= max;
                g /= max;
                b /= max;
            }

            return $"#{ToByte(InverseGamma(r)):X2}{ToByte(InverseGamma(g)):X2}{ToByte(InverseGamma(b)):X2}";
        }

        if (mirek is > 0)
        {
            return MirekToHex(mirek.Value);
        }

        return "#FDE68A";
    }

    public static string MirekToHex(int mirek)
    {
        var clamped = Math.Clamp(mirek, 153, 500);
        var kelvin = 1_000_000.0 / clamped;
        var temp = kelvin / 100.0;

        double r, g, b;
        if (temp <= 66)
        {
            r = 255;
            g = 99.4708025861 * Math.Log(temp) - 161.1195681661;
            b = temp <= 19 ? 0 : 138.5177312231 * Math.Log(temp - 10) - 305.0447927307;
        }
        else
        {
            r = 329.698727446 * Math.Pow(temp - 60, -0.1332047592);
            g = 288.1221695283 * Math.Pow(temp - 60, -0.0755148492);
            b = 255;
        }

        return $"#{ClampByte(r):X2}{ClampByte(g):X2}{ClampByte(b):X2}";
    }

    public static int PercentToHueBri(int percent) =>
        Math.Clamp((int)Math.Round(Math.Clamp(percent, 1, 100) * 2.54), 1, 254);

    public static int HueBriToPercent(int bri) =>
        Math.Clamp((int)Math.Round(Math.Clamp(bri, 0, 254) / 2.54), 1, 100);

    private static (byte R, byte G, byte B) ParseHex(string hex)
    {
        var clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 &&
            byte.TryParse(clean[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(clean[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(clean[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return (r, g, b);
        }

        return (255, 224, 150);
    }

    private static double GammaCorrect(double c) =>
        c > 0.04045 ? Math.Pow((c + 0.055) / 1.055, 2.4) : c / 12.92;

    private static double InverseGamma(double c)
    {
        var clamped = Math.Clamp(c, 0.0, 1.0);
        return clamped <= 0.0031308 ? 12.92 * clamped : 1.055 * Math.Pow(clamped, 1.0 / 2.4) - 0.055;
    }

    private static byte ToByte(double c) => (byte)Math.Clamp((int)Math.Round(c * 255.0), 0, 255);

    private static byte ClampByte(double c) => (byte)Math.Clamp((int)Math.Round(c), 0, 255);
}
