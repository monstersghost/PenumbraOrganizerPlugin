namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>The folders (any depth) the Protect tab offers for "sort inside".</summary>
public static class SortInsideFolderChoices
{
    public static IReadOnlyList<string> Candidates(IEnumerable<OrganizerModRow> rows, IEnumerable<string> chosen)
    {
        var taken = new HashSet<string>(
            chosen.Select(ToolFolders.NormalizeChosenFolder).OfType<string>(), StringComparer.OrdinalIgnoreCase);

        // folder path -> whether any row there is not tool-managed
        var folders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var segments = row.CurrentPath.Split('/');
            if (segments.Length < 2 || ToolFolders.IsSorterFolder(segments[0]))
                continue;
            for (var depth = 1; depth < segments.Length; depth++)
            {
                var path = string.Join('/', segments.Take(depth));
                folders[path] = folders.GetValueOrDefault(path) || !row.IsToolManaged;
            }
        }

        return folders.Where(f => f.Value && !taken.Contains(f.Key))
            .Select(f => f.Key)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Whether a chosen folder still resolves to Protected, so nothing inside it would move.</summary>
    public static bool ResolvesToProtected(OrganizerState state, string folder) =>
        state.ResolveFolderRule(folder).Rule == FolderProtectionRule.Protected;
}
