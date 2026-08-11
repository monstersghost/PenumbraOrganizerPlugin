namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Which mods and folders the author has chosen to publish. Holds all of the export screen's
/// decisions so none of them live in the draw call, where they could not be tested.
/// </summary>
/// <remarks>
/// Everything starts included. That is the only defensible default for a screen whose job is to let
/// the author take things OUT: starting empty would make "export" mean "export nothing" for anyone
/// who skimmed past the screen, and the reviewed list would not resemble their library.
/// <para>
/// The search filter deliberately lives outside this type. Filtering narrows what is DISPLAYED and
/// must never change what is included -- otherwise a filtered "exclude all" silently drops every row
/// the author could not see. <see cref="MatchesFilter"/> exists for the UI to filter with; nothing
/// here consults it.
/// </para>
/// </remarks>
public sealed class TemplateExportSelection
{
    private readonly Dictionary<string, string?> _folderByIdentifier;
    private readonly HashSet<string> _includedIdentifiers;
    private readonly HashSet<string> _includedFolders;

    public TemplateExportSelection(
        IReadOnlyCollection<OrganizerModRow> rows,
        IReadOnlyCollection<string> allFolders)
    {
        _folderByIdentifier = rows.ToDictionary(
            row => row.Identifier,
            row => OrganizationCleanupPlanner.GetVirtualParent(row.CurrentPath),
            StringComparer.Ordinal);

        _includedIdentifiers = new HashSet<string>(_folderByIdentifier.Keys, StringComparer.Ordinal);
        _includedFolders = new HashSet<string>(allFolders, StringComparer.Ordinal);
    }

    public IReadOnlySet<string> IncludedIdentifiers => _includedIdentifiers;

    public IReadOnlyCollection<string> IncludedFolders =>
        [.. _includedFolders.OrderBy(f => f, StringComparer.Ordinal)];

    public int IncludedRowCount => _includedIdentifiers.Count;

    public int ExcludedRowCount => _folderByIdentifier.Count - _includedIdentifiers.Count;

    public int IncludedFolderCount => _includedFolders.Count;

    public bool IsRowIncluded(string identifier) => _includedIdentifiers.Contains(identifier);

    public bool IsFolderIncluded(string folder) => _includedFolders.Contains(folder);

    public void SetRow(string identifier, bool included)
    {
        if (!_folderByIdentifier.ContainsKey(identifier))
            return;

        if (included)
            _includedIdentifiers.Add(identifier);
        else
            _includedIdentifiers.Remove(identifier);
    }

    public void SetAllRows(bool included)
    {
        if (included)
            _includedIdentifiers.UnionWith(_folderByIdentifier.Keys);
        else
            _includedIdentifiers.Clear();
    }

    /// <summary>
    /// Toggles the folder itself and every mod at or beneath it. One control, because a folder the
    /// author excludes should not keep publishing its contents through the entry list.
    /// </summary>
    public void SetFolder(string folder, bool included)
    {
        if (included)
            _includedFolders.Add(folder);
        else
            _includedFolders.Remove(folder);

        foreach (var (identifier, rowFolder) in _folderByIdentifier)
        {
            if (rowFolder is not null && IsAtOrUnder(rowFolder, folder))
                SetRow(identifier, included);
        }
    }

    // Boundary-safe, matching OrganizationCleanupPlanner.IsUnderAnyProtectedFolder: "Gear" covers
    // "Gear/Head" but not "Gearbox".
    private static bool IsAtOrUnder(string candidate, string folder) =>
        string.Equals(candidate, folder, StringComparison.Ordinal)
        || candidate.StartsWith(folder + "/", StringComparison.Ordinal);

    /// <summary>
    /// The display filter. Case-insensitive substring, because an author scanning a 900-row list is
    /// looking for a name they half-remember, not writing a query.
    /// </summary>
    public static bool MatchesFilter(string name, string? query) =>
        string.IsNullOrWhiteSpace(query)
        || name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}
