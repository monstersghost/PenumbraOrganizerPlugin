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

        var byCurrentPath = moves.ToDictionary(m => m.CurrentPath, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<ApplyStep>();
        var groupId = 0;

        foreach (var start in moves.OrderBy(m => m.Identifier, StringComparer.Ordinal))
        {
            if (visited.Contains(start.CurrentPath))
                continue;

            var chain = new List<ModMove>();
            ModMove? cursor = start;
            while (cursor is not null && visited.Add(cursor.CurrentPath))
            {
                chain.Add(cursor);
                byCurrentPath.TryGetValue(cursor.TargetPath, out cursor);
            }

            // Each emitted component appends its steps as one contiguous block, then bumps groupId -
            // so GroupIds are 0-based and every group occupies a contiguous StepIndex range once these
            // steps are numbered by OperationPlan (design doc section 3).
            //
            // `cursor` is non-null only if it looped back into an already-visited path. Given the
            // uniqueness guarantees above, that path can only be this chain's own start (no other
            // chain's target can coincide with it) - so a non-null cursor here always means a cycle.
            if (cursor is null)
            {
                for (var i = chain.Count - 1; i >= 0; i--)
                    steps.Add(new ApplyStep(chain[i].Identifier, chain[i].TargetPath, IsTemporary: false, GroupId: groupId));
            }
            else
            {
                steps.Add(new ApplyStep(chain[0].Identifier, temporaryPathFactory(chain[0]), IsTemporary: true, GroupId: groupId));
                for (var i = chain.Count - 1; i >= 1; i--)
                    steps.Add(new ApplyStep(chain[i].Identifier, chain[i].TargetPath, IsTemporary: false, GroupId: groupId));
                steps.Add(new ApplyStep(chain[0].Identifier, chain[0].TargetPath, IsTemporary: false, GroupId: groupId));
            }

            groupId++;
        }

        return steps;
    }
}
