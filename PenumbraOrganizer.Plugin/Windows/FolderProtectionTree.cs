using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>One folder in the Protect tab's tree.</summary>
/// <param name="Path">Full virtual path, e.g. "haloween/animation".</param>
/// <param name="Name">The last segment, which is what the row shows.</param>
public sealed record FolderTreeNode(string Path, string Name, IReadOnlyList<FolderTreeNode> Children)
{
    /// <summary>Every folder below this one, at any depth. Shown in the row's label.</summary>
    public int DescendantFolderCount => Children.Count + Children.Sum(c => c.DescendantFolderCount);
}

/// <summary>
/// Builds and draws the Protect tab's folder tree. The flat one-row-per-folder list it replaces
/// became unusable once mods were sorted into per-number subfolders: hundreds of fully-qualified
/// rows, nothing collapsible.
/// </summary>
public static class FolderProtectionTree
{
    /// <summary>
    /// Turns folder paths into a tree. Intermediate segments missing from the input become nodes
    /// too, so the tree has no holes; siblings are ordinal-sorted, matching the flat list's order.
    /// </summary>
    public static IReadOnlyList<FolderTreeNode> Build(IEnumerable<string> folderPaths)
    {
        var children = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var roots = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in folderPaths)
        {
            var normalized = OrganizationCleanupPlanner.NormalizeFolderPath(path);
            if (normalized is null)
                continue;

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length; i++)
            {
                var current = string.Join('/', segments.Take(i + 1));
                if (i == 0)
                {
                    roots.Add(current);
                    continue;
                }

                var parent = string.Join('/', segments.Take(i));
                if (!children.TryGetValue(parent, out var siblings))
                    children[parent] = siblings = new SortedSet<string>(StringComparer.Ordinal);
                siblings.Add(current);
            }
        }

        return roots.Select(path => BuildNode(path, children)).ToList();
    }

    private static FolderTreeNode BuildNode(string path, Dictionary<string, SortedSet<string>> children)
    {
        var childNodes = children.TryGetValue(path, out var paths)
            ? paths.Select(child => BuildNode(child, children)).ToList()
            : [];
        var name = path[(path.LastIndexOf('/') + 1)..];
        return new FolderTreeNode(path, name, childNodes);
    }

    /// <summary>
    /// Whether this branch survives the tab's search box: the node's own path matches, or some
    /// descendant's does. A branch kept only by a descendant is force-expanded when drawn, so the
    /// match is visible without digging.
    /// </summary>
    public static bool MatchesFilter(FolderTreeNode node, string filter)
    {
        if (filter.Length == 0)
            return true;
        return node.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.Children.Any(child => MatchesFilter(child, filter));
    }
}
