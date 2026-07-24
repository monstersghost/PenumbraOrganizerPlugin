using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class RecoveryClassifierTests
{
    private static OperationPlan SimplePlan(string id = "mod-a", string snapshotPath = "Gear/A", string finalPath = "Weapons/A") =>
        OperationPlan.Create(
            OperationType.Apply, [new(0, id, finalPath, OperationStepKind.FinalMove, 0)],
            [new(id, snapshotPath, finalPath, id)]);

    private static LiveModSnapshot Live(params LiveMod[] mods) => LiveModSnapshotBuilder.Build(mods);

    [Fact]
    public void Classify_LiveAtFinalPathOnly_AtIntended()
    {
        var plan = SimplePlan();
        var live = Live(new LiveMod("mod-a", "mod-a", "Weapons/A", false));

        var result = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(ItemRecoveryState.AtIntended, Assert.Single(result).State);
    }

    [Fact]
    public void Classify_LiveAtSnapshotPathOnly_AtSnapshot()
    {
        var plan = SimplePlan();
        var live = Live(new LiveMod("mod-a", "mod-a", "Gear/A", false));

        var result = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(ItemRecoveryState.AtSnapshot, Assert.Single(result).State);
    }

    [Fact]
    public void Classify_SnapshotAndFinalCoincide_AtBoth()
    {
        var plan = SimplePlan(snapshotPath: "Gear/A", finalPath: "Gear/A");
        var live = Live(new LiveMod("mod-a", "mod-a", "Gear/A", false));

        var result = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(ItemRecoveryState.AtBoth, Assert.Single(result).State);
    }

    [Fact]
    public void Classify_LiveAtNeitherPath_AtNeither()
    {
        var plan = SimplePlan();
        var live = Live(new LiveMod("mod-a", "mod-a", "SomewhereElse/A", false));

        var result = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(ItemRecoveryState.AtNeither, Assert.Single(result).State);
    }

    [Fact]
    public void Classify_IdentifierNotInLiveSnapshot_MissingLive()
    {
        var plan = SimplePlan();
        var live = Live();

        var result = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(ItemRecoveryState.MissingLive, Assert.Single(result).State);
    }

    [Fact]
    public void Classify_LiveAtCycleBreakingTemporaryPath_AtKnownIntermediate()
    {
        // Build a plan with a genuine two-way cycle so a real CycleBreakingTemporaryMove step exists.
        var moves = new[] { new ModMove("X", "Gear/A", "Gear/B"), new ModMove("Y", "Gear/B", "Gear/A") };
        var steps = ApplyPlanner.OrderMovesForApply(moves);
        var executionSteps = steps.Select((s, i) => new OperationExecutionStep(
            i, s.Identifier, s.TargetPath,
            s.IsTemporary ? OperationStepKind.CycleBreakingTemporaryMove : OperationStepKind.FinalMove, s.GroupId)).ToList();
        var recoveryTargets = new[] { new OperationRecoveryTarget("X", "Gear/A", "Gear/B", "X"), new OperationRecoveryTarget("Y", "Gear/B", "Gear/A", "Y") };
        var plan = OperationPlan.Create(OperationType.Apply, executionSteps, recoveryTargets);
        var temporaryStep = executionSteps.Single(s => s.Kind == OperationStepKind.CycleBreakingTemporaryMove);

        var live = Live(new LiveMod(temporaryStep.Identifier, temporaryStep.Identifier, temporaryStep.TargetRawPath, false));

        var result = RecoveryClassifier.Classify(plan, live);

        var classification = result.Single(c => c.Identifier == temporaryStep.Identifier);
        Assert.Equal(ItemRecoveryState.AtKnownIntermediate, classification.State);
    }

    [Fact]
    public void Classify_DuplicateLiveIdentifiers_DoesNotSpecialCase()
    {
        // Classify itself stays unconditional regardless of DuplicateIdentifiers - that's a
        // resolution-layer concern (Keep Current tolerates it, D2's Continue/Restore won't), not
        // something this pure function decides.
        var plan = SimplePlan();
        var mods = new[] { new LiveMod("mod-a", "mod-a", "Weapons/A", false) };
        var liveWithDuplicates = new LiveModSnapshot(
            new Dictionary<string, LiveMod> { ["mod-a"] = mods[0] },
            new HashSet<string> { "mod-a" });

        var result = RecoveryClassifier.Classify(plan, liveWithDuplicates);

        Assert.Equal(ItemRecoveryState.AtIntended, Assert.Single(result).State);
    }
}
