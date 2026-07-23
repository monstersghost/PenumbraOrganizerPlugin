using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class PathMutationOperationTests
{
    private sealed class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    private static OperationExecutionStep Step(int index, string id, string target, OperationStepKind kind, int group) =>
        new(index, id, target, kind, group);

    private static OperationRecoveryTarget Target(string id, string snapshotPath, string finalPath) =>
        new(id, snapshotPath, finalPath, id);

    private static SetModPathResult Success => new(SetModPathStatus.Success, "Success", null);
    private static SetModPathResult NothingChanged => new(SetModPathStatus.NothingChanged, "NothingChanged", null);
    private static SetModPathResult PathRenameFailed => new(SetModPathStatus.PathRenameFailed, "PathRenameFailed", "collision");
    private static SetModPathResult ProviderUnavailable => new(SetModPathStatus.ProviderUnavailable, "SystemDisposed", "unavailable");

    private static OperationJournal NewJournal(OperationPlan plan) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: plan.OperationId,
        Type: plan.Type,
        Stage: OperationStage.Mutating,
        Resolution: OperationResolution.None,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: DateTimeOffset.UtcNow,
        TotalSteps: plan.ExecutionSteps.Count,
        ProcessedStepCount: 0,
        LastCompletedIdentifier: null,
        SnapshotId: Guid.NewGuid(),
        PlanId: plan.OperationId,
        TargetHash: plan.IntegrityHash,
        RecoveryOfOperationId: null,
        UpdatedAt: DateTimeOffset.UtcNow);

    private static string TempResultsDir(out DirectoryInfo dir)
    {
        dir = Directory.CreateTempSubdirectory();
        return dir.FullName;
    }

    [Fact]
    public void Advance_TwoIndependentSuccessfulSteps_ProcessesBothInOneCallGivenAmpleBudget()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0), Step(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A"), Target("mod-b", "Gear/B", "Weapons/B") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueSetModPathResult(Success);
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.MutationFinished, result.Status);
            Assert.Equal(2, result.Journal.ProcessedStepCount);
            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-a"]);
            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-b"]);

            var results = StepResultLog.ReadAll(OperationBundlePaths.ResultsPath(dir));
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(OperationStepDisposition.Succeeded, r.Disposition));
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_AlwaysProcessesAtLeastOneStepEvenWithZeroBudget()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.Zero, stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(1, result.Journal.ProcessedStepCount);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_ItemFailure_CascadesTheWholeGroupAndContinuesToTheNextGroup()
    {
        // Group 0: a two-way swap (temp hop + final move for X, final move for Y) where the temp
        // hop fails. Group 1: an unrelated independent step that must still succeed.
        var steps = new[]
        {
            Step(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
            Step(1, "Y", "P0", OperationStepKind.FinalMove, 0),
            Step(2, "X", "P2", OperationStepKind.FinalMove, 0),
            Step(3, "mod-c", "Weapons/C", OperationStepKind.FinalMove, 1),
        };
        var targets = new[] { Target("X", "P0", "P2"), Target("Y", "P2", "P0"), Target("mod-c", "Gear/C", "Weapons/C") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(PathRenameFailed); // X's temp hop fails
        adapter.EnqueueSetModPathResult(Success); // mod-c, group 1, unaffected
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.MutationFinished, result.Status);
            Assert.Equal(4, result.Journal.ProcessedStepCount); // cascaded past the whole group 0 range, then processed group 1
            Assert.Equal(TargetMutationStatus.SkippedAfterEarlierFailure, op.MutationStatusByIdentifier["X"]);
            Assert.Equal(TargetMutationStatus.SkippedAfterEarlierFailure, op.MutationStatusByIdentifier["Y"]);
            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-c"]);

            var results = StepResultLog.ReadAll(OperationBundlePaths.ResultsPath(dir));
            Assert.Equal(4, results.Count);
            Assert.Equal(OperationStepDisposition.Failed, results.Single(r => r.StepIndex == 0).Disposition);
            Assert.Equal(OperationStepDisposition.SkippedAfterEarlierFailure, results.Single(r => r.StepIndex == 1).Disposition);
            Assert.Equal(OperationStepDisposition.SkippedAfterEarlierFailure, results.Single(r => r.StepIndex == 2).Disposition);
            Assert.Equal(OperationStepDisposition.Succeeded, results.Single(r => r.StepIndex == 3).Disposition);

            // Only TWO SetModPath calls were ever made - steps 1 and 2 (the rest of the cascaded
            // group) were never attempted, proving the cascade skips rather than tries-then-discards.
            Assert.Equal(2, adapter.SetModPathCalls.Count);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void MutationStatusByIdentifier_TempHopSucceededButFinalMoveCascadeSkipped_ReportsSkippedNotSucceeded()
    {
        // Regression test: X's temp hop succeeds (step 0), but Y's final move (step 1, same
        // group) fails, cascading a skip onto X's own final move (step 2) - X's actual LAST step
        // is the skip, not the earlier successful temp hop. FindLastExecutedStatus must report
        // X's true last step's disposition (SkippedAfterEarlierFailure), not incorrectly unwind
        // past the skip to X's earlier (now-stale) successful temp hop.
        var steps = new[]
        {
            Step(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
            Step(1, "Y", "P0", OperationStepKind.FinalMove, 0),
            Step(2, "X", "P2", OperationStepKind.FinalMove, 0),
        };
        var targets = new[] { Target("X", "P0", "P2"), Target("Y", "P2", "P0") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success); // X's temp hop succeeds
        adapter.EnqueueSetModPathResult(PathRenameFailed); // Y's final move fails, cascades a skip onto X's final move
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(TargetMutationStatus.SkippedAfterEarlierFailure, op.MutationStatusByIdentifier["X"]);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_FinalStepStatusReflectsTheLastStepEvenWhenAnEarlierTempHopForTheSameIdentifierSucceeded()
    {
        // X's temp hop (step 0) succeeds; X's final move (step 2) fails. MutationStatusByIdentifier
        // must report the LAST step's outcome (FinalStepFailed), not get stuck on the temp hop's
        // success - this is the derived-property behavior that replaces opportunistic mutation.
        var steps = new[]
        {
            Step(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
            Step(1, "Y", "P0", OperationStepKind.FinalMove, 0),
            Step(2, "X", "P2", OperationStepKind.FinalMove, 0),
        };
        var targets = new[] { Target("X", "P0", "P2"), Target("Y", "P2", "P0") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success); // temp hop succeeds
        adapter.EnqueueSetModPathResult(Success); // Y succeeds
        adapter.EnqueueSetModPathResult(PathRenameFailed); // X's final move fails
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(TargetMutationStatus.FinalStepFailed, op.MutationStatusByIdentifier["X"]);
            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["Y"]);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_NothingChanged_TreatedAsSuccessNotFailure()
    {
        // Design decision, made explicit here: NothingChanged means a real SetModPath call WAS
        // made and Penumbra reported no effective change - it is not a skip (nothing was ever
        // attempted), so it maps to OperationStepDisposition.Succeeded / FinalStepSucceeded, the
        // same as an ordinary Success, not to SkippedAlreadySatisfied (which is reserved for a
        // step this engine never attempts at all - see Task 4's Interfaces note).
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(NothingChanged);
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(TargetMutationStatus.FinalStepSucceeded, op.MutationStatusByIdentifier["mod-a"]);
            var result = Assert.Single(StepResultLog.ReadAll(OperationBundlePaths.ResultsPath(dir)));
            Assert.Equal(OperationStepDisposition.Succeeded, result.Disposition);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_StopRequestedAtCallEntry_ProcessesNoStepsAndReturnsCancellationObserved()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations(); // no result queued - must never be called
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: true, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.CancellationObserved, result.Status);
            Assert.Equal(MutationStopReason.UserCancellation, result.StopReason);
            Assert.Equal(0, result.Journal.ProcessedStepCount);
            Assert.Empty(adapter.SetModPathCalls);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_MultipleCallsAcrossFrames_ResumesFromWhereItLeftOff()
    {
        var steps = new[]
        {
            Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            Step(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
        };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A"), Target("mod-b", "Gear/B", "Weapons/B") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueSetModPathResult(Success);
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var afterFirst = op.Advance(NewJournal(plan), TimeSpan.Zero, stopRequested: false, checkpointIfDue: _ => { });
            var afterSecond = op.Advance(afterFirst.Journal, TimeSpan.Zero, stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.Working, afterFirst.Status);
            Assert.Equal(1, afterFirst.Journal.ProcessedStepCount);
            Assert.Equal(MutationAdvanceStatus.MutationFinished, afterSecond.Status);
            Assert.Equal(2, afterSecond.Journal.ProcessedStepCount);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_FrameBudgetExceeded_StopsStartingNewStepsWithinTheSameCall()
    {
        // Three steps, each consuming 3ms (via onCall clock-advance), budget 4ms: step 0 (0->3ms),
        // check before step 1: elapsed 3ms < 4ms budget -> proceed; step 1 (3->6ms), check before
        // step 2: elapsed 6ms >= 4ms budget -> stop. Only steps 0 and 1 process in this call.
        var steps = new[]
        {
            Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            Step(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
            Step(2, "mod-c", "Weapons/C", OperationStepKind.FinalMove, 2),
        };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A"), Target("mod-b", "Gear/B", "Weapons/B"), Target("mod-c", "Gear/C", "Weapons/C") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var clock = new FakeClock();
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success, onCall: () => clock.Advance(TimeSpan.FromMilliseconds(3)));
        adapter.EnqueueSetModPathResult(Success, onCall: () => clock.Advance(TimeSpan.FromMilliseconds(3)));
        // A third result is deliberately NOT queued - if step 2 were incorrectly attempted this
        // call, FakePenumbraOperations would throw and fail the test.
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, clock, new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromMilliseconds(4), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.Working, result.Status);
            Assert.Equal(2, result.Journal.ProcessedStepCount);
            Assert.Equal(2, adapter.SetModPathCalls.Count);

            // A second call resumes and finishes the remaining step.
            adapter.EnqueueSetModPathResult(Success, onCall: () => clock.Advance(TimeSpan.FromMilliseconds(3)));
            var second = op.Advance(result.Journal, TimeSpan.FromMilliseconds(4), stopRequested: false, checkpointIfDue: _ => { });
            Assert.Equal(MutationAdvanceStatus.MutationFinished, second.Status);
            Assert.Equal(3, second.Journal.ProcessedStepCount);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_SinglePathologicalCallExceedsBudgetButStillCompletesAndEmitsASlowCallDiagnostic()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var clock = new FakeClock();
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success, onCall: () => clock.Advance(TimeSpan.FromMilliseconds(80)));
        var diagnostics = new RecordingDiagnosticsSink();
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, clock, diagnostics, dir);
            var journal = NewJournal(plan);
            var result = op.Advance(journal, TimeSpan.FromMilliseconds(4), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.MutationFinished, result.Status);
            Assert.Equal(1, result.Journal.ProcessedStepCount); // completed despite exceeding the budget
            var slowCall = Assert.Single(diagnostics.SlowCalls);
            Assert.Equal("mod-a", slowCall.Identifier);
            Assert.Equal(journal.OperationId, slowCall.OperationId);
            Assert.True(slowCall.Duration >= TimeSpan.FromMilliseconds(50));
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_ProviderUnavailable_ReturnsIntegrityFailureAndStopsWithoutCascading()
    {
        var steps = new[]
        {
            Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            Step(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
        };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A"), Target("mod-b", "Gear/B", "Weapons/B") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(ProviderUnavailable);
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.IntegrityFailure, result.Status);
            Assert.Equal(MutationStopReason.ProviderUnavailable, result.StopReason);
            Assert.Equal(0, result.Journal.ProcessedStepCount); // step 0 itself never succeeded, so the cursor doesn't advance past it
            Assert.Single(adapter.SetModPathCalls); // step 1 was never attempted
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void MutationStatusByIdentifier_TargetNeverAttempted_ReportsNotAttemptedNotFalselySucceeded()
    {
        // Regression test: OperationStepDisposition.Succeeded is the enum's default value (0),
        // so a naive dictionary lookup for a step that was never reached would silently return
        // Succeeded instead of "no entry". mod-b's step is never attempted because mod-a's step
        // returns ProviderUnavailable and Advance stops immediately without cascading.
        var steps = new[]
        {
            Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0),
            Step(1, "mod-b", "Weapons/B", OperationStepKind.FinalMove, 1),
        };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A"), Target("mod-b", "Gear/B", "Weapons/B") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(ProviderUnavailable);
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(TargetMutationStatus.NotAttempted, op.MutationStatusByIdentifier["mod-b"]);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_UnexpectedExceptionFromAdapter_ReturnsIntegrityFailureRatherThanThrowing()
    {
        var steps = new[] { Step(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new[] { Target("mod-a", "Gear/A", "Weapons/A") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathException(new InvalidOperationException("simulated adapter failure"));
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            var result = op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => { });

            Assert.Equal(MutationAdvanceStatus.IntegrityFailure, result.Status);
            Assert.Equal(MutationStopReason.UnexpectedFatalException, result.StopReason);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }

    [Fact]
    public void Advance_CallsCheckpointCallbackOnceForEachStepAndOnceForTheWholeCascadeBatch()
    {
        var steps = new[]
        {
            Step(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
            Step(1, "Y", "P0", OperationStepKind.FinalMove, 0),
            Step(2, "X", "P2", OperationStepKind.FinalMove, 0),
            Step(3, "mod-c", "Weapons/C", OperationStepKind.FinalMove, 1),
        };
        var targets = new[] { Target("X", "P0", "P2"), Target("Y", "P2", "P0"), Target("mod-c", "Gear/C", "Weapons/C") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(PathRenameFailed); // X's temp hop fails - cascades steps 1,2 in one batch
        adapter.EnqueueSetModPathResult(Success); // mod-c, its own call
        var checkpointCallCount = 0;
        var dir = TempResultsDir(out var dirInfo);
        try
        {
            var op = new PathMutationOperation(plan, adapter, new FakeClock(), new NoOpDiagnosticsSink(), dir);
            op.Advance(NewJournal(plan), TimeSpan.FromSeconds(1), stopRequested: false, checkpointIfDue: _ => checkpointCallCount++);

            // One call for the failed step + its cascade batch, one call for mod-c: 2 total.
            Assert.Equal(2, checkpointCallCount);
        }
        finally
        {
            dirInfo.Delete(recursive: true);
        }
    }
}
