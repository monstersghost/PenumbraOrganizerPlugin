using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationPlanBuilderTests
{
    private static OrganizerModRow Row(string id, string name, string currentPath, string proposedPath) => new()
    {
        Identifier = id, Name = name, Author = "", CurrentPath = currentPath, ProposedPath = proposedPath,
    };

    [Fact]
    public void BuildApplyPlan_IndependentMoves_ProducesOneStepPerMod()
    {
        var rows = new[]
        {
            Row("mod-a", "Mod A", "Gear/A", "Weapons/A"),
            Row("mod-b", "Mod B", "Gear/B", "Weapons/B"),
        };

        var plan = OperationPlanBuilder.BuildApplyPlan(rows);

        Assert.Equal(OperationType.Apply, plan.Type);
        Assert.Equal(2, plan.ExecutionSteps.Count);
        Assert.Equal(2, plan.RecoveryTargets.Count);
        Assert.All(plan.ExecutionSteps, s => Assert.Equal(OperationStepKind.FinalMove, s.Kind));
        var targetA = plan.RecoveryTargets.Single(t => t.Identifier == "mod-a");
        Assert.Equal("Gear/A", targetA.SnapshotRawPath);
        Assert.Equal("Weapons/A", targetA.FinalRawPath);
        Assert.Equal("Mod A", targetA.ModName);
    }

    [Fact]
    public void BuildApplyPlan_TwoWayCycle_ProducesATemporaryHopStep()
    {
        // X wants Y's current path and Y wants X's current path - ApplyPlanner.OrderMovesForApply
        // must break this cycle with a temporary hop, which this builder must faithfully translate
        // into an OperationStepKind.CycleBreakingTemporaryMove step.
        var rows = new[]
        {
            Row("X", "Mod X", "Gear/A", "Gear/B"),
            Row("Y", "Mod Y", "Gear/B", "Gear/A"),
        };

        var plan = OperationPlanBuilder.BuildApplyPlan(rows);

        Assert.Equal(3, plan.ExecutionSteps.Count); // temp hop + 2 final moves
        Assert.Contains(plan.ExecutionSteps, s => s.Kind == OperationStepKind.CycleBreakingTemporaryMove);
        Assert.Equal(2, plan.RecoveryTargets.Count); // still one recovery target per identifier, not per step
    }

    [Fact]
    public void BuildApplyPlan_StepIndicesAreSequentialFromZero()
    {
        var rows = new[]
        {
            Row("mod-a", "Mod A", "Gear/A", "Weapons/A"),
            Row("mod-b", "Mod B", "Gear/B", "Weapons/B"),
            Row("mod-c", "Mod C", "Gear/C", "Weapons/C"),
        };

        var plan = OperationPlanBuilder.BuildApplyPlan(rows);

        Assert.Equal([0, 1, 2], plan.ExecutionSteps.Select(s => s.StepIndex).ToArray());
    }

    [Fact]
    public void BuildApplyPlan_EmptyRows_ProducesAValidZeroStepPlan()
    {
        var plan = OperationPlanBuilder.BuildApplyPlan([]);

        Assert.Empty(plan.ExecutionSteps);
        Assert.Empty(plan.RecoveryTargets);
        Assert.True(plan.Verify()); // OperationPlan.Create's own integrity hash still checks out
    }

    private static LiveMod Live(string id, string name) => new(id, name, "", false);

    [Fact]
    public void BuildNamedMoves_HappyPath_ResolvesNamesFromCurrentMods()
    {
        var moves = new[] { new ModMove("mod-a", "Gear/A", "Weapons/A") };
        var currentMods = new[] { Live("mod-a", "Mod A") };

        var named = OperationPlanBuilder.BuildNamedMoves(moves, currentMods);

        Assert.Single(named);
        Assert.Equal("mod-a", named[0].Identifier);
        Assert.Equal("Mod A", named[0].ModName);
        Assert.Equal("Gear/A", named[0].CurrentPath);
        Assert.Equal("Weapons/A", named[0].TargetPath);
    }

    [Fact]
    public void BuildNamedMoves_MoveIdentifierNotInCurrentMods_ThrowsNamingTheIdentifier()
    {
        var moves = new[] { new ModMove("mod-a", "Gear/A", "Weapons/A") };
        var currentMods = Array.Empty<LiveMod>();

        var exception = Assert.Throws<InvalidOperationException>(() => OperationPlanBuilder.BuildNamedMoves(moves, currentMods));
        Assert.Contains("mod-a", exception.Message);
    }

    [Fact]
    public void BuildNamedMoves_DuplicateIdentifierInCurrentMods_ThrowsNamingTheDuplicates()
    {
        var moves = new[] { new ModMove("mod-a", "Gear/A", "Weapons/A") };
        var currentMods = new[] { Live("mod-a", "Mod A"), Live("mod-a", "Mod A Duplicate") };

        var exception = Assert.Throws<InvalidOperationException>(() => OperationPlanBuilder.BuildNamedMoves(moves, currentMods));
        Assert.Contains("mod-a", exception.Message);
    }

    private static NamedModMove Named(string id, string name, string currentPath, string targetPath) =>
        new(id, name, currentPath, targetPath);

    [Fact]
    public void BuildRestoreOperationPlan_IndependentMoves_ProducesOneStepPerMod()
    {
        var moves = new[]
        {
            Named("mod-a", "Mod A", "Weapons/A", "Gear/A"),
            Named("mod-b", "Mod B", "Weapons/B", "Gear/B"),
        };

        var plan = OperationPlanBuilder.BuildRestoreOperationPlan(moves);

        Assert.Equal(OperationType.Restore, plan.Type);
        Assert.Equal(2, plan.ExecutionSteps.Count);
        Assert.Equal(2, plan.RecoveryTargets.Count);
        Assert.All(plan.ExecutionSteps, s => Assert.Equal(OperationStepKind.FinalMove, s.Kind));
        var targetA = plan.RecoveryTargets.Single(t => t.Identifier == "mod-a");
        Assert.Equal("Weapons/A", targetA.SnapshotRawPath);
        Assert.Equal("Gear/A", targetA.FinalRawPath);
        Assert.Equal("Mod A", targetA.ModName);
    }

    [Fact]
    public void BuildRestoreOperationPlan_TwoWayCycle_ProducesATemporaryHopStep()
    {
        var moves = new[]
        {
            Named("X", "Mod X", "Gear/A", "Gear/B"),
            Named("Y", "Mod Y", "Gear/B", "Gear/A"),
        };

        var plan = OperationPlanBuilder.BuildRestoreOperationPlan(moves);

        Assert.Equal(3, plan.ExecutionSteps.Count); // temp hop + 2 final moves
        Assert.Contains(plan.ExecutionSteps, s => s.Kind == OperationStepKind.CycleBreakingTemporaryMove);
        Assert.Equal(2, plan.RecoveryTargets.Count);
        // Recovery targets carry the real current/target paths, never a temporary cycle-breaking hop path.
        var targetX = plan.RecoveryTargets.Single(t => t.Identifier == "X");
        Assert.Equal("Gear/A", targetX.SnapshotRawPath);
        Assert.Equal("Gear/B", targetX.FinalRawPath);
    }

    [Fact]
    public void BuildRestoreOperationPlan_EmptyMoves_ProducesAValidZeroStepPlan()
    {
        var plan = OperationPlanBuilder.BuildRestoreOperationPlan([]);

        Assert.Empty(plan.ExecutionSteps);
        Assert.Empty(plan.RecoveryTargets);
        Assert.True(plan.Verify());
    }

    [Fact]
    public void BuildRestoreOperationPlan_DuplicateIdentifiers_ThrowsOperationPlansExistingDiagnostic()
    {
        var moves = new[]
        {
            Named("mod-a", "Mod A", "Gear/A", "Weapons/A"),
            Named("mod-a", "Mod A", "Gear/A", "Weapons/B"),
        };

        var exception = Assert.Throws<InvalidOperationException>(() => OperationPlanBuilder.BuildRestoreOperationPlan(moves));
        Assert.Contains("Duplicate recovery target identifier", exception.Message);
    }
}
