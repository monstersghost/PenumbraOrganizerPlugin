using Dalamud.Bindings.ImGui;
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
        if (string.IsNullOrEmpty(filter))
            return true;
        return node.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.Children.Any(child => MatchesFilter(child, filter));
    }

    /// <summary>
    /// Draws the tree. Each row carries one tri-state control that cycles
    /// inherit -> protect -> exclude -> inherit; the state is drawn dimmed when it comes from an
    /// ancestor rather than this folder's own rule, so a user can tell what they have set from what
    /// they have inherited.
    /// </summary>
    /// <param name="nodes">Roots from <see cref="Build"/>.</param>
    /// <param name="resolve">A folder's effective rule and the folder that supplied it.</param>
    /// <param name="counts">Per-folder mod counts, from BuildProtectedModCountsByFolder.</param>
    /// <param name="knownFolders">Folders that currently hold mods; others are labelled empty.</param>
    /// <param name="filter">The tab's search text. Non-matching branches are hidden; matching ones
    /// are force-expanded without touching <paramref name="expanded"/>.</param>
    /// <param name="expanded">
    /// Open folders, persisted. Mutated in place as the user opens and closes nodes; the caller
    /// saves the config when <c>true</c> comes back.
    /// </param>
    /// <param name="onCycle">Called with the folder and the rule it should now carry.</param>
    /// <param name="onUnprotectContents">Called with the folder whose contents should be cleared.</param>
    /// <returns>True when <paramref name="expanded"/> changed and should be persisted.</returns>
    public static bool Draw(
        IReadOnlyList<FolderTreeNode> nodes,
        Func<string, OrganizerState.FolderRuleResolution> resolve,
        IReadOnlyDictionary<string, OrganizerState.FolderProtectionCounts> counts,
        IReadOnlySet<string> knownFolders,
        string filter,
        HashSet<string> expanded,
        Action<string, FolderProtectionRule> onCycle,
        Action<string> onUnprotectContents)
    {
        var expansionChanged = false;
        foreach (var node in nodes)
            DrawNode(node, resolve, counts, knownFolders, filter, expanded, onCycle, onUnprotectContents,
                ref expansionChanged);
        return expansionChanged;
    }

    private static void DrawNode(
        FolderTreeNode node,
        Func<string, OrganizerState.FolderRuleResolution> resolve,
        IReadOnlyDictionary<string, OrganizerState.FolderProtectionCounts> counts,
        IReadOnlySet<string> knownFolders,
        string filter,
        HashSet<string> expanded,
        Action<string, FolderProtectionRule> onCycle,
        Action<string> onUnprotectContents,
        ref bool expansionChanged)
    {
        if (!MatchesFilter(node, filter))
            return;

        var resolution = resolve(node.Path);
        var ownsRule = string.Equals(resolution.Source, node.Path, StringComparison.Ordinal);

        // The tri-state control, drawn before the tree node so the whole row reads
        // "[state] name (counts)". ImGui's Checkbox is two-state, so this is a small button
        // whose glyph carries the state: solid when this folder owns the rule, dimmed when the
        // state is inherited from an ancestor.
        var glyph = resolution.Rule switch
        {
            FolderProtectionRule.Protected => "[x]",
            FolderProtectionRule.Excluded => "[-]",
            _ => "[ ]",
        };
        // Push/Pop rather than ImRaii.PushColor: PushStyleColor paired by hand is this codebase's
        // only colour idiom (see MainWindow.TextColoredWrapped), and ImRaii.PushColor is not used
        // anywhere here, so its availability in this Dalamud version is unverified.
        var dim = !ownsRule && resolution.Rule != FolderProtectionRule.None;
        if (dim)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        var cycled = ImGui.SmallButton($"{glyph}###protect-folder-cycle-{node.Path}");
        if (dim)
            ImGui.PopStyleColor();
        if (cycled)
            onCycle(node.Path, NextRule(ownsRule ? resolution.Rule : FolderProtectionRule.None));

        if (ImGui.IsItemHovered())
        {
            var state = resolution.Rule switch
            {
                FolderProtectionRule.Protected when ownsRule => "Protected by this folder.",
                FolderProtectionRule.Protected => $"Protected by \"{resolution.Source}\".",
                FolderProtectionRule.Excluded when ownsRule => "Excluded by this folder: sorting may move these mods.",
                FolderProtectionRule.Excluded => $"Excluded by \"{resolution.Source}\": sorting may move these mods.",
                _ => "No rule: not protected by any folder.",
            };
            ImGui.SetTooltip($"{state}\nClick to cycle protect / exclude / inherit.");
        }

        ImGui.SameLine();

        var folderCounts = counts.GetValueOrDefault(node.Path);
        var label = knownFolders.Contains(node.Path)
            ? $"{node.Name}  ({folderCounts.Protected}/{folderCounts.Total} protected"
                + (node.DescendantFolderCount > 0 ? $", {node.DescendantFolderCount} {(node.DescendantFolderCount == 1 ? "subfolder" : "subfolders")})" : ")")
            : $"{node.Name}  (currently empty)";

        // Forced open while a search is running so a match deeper in the branch is visible without
        // digging - and deliberately not written to `expanded`, so clearing the search restores
        // whatever the user had open.
        var searching = filter.Length > 0;
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth
            | (node.Children.Count == 0 ? ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen : ImGuiTreeNodeFlags.None);
        // Always, every frame, in both branches - this set is the single source of truth for which
        // nodes are open, the way a controlled input works. A once-only condition cannot do the
        // job: forcing a node open during a search writes that state into ImGui's own tree
        // storage, so a later Once sees a value already there, declines to reapply, and the node
        // stays open after the search clears - at which point the sync below would write it into
        // `expanded` and save it. A click still toggles correctly, because TreeNodeEx returns the
        // post-click state, the sync records it, and the next frame forces what was recorded.
        ImGui.SetNextItemOpen(searching || expanded.Contains(node.Path), ImGuiCond.Always);

        // "###" discards the label (which embeds live counts) from the ID hash, so the ID is stable.
        var open = ImGui.TreeNodeEx($"{label}###protect-folder-node-{node.Path}", flags);
        // On the label, not the cycle button: that button already shows a tooltip naming the rule's
        // source, and ImGui binds a tooltip to the last submitted item, so two on one item fight.
        Help.Tooltip(HelpTopics.ProtectFolderRuleCycle);
        if (!searching && node.Children.Count > 0 && open != expanded.Contains(node.Path))
        {
            if (open)
                expanded.Add(node.Path);
            else
                expanded.Remove(node.Path);
            expansionChanged = true;
        }

        if (folderCounts.Freeable > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Unprotect {folderCounts.Freeable} inside###protect-folder-clear-{node.Path}"))
                onUnprotectContents(node.Path);
            Help.Tooltip(HelpTopics.ProtectFolderUnprotectContents);
        }

        if (!open)
            return;

        foreach (var child in node.Children)
            DrawNode(child, resolve, counts, knownFolders, filter, expanded, onCycle, onUnprotectContents,
                ref expansionChanged);

        // Leaves pass NoTreePushOnOpen, so only a node with children pushed anything to pop.
        if (node.Children.Count > 0)
            ImGui.TreePop();
    }

    private static FolderProtectionRule NextRule(FolderProtectionRule current) => current switch
    {
        FolderProtectionRule.None => FolderProtectionRule.Protected,
        FolderProtectionRule.Protected => FolderProtectionRule.Excluded,
        _ => FolderProtectionRule.None,
    };
}
