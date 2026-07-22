using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class StepResultReconcilerTests
{
    private static readonly OperationExecutionStep[] ThreeSteps =
    [
        new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
        new(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
        new(2, "mod-c", "Weapons/C", OperationStepKind.FinalMove, 2),
    ];

    private static readonly OperationRecoveryTarget[] ThreeTargets =
    [
        new("mod-a", "Gear/A", "Weapons/A", "A"),
        new("mod-b", "Gear/B", "Weapons/B", "B"),
        new("mod-c", "Gear/C", "Weapons/C", "C"),
    ];

    private static OperationPlan SamplePlan() =>
        OperationPlan.Create(OperationType.Apply, ThreeSteps, ThreeTargets);

    private static OperationJournal SampleJournal(int processedStepCount, Guid planId) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: Guid.NewGuid(),
        Type: OperationType.Apply,
        Stage: OperationStage.Mutating,
        Resolution: OperationResolution.None,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: DateTimeOffset.UtcNow,
        TotalSteps: 3,
        ProcessedStepCount: processedStepCount,
        LastCompletedIdentifier: null,
        SnapshotId: Guid.NewGuid(),
        PlanId: planId,
        TargetHash: "irrelevant-to-this-test",
        RecoveryOfOperationId: null,
        UpdatedAt: DateTimeOffset.UtcNow);

    private static OperationStepResult Result(int stepIndex, string identifier) => new(
        stepIndex, identifier, OperationStepDisposition.Succeeded, "Success", null, DateTimeOffset.UtcNow, 5);

    [Fact]
    public void Reconcile_ProcessedStepCountZero_ConsistentEvenWithNoResults()
    {
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 0, plan.OperationId);

        var result = StepResultReconciler.Reconcile(journal, plan, []);

        Assert.Equal(StepResultReconciliationStatus.Consistent, result.Status);
    }

    [Fact]
    public void Reconcile_ExactCoverageWithMatchingIdentifiers_Consistent()
    {
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 2, plan.OperationId);
        var results = new[] { Result(0, "mod-a"), Result(1, "mod-b") };

        var result = StepResultReconciler.Reconcile(journal, plan, results);

        Assert.Equal(StepResultReconciliationStatus.Consistent, result.Status);
    }

    [Fact]
    public void Reconcile_ExtraResultsAheadOfCursor_StillConsistent()
    {
        // The append-before-checkpoint ordering means the log can legitimately be ahead of the
        // journal after a crash - this must not be treated as inconsistent.
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 1, plan.OperationId);
        var results = new[] { Result(0, "mod-a"), Result(1, "mod-b"), Result(2, "mod-c") };

        var result = StepResultReconciler.Reconcile(journal, plan, results);

        Assert.Equal(StepResultReconciliationStatus.Consistent, result.Status);
    }

    [Fact]
    public void Reconcile_BadDataAheadOfCursor_StillConsistent_ProvesResultsPastCursorAreNeverInspected()
    {
        // Unlike Reconcile_ExtraResultsAheadOfCursor_StillConsistent (which uses valid data past the
        // cursor, so it can't distinguish "ignored" from "inspected but happened to be fine"), this
        // plants a duplicate AND a wrong identifier at step index 1 - both of which are individually
        // sufficient to force Indeterminate if step 1 were below the cursor (see the duplicate and
        // mismatch tests below). With processedStepCount=1, step 1 is past the cursor, so if the
        // implementation genuinely never inspects results at/after the cursor, this must still be
        // Consistent despite the garbage data sitting right there in the results list.
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 1, plan.OperationId);
        var results = new[]
        {
            Result(0, "mod-a"),
            Result(1, "totally-wrong-identifier"),
            Result(1, "totally-wrong-identifier"), // duplicate of the bad result above
        };

        var result = StepResultReconciler.Reconcile(journal, plan, results);

        Assert.Equal(StepResultReconciliationStatus.Consistent, result.Status);
    }

    [Fact]
    public void Reconcile_MissingResultBelowCursor_Indeterminate()
    {
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 2, plan.OperationId);
        var results = new[] { Result(0, "mod-a") }; // step 1 missing, but cursor claims 2 processed

        var result = StepResultReconciler.Reconcile(journal, plan, results);

        Assert.Equal(StepResultReconciliationStatus.Indeterminate, result.Status);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void Reconcile_DuplicateResultForSameStepIndexBelowCursor_Indeterminate()
    {
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 1, plan.OperationId);
        var results = new[] { Result(0, "mod-a"), Result(0, "mod-a") };

        var result = StepResultReconciler.Reconcile(journal, plan, results);

        Assert.Equal(StepResultReconciliationStatus.Indeterminate, result.Status);
    }

    [Fact]
    public void Reconcile_IdentifierMismatchBelowCursor_Indeterminate()
    {
        var plan = SamplePlan();
        var journal = SampleJournal(processedStepCount: 1, plan.OperationId);
        var results = new[] { Result(0, "wrong-identifier") }; // step 0's real identifier is "mod-a"

        var result = StepResultReconciler.Reconcile(journal, plan, results);

        Assert.Equal(StepResultReconciliationStatus.Indeterminate, result.Status);
    }
}
