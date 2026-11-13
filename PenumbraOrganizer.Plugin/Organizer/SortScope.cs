namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Which rows a sort may touch. A run-time choice, never persisted and never a protect rule.
/// </summary>
public sealed record SortScope(bool RootOnly, IReadOnlySet<string>? Identifiers = null)
{
    public static SortScope WholeLibrary { get; } = new(false);

    public bool Includes(OrganizerModRow row) =>
        !row.Protected
        && (!RootOnly || IsAtRoot(row.CurrentPath))
        && (Identifiers?.Contains(row.Identifier) ?? true);

    public static bool IsAtRoot(string currentPath) =>
        OrganizationCleanupPlanner.GetVirtualParent(currentPath) is null;
}
