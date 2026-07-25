namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ContinuationPlanStatus { Ready, Blocked }

public enum ContinuationBlockReason { None, BlockingClassificationPresent, InconsistentRecoveryTargets, ReplanFailed }

public sealed record ContinuationPlanResult(
    ContinuationPlanStatus Status, IReadOnlyList<NamedModMove> ResidualMoves,
    ContinuationBlockReason Reason = ContinuationBlockReason.None);

/// <summary>
/// Design doc section 3. Computes the residual move set for Continue from an already-built
/// RecoveryAssessment - never re-classifies, never re-reads live state itself (the caller supplies
/// whichever assessment it wants evaluated). AtKnownIntermediate's validity is proven by attempting
/// ApplyPlanner.OrderMovesForApply on the full candidate set, the exact operation Continue would
/// actually perform - not by hand-enumerated consistency rules. Never throws for malformed input.
/// </summary>
public static class ContinuationPlanner
{
    public static ContinuationPlanResult TryBuildResidualMoves(OperationPlan interruptedPlan, RecoveryAssessment assessment)
    {
        // "Any blocking classification present" is enforced by the per-item switch below returning
        // Blocked immediately (discarding whatever candidateMoves had accumulated so far) rather than
        // by a separate upfront scan - one pass, same all-or-nothing result.
        var targetByIdentifier = new Dictionary<string, OperationRecoveryTarget>(StringComparer.Ordinal);
        foreach (var target in interruptedPlan.RecoveryTargets)
        {
            if (!targetByIdentifier.TryAdd(target.Identifier, target))
                return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.InconsistentRecoveryTargets);
        }

        // A duplicate identifier in Classifications would indirectly collide in
        // ApplyPlanner.OrderMovesForApply's own CurrentPath dictionary below (both duplicate entries
        // resolve to the identical live path/target path), but checking explicitly keeps this
        // method's totality independent of that other method's internals.
        var classifiedIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        var candidateMoves = new List<NamedModMove>();
        foreach (var classification in assessment.Classifications)
        {
            if (!classifiedIdentifiers.Add(classification.Identifier))
                return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.InconsistentRecoveryTargets);

            // Exhaustive (defensive - RecoveryClassifier.Classify only ever produces these six named
            // values, so `default` is not reachable from real classification output today, but an
            // explicit switch makes that an enumerated decision, not an implicit fallthrough).
            switch (classification.State)
            {
                case ItemRecoveryState.AtIntended:
                case ItemRecoveryState.AtBoth:
                    continue; // already at the final target, nothing to queue
                case ItemRecoveryState.AtSnapshot:
                case ItemRecoveryState.AtKnownIntermediate:
                    break;
                case ItemRecoveryState.AtNeither:
                case ItemRecoveryState.MissingLive:
                    return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.BlockingClassificationPresent);
                default:
                    return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.InconsistentRecoveryTargets);
            }

            if (!targetByIdentifier.TryGetValue(classification.Identifier, out var target) ||
                !assessment.LiveSnapshot.Mods.TryGetValue(classification.Identifier, out var live))
                return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.InconsistentRecoveryTargets);

            candidateMoves.Add(new NamedModMove(classification.Identifier, target.ModName, live.FullPath, target.FinalRawPath));
        }

        if (candidateMoves.Count == 0)
            return new ContinuationPlanResult(ContinuationPlanStatus.Ready, []); // every target already at its final path - a valid, empty Continue

        try
        {
            // The exact operation Continue would perform - not evaluated for its result here, only
            // for whether it throws. OperationPlanBuilder.BuildOperationPlan (Task 2) re-runs this
            // same call for real when Continue is actually resolved; this is a dry run to decide
            // whether that later call would itself succeed.
            ApplyPlanner.OrderMovesForApply(candidateMoves.Select(m => new ModMove(m.Identifier, m.CurrentPath, m.TargetPath)).ToList());
        }
        catch (ArgumentException)
        {
            return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.ReplanFailed);
        }

        return new ContinuationPlanResult(ContinuationPlanStatus.Ready, candidateMoves);
    }
}
