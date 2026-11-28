namespace Denon.App.Desktop.Infrastructure;

/// <summary>A new id that no existing one has: "scene-3", or "scene-3-2" and up when it is taken.</summary>
public static class UniqueId
{
    public static string From(string baseId, IEnumerable<string?> existing)
    {
        var taken = new HashSet<string>(existing.Where(static id => !string.IsNullOrWhiteSpace(id))!, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseId))
        {
            return baseId;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{baseId}-{n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
