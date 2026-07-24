namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ItemRecoveryState { AtSnapshot, AtIntended, AtBoth, AtKnownIntermediate, AtNeither, MissingLive }

public sealed record ItemRecoveryClassification(string Identifier, ItemRecoveryState State);

/// <summary>
/// Design doc section 8, reconciled against shipped code. Classifies each of the interrupted plan's
/// RecoveryTargets against live state, using PenumbraPathSemantics.AreEquivalent (never raw string
/// equality) for every path comparison. Depends only on OperationPlan, never RollbackSnapshot - every
/// path this classifier needs (SnapshotRawPath, FinalRawPath, temporary hop targets) is already
/// embedded in the plan at construction time.
/// </summary>
public static class RecoveryClassifier
{
    public static IReadOnlyList<ItemRecoveryClassification> Classify(OperationPlan plan, LiveModSnapshot liveSnapshot)
    {
        // Exactly one temporary step per identifier is guaranteed by ApplyPlanner.OrderMovesForApply's
        // own structure: each identifier appears in exactly one chain (the algorithm's `visited` set
        // prevents an identifier's CurrentPath from being entered twice), and only chain[0] of a cycle
        // - entered once - ever receives IsTemporary: true.
        var temporaryTargetByIdentifier = plan.ExecutionSteps
            .Where(s => s.Kind == OperationStepKind.CycleBreakingTemporaryMove)
            .ToDictionary(s => s.Identifier, s => s.TargetRawPath, StringComparer.Ordinal);

        return plan.RecoveryTargets
            .Select(t => new ItemRecoveryClassification(t.Identifier, ClassifyOne(t, liveSnapshot, temporaryTargetByIdentifier)))
            .ToList();
    }

    private static ItemRecoveryState ClassifyOne(
        OperationRecoveryTarget target, LiveModSnapshot liveSnapshot,
        IReadOnlyDictionary<string, string> temporaryTargetByIdentifier)
    {
        if (!liveSnapshot.Mods.TryGetValue(target.Identifier, out var live))
            return ItemRecoveryState.MissingLive;

        var atFinal = PenumbraPathSemantics.AreEquivalent(live.FullPath, target.FinalRawPath, target.ModName);
        var atSnapshot = PenumbraPathSemantics.AreEquivalent(live.FullPath, target.SnapshotRawPath, target.ModName);

        // AtBoth means live state is semantically equivalent to BOTH the snapshot and intended paths
        // (per PenumbraPathSemantics, not raw string identity) - not necessarily that the two raw
        // paths themselves are byte-identical.
        if (atFinal && atSnapshot)
            return ItemRecoveryState.AtBoth;
        if (atFinal)
            return ItemRecoveryState.AtIntended;
        if (atSnapshot)
            return ItemRecoveryState.AtSnapshot;
        if (temporaryTargetByIdentifier.TryGetValue(target.Identifier, out var tempPath)
            && PenumbraPathSemantics.AreEquivalent(live.FullPath, tempPath, target.ModName))
            return ItemRecoveryState.AtKnownIntermediate;

        return ItemRecoveryState.AtNeither;
    }
}
