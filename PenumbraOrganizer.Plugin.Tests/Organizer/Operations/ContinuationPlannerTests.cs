using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class ContinuationPlannerTests
{
    private static OperationPlan Plan(params OperationRecoveryTarget[] targets)
    {
        var steps = targets
            .Select((t, i) => new OperationExecutionStep(i, t.Identifier, t.FinalRawPath, OperationStepKind.FinalMove, i))
            .ToList();
        return OperationPlan.Create(OperationType.Apply, steps, targets);
    }

    private static RecoveryAssessment Assessment(
        IReadOnlyDictionary<string, LiveMod> mods, params ItemRecoveryClassification[] classifications) =>
        new(new LiveModSnapshot(mods, new HashSet<string>()), classifications, "irrelevant-fingerprint");

    [Fact]
    public void AtIntendedAndAtBoth_ProduceNoResidualMoves()
    {
        var target = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var plan = Plan(target);
        var mods = new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Weapons/A", false) };
        var assessment = Assessment(mods, new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtIntended));

        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Equal(ContinuationPlanStatus.Ready, result.Status);
        Assert.Empty(result.ResidualMoves);
    }

    [Fact]
    public void AtSnapshot_ProducesAMoveFromLivePathToFinalRawPath()
    {
        var target = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var plan = Plan(target);
        var mods = new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Gear/A", false) };
        var assessment = Assessment(mods, new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtSnapshot));

        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Equal(ContinuationPlanStatus.Ready, result.Status);
        var move = Assert.Single(result.ResidualMoves);
        Assert.Equal("mod-a", move.Identifier);
        Assert.Equal("Gear/A", move.CurrentPath);
        Assert.Equal("Weapons/A", move.TargetPath);
    }

    [Theory]
    [InlineData(ItemRecoveryState.AtNeither)]
    [InlineData(ItemRecoveryState.MissingLive)]
    public void BlockingClassification_BlocksTheWholeResultNotJustThatIdentifier(ItemRecoveryState blockingState)
    {
        var targetA = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var targetB = new OperationRecoveryTarget("mod-b", "Gear/B", "Weapons/B", "mod-b");
        var plan = Plan(targetA, targetB);
        var mods = new Dictionary<string, LiveMod>
        {
            ["mod-a"] = new("mod-a", "mod-a", "Gear/A", false),
            ["mod-b"] = new("mod-b", "mod-b", "Gear/B", false),
        };
        var assessment = Assessment(mods,
            new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtSnapshot), // would otherwise be a valid move
            new ItemRecoveryClassification("mod-b", blockingState));

        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Equal(ContinuationPlanStatus.Blocked, result.Status);
        Assert.Equal(ContinuationBlockReason.BlockingClassificationPresent, result.Reason);
        Assert.Empty(result.ResidualMoves);
    }

    [Fact]
    public void AtKnownIntermediateCollision_BlocksWithReplanFailedRatherThanThrowing()
    {
        // Two residual moves that would collide on the same CurrentPath - ApplyPlanner.OrderMovesForApply's
        // own ToDictionary(m => m.CurrentPath) throws ArgumentException on this; TryBuildResidualMoves
        // must catch it and report Blocked/ReplanFailed, not let the exception escape.
        var targetA = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var targetB = new OperationRecoveryTarget("mod-b", "Gear/B", "Weapons/B", "mod-b");
        var plan = Plan(targetA, targetB);
        var mods = new Dictionary<string, LiveMod>
        {
            ["mod-a"] = new("mod-a", "mod-a", "Collision/Path", false),
            ["mod-b"] = new("mod-b", "mod-b", "Collision/Path", false), // same live path as mod-a
        };
        var assessment = Assessment(mods,
            new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtSnapshot),
            new ItemRecoveryClassification("mod-b", ItemRecoveryState.AtSnapshot));

        var exception = Record.Exception(() => ContinuationPlanner.TryBuildResidualMoves(plan, assessment));
        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Null(exception);
        Assert.Equal(ContinuationPlanStatus.Blocked, result.Status);
        Assert.Equal(ContinuationBlockReason.ReplanFailed, result.Reason);
    }

    [Fact]
    public void AllTargetsAtIntendedOrAtBoth_ProducesReadyWithEmptyMoveList()
    {
        var targetA = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var targetB = new OperationRecoveryTarget("mod-b", "Gear/B", "Weapons/B", "mod-b");
        var plan = Plan(targetA, targetB);
        var mods = new Dictionary<string, LiveMod>
        {
            ["mod-a"] = new("mod-a", "mod-a", "Weapons/A", false),
            ["mod-b"] = new("mod-b", "mod-b", "Weapons/B", false),
        };
        var assessment = Assessment(mods,
            new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtIntended),
            new ItemRecoveryClassification("mod-b", ItemRecoveryState.AtBoth));

        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Equal(ContinuationPlanStatus.Ready, result.Status);
        Assert.Empty(result.ResidualMoves);
    }

    [Fact]
    public void ClassificationWithNoMatchingRecoveryTarget_BlocksWithInconsistentRecoveryTargetsRatherThanThrowing()
    {
        // A classification identifier absent from the plan's own RecoveryTargets isn't reachable from
        // RecoveryClassifier.Classify (it iterates plan.RecoveryTargets itself) but the method must
        // still be total against this hand-constructed, deliberately-mismatched input.
        var plan = Plan(new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a"));
        var mods = new Dictionary<string, LiveMod> { ["mod-x"] = new("mod-x", "mod-x", "Gear/X", false) };
        var assessment = Assessment(mods, new ItemRecoveryClassification("mod-x", ItemRecoveryState.AtSnapshot));

        var exception = Record.Exception(() => ContinuationPlanner.TryBuildResidualMoves(plan, assessment));
        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Null(exception);
        Assert.Equal(ContinuationPlanStatus.Blocked, result.Status);
        Assert.Equal(ContinuationBlockReason.InconsistentRecoveryTargets, result.Reason);
    }

    [Fact]
    public void DuplicateClassificationForSameIdentifier_BlocksWithInconsistentRecoveryTargetsRatherThanThrowing()
    {
        // Review point 5: two classifications for the same identifier would (indirectly) collide in
        // ApplyPlanner.OrderMovesForApply's own CurrentPath dictionary since both produce an identical
        // NamedModMove - but that's an indirect guarantee this method's own totality shouldn't depend
        // on another method's internals for. Checked explicitly instead.
        var target = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var plan = Plan(target);
        var mods = new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Gear/A", false) };
        var assessment = Assessment(mods,
            new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtSnapshot),
            new ItemRecoveryClassification("mod-a", ItemRecoveryState.AtSnapshot)); // duplicate

        var exception = Record.Exception(() => ContinuationPlanner.TryBuildResidualMoves(plan, assessment));
        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Null(exception);
        Assert.Equal(ContinuationPlanStatus.Blocked, result.Status);
        Assert.Equal(ContinuationBlockReason.InconsistentRecoveryTargets, result.Reason);
    }

    [Fact]
    public void OutOfRangeClassificationState_BlocksRatherThanBeingTreatedLikeAtIntended()
    {
        // Not reachable from real RecoveryClassifier.Classify output (a closed 6-value enum), but the
        // per-item switch must be an explicit, exhaustive decision rather than an implicit fallthrough
        // that silently treats any unrecognized value the same as AtIntended/AtBoth (skip, no move).
        var target = new OperationRecoveryTarget("mod-a", "Gear/A", "Weapons/A", "mod-a");
        var plan = Plan(target);
        var mods = new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Gear/A", false) };
        var assessment = Assessment(mods, new ItemRecoveryClassification("mod-a", (ItemRecoveryState)99));

        var exception = Record.Exception(() => ContinuationPlanner.TryBuildResidualMoves(plan, assessment));
        var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);

        Assert.Null(exception);
        Assert.Equal(ContinuationPlanStatus.Blocked, result.Status);
        Assert.Equal(ContinuationBlockReason.InconsistentRecoveryTargets, result.Reason);
    }
}
