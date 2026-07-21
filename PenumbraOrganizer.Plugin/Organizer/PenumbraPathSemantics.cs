namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Mirrors the parts of Penumbra/Luna's filesystem-name semantics that decide what actually
/// persists across a save/load round-trip, so this plugin never proposes a "move" that Penumbra
/// would store as a no-op and then rearrange on the next Rediscover Mods.
///
/// Ground truth (Luna source, verified 2026-07-19):
/// - FileSystemUtility.FixName: every node name is whitespace-trimmed; '/' becomes '\';
///   empty becomes "&lt;None&gt;".
/// - FileSystemUtility.IsDuplicateName: any name ending in " (uint)" is a duplicate marker.
/// - DataPath.UpdateByNode: a leaf whose duplicate-marker base equals the mod's display name
///   persists SortName = null - the " (N)" suffix is DISCARDED on save, and reassigned in
///   arbitrary enumeration order by ModFileSystemSaver.CreateDataNodes on every load.
///
/// Consequence: " (N)" suffixes on a leaf that is otherwise the mod's own display name are
/// transient tie-breakers, not identity. Two paths differing only by such a suffix are the
/// same persisted location.
/// </summary>
public static class PenumbraPathSemantics
{
    /// <summary> Mirror of Luna's FixName: trim, '/' to '\', empty to "&lt;None&gt;". </summary>
    public static string FixName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "<None>";
        return trimmed.Replace('/', '\\');
    }

    /// <summary>
    /// Mirror of Luna's IsDuplicateName: true when the name has the exact form
    /// "Base (number)" - trailing ')', matching " (", non-empty unsigned-integer content.
    /// </summary>
    public static bool IsDuplicateName(string name, out string baseName)
    {
        baseName = name;
        if (!name.EndsWith(')'))
            return false;

        var idx = name.LastIndexOf('(');
        if (idx < 2 || idx == name.Length - 2 || name[idx - 1] != ' ')
            return false;

        if (!uint.TryParse(name.AsSpan(idx + 1, name.Length - idx - 2), out _))
            return false;

        baseName = name[..(idx - 1)];
        return true;
    }

    /// <summary>
    /// True when two full virtual paths resolve to the same persisted location for a mod with
    /// the given display name: same folder, and leaves identical after reducing a transient
    /// " (N)" duplicate marker whose base is the display name. Comparison is OrdinalIgnoreCase,
    /// matching Penumbra's own sibling-uniqueness comparer.
    /// </summary>
    public static bool AreEquivalent(string currentPath, string proposedPath, string displayName)
    {
        if (string.Equals(currentPath, proposedPath, StringComparison.OrdinalIgnoreCase))
            return true;

        SplitPath(currentPath, out var currentFolder, out var currentLeaf);
        SplitPath(proposedPath, out var proposedFolder, out var proposedLeaf);

        if (!string.Equals(currentFolder, proposedFolder, StringComparison.OrdinalIgnoreCase))
            return false;

        var display = FixName(displayName);
        return string.Equals(
            LeafToken(currentLeaf, display), LeafToken(proposedLeaf, display),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Canonical form of a full virtual path for a mod with the given display name: same value
    /// for any two paths <see cref="AreEquivalent"/> would call equivalent. Used wherever paths
    /// need to be hashed or grouped rather than compared pairwise (operation plan integrity hash).
    /// </summary>
    public static string Normalize(string path, string displayName)
    {
        SplitPath(path, out var folder, out var leaf);
        var display = FixName(displayName);
        var token = LeafToken(leaf, display);
        return (folder + "\\" + token).ToLowerInvariant();
    }

    /// <summary>
    /// The persisted identity of a leaf name, per DataPath.UpdateByNode:
    /// - a leaf equal to the display name persists as SortName = null (checked FIRST, so a mod
    ///   genuinely named "Foo (2)" keeps that identity);
    /// - otherwise a duplicate-marked leaf persists as its base (the marker is stripped even
    ///   for custom names - UpdateByNode's duplicate branch ends "SortName = baseName");
    /// - anything else persists as-is.
    /// </summary>
    private static string LeafToken(string leaf, string fixedDisplayName)
    {
        var trimmed = leaf.Trim();
        if (string.Equals(trimmed, fixedDisplayName, StringComparison.OrdinalIgnoreCase))
            return fixedDisplayName;

        if (IsDuplicateName(trimmed, out var baseName))
            return string.Equals(baseName, fixedDisplayName, StringComparison.OrdinalIgnoreCase)
                ? fixedDisplayName
                : baseName;

        return trimmed;
    }

    private static void SplitPath(string path, out string folder, out string leaf)
    {
        var idx = path.LastIndexOf('/');
        if (idx < 0)
        {
            folder = string.Empty;
            leaf = path;
        }
        else
        {
            folder = path[..idx];
            leaf = path[(idx + 1)..];
        }
    }
}
