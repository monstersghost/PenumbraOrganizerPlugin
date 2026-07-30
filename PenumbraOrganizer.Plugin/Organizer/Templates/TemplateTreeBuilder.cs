namespace PenumbraOrganizer.Plugin.Organizer.Templates;

public sealed record TemplateTreeNode(
    string Segment,
    string FullPath,
    int DirectCount,
    int TotalCount,
    IReadOnlyList<TemplateTreeNode> Children);

/// <summary>
/// Builds the preview tree from a template's declared folders plus a plan's per-folder counts.
/// Kept out of the draw method deliberately: nesting, intermediate parents and count roll-up are
/// real logic, and inside an ImGui frame they would be untestable.
///
/// The two inputs are different sets on purpose -- a template can declare an empty bucket the
/// author wants an importer to fill in themselves, and a plan can place mods in a folder the
/// declared list never mentioned. Both appear.
/// </summary>
public static class TemplateTreeBuilder
{
    private sealed class Builder
    {
        public int DirectCount;
        public readonly SortedDictionary<string, Builder> Children = new(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<TemplateTreeNode> Build(
        IEnumerable<string> folders,
        IReadOnlyDictionary<string, int> folderCounts)
    {
        var roots = new SortedDictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
            Ensure(roots, folder);

        foreach (var (folder, count) in folderCounts)
            Ensure(roots, folder).DirectCount = count;

        return Materialize(roots, parentPath: string.Empty);
    }

    private static Builder Ensure(SortedDictionary<string, Builder> roots, string folder)
    {
        var level = roots;
        Builder? current = null;

        foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!level.TryGetValue(segment, out var next))
                level[segment] = next = new Builder();

            current = next;
            level = next.Children;
        }

        // A folder that is empty or all separators has no node of its own; returning a throwaway
        // keeps the caller's assignment harmless rather than needing a null check.
        return current ?? new Builder();
    }

    private static IReadOnlyList<TemplateTreeNode> Materialize(
        SortedDictionary<string, Builder> level, string parentPath)
    {
        var nodes = new List<TemplateTreeNode>(level.Count);
        foreach (var (segment, builder) in level)
        {
            var fullPath = parentPath.Length == 0 ? segment : $"{parentPath}/{segment}";
            var children = Materialize(builder.Children, fullPath);
            var total = builder.DirectCount + children.Sum(child => child.TotalCount);

            nodes.Add(new TemplateTreeNode(segment, fullPath, builder.DirectCount, total, children));
        }

        return nodes;
    }
}
