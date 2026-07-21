using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class RecoveryClassifierTests
{
    private static OperationPlanItem Item(string id, string original, string intended, string name = "Mod") =>
        new(id, original, intended, name);

    private static RollbackSnapshot Snapshot(params (string Id, string Path)[] entries) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, null, "test snapshot",
        entries.ToDictionary(e => e.Id, e => e.Path, StringComparer.Ordinal));

    private static LiveMod Live(string id, string path, string name = "Mod") =>
        new(id, name, path, HeliosphereManaged: false);

    [Fact]
    public void ClassifyItems_AtOriginal_WhenLiveMatchesSnapshotOnly()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Gear\\A") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.AtOriginal, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_AtTarget_WhenLiveMatchesIntendedOnly()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Weapons\\A") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.AtTarget, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_AtBoth_WhenSnapshotAndTargetAreTheSameLocation()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Gear\\A (2)")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Gear\\A") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.AtBoth, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_AtNeither_WhenLiveMatchesNeitherOriginalNorTarget()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Somewhere\\Else") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.AtNeither, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_MissingLive_WhenPlannedModIsAbsentFromLiveMods()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = Array.Empty<LiveMod>();

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.MissingLive, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_MissingSnapshot_WhenPlannedModIsAbsentFromSnapshot()
    {
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(); // empty — m1 was never captured
        var live = new[] { Live("m1", "Gear\\A") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.MissingSnapshot, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_DuplicateMarkerReshuffling_StillClassifiesAsAtTarget()
    {
        // Same regression the design review flagged for string-equality comparisons: Penumbra
        // discards " (N)" on save and reassigns it arbitrarily on load, so a live path carrying
        // a different suffix than the plan's IntendedRawPath must still classify as AtTarget.
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A (2)", "A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Weapons\\A (7)", "A") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Equal(ItemRecoveryState.AtTarget, result.Single(r => r.Identifier == "m1").State);
    }

    [Fact]
    public void ClassifyItems_ReturnsOneEntryPerPlanItem_IgnoringUnplannedLiveMods()
    {
        // MissingPlan is a diagnostics-only signal (design section 13), surfaced by the caller
        // comparing live mods against plan identifiers directly — ClassifyItems iterates the
        // plan, so an unplanned live mod simply never appears in its output.
        var plan = OperationPlan.Create(OperationType.Apply, [Item("m1", "Gear\\A", "Weapons\\A")]);
        var snapshot = Snapshot(("m1", "Gear\\A"));
        var live = new[] { Live("m1", "Gear\\A"), Live("m2", "Gear\\Unplanned") };

        var result = RecoveryClassifier.ClassifyItems(plan, snapshot, live);

        Assert.Single(result);
        Assert.Equal("m1", result[0].Identifier);
    }
}

public class RecoveryOutcomeTests
{
    private static ItemRecoveryClassification C(ItemRecoveryState state) => new("m", state);

    [Fact]
    public void DeriveOutcome_NoMutationsDetected_WhenAllChangedItemsAreAtOriginal()
    {
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.AtOriginal)]);

        Assert.Equal(RecoveryOutcome.NoMutationsDetected, outcome);
    }

    [Fact]
    public void DeriveOutcome_CompletedButNotFinalized_WhenAllChangedItemsAreAtTarget()
    {
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtTarget), C(ItemRecoveryState.AtTarget)]);

        Assert.Equal(RecoveryOutcome.CompletedButNotFinalized, outcome);
    }

    [Fact]
    public void DeriveOutcome_PartiallyApplied_WhenMixOfOriginalAndTargetWithNoNeither()
    {
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.AtTarget)]);

        Assert.Equal(RecoveryOutcome.PartiallyApplied, outcome);
    }

    [Fact]
    public void DeriveOutcome_Indeterminate_WhenAnyItemIsAtNeither()
    {
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.AtNeither)]);

        Assert.Equal(RecoveryOutcome.Indeterminate, outcome);
    }

    [Fact]
    public void DeriveOutcome_Indeterminate_WhenAnyItemIsUnexpectedlyMissing()
    {
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.MissingLive)]);

        Assert.Equal(RecoveryOutcome.Indeterminate, outcome);
    }

    [Fact]
    public void DeriveOutcome_AtBothItemsAreExcludedFromClassificationEntirely()
    {
        // AtBoth items must not inflate the "all AtOriginal" or "all AtTarget" checks in either
        // direction (design section 7) — mixing them in with real AtOriginal items must not flip
        // a genuinely-unstarted operation into looking "partially applied".
        var outcome = RecoveryClassifier.DeriveOutcome(
            [C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.AtOriginal), C(ItemRecoveryState.AtBoth)]);

        Assert.Equal(RecoveryOutcome.NoMutationsDetected, outcome);
    }

    [Fact]
    public void DeriveOutcome_NoMutationsDetected_WhenOnlyAtBothItemsExist()
    {
        // Degenerate case: every planned item turned out to be a no-op (original and target
        // normalize to the same location). Nothing changed, so nothing was mutated.
        var outcome = RecoveryClassifier.DeriveOutcome([C(ItemRecoveryState.AtBoth), C(ItemRecoveryState.AtBoth)]);

        Assert.Equal(RecoveryOutcome.NoMutationsDetected, outcome);
    }
}
