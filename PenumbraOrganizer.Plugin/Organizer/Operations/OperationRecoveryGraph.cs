namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum OperationRecoveryGraphStatus { SingleAuthoritative, MultipleDisconnectedRoots, CycleDetected }

public sealed record OperationRecoveryGraphResult(
    OperationRecoveryGraphStatus Status,
    IReadOnlyList<Guid> AuthoritativeOperationIds,
    IReadOnlyList<Guid> AllOperationIds);

/// <summary>
/// Design doc section 4a, steps 2-5: given a set of already-loaded, already-confirmed-non-terminal
/// journals, find which one (or which several, if genuinely ambiguous) is operationally
/// authoritative after a crash. A RecoveryOfOperationId only forms a graph edge if the referenced
/// parent is present in this same input set - a parent that already terminalized cleanly (the
/// common case) simply isn't in the "non-terminal journals" list, so its child is its own
/// single-node component, not a dangling edge.
/// </summary>
public static class OperationRecoveryGraph
{
    public static OperationRecoveryGraphResult Analyze(IReadOnlyList<OperationJournal> journals)
    {
        var idSet = journals.Select(j => j.OperationId).ToHashSet();
        var allIds = idSet.ToList();

        // Only edges where the parent is also in this set count for graph structure.
        var childToParent = journals
            .Where(j => j.RecoveryOfOperationId is { } parentId && idSet.Contains(parentId))
            .ToDictionary(j => j.OperationId, j => j.RecoveryOfOperationId!.Value);

        if (TryFindCycle(childToParent, out var cycleMembers))
            return new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.CycleDetected, cycleMembers.ToList(), allIds);

        // A journal is a leaf (authoritative within its component) if no other journal in the set
        // points at it as a parent.
        var referencedAsParent = childToParent.Values.ToHashSet();
        var leaves = allIds.Where(id => !referencedAsParent.Contains(id)).ToList();

        return leaves.Count switch
        {
            1 => new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, leaves, allIds),
            _ => new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, leaves, allIds),
        };
    }

    private static bool TryFindCycle(Dictionary<Guid, Guid> childToParent, out HashSet<Guid> cycleMembers)
    {
        if (!TryFindCoreCycle(childToParent, out var coreCycle))
        {
            cycleMembers = [];
            return false;
        }

        // A single walk only discovers the core cyclic nodes themselves (e.g. {B, C}). Any number of
        // separate, structurally-identical non-cyclic "tails" can feed into that same cycle from
        // different directions (e.g. D -> B and E -> C), and which one (if any) the first walk
        // happened to pass through depends on Dictionary key enumeration order - not on graph
        // structure. Do a second, full pass over every node with a parent edge and include it (and
        // everything on its walk-up) whenever that walk reaches the core cycle. This makes the
        // reported set the complete, order-independent set of every journal that transitively feeds
        // into the cycle.
        cycleMembers = new HashSet<Guid>(coreCycle);
        foreach (var start in childToParent.Keys)
        {
            var walked = new HashSet<Guid>();
            var current = start;
            while (true)
            {
                if (coreCycle.Contains(current))
                {
                    cycleMembers.UnionWith(walked);
                    cycleMembers.Add(current);
                    break;
                }

                if (!walked.Add(current))
                    break; // a different, disjoint cycle - not connected to coreCycle, stop walking it here.

                if (!childToParent.TryGetValue(current, out var parent))
                    break; // walked off the set without ever reaching the core cycle.

                current = parent;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds ONE genuine cycle (the nodes that actually repeat when walking parent edges) and
    /// returns just that core cyclic node set - not the entire path walked to discover it.
    /// </summary>
    private static bool TryFindCoreCycle(Dictionary<Guid, Guid> childToParent, out HashSet<Guid> coreCycle)
    {
        var globallyResolved = new HashSet<Guid>();

        foreach (var start in childToParent.Keys)
        {
            if (globallyResolved.Contains(start))
                continue;

            var pathOrder = new List<Guid>();
            var pathIndex = new Dictionary<Guid, int>();
            var current = start;
            while (childToParent.TryGetValue(current, out var parent))
            {
                if (pathIndex.TryGetValue(current, out var repeatIndex))
                {
                    // Only the nodes from the first occurrence of the repeated node onward form the
                    // actual cycle - everything before that is a non-cyclic tail leading into it.
                    coreCycle = pathOrder.Skip(repeatIndex).ToHashSet();
                    return true;
                }

                pathIndex[current] = pathOrder.Count;
                pathOrder.Add(current);
                current = parent;
            }

            pathOrder.Add(current); // the terminal node this path walked up to (not itself a child of anything in-set)
            globallyResolved.UnionWith(pathOrder);
        }

        coreCycle = [];
        return false;
    }
}
