using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;

namespace Denon.App.Core.Setup;

/// <summary>The outcome of reading one profile: the profile, or why it was refused.</summary>
public sealed record ProfileValidationResult(ReceiverProfile? Profile, IReadOnlyList<string> Errors)
{
    public bool IsValid => Profile is not null && Errors.Count == 0;
}

/// <summary>
/// Reads a profile from JSON: first against the bundled JSON Schema, then against the rules a schema cannot say
/// (ids that exist, recommended values that are allowed values, every command the profile can build passes the gate).
/// A profile that fails is not used at all.
/// </summary>
public static class ProfileValidator
{
    private const string SchemaResourceName = "Denon.App.Core.Setup.receiver-profile.schema.json";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Lazy<JsonSchema> Schema = new(LoadSchema);

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The bundled schema text, so the docs and the user can read the same file the app validates with.</summary>
    public static string ReadSchemaText()
    {
        using var stream = typeof(ProfileValidator).Assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {SchemaResourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static JsonSchema LoadSchema() => JsonSchema.FromText(ReadSchemaText());

    /// <param name="json">The profile text.</param>
    /// <param name="origin">"bundled" or the file path, kept on the profile and shown with its problems.</param>
    public static ProfileValidationResult Parse(string json, string origin)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            return new ProfileValidationResult(null, [$"Not valid JSON: {ex.Message}"]);
        }

        using var _ = document;
        var schemaErrors = ValidateAgainstSchema(document.RootElement);
        if (schemaErrors.Count > 0)
        {
            return new ProfileValidationResult(null, schemaErrors);
        }

        ReceiverProfile? profile;
        try
        {
            profile = document.RootElement.Deserialize<ReceiverProfile>(ProfileJson.Options);
        }
        catch (JsonException ex)
        {
            return new ProfileValidationResult(null, [$"The profile could not be read: {ex.Message}"]);
        }

        if (profile is null)
        {
            return new ProfileValidationResult(null, ["The profile could not be read."]);
        }

        profile = profile with { Origin = origin };
        var errors = CheckRules(profile);
        return errors.Count == 0
            ? new ProfileValidationResult(profile, [])
            : new ProfileValidationResult(null, errors);
    }

    /// <summary>Schema errors as "path: message" lines. Empty when the JSON fits the schema.</summary>
    public static IReadOnlyList<string> ValidateAgainstSchema(JsonElement node)
    {
        var results = Schema.Value.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return [];
        }

        var errors = new List<string>();
        foreach (var detail in (results.Details ?? []).Prepend(results))
        {
            if (detail.IsValid || detail.Errors is null)
            {
                continue;
            }

            foreach (var (_, message) in detail.Errors)
            {
                errors.Add($"{detail.InstanceLocation}: {message}");
            }
        }

        if (errors.Count == 0)
        {
            errors.Add("The profile does not match the schema.");
        }

