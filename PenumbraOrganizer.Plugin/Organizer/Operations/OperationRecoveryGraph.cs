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
        var globallyResolved = new HashSet<Guid>();

        foreach (var start in childToParent.Keys)
        {
            if (globallyResolved.Contains(start))
                continue;

            var pathVisited = new HashSet<Guid>();
            var current = start;
            while (childToParent.TryGetValue(current, out var parent))
            {
                if (!pathVisited.Add(current))
                {
                    cycleMembers = pathVisited;
                    return true;
                }

                current = parent;
            }

            pathVisited.Add(current); // the terminal node this path walked up to (not itself a child of anything in-set)
            globallyResolved.UnionWith(pathVisited);
        }

        cycleMembers = [];
        return false;
    }
}
