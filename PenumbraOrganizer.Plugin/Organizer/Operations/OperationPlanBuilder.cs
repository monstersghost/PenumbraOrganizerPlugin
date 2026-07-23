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
}
