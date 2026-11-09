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
        return anchor is null ? normalPath : ApplyAnchorTo(anchor, normalPath);
    }

    /// <summary>Prefixes a built path with the anchor unless it already starts with it (or equals it).</summary>
    public static string ApplyAnchorTo(string anchor, string normalPath)
    {
        if (string.Equals(normalPath, anchor, StringComparison.OrdinalIgnoreCase)
            || normalPath.StartsWith(anchor + "/", StringComparison.OrdinalIgnoreCase))
            return normalPath;

        return $"{anchor}/{normalPath}";
    }

    /// <summary>
    /// Trims each segment, drops empty ones and rejoins with "/". Null when nothing is left or the
    /// first segment is one of the sorter's own folders.
    /// </summary>
    public static string? NormalizeChosenFolder(string? folder)
    {
        if (folder is null)
            return null;
        var segments = folder.Split('/').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (segments.Length == 0 || SorterFolders.Contains(segments[0]))
            return null;
        return string.Join('/', segments);
    }

    /// <summary>
    /// The deepest chosen folder that is a path prefix of the mod's current folder (every segment but
    /// the leaf), spelled as the mod spells it. Null when none matches or the mod is under a sorter folder.
    /// </summary>
    public static string? DeepestAnchor(string currentPath, IReadOnlySet<string> chosenFolders)
    {
        var segments = currentPath.Split('/');
        var folderCount = segments.Length - 1;
        if (folderCount < 1 || SorterFolders.Contains(segments[0]))
            return null;

        for (var depth = folderCount; depth >= 1; depth--)
        {
            var candidate = string.Join('/', segments.Take(depth));
            if (chosenFolders.Contains(candidate))
                return candidate;
        }
        return null;
    }
}
