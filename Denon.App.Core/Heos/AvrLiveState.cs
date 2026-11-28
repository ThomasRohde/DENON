using System.Collections.Immutable;
using Denon.App.Core.Models;

namespace Denon.App.Core.Heos;

/// <summary>
/// What the receiver last said on port 23, field by field. Answers to queries and the status lines the receiver
/// pushes by itself look the same (for example "MV50") and both are the real state, so every line goes in here, in
/// the order it came. A null field is a field the receiver did not report on this link yet.
/// </summary>
public sealed record AvrLiveState
{
    public static AvrLiveState Empty { get; } = new();

    public bool? IsPoweredOn { get; init; }

    public bool? IsMainZoneOn { get; init; }

    public int? Volume { get; init; }

    public double? VolumeExact { get; init; }

    public int? MaxVolume { get; init; }

    public bool? IsMuted { get; init; }

    public string? InputSource { get; init; }

    public string? SoundMode { get; init; }

    public string? EcoMode { get; init; }

    /// <summary>True when a sleep line came in; then <see cref="SleepTimerMinutes"/> null means the timer is off.</summary>
    public bool HasSleepTimer { get; init; }

    public int? SleepTimerMinutes { get; init; }

    public bool? IsZone2On { get; init; }

    /// <summary>
    /// The newest acoustic line (PS* and DIM) for each setting, for example "PSDYNEQ" → "PSDYNEQ OFF". The newest line
    /// for a setting is its real value, so <see cref="ApplyAcousticsTo"/> can apply them in any order.
    /// </summary>
    public ImmutableDictionary<string, string> AcousticLines { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>Reads one line. A line it does not know (or a value it cannot read) leaves the state as it is.</summary>
    public AvrLiveState Apply(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length < 3)
        {
            return this;
        }

        if (AcousticKey(line) is { } key)
        {
            return this with { AcousticLines = AcousticLines.SetItem(key, line) };
        }

        if (line.StartsWith("PW", StringComparison.OrdinalIgnoreCase))
        {
            return OnOff(line, "PWON", "PWSTANDBY") is { } on ? this with { IsPoweredOn = on } : this;
        }

        if (line.StartsWith("ZM", StringComparison.OrdinalIgnoreCase))
        {
            return OnOff(line, "ZMON", "ZMOFF") is { } on ? this with { IsMainZoneOn = on } : this;
        }

        if (line.StartsWith("MVMAX", StringComparison.OrdinalIgnoreCase))
        {
            return DenonAvrClient.TryParseDenonVolumeToken(line["MVMAX".Length..].Trim(), out var max, out _)
                ? this with { MaxVolume = Math.Clamp(max, 10, 98) }
                : this;
        }

        if (line.StartsWith("MV", StringComparison.OrdinalIgnoreCase))
        {
            return DenonAvrClient.TryParseDenonVolumeToken(line[2..].Trim(), out var volume, out var exact)
                ? this with { Volume = volume, VolumeExact = exact }
                : this;
        }

        if (line.StartsWith("MU", StringComparison.OrdinalIgnoreCase))
        {
            return OnOff(line, "MUON", "MUOFF") is { } on ? this with { IsMuted = on } : this;
        }

        if (line.StartsWith("SI", StringComparison.OrdinalIgnoreCase))
        {
            var input = line[2..].Trim();
            return input.Length > 0 ? this with { InputSource = input } : this;
        }

        if (line.StartsWith("ECO", StringComparison.OrdinalIgnoreCase))
        {
            var mode = line[3..].Trim().ToUpperInvariant();
            return mode is "ON" or "AUTO" or "OFF" ? this with { EcoMode = mode } : this;
        }

        if (line.StartsWith("SLP", StringComparison.OrdinalIgnoreCase))
        {
            var value = line[3..].Trim();
            if (string.Equals(value, "OFF", StringComparison.OrdinalIgnoreCase))
            {
                return this with { HasSleepTimer = true, SleepTimerMinutes = null };
            }

            return int.TryParse(value, out var minutes) && minutes > 0
                ? this with { HasSleepTimer = true, SleepTimerMinutes = minutes }
                : this;
        }

        if (line.StartsWith("Z2", StringComparison.OrdinalIgnoreCase))
        {
            // Only the power line: Z2TV is the zone's source and Z240 its volume.
            return OnOff(line, "Z2ON", "Z2OFF") is { } on ? this with { IsZone2On = on } : this;
        }

        if (line.StartsWith("MS", StringComparison.OrdinalIgnoreCase))
        {
            var mode = line[2..].Trim();
            // Quick Select and Smart Select memories are not sound modes.
            return mode.Length > 0 &&
                   !mode.StartsWith("QUICK", StringComparison.OrdinalIgnoreCase) &&
                   !mode.StartsWith("SMART", StringComparison.OrdinalIgnoreCase)
                ? this with { SoundMode = mode }
                : this;
        }

        return this;
    }

