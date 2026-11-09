namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// What the sorter does with the mods a tool (Heliosphere or Proteus) made or installed. Both tools
/// find their mods by directory, not by Penumbra path, so moving them is safe; the modes choose how
/// much to move. The numeric values are what the config stores.
/// </summary>
public enum ToolMode
{
    /// <summary>Never moved. The default.</summary>
    Protected = 0,

    /// <summary>Moved only beneath the folder they already sit in.</summary>
    SortInside = 1,

    /// <summary>Sorted like any other mod.</summary>
    Normal = 2,
}

/// <summary>How a mod relates to Proteus.</summary>
public enum ProteusRole
{
    None = 0,

    /// <summary>Made by Proteus: its meta.json author is exactly "Proteus".</summary>
    Made = 1,

    /// <summary>Proteus's own managed output mod (directory "Proteus", author "Proteus").</summary>
    Managed = 2,
}

/// <summary>The saved tool settings the protection rule and the sort consult.</summary>
/// <param name="SortInsideFolders">
/// Folders (any depth, slash paths) the user keeps as the top level while their mods are sorted beneath them
/// (case-insensitive). Never contains one of the sorter's own folders.
/// </param>
// SortInsideFolders is compared by reference (record equality); null means none.
public readonly record struct ToolPolicy(
    ToolMode Heliosphere, ToolMode Proteus, bool ProteusManagedAtRoot,
    IReadOnlySet<string>? SortInsideFolders = null)
{
    public static ToolPolicy Default => new(ToolMode.Protected, ToolMode.Protected, false);

    /// <summary>A stored value this build does not know reads as Protected, the safe default.</summary>
    public static ToolMode Normalize(ToolMode mode) =>
        Enum.IsDefined(mode) ? mode : ToolMode.Protected;

    public static ToolPolicy From(
        int heliosphere, int proteus, bool managedAtRoot, IEnumerable<string>? sortInsideFolders = null)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in sortInsideFolders ?? [])
        {
            if (ToolFolders.NormalizeChosenFolder(folder) is { } normalized)
                folders.Add(normalized);
        }
        return new(Normalize((ToolMode)heliosphere), Normalize((ToolMode)proteus), managedAtRoot,
            folders.Count == 0 ? null : folders);
    }
}
