using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationPlanTests
{
    // Two independent final moves, each its own group. A minimal valid plan.
    private static OperationExecutionStep[] TwoFinalSteps() =>
    [
        new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
        new(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
    ];

    private static OperationRecoveryTarget[] TwoFinalTargets() =>
    [
        new("mod-a", "Gear/A", "Weapons/A", "A"),
        new("mod-b", "Gear/B", "Weapons/B", "B"),
    ];

    // A two-way swap resolved with a cycle-breaking temporary hop: X and Y trade slots.
    private static OperationExecutionStep[] SwapSteps() =>
    [
        new(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
        new(1, "Y", "P0", OperationStepKind.FinalMove, 0),
        new(2, "X", "P2", OperationStepKind.FinalMove, 0),
    ];

    private static OperationRecoveryTarget[] SwapTargets() =>
    [
        new("X", "P0", "P2", "X"),
        new("Y", "P2", "P0", "Y"),
    ];

    [Fact]
    public void Create_ValidPlan_VerifiesAndCarriesSchemaVersion2()
    {
        var plan = OperationPlan.Create(OperationType.Apply, TwoFinalSteps(), TwoFinalTargets());

        Assert.True(plan.Verify());
        Assert.Equal(2, plan.SchemaVersion);
        Assert.Equal(2, plan.ExecutionSteps.Count);
        Assert.Equal(2, plan.RecoveryTargets.Count);
    }

    [Fact]
    public void Create_ValidCyclePlan_Verifies()
    {
        var plan = OperationPlan.Create(OperationType.Apply, SwapSteps(), SwapTargets());

        Assert.True(plan.Verify());
    }

    [Fact]
    public void Create_ThrowsWhenStepIndicesAreNotContiguous()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            new(5, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1), // gap
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, TwoFinalTargets()));
    }

    [Fact]
    public void Create_ThrowsWhenGroupIdsAreNotZeroBasedContiguous()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            new(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 2), // jumps 0 -> 2
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, TwoFinalTargets()));
    }

    [Fact]
    public void Create_ThrowsWhenAnIdentifierAppearsInTwoGroups()
    {
        // mod-a in group 0 and group 1 - group ranges would not be contiguous per identifier.
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            new(1, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 1),
        };
        var targets = new OperationRecoveryTarget[] { new("mod-a", "Gear/A", "Weapons/A", "A") };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void Create_ThrowsWhenLastStepForAnIdentifierIsTemporary()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0), // temp is the only/last step for X
        };
        var targets = new OperationRecoveryTarget[] { new("X", "P0", "P2", "X") };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void Create_ThrowsWhenLastStepDoesNotTargetTheRecoveryTargetsFinalRawPath()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/WrongPath", OperationStepKind.FinalMove, 0), // doesn't match target's FinalRawPath below
        };
        var targets = new OperationRecoveryTarget[] { new("mod-a", "Gear/A", "Weapons/A", "A") };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void Create_ThrowsWhenARecoveryTargetsIdentifierMapsToNoGroup()
    {
        // "mod-b" has a recovery target but never appears in any execution step, so it maps to zero
        // GroupIds rather than exactly one - invariant 10, checked explicitly and independently of
        // the "identifier appears in two groups" check (invariant 9), which never even sees it.
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
        };
        var targets = new OperationRecoveryTarget[]
        {
            new("mod-a", "Gear/A", "Weapons/A", "A"),
            new("mod-b", "Gear/B", "Weapons/B", "B"),
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void Create_ThrowsWhenACycleBreakingTemporaryStepAndItsFinalStepHaveDifferentGroupIds()
    {
        // Deliberately malformed input: ApplyPlanner would never emit this itself, but OperationPlan.Create
        // takes raw lists and must defend against a malformed caller. X's temp hop is in group 0 while its
        // final move ends up in group 1 - invariant 11, checked explicitly.
        var steps = new OperationExecutionStep[]
        {
            new(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
            new(1, "Y", "P0", OperationStepKind.FinalMove, 1),
            new(2, "X", "P2", OperationStepKind.FinalMove, 1),
        };
        var targets = new OperationRecoveryTarget[]
        {
            new("X", "P0", "P2", "X"),
            new("Y", "P2", "P0", "Y"),
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void Create_ThrowsWhenFirstStepHasNegativeGroupId()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, -1),
        };
        var targets = new OperationRecoveryTarget[] { new("mod-a", "Gear/A", "Weapons/A", "A") };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void Create_ThrowsWhenFirstStepHasNonZeroGroupId()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 1),
        };
        var targets = new OperationRecoveryTarget[] { new("mod-a", "Gear/A", "Weapons/A", "A") };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void Create_ThrowsWhenAStepHasNoRecoveryTarget()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "orphan", "Weapons/O", OperationStepKind.FinalMove, 0),
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, TwoFinalTargets()));
    }

    [Fact]
    public void Create_ThrowsWhenARecoveryTargetHasNoStep()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
        };
        var targets = new OperationRecoveryTarget[]
        {
            new("mod-a", "Gear/A", "Weapons/A", "A"),
            new("mod-b", "Gear/B", "Weapons/B", "B"), // no step
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void Create_ThrowsWhenTargetIdentifiersAreNotUnique()
    {
        var steps = new OperationExecutionStep[]
        {
            new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
        };
        var targets = new OperationRecoveryTarget[]
        {
            new("mod-a", "Gear/A", "Weapons/A", "A"),
            new("mod-a", "Gear/A", "Weapons/A", "A"), // duplicate identifier
        };

        Assert.Throws<InvalidOperationException>(() =>
            OperationPlan.Create(OperationType.Apply, steps, targets));
    }

    [Fact]
    public void ComputeIntegrityHash_ChangesWhenStepKindChanges()
    {
        // The exact regression the canonical hash closes: Kind must be bound, or a step could flip
        // FinalMove <-> CycleBreakingTemporaryMove with identical identifier/path and the same hash.
        var targets = new OperationRecoveryTarget[] { new("X", "P0", "P2", "X") };
        var asTemporary = new OperationExecutionStep[] { new(0, "X", "P2", OperationStepKind.CycleBreakingTemporaryMove, 0) };
        var asFinal = new OperationExecutionStep[] { new(0, "X", "P2", OperationStepKind.FinalMove, 0) };

        Assert.NotEqual(
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, asTemporary, targets),
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, asFinal, targets));
    }

    [Fact]
    public void ComputeIntegrityHash_ChangesWhenGroupIdChanges()
    {
        var targets = new OperationRecoveryTarget[] { new("X", "P0", "P2", "X") };
        var group0 = new OperationExecutionStep[] { new(0, "X", "P2", OperationStepKind.FinalMove, 0) };
        var group1 = new OperationExecutionStep[] { new(0, "X", "P2", OperationStepKind.FinalMove, 1) };

        Assert.NotEqual(
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, group0, targets),
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, group1, targets));
    }

    [Fact]
    public void ComputeIntegrityHash_IsUnchangedByPenumbraDuplicateMarkerReshuffling()
    {
        // "Weapons/A" and "Weapons/A (3)" are the same persisted location for a mod named "A".
        var targets = new OperationRecoveryTarget[] { new("mod-a", "Gear/A", "Weapons/A", "A") };
        var plain = new OperationExecutionStep[] { new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var marked = new OperationExecutionStep[] { new(0, "mod-a", "Weapons/A (3)", OperationStepKind.FinalMove, 0) };

        Assert.Equal(
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, plain, targets),
            OperationPlan.ComputeIntegrityHash(OperationType.Apply, marked, targets));
    }

    [Fact]
    public void SaveThenTryLoad_RoundTripsAndVerifies()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            var plan = OperationPlan.Create(OperationType.Restore, SwapSteps(), SwapTargets());

            OperationPlanCodec.Save(path, plan);
            var loaded = OperationPlanCodec.TryLoad(path, out var result);

            Assert.True(loaded);
            Assert.NotNull(result);
            Assert.Equal(plan.OperationId, result!.OperationId);
            Assert.Equal(plan.IntegrityHash, result.IntegrityHash);
            Assert.True(result.Verify());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Save_WritesEnumsAsStrings()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            OperationPlanCodec.Save(path, OperationPlan.Create(OperationType.Apply, SwapSteps(), SwapTargets()));

            var json = File.ReadAllText(path);
            Assert.Contains("\"CycleBreakingTemporaryMove\"", json);
            Assert.Contains("\"Apply\"", json);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenFileMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var loaded = OperationPlanCodec.TryLoad(Path.Combine(dir.FullName, "missing.json"), out var result);
            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenIntegrityHashHasBeenTampered()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            var plan = OperationPlan.Create(OperationType.Apply, SwapSteps(), SwapTargets());
            OperationPlanCodec.Save(path, plan);

            File.WriteAllText(path, File.ReadAllText(path).Replace(plan.IntegrityHash, "tampered-hash-value"));

            var loaded = OperationPlanCodec.TryLoad(path, out var result);
            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenSchemaVersionIsNotCurrent()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            OperationPlanCodec.Save(path, OperationPlan.Create(OperationType.Apply, SwapSteps(), SwapTargets()));

            // Simulate an older on-disk schema: force SchemaVersion to 1. No migration - TryLoad rejects it.
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"SchemaVersion\":2", "\"SchemaVersion\":1"));

            var loaded = OperationPlanCodec.TryLoad(path, out var result);
            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
