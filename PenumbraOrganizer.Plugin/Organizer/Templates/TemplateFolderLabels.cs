namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Renames canonical folder paths (the output of ModTypeFolders.GetFolder) using a template's
/// folderLabels map. Longest-prefix, segment-boundary matching: {"Gear": "Equipment"} rewrites
/// both "Gear" and "Gear/Head", but never "Gearbox". Exact-key-only matching was rejected because
/// it produces exactly the split tree this feature exists to avoid.
///
/// Renaming is applied once and non-recursively — the output is never re-matched — so a map
/// cannot loop or cascade.
/// </summary>
public static class TemplateFolderLabels
{
    public static Func<string, string> Create(IReadOnlyDictionary<string, string> labels)
    {
        if (labels.Count == 0)
            return static folder => folder;

        // Longest key first, so the most specific match wins.
        var ordered = labels
            .OrderByDescending(pair => pair.Key.Count(c => c == '/'))
            .ThenByDescending(pair => pair.Key.Length)
            .ToList();

        return folder =>
        {
            foreach (var (key, replacement) in ordered)
            {
                if (string.Equals(folder, key, StringComparison.Ordinal))
                    return replacement;

                // Segment boundary: "Gear" matches "Gear/Head" but not "Gearbox".
                if (folder.Length > key.Length
                    && folder[key.Length] == '/'
                    && folder.AsSpan(0, key.Length).SequenceEqual(key))
                {
                    return replacement + folder[key.Length..];
                }
            }

            return folder;
        };
    }
}
