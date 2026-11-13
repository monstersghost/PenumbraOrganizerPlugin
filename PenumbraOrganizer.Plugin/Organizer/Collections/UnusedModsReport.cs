namespace PenumbraOrganizer.Plugin.Organizer.Collections;

public sealed record UnusedModEntry(
    string Identifier, string Name, string CurrentPath, bool Protected)
{
    public bool WillMove => !Protected;
}

/// <summary>Mods no readable collection enables.</summary>
public static class UnusedModsReport
{
    public const string ProtectedNote = "protected - will not move";

    public static IReadOnlyList<UnusedModEntry> Build(
        IEnumerable<OrganizerModRow> rows, IReadOnlySet<string> usedDirectories)
    {
        var used = new HashSet<string>(usedDirectories, StringComparer.OrdinalIgnoreCase);
        return rows
            .Where(row => !used.Contains(row.Identifier))
            .Select(row => new UnusedModEntry(row.Identifier, row.Name, row.CurrentPath, row.Protected))
            .OrderBy(entry => entry.CurrentPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
