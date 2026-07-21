namespace PenumbraOrganizer.Plugin.Organizer;

// Description is a human-readable summary of what's customized, rendered by the UI next to
// each unchecked customized-empty entry.
public sealed record CustomizedFolder(string Path, string Description);

public static class OrganizationCleanupPlanner
{
    // Parent-folder extraction for Penumbra virtual paths (forward-slash separated —
    // System.IO.Path assumes the OS separator and is not safe here). A path with no '/' is a
    // root-level mod: it occupies no folder, so this returns null rather than an empty string
    // or the mod's own name. Trailing slashes are trimmed defensively; a leading slash falls
    // out of the index > 0 check (index 0 → null, treated as root-level).
    public static string? GetVirtualParent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index > 0 ? trimmed[..index] : null;
    }

    // Applied to a folder path before storing it as protected, and before comparing a live
    // scanned path's virtual parent against stored protected folders, so both sides of every
    // comparison go through the same normalization. The only way a path enters protected-folder
    // storage today is via a checkbox next to an already-GetVirtualParent-derived value (never
    // free-typed), which narrows the practical need for this — kept anyway to defend a
    // persisted path from a prior session against a live one that could differ in incidental
    // formatting.
    public static string? NormalizeFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var withForwardSlashes = path.Replace('\\', '/');
        var collapsed = System.Text.RegularExpressions.Regex.Replace(withForwardSlashes, "/+", "/");
        var trimmed = collapsed.Trim('/', ' ');
        return trimmed.Length == 0 ? null : trimmed;
    }

    // Recursive, boundary-safe: a mod under "Gear/Feet/Sub" is protected by "Gear/Feet". Never a
    // bare StartsWith — mirrors IsOccupied's exact rule below ("Body" must not match
    // "BodyMods/Author").
    public static bool IsUnderAnyProtectedFolder(string currentPath, IReadOnlySet<string> protectedFolders)
    {
        var parent = GetVirtualParent(currentPath);
        if (parent is null)
            return false;
        return protectedFolders.Any(folder =>
            parent.Equals(folder, StringComparison.Ordinal) ||
            parent.StartsWith(folder + "/", StringComparison.Ordinal));
    }

    public static (IReadOnlyList<string> PlainEmpty, IReadOnlyList<CustomizedFolder> CustomizedEmpty)
        DetectOrphaned(OrganizationJson data, IReadOnlySet<string> occupiedFolders)
    {
        var plain = new List<string>();
        var customized = new List<CustomizedFolder>();

        foreach (var (path, folder) in data.Folders)
        {
            if (IsOccupied(path, occupiedFolders))
                continue;

            if (IsPlain(folder))
                plain.Add(path);
            else
                customized.Add(new CustomizedFolder(path, DescribeCustomization(folder)));
        }

        plain.Sort(StringComparer.Ordinal);
        customized.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (plain, customized);
    }

    public static OrganizationJson Prune(OrganizationJson data, IReadOnlySet<string> selectedPaths)
    {
        var remaining = data.Folders
            .Where(kvp => !selectedPaths.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

        return new OrganizationJson
        {
            Version = data.Version,
            Folders = remaining,
            Separators = data.Separators,
            ExtensionData = data.ExtensionData,
        };
    }

    // Occupied when it equals, or is a segment-boundary-safe ancestor of, any occupied folder.
    // Never a bare StartsWith — "Body" must not match "BodyMods/Author".
    private static bool IsOccupied(string folder, IReadOnlySet<string> occupiedFolders) =>
        occupiedFolders.Any(occupied =>
            occupied.Equals(folder, StringComparison.Ordinal) ||
            occupied.StartsWith(folder + "/", StringComparison.Ordinal));

    private static bool IsPlain(FolderData folder) =>
        folder.ExpandedColor is null &&
        folder.CollapsedColor is null &&
        folder.SortMode is null &&
        folder.IsSeparator is null &&
        (folder.ExtensionData is null || folder.ExtensionData.Count == 0);

    private static string DescribeCustomization(FolderData folder)
    {
        var parts = new List<string>();
        if (folder.ExpandedColor is not null || folder.CollapsedColor is not null)
            parts.Add("custom color");
        if (folder.SortMode is not null)
            parts.Add($"sort: {folder.SortMode}");
        if (folder.IsSeparator is not null)
            parts.Add("separator flag");
        if (folder.ExtensionData is { Count: > 0 })
            parts.Add("unknown settings");
        return string.Join(", ", parts);
    }
}
