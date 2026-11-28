namespace Denon.App.Core.Setup;

/// <summary>A profile file that was refused, and why. The user sees these next to the profile list.</summary>
public sealed record ProfileProblem(string Origin, IReadOnlyList<string> Errors);

/// <summary>
/// The profiles the app knows: the ones bundled in the app, and the user's own files in the profiles folder. A user
/// profile with the same id as a bundled one replaces it, and when both a user and a bundled profile cover a model the
/// user's wins. A file that fails validation is left out and listed in <see cref="Problems"/>.
/// </summary>
public sealed class ReceiverProfileLibrary
{
    private const string BundledPrefix = "Denon.App.Core.Setup.Profiles.";

    private ReceiverProfileLibrary(IReadOnlyList<ReceiverProfile> profiles, IReadOnlyList<ProfileProblem> problems)
    {
        Profiles = profiles;
        Problems = problems;
    }

    public IReadOnlyList<ReceiverProfile> Profiles { get; }

    public IReadOnlyList<ProfileProblem> Problems { get; }

    /// <param name="userDirectory">The user's profiles folder; null or a missing folder means no user profiles.</param>
    public static ReceiverProfileLibrary Load(string? userDirectory)
    {
        var problems = new List<ProfileProblem>();
        var bundled = LoadBundled(problems);
        var user = LoadUser(userDirectory, problems);

        var userIds = user.Select(static p => p.Id).ToHashSet(StringComparer.Ordinal);
        var profiles = user.Concat(bundled.Where(p => !userIds.Contains(p.Id))).ToList();
        return new ReceiverProfileLibrary(profiles, problems);
    }

    /// <summary>The best profile for the model HEOS reports, or null when none covers it.</summary>
    public ReceiverProfile? FindForModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        // Profiles are in "user first" order; the longest matching model name wins inside each group.
        return Profiles
            .Select(profile => (Profile: profile, Match: BestMatchLength(profile, model)))
            .Where(static x => x.Match > 0)
            .OrderBy(static x => x.Profile.IsBundled)
            .ThenByDescending(static x => x.Match)
            .Select(static x => x.Profile)
            .FirstOrDefault();
    }

    public ReceiverProfile? FindById(string? id) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The profile with the changes for <paramref name="model"/> applied: settings the model lacks are dropped and its
    /// own recommended values replace the family's. A model without an override gets the profile as it is.
    /// </summary>
    public static ReceiverProfile Resolve(ReceiverProfile profile, string? model)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ModelOverrides is not { Count: > 0 } overrides || string.IsNullOrWhiteSpace(model))
        {
            return profile;
        }

        var matching = overrides.Where(pair => ModelNames.Matches(pair.Key, model)).Select(static pair => pair.Value).ToList();
        if (matching.Count == 0)
        {
            return profile;
        }

        var excluded = matching.SelectMany(static o => o.Exclude ?? []).ToHashSet(StringComparer.Ordinal);
        var settings = new List<ProfileSetting>();
        foreach (var setting in profile.Settings.Where(s => !excluded.Contains(s.Id)))
        {
            var recommend = setting.Recommend is null ? null : new Dictionary<string, RecommendedValue>(setting.Recommend);
            foreach (var modelOverride in matching)
            {
                if (modelOverride.Recommend is not null && modelOverride.Recommend.TryGetValue(setting.Id, out var replaced))
                {
                    recommend ??= [];
                    foreach (var (use, value) in replaced)
                    {
                        recommend[use] = value;
                    }
                }
            }

            // A setting that needs a dropped one cannot work either.
            if (setting.Requires?.Keys.Any(excluded.Contains) == true)
            {
                continue;
            }

            settings.Add(recommend is null ? setting : setting with { Recommend = recommend });
        }

        return profile with { Settings = settings };
    }

    private static int BestMatchLength(ReceiverProfile profile, string model) =>
        profile.Models.Where(m => ModelNames.Matches(m, model)).Select(static m => ModelNames.Normalize(m).Length).DefaultIfEmpty(0).Max();

    private static List<ReceiverProfile> LoadBundled(List<ProfileProblem> problems)
    {
        var assembly = typeof(ReceiverProfileLibrary).Assembly;
        var profiles = new List<ReceiverProfile>();
        foreach (var name in assembly.GetManifestResourceNames().Where(static n => n.StartsWith(BundledPrefix, StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            Add(ProfileValidator.Parse(reader.ReadToEnd(), "bundled"), name[BundledPrefix.Length..], profiles, problems);
        }

        return profiles;
    }

    private static List<ReceiverProfile> LoadUser(string? directory, List<ProfileProblem> problems)
    {
        var profiles = new List<ReceiverProfile>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return profiles;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 512 * 1024)
                {
                    problems.Add(new ProfileProblem(path, ["The file is larger than 512 KB."]));
                    continue;
                }

                Add(ProfileValidator.Parse(File.ReadAllText(path), path), path, profiles, problems);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new ProfileProblem(path, [$"The file could not be read: {ex.Message}"]));
            }
        }

        // Two user files with one id: the first (by file name) is used, the other is reported.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<ReceiverProfile>();
        foreach (var profile in profiles)
        {
            if (seen.Add(profile.Id))
            {
                unique.Add(profile);
            }
            else
            {
                problems.Add(new ProfileProblem(profile.Origin, [$"The id '{profile.Id}' is already used by another profile file."]));
            }
        }

        return unique;
    }

    private static void Add(ProfileValidationResult result, string origin, List<ReceiverProfile> profiles, List<ProfileProblem> problems)
    {
        if (result.IsValid)
        {
            profiles.Add(result.Profile!);
        }
        else
        {
            problems.Add(new ProfileProblem(origin, result.Errors));
        }
    }
}
