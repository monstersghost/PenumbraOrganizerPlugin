namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ItemRecoveryState
{
    AtOriginal,
    AtTarget,
    AtBoth,
    AtNeither,
    MissingLive,
    MissingSnapshot,
    MissingPlan,
}

public sealed record ItemRecoveryClassification(string Identifier, ItemRecoveryState State);

/// <summary>
/// Design doc section 7. Every comparison uses PenumbraPathSemantics.AreEquivalent, never raw
/// string equality — see PenumbraPathSemantics.cs for why a live path can legitimately differ
/// from a saved path only in its " (N)" duplicate-marker suffix and still be the same location.
/// </summary>
public static class RecoveryClassifier
{
    public static IReadOnlyList<ItemRecoveryClassification> ClassifyItems(
        OperationPlan plan, RollbackSnapshot snapshot, IReadOnlyList<LiveMod> liveMods)
    {
        var liveByIdentifier = liveMods.ToDictionary(m => m.Identifier, StringComparer.Ordinal);
        var results = new List<ItemRecoveryClassification>(plan.Items.Count);

        foreach (var item in plan.Items)
        {
            var hasSnapshot = snapshot.ModPaths.TryGetValue(item.Identifier, out var snapshotPath);
            var hasLive = liveByIdentifier.TryGetValue(item.Identifier, out var liveMod);

            var state = (hasSnapshot, hasLive) switch
            {
                (false, _) => ItemRecoveryState.MissingSnapshot,
                (true, false) => ItemRecoveryState.MissingLive,
                (true, true) => ClassifyPresent(item, snapshotPath!, liveMod!.FullPath),
            };

            results.Add(new ItemRecoveryClassification(item.Identifier, state));
        }

        return results;
    }

    private static ItemRecoveryState ClassifyPresent(OperationPlanItem item, string snapshotPath, string livePath)
    {
        var snapshotEqualsTarget = PenumbraPathSemantics.AreEquivalent(snapshotPath, item.IntendedRawPath, item.DisplayName);
        if (snapshotEqualsTarget)
            return ItemRecoveryState.AtBoth;

        var liveAtOriginal = PenumbraPathSemantics.AreEquivalent(livePath, snapshotPath, item.DisplayName);
        var liveAtTarget = PenumbraPathSemantics.AreEquivalent(livePath, item.IntendedRawPath, item.DisplayName);

        return (liveAtOriginal, liveAtTarget) switch
        {
            (true, false) => ItemRecoveryState.AtOriginal,
            (false, true) => ItemRecoveryState.AtTarget,
            _ => ItemRecoveryState.AtNeither,
        };
    }
}
