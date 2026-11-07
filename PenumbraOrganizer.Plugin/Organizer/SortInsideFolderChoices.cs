namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>The top-level folders the Protect tab offers for "sort inside".</summary>
public static class SortInsideFolderChoices
{
    public static IReadOnlyList<string> Candidates(IEnumerable<OrganizerModRow> rows, IEnumerable<string> chosen)
    {
        var taken = new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase);
        return rows.Select(r => ToolFolders.AnchorOf(r.CurrentPath))
            .Where(a => a is not null && !taken.Contains(a))
            .Select(a => a!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