    /// <summary>The status with every field the receiver reported on this link.</summary>
    public ReceiverStatus ApplyTo(ReceiverStatus status) => status with
    {
        IsPoweredOn = IsPoweredOn ?? status.IsPoweredOn,
        IsMainZoneOn = IsMainZoneOn ?? status.IsMainZoneOn,
        Volume = Volume ?? status.Volume,
        VolumeExact = VolumeExact ?? status.VolumeExact,
        MaxVolume = MaxVolume ?? status.MaxVolume,
        IsMuted = IsMuted ?? status.IsMuted,
        InputSource = InputSource ?? status.InputSource,
        SoundMode = SoundMode ?? status.SoundMode,
        EcoMode = EcoMode ?? status.EcoMode,
        SleepTimerMinutes = HasSleepTimer ? SleepTimerMinutes : status.SleepTimerMinutes,
        IsZone2On = IsZone2On ?? status.IsZone2On,
    };

    /// <summary>
    /// Reads one acoustic line (PS* and DIM) into <paramref name="acoustics"/>. Returns the same instance for any other
    /// line, or a value it cannot read.
    /// </summary>
    public static AcousticStatus ApplyAcoustic(AcousticStatus acoustics, string rawLine)
    {
        var line = rawLine.Trim();
        if (line.StartsWith("PSMULTEQ:", StringComparison.OrdinalIgnoreCase))
        {
            var mode = line["PSMULTEQ:".Length..].Trim().ToUpperInvariant();
            return mode.Length > 0 ? acoustics with { MultEq = mode } : acoustics;
        }

        if (line.StartsWith("PSDYNEQ", StringComparison.OrdinalIgnoreCase))
        {
            return DenonAvrClient.ParseOnOff(line["PSDYNEQ".Length..]) is { } on ? acoustics with { DynamicEq = on } : acoustics;
        }

        if (line.StartsWith("PSDYNVOL", StringComparison.OrdinalIgnoreCase))
        {
            var mode = line["PSDYNVOL".Length..].Trim().ToUpperInvariant();
            return mode is "OFF" or "LIT" or "MED" or "HEV" ? acoustics with { DynamicVolume = mode } : acoustics;
        }

        if (line.StartsWith("PSRSTR", StringComparison.OrdinalIgnoreCase))
        {
            var mode = line["PSRSTR".Length..].Trim().ToUpperInvariant();
            return mode is "OFF" or "LOW" or "MED" or "HI" ? acoustics with { Restorer = mode } : acoustics;
        }

        if (line.StartsWith("DIM", StringComparison.OrdinalIgnoreCase))
        {
            var mode = DenonAvrClient.NormalizeDimmerCode(line["DIM".Length..]);
            return mode is "BRI" or "DIM" or "DAR" or "OFF" ? acoustics with { Dimmer = mode } : acoustics;
        }

        if (line.StartsWith("PSSWL", StringComparison.OrdinalIgnoreCase))
        {
            return ReadLevelAdjust(line["PSSWL".Length..], acoustics.SubwooferAdjustEnabled, acoustics.SubwooferTrimDb) is { } sw
                ? acoustics with { SubwooferAdjustEnabled = sw.Enabled, SubwooferTrimDb = sw.Db }
                : acoustics;
        }

        if (line.StartsWith("PSDIL", StringComparison.OrdinalIgnoreCase))
        {
            return ReadLevelAdjust(line["PSDIL".Length..], acoustics.DialogAdjustEnabled, acoustics.DialogTrimDb) is { } dil
                ? acoustics with { DialogAdjustEnabled = dil.Enabled, DialogTrimDb = dil.Db }
                : acoustics;
        }

        if (line.StartsWith("PSTONE CTRL", StringComparison.OrdinalIgnoreCase))
        {
            return DenonAvrClient.ParseOnOff(line["PSTONE CTRL".Length..]) is { } on ? acoustics with { ToneControlEnabled = on } : acoustics;
        }

        if (line.StartsWith("PSBAS", StringComparison.OrdinalIgnoreCase) && IsDigits(line["PSBAS".Length..]))
        {
            return acoustics with { BassDb = DenonAvrClient.ParseDenonOffsetDb(line["PSBAS".Length..], -6, 6) };
        }

        if (line.StartsWith("PSTRE", StringComparison.OrdinalIgnoreCase) && IsDigits(line["PSTRE".Length..]))
        {
            return acoustics with { TrebleDb = DenonAvrClient.ParseDenonOffsetDb(line["PSTRE".Length..], -6, 6) };
        }

        return acoustics;
    }

