namespace PenumbraOrganizer.Plugin.Organizer;

public sealed record BackupEntry(string Identifier, string PreviousPath);

public sealed record ApplyResult(string Identifier, bool Success, string? FailureReason);

public sealed record ModMove(string Identifier, string CurrentPath, string TargetPath);

public sealed record ApplyStep(string Identifier, string TargetPath, bool IsTemporary, int GroupId);

public static class ApplyPlanner
{
    public static IReadOnlyList<BackupEntry> BuildBackup(IReadOnlyList<OrganizerModRow> touchedRows) =>
        touchedRows
            .GroupBy(r => r.Identifier, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(r => r.Identifier, StringComparer.Ordinal)
            .Select(r => new BackupEntry(r.Identifier, r.CurrentPath))
            .ToList();

    public static IReadOnlySet<string> BlockingIdentifiers(ReviewResult validation)
    {
        var identifiers = new HashSet<string>(validation.ProtectedViolations, StringComparer.Ordinal);
        foreach (var group in validation.PathCollisions.Values)
            identifiers.UnionWith(group);
        return identifiers;
    }

    public static IReadOnlyList<BackupEntry> Retain(
        IReadOnlyList<BackupEntry> entries, IReadOnlyList<ApplyResult> results, bool keepSuccessful)
    {
        var resultsById = results.ToDictionary(r => r.Identifier, StringComparer.Ordinal);
        return entries
            .Where(e => resultsById.TryGetValue(e.Identifier, out var result) && result.Success == keepSuccessful)
            .ToList();
    }

    // Penumbra's virtual filesystem gives folders and mod leaves the same name-uniqueness
    // namespace (OtterGui.Filesystem.FileSystem<T>): SetModPath fails with the opaque
    // PathRenameFailed if a ProposedPath's full path already belongs to an existing (often
    // orphaned/empty) folder entry in organization.json. Existing-folder membership check is
    // caller-driven via the comparer on existingFolderPaths, so it can match Penumbra's own
    // OrdinalIgnoreCase sibling comparer.
    public static IReadOnlyList<string> FolderPathCollisions(
        IReadOnlyList<OrganizerModRow> touchedRows, IReadOnlySet<string> existingFolderPaths) =>
        touchedRows
            .Where(r => existingFolderPaths.Contains(r.ProposedPath))
            .Select(r => r.Identifier)
            .ToList();

    // Penumbra's virtual filesystem gives every touched mod's CurrentPath and TargetPath the same
    // name-uniqueness namespace as every other touched mod, so a naive single pass over `moves`
    // deadlocks whenever two or more moves form a swap/rotation (A's target is B's current slot,
    // and B's target is - directly or via a longer chain - A's current slot): at the moment each
    // is applied, its target is still occupied by another not-yet-moved member of the same cycle,
    // and every member of that cycle fails with Penumbra's opaque PathRenameFailed.
    //
    // Because CollisionDisambiguator already guarantees every touched row's ProposedPath is
    // unique, and every touched row's CurrentPath is inherently unique (two mods can't already
    // occupy the same real Penumbra path), the move graph (CurrentPath -> TargetPath edges)
    // decomposes cleanly into disjoint simple chains (ending at a path nothing in this batch
    // currently occupies) and disjoint simple cycles. Chains are safe to resolve by processing
    // in reverse, so each target is vacated before something moves into it. Cycles have no such
    // free starting point, so one member is routed through a temporary path first to break the
    // deadlock, then the rest of the cycle drains in reverse, then the parked mod completes its
    // move into its real target once that slot has been freed.
    public static IReadOnlyList<ApplyStep> OrderMovesForApply(
        IReadOnlyList<ModMove> moves, Func<ModMove, string>? temporaryPathFactory = null)
    {
        temporaryPathFactory ??= m => $"{m.CurrentPath}__organizer_apply_tmp__{Guid.NewGuid():N}";

        // A mod sitting at a path that another move needs as a FOLDER ("Body/Bibo+" the mod, while
        // the sort files mods under "Body/Bibo+/..."). Mods and folders share one namespace, so
        // nothing can move beneath that path until the mod has left it. Every such mod parks at a
        // temporary path first. All parking happens up front, in the first group, so that no
        // blocker's final move can run while another blocker still sits where a folder is needed.
        var blockers = moves
            .Where(m => moves.Any(o => o.TargetPath.StartsWith(m.CurrentPath + "/", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.Identifier, StringComparer.Ordinal)
            .ToList();
        var parkedAt = blockers.ToDictionary(m => m.Identifier, temporaryPathFactory, StringComparer.Ordinal);

        // From here a parked blocker "is" at its temp path: its original path is free, which is also
        // what breaks any cycle it would have been part of.
        string EffectiveCurrent(ModMove m) => parkedAt.TryGetValue(m.Identifier, out var temp) ? temp : m.CurrentPath;

        var byCurrentPath = moves.ToDictionary(EffectiveCurrent, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<(List<ModMove> Chain, bool IsCycle)>();

        foreach (var start in moves.OrderBy(m => m.Identifier, StringComparer.Ordinal))
        {
            if (visited.Contains(EffectiveCurrent(start)))
                continue;

            var chain = new List<ModMove>();
            ModMove? cursor = start;
            while (cursor is not null && visited.Add(EffectiveCurrent(cursor)))
            {
                chain.Add(cursor);
                byCurrentPath.TryGetValue(cursor.TargetPath, out cursor);
            }

            // `cursor` is non-null only if it looped back into an already-visited path. Given the
            // uniqueness guarantees above, that path can only be this chain's own start (no other
            // chain's target can coincide with it) - so a non-null cursor here always means a cycle.
            components.Add((chain, cursor is not null));
        }

        var steps = new List<ApplyStep>();
        var groupId = 0;

        // Each component's steps are one contiguous block with one GroupId (design doc section 3).
        // Components that contain a blocker, and every parking step, form group 0.
        void Emit((List<ModMove> Chain, bool IsCycle) component, int group)
        {
            var chain = component.Chain;
            if (!component.IsCycle)
            {
                for (var i = chain.Count - 1; i >= 0; i--)
                    steps.Add(new ApplyStep(chain[i].Identifier, chain[i].TargetPath, IsTemporary: false, GroupId: group));
                return;
            }

            steps.Add(new ApplyStep(chain[0].Identifier, temporaryPathFactory(chain[0]), IsTemporary: true, GroupId: group));
            for (var i = chain.Count - 1; i >= 1; i--)
                steps.Add(new ApplyStep(chain[i].Identifier, chain[i].TargetPath, IsTemporary: false, GroupId: group));
            steps.Add(new ApplyStep(chain[0].Identifier, chain[0].TargetPath, IsTemporary: false, GroupId: group));
        }

        if (blockers.Count > 0)
        {
            foreach (var blocker in blockers)
                steps.Add(new ApplyStep(blocker.Identifier, parkedAt[blocker.Identifier], IsTemporary: true, GroupId: groupId));

            foreach (var component in components.Where(c => c.Chain.Any(m => parkedAt.ContainsKey(m.Identifier))))
                Emit(component, groupId);

            groupId++;
            components = [.. components.Where(c => !c.Chain.Any(m => parkedAt.ContainsKey(m.Identifier)))];
        }

        foreach (var component in components)
        {
            Emit(component, groupId);
            groupId++;
        }

        return steps;
    }
}
