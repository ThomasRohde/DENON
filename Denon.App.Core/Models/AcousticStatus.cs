namespace Denon.App.Core.Models;

public sealed record AcousticStatus(
    string MultEq,
    bool? DynamicEq,
    string DynamicVolume,
    string Restorer,
    string Dimmer,
    bool? SubwooferAdjustEnabled,
    int SubwooferTrimDb,
    bool? DialogAdjustEnabled,
    int DialogTrimDb,
    bool? ToneControlEnabled,
    int BassDb,
    int TrebleDb,
    DateTimeOffset RetrievedAt)
{
    public static AcousticStatus Empty { get; } = new(
        MultEq: string.Empty,
        DynamicEq: null,
        DynamicVolume: string.Empty,
        Restorer: string.Empty,
        Dimmer: string.Empty,
        SubwooferAdjustEnabled: null,
        SubwooferTrimDb: 0,
        DialogAdjustEnabled: null,
        DialogTrimDb: 0,
        ToneControlEnabled: null,
        BassDb: 0,
        TrebleDb: 0,
        RetrievedAt: DateTimeOffset.MinValue);
}

public sealed record AcousticOption(string Code, string DisplayName);

public static class DenonAcousticCatalog
{
    public static IReadOnlyList<AcousticOption> MultEqModes { get; } =
    [
        new("AUDYSSEY", "Reference"),
        new("BYP.LR", "L/R Bypass"),
        new("FLAT", "Flat"),
        new("OFF", "Off"),
    ];

    public static IReadOnlyList<AcousticOption> DynamicVolumeModes { get; } =
    [
        new("OFF", "Off"),
        new("LIT", "Light"),
        new("MED", "Medium"),
        new("HEV", "Heavy (Night)"),
    ];

    public static IReadOnlyList<AcousticOption> RestorerModes { get; } =
    [
        new("OFF", "Off"),
        new("LOW", "Low"),
        new("MED", "Medium"),
        new("HI", "High"),
    ];

    public static IReadOnlyList<AcousticOption> DimmerModes { get; } =
    [
        new("BRI", "Bright"),
        new("DIM", "Dim"),
        new("DAR", "Dark"),
        new("OFF", "Display Off"),
    ];

    public static string FormatDbOffset(int db) => db > 0 ? $"+{db} dB" : $"{db} dB";
}