    /// <summary>True for a line <see cref="ApplyAcoustic"/> reads.</summary>
    public static bool IsAcousticLine(string line) =>
        line.StartsWith("PS", StringComparison.OrdinalIgnoreCase) || line.StartsWith("DIM", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="acoustics"/> with the newest acoustic line of each setting on this link. A full acoustic read
    /// takes about a second; a setting changed on the remote after its query answered is newer than the read.
    /// </summary>
    public AcousticStatus ApplyAcousticsTo(AcousticStatus acoustics) =>
        AcousticLines.Values.Aggregate(acoustics, ApplyAcoustic);

    /// <summary>
    /// The setting an acoustic line sets, or null when <see cref="ApplyAcoustic"/> cannot read it. The trims have
    /// two: on/off and the level ("PSSWL ON" and "PSSWL 52" are both kept).
    /// </summary>
    internal static string? AcousticKey(string line)
    {
        if (!IsAcousticLine(line) || ReferenceEquals(ApplyAcoustic(AcousticStatus.Empty, line), AcousticStatus.Empty))
        {
            return null;
        }

        foreach (var prefix in AcousticPrefixes)
        {
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return prefix is "PSSWL" or "PSDIL"
                ? prefix + (DenonAvrClient.ParseOnOff(line[prefix.Length..]) is null ? ":LEVEL" : ":ONOFF")
                : prefix;
        }

        return null;
    }

    private static readonly string[] AcousticPrefixes =
        ["PSMULTEQ:", "PSDYNEQ", "PSDYNVOL", "PSRSTR", "DIM", "PSSWL", "PSDIL", "PSTONE CTRL", "PSBAS", "PSTRE"];

    private static bool? OnOff(string line, string on, string off) =>
        string.Equals(line, on, StringComparison.OrdinalIgnoreCase) ? true
        : string.Equals(line, off, StringComparison.OrdinalIgnoreCase) ? false
        : null;

    private static bool IsDigits(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && trimmed.All(char.IsDigit);
    }

    /// <summary>"ON", "OFF" or a level such as "50" (0 dB); null for anything else (for example "2 50" for a second subwoofer).</summary>
    private static (bool? Enabled, int Db)? ReadLevelAdjust(string value, bool? enabled, int db)
    {
        var trimmed = value.Trim();
        if (DenonAvrClient.ParseOnOff(trimmed) is { } on)
        {
            return (on, db);
        }

        return IsDigits(trimmed) ? (enabled, DenonAvrClient.ParseDenonOffsetDb(trimmed, -12, 12)) : null;
    }
}
