namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Pure glue between OrganizerState's rows and an OperationPlan - reuses the already-battle-tested
/// ApplyPlanner.OrderMovesForApply for cycle-breaking/ordering, then translates its ApplyStep
/// output (Identifier, TargetPath, IsTemporary, GroupId) into OperationExecutionStep, and each
/// touched row into one OperationRecoveryTarget (one per identifier, not one per execution step -
/// a cycle-breaking plan has more steps than targets). No Dalamud dependency - fully unit-tested.
/// </summary>
public static class OperationPlanBuilder
{
    public static OperationPlan BuildApplyPlan(IReadOnlyList<OrganizerModRow> touchedRows)
    {
        var moves = touchedRows
            .Select(r => new ModMove(r.Identifier, r.CurrentPath, r.ProposedPath))
            .ToList();
        var applySteps = ApplyPlanner.OrderMovesForApply(moves);

        var executionSteps = applySteps
            .Select((s, index) => new OperationExecutionStep(
                index, s.Identifier, s.TargetPath,
                s.IsTemporary ? OperationStepKind.CycleBreakingTemporaryMove : OperationStepKind.FinalMove,
                s.GroupId))
            .ToList();

        var recoveryTargets = touchedRows
            .Select(r => new OperationRecoveryTarget(r.Identifier, r.CurrentPath, r.ProposedPath, r.Name))
            .ToList();

        return OperationPlan.Create(OperationType.Apply, executionSteps, recoveryTargets);
    }

    // currentMods is expected identifier-unique - the same invariant Plugin.cs's own
    // ReadCurrentModPaths() already relies on elsewhere (GetModListAdapter keys by Penumbra's own
    // directory identifier), but enforced explicitly here (unlike that existing call site) so a
    // violation fails with a clear diagnostic naming the offending identifiers, not a bare LINQ
    // ArgumentException from ToDictionary. Every move's identifier is guaranteed present in
    // currentMods by construction: RollbackHistory.BuildRestorePlan only ever emits a move for a
    // mod found in both the target snapshot and currentMods. The lookup below still throws with a
    // named identifier if that invariant is ever violated, rather than failing later with a bare
    // KeyNotFoundException.
    public static IReadOnlyList<NamedModMove> BuildNamedMoves(IReadOnlyList<ModMove> moves, IReadOnlyList<LiveMod> currentMods)
    {
        var duplicates = currentMods
            .GroupBy(m => m.Identifier, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Current mod list contains duplicate identifiers: {string.Join(", ", duplicates)}");

        var nameByIdentifier = currentMods.ToDictionary(m => m.Identifier, m => m.Name, StringComparer.Ordinal);
        return moves
            .Select(m => new NamedModMove(
                m.Identifier,
                nameByIdentifier.TryGetValue(m.Identifier, out var name)
                    ? name
                    : throw new InvalidOperationException($"Restore move for '{m.Identifier}' has no matching live mod."),
                m.CurrentPath, m.TargetPath))
            .ToList();
    }

    public static OperationPlan BuildRestoreOperationPlan(IReadOnlyList<NamedModMove> namedMoves)
    {
        // Check for duplicate identifiers before processing, so OperationPlan.Create's own
        // diagnostic is visible (not masked by ApplyPlanner's dictionary insert error).
        var seenIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in namedMoves)
        {
            if (!seenIdentifiers.Add(m.Identifier))
                throw new InvalidOperationException($"Duplicate recovery target identifier '{m.Identifier}'.");
        }

        var moves = namedMoves.Select(m => new ModMove(m.Identifier, m.CurrentPath, m.TargetPath)).ToList();
        var restoreSteps = ApplyPlanner.OrderMovesForApply(moves);

        var executionSteps = restoreSteps
            .Select((s, index) => new OperationExecutionStep(
                index, s.Identifier, s.TargetPath,
                s.IsTemporary ? OperationStepKind.CycleBreakingTemporaryMove : OperationStepKind.FinalMove,
                s.GroupId))
            .ToList();

        var recoveryTargets = namedMoves
            .Select(m => new OperationRecoveryTarget(m.Identifier, m.CurrentPath, m.TargetPath, m.ModName))
            .ToList();

        return OperationPlan.Create(OperationType.Restore, executionSteps, recoveryTargets);
    }
}

public sealed record NamedModMove(string Identifier, string ModName, string CurrentPath, string TargetPath);
