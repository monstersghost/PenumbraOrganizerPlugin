using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// "Sort inside their folder": keeps a tool's mod under the folder it already sits in.
/// </summary>
/// <remarks>
/// The anchor is the first segment of the mod's CURRENT path, so it works in any language (Proteus
/// names its folder in the player's language) and respects where the user put the mod. Three cases
/// need no prefix: the mod is at the root, the anchor is one of the sorter's own folders (else
/// <c>Gear/Gear/...</c>), or the sorted path already starts with the anchor (Group by Creator).
/// </remarks>
public static class ToolFolders
{
    // Every folder the sorter itself creates at the top level: one per mod category, the combined
    // Animation and VFX parent, and the unclassified bucket.
    private static readonly HashSet<string> SorterFolders = new(
        [.. Enum.GetNames<ModCategory>(), "Animation and VFX", "Review"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a top-level folder name is one the sorter itself creates (case-insensitive).</summary>
    public static bool IsSorterFolder(string folderName) => SorterFolders.Contains(folderName);

    /// <summary>
    /// The top folder a mod would be kept under, or null when it has none (at the root, or already
    /// inside one of the sorter's own folders). Numbered layouts are built per anchor so a tool's
    /// bodies do not take numbers from the plain folders.
    /// </summary>
    public static string? AnchorOf(string currentPath)
    {
        var slash = currentPath.IndexOf('/');
        if (slash <= 0)
            return null;

        var anchor = currentPath[..slash];
        return SorterFolders.Contains(anchor) ? null : anchor;
    }

    public static string ApplyAnchor(string currentPath, string normalPath)
    {
        var anchor = AnchorOf(currentPath);
        if (anchor is null)
            return normalPath;

        var normalFirst = normalPath.Split('/')[0];
        if (string.Equals(normalFirst, anchor, StringComparison.OrdinalIgnoreCase))
            return normalPath;

        return $"{anchor}/{normalPath}";
    }
}
