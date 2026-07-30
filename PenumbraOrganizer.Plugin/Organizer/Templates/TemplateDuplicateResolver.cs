namespace PenumbraOrganizer.Plugin.Organizer.Templates;

public sealed record TemplateDuplicateResolution(
    IReadOnlyDictionary<string, string> EntriesByNormalizedName,
    IReadOnlyList<TemplateWarning> Warnings);

/// <summary>
/// The one duplicate rule, used for in-document duplicates on import and for normalized-name
/// collisions among the author's own mods on export — they are the same problem.
///
/// Agreeing duplicates collapse to one entry with a warning. Disagreeing duplicates drop the
/// whole group: keeping an arbitrary winner would publish a silent choice between two different
/// intents. "Last entry wins" is deliberately not used, because JSON array ordering must never
/// change meaning.
/// </summary>
public static class TemplateDuplicateResolver
{
    public static TemplateDuplicateResolution Resolve(IEnumerable<TemplateEntry> entries)
    {
        var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var warnings = new List<TemplateWarning>();

        foreach (var entry in entries)
        {
            // Entry keys are untrusted input: re-normalize rather than believe them.
            var key = ModNameNormalizer.Normalize(entry.N);
            if (key.Length == 0 || !TemplatePathValidator.IsValidFolder(entry.F))
            {
                warnings.Add(new TemplateWarning(TemplateWarningCode.InvalidEntryPath, entry.N));
                continue;
            }

            if (!byKey.TryGetValue(key, out var folders))
                byKey[key] = folders = [];
            folders.Add(entry.F);
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, folders) in byKey.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var distinct = folders.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count > 1)
            {
                warnings.Add(new TemplateWarning(TemplateWarningCode.ConflictingDuplicateEntry, key));
                continue;
            }

            if (folders.Count > 1)
                warnings.Add(new TemplateWarning(TemplateWarningCode.DuplicateEntry, key));

            resolved[key] = distinct[0];
        }

        // Order the warnings deterministically before returning. The invalid-entry pass above
        // runs in input order while the duplicate pass is key-sorted, so without this a template
        // holding two invalid entries would produce different warning sequences for identical
        // content in a different array order -- exactly what the order-independence rule forbids.
        var orderedWarnings = warnings
            .OrderBy(warning => warning.Subject, StringComparer.Ordinal)
            .ThenBy(warning => warning.Code)
            .ToList();

        return new TemplateDuplicateResolution(resolved, orderedWarnings);
    }
}