        return errors.Distinct().Take(20).ToList();
    }

    /// <summary>The rules beyond the schema. Also used for a resolved profile in tests.</summary>
    public static List<string> CheckRules(ReceiverProfile profile)
    {
        var errors = new List<string>();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < profile.Settings.Count; index++)
        {
            var setting = profile.Settings[index];
            if (!ids.TryAdd(setting.Id, index))
            {
                errors.Add($"settings: the id '{setting.Id}' is used twice.");
                continue;
            }

            CheckSetting(profile, setting, index, ids, errors);
        }

        var normalizedModels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in profile.Models)
        {
            if (!normalizedModels.Add(ModelNames.Normalize(model)))
            {
                errors.Add($"models: '{model}' is the same model as another entry.");
            }
        }

        foreach (var (model, modelOverride) in profile.ModelOverrides ?? new Dictionary<string, ModelOverride>())
        {
            if (!profile.Models.Any(m => ModelNames.Normalize(m) == ModelNames.Normalize(model)))
            {
                errors.Add($"modelOverrides: '{model}' is not in models.");
            }

            foreach (var id in modelOverride.Exclude ?? [])
            {
                if (!ids.ContainsKey(id))
                {
                    errors.Add($"modelOverrides.{model}.exclude: no setting '{id}'.");
                }
            }

            foreach (var (id, recommend) in modelOverride.Recommend ?? new Dictionary<string, IReadOnlyDictionary<string, RecommendedValue>>())
            {
                if (!ids.TryGetValue(id, out var settingIndex))
                {
                    errors.Add($"modelOverrides.{model}.recommend: no setting '{id}'.");
                    continue;
                }

                var setting = profile.Settings[settingIndex];
                if (setting.Tier == SettingTier.Advice)
                {
                    errors.Add($"modelOverrides.{model}.recommend: '{id}' is advice only and has no recommended value.");
                    continue;
                }

                foreach (var (use, value) in recommend)
                {
                    CheckRecommended(setting, $"modelOverrides.{model}.recommend.{id}.{use}", value, errors);
                }
            }
        }

        return errors;
    }

    private static void CheckSetting(
        ReceiverProfile profile,
        ProfileSetting setting,
        int index,
        Dictionary<string, int> ids,
        List<string> errors)
    {
        var where = $"settings.{setting.Id}";

        if (setting.IsReadable)
        {
            try
            {
                AvrCommandGate.BuildRead(setting);
                var regex = new Regex(setting.ReadParse!, RegexOptions.CultureInvariant, RegexTimeout);
                if (regex.GetGroupNumbers().Length < 2)
                {
                    errors.Add($"{where}.readParse: needs a capture group for the value, for example \"^PSREFLEV (\\\\d+)$\".");
                }
            }
            catch (ArgumentException ex)
            {
                errors.Add($"{where}.readParse: not a valid regular expression ({ex.Message}).");
            }
            catch (SetupCommandRefusedException ex)
            {
                errors.Add($"{where}.read: {ex.Message}");
            }
        }

        if (setting.Tier == SettingTier.Advice)
        {
            if (setting.WarnWhen is not null && !setting.IsReadable)
            {
                errors.Add($"{where}.warnWhen: a warning needs a read command to look at the value.");
            }

            if (setting.WarnWhen?.AtLimits is { } limits && limits.Min > limits.Max)
            {
                errors.Add($"{where}.warnWhen.atLimits: min is above max.");
            }
        }
        else
        {
            var values = setting.Values ?? [];
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                if (!tokens.Add(value.Token))
                {
                    errors.Add($"{where}.values: the token '{value.Token}' is listed twice.");
                    continue;
                }

                try
                {
                    AvrCommandGate.BuildWrite(setting, value.Token);
                }
                catch (SetupCommandRefusedException ex)
                {
                    errors.Add($"{where}.write: {ex.Message}");
                    break;
                }
            }

            foreach (var (use, value) in setting.Recommend ?? new Dictionary<string, RecommendedValue>())
            {
                CheckRecommended(setting, $"{where}.recommend.{use}", value, errors);
            }
        }

        foreach (var (requiredId, token) in setting.Requires ?? new Dictionary<string, string>())
        {
            if (!ids.TryGetValue(requiredId, out var requiredIndex) || requiredIndex >= index)
            {
                errors.Add($"{where}.requires: '{requiredId}' must be a setting listed before this one.");
                continue;
            }

            var required = profile.Settings[requiredIndex];
            if (required.Values is { Count: > 0 } && required.FindValue(token) is null)
            {
                errors.Add($"{where}.requires: '{token}' is not a value of '{requiredId}'.");
            }
        }
    }

    private static void CheckRecommended(ProfileSetting setting, string where, RecommendedValue value, List<string> errors)
    {
        foreach (var token in value.Tokens)
        {
            if (setting.FindValue(token) is null)
            {
                errors.Add($"{where}: '{token}' is not one of the values '{setting.Id}' allows.");
            }
        }
    }
}

/// <summary>Loose model-name matching: HEOS says "Denon AVR-X2400H", a profile lists "AVR-X2400H" or "X2400H".</summary>
public static class ModelNames
{
    private static readonly string[] Brands = ["DENON", "MARANTZ"];

    /// <summary>Upper case letters and digits only, without the brand and the "AVR" before the model: "X2400H".</summary>
    public static string Normalize(string? model)
    {
        var text = new string((model ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        foreach (var brand in Brands)
        {
            if (text.StartsWith(brand, StringComparison.Ordinal))
            {
                text = text[brand.Length..];
            }
        }

        return text.StartsWith("AVR", StringComparison.Ordinal) && text.Length > 3 ? text[3..] : text;
    }

    /// <summary>True when the receiver's model name is the profile's model (it may carry a region suffix: "X2400HBKE2").</summary>
    public static bool Matches(string profileModel, string? receiverModel)
    {
        var wanted = Normalize(profileModel);
        var actual = Normalize(receiverModel);
        return wanted.Length > 0 && actual.StartsWith(wanted, StringComparison.Ordinal);
    }
}
