using System.Text.Json;
using System.Text.Json.Serialization;

namespace Denon.App.Core.Setup;

/// <summary>Where a setting lives on the receiver. Decides what the check reads and what an apply may touch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingScope>))]
public enum SettingScope
{
    /// <summary>One value for the whole receiver.</summary>
    Global,

    /// <summary>One value per input: a read or a write affects the input that is selected now.</summary>
    PerInput,

    /// <summary>One value per sound mode: a read or a write affects the sound mode that is active now.</summary>
    PerSoundMode,
}

/// <summary>What the app may do with a setting.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingTier>))]
public enum SettingTier
{
    /// <summary>Safe to apply. Selected for apply by default.</summary>
    Apply,

    /// <summary>Changes how the sound feels or depends on taste. Applied only when the user ticks it.</summary>
    Confirm,

    /// <summary>Text only, or a read-only check. Never written: the receiver's protocol cannot set it.</summary>
    Advice,
}

/// <summary>What the user listens to. A profile gives one recommended value per use.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ListeningUse>))]
public enum ListeningUse
{
    Films,
    Music,
    LateNight,
    Tv,
}

public static class ListeningUses
{
    public static string Key(ListeningUse use) => use switch
    {
        ListeningUse.Films => "films",
        ListeningUse.Music => "music",
        ListeningUse.LateNight => "lateNight",
        ListeningUse.Tv => "tv",
        _ => throw new ArgumentOutOfRangeException(nameof(use)),
    };

    public static string Label(ListeningUse use) => use switch
    {
        ListeningUse.Films => "Films",
        ListeningUse.Music => "Music",
        ListeningUse.LateNight => "Late night",
        ListeningUse.Tv => "TV",
        _ => throw new ArgumentOutOfRangeException(nameof(use)),
    };
}

/// <summary>One value a setting may be set to: the token the receiver uses, and the name the app shows.</summary>
public sealed record ProfileValue(string Token, string? Label = null)
{
    public string Display => string.IsNullOrWhiteSpace(Label) ? Token : Label;
}

/// <summary>The sound modes (the MS? answer, upper case) in which the receiver does not apply or answer the setting.</summary>
public sealed record BlockedWhen(IReadOnlyList<string>? SoundModes = null);

/// <summary>
/// A read-only rule on a setting's current value: a warning, never a change. <see cref="AtLimits"/> warns at the
/// ends of a numeric range; <see cref="Values"/> warns on listed tokens.
/// </summary>
public sealed record WarnWhen(NumericLimits? AtLimits = null, IReadOnlyList<string>? Values = null, string Message = "");

public sealed record NumericLimits(int Min, int Max);

/// <summary>Per-model changes on top of a family profile.</summary>
/// <param name="Exclude">Setting ids this model does not have.</param>
/// <param name="Recommend">Setting id → use → recommended value, replacing the family's.</param>
public sealed record ModelOverride(
    IReadOnlyList<string>? Exclude = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, RecommendedValue>>? Recommend = null);

/// <summary>
/// The recommended value(s) of a setting for one use. The first token is the one an apply writes; any listed token
/// counts as fine in a check ("Light or Medium").
/// </summary>
[JsonConverter(typeof(RecommendedValueConverter))]
public sealed record RecommendedValue(IReadOnlyList<string> Tokens)
{
    public string Preferred => Tokens[0];

    public bool Accepts(string? token) =>
        token is not null && Tokens.Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads a recommended value written as "OFF" or as ["LIT", "MED"].</summary>
public sealed class RecommendedValueConverter : JsonConverter<RecommendedValue>
{
    public override RecommendedValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new RecommendedValue([reader.GetString() ?? string.Empty]);
        }

        var tokens = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? [];
        return new RecommendedValue(tokens);
    }

    public override void Write(Utf8JsonWriter writer, RecommendedValue value, JsonSerializerOptions options)
    {
        if (value.Tokens.Count == 1)
        {
            writer.WriteStringValue(value.Tokens[0]);
        }
        else
        {
            JsonSerializer.Serialize(writer, value.Tokens, options);
        }
    }
}

/// <summary>One receiver setting in a profile. The profile is data: the app only ever sends what this describes.</summary>
public sealed record ProfileSetting
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string Group { get; init; }

    public required SettingScope Scope { get; init; }

    public required SettingTier Tier { get; init; }

    /// <summary>The read command, for example "PSREFLEV ?". Absent when the protocol cannot read the setting.</summary>
    public string? Read { get; init; }

    /// <summary>A regular expression for the answer line. Its group "value" (or the first group) is the token.</summary>
    public string? ReadParse { get; init; }

    /// <summary>"none" (the token as sent) or "offset50" (a number the receiver sends as 50 + dB, read as dB).</summary>
    public string Decode { get; init; } = "none";

    /// <summary>The write command with one {value} placeholder, for example "PSREFLEV {value}".</summary>
    public string? Write { get; init; }

    /// <summary>The only values a write may carry.</summary>
    public IReadOnlyList<ProfileValue>? Values { get; init; }

    public string? Unit { get; init; }

    public BlockedWhen? BlockedWhen { get; init; }

    /// <summary>Setting id → token that setting must have for this one to work ("dynamicEq": "ON").</summary>
    public IReadOnlyDictionary<string, string>? Requires { get; init; }

    /// <summary>Use key (films, music, lateNight, tv) → recommended value.</summary>
    public IReadOnlyDictionary<string, RecommendedValue>? Recommend { get; init; }

    public WarnWhen? WarnWhen { get; init; }

    /// <summary>Text for settings the protocol cannot read or set.</summary>
    public string? Advice { get; init; }

    public string Why { get; init; } = string.Empty;

    public IReadOnlyList<string> Sources { get; init; } = [];

    public bool IsReadable => !string.IsNullOrWhiteSpace(Read) && !string.IsNullOrWhiteSpace(ReadParse);

    public bool IsWritable => Tier != SettingTier.Advice && !string.IsNullOrWhiteSpace(Write);

    public ProfileValue? FindValue(string? token) =>
        token is null ? null : Values?.FirstOrDefault(v => string.Equals(v.Token, token, StringComparison.OrdinalIgnoreCase));

    public RecommendedValue? RecommendationFor(ListeningUse use) =>
        Recommend is not null && Recommend.TryGetValue(ListeningUses.Key(use), out var value) ? value : null;
}

/// <summary>A receiver family's recommended settings, as loaded from JSON.</summary>
public sealed record ReceiverProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Model names this profile covers ("AVR-X2400H", "X1400H"). Matched loosely against what HEOS reports.</summary>
    public required IReadOnlyList<string> Models { get; init; }

    public string Description { get; init; } = string.Empty;

    public required IReadOnlyList<ProfileSetting> Settings { get; init; }

    /// <summary>Model name → changes for that model.</summary>
    public IReadOnlyDictionary<string, ModelOverride>? ModelOverrides { get; init; }

    /// <summary>Where the profile came from: "bundled" or the path of the user's file. Not part of the JSON.</summary>
    [JsonIgnore]
    public string Origin { get; init; } = "bundled";

    [JsonIgnore]
    public bool IsBundled => Origin == "bundled";
}

/// <summary>The JSON options every profile read uses.</summary>
public static class ProfileJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.Strict,
    };
}
