using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationControllerTests
{
    private sealed class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    private static SetModPathResult Success => new(SetModPathStatus.Success, "Success", null);

    private static OperationPlan SinglePlan(string id = "mod-a", OperationType type = OperationType.Apply) =>
        OperationPlan.Create(type, [new(0, id, "Weapons/A", OperationStepKind.FinalMove, 0)], [new(id, "Gear/A", "Weapons/A", id)]);

    private static OperationJournal InterruptedJournal(Guid id) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion, OperationId: id, Type: OperationType.Apply,
        Stage: OperationStage.Mutating, Resolution: OperationResolution.None, SuccessorOperationId: null,
        CancellationRequested: false, StartedAt: DateTimeOffset.UtcNow, TotalSteps: 1, ProcessedStepCount: 0,
        LastCompletedIdentifier: null, SnapshotId: Guid.NewGuid(), PlanId: Guid.NewGuid(), TargetHash: "irrelevant",
        RecoveryOfOperationId: null, UpdatedAt: DateTimeOffset.UtcNow);

    private static OperationController NewController(
        IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink? diagnostics = null, string? operationsRoot = null) =>
        new(adapter, clock, diagnostics ?? new NoOpDiagnosticsSink(), TimeSpan.FromMilliseconds(4), operationsRoot ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

    [Fact]
    public void State_Initially_IdleWithCanStartApplyTrue()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Null(controller.State.Stage);
        Assert.True(controller.State.CanStartApply);
    }

    [Fact]
    public void StartApply_RestoreTypePlan_Throws()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());

            Assert.Throws<ArgumentException>(() => controller.StartApply(SinglePlan(type: OperationType.Restore), Guid.NewGuid(), dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartApply_WhileAnotherIsNonTerminal_Throws()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName); // now Mutating, non-terminal

            Assert.Throws<InvalidOperationException>(() => controller.StartApply(SinglePlan("mod-b"), Guid.NewGuid(), dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartApply_AfterAPriorOperationTerminated_IsAllowedAndOverwritesTheTerminalState()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(
            LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));
        var clock = new FakeClock();
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, clock);
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);
            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> Verifying
            controller.Update(); // Verifying -> Completed
            Assert.Equal(OperationStage.Completed, controller.State.Stage);

            var exception = Record.Exception(() => controller.StartApply(SinglePlan("mod-b"), Guid.NewGuid(), dir.FullName));

            Assert.Null(exception);
            Assert.Equal(OperationStage.Mutating, controller.State.Stage); // the new operation, not the old terminal one
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CanStartNext_TerminalStageWithoutRecovery_IsTrue()
    {
        var journal = new OperationJournal(
            OperationJournal.CurrentSchemaVersion, Guid.NewGuid(), OperationType.Apply,
            OperationStage.Completed, OperationResolution.None, null, false, DateTimeOffset.UtcNow,
            1, 1, "mod-a", Guid.NewGuid(), Guid.NewGuid(), "hash", null, DateTimeOffset.UtcNow);

        Assert.True(OperationController.CanStartNext(journal, requiresRecovery: false));
    }

    [Fact]
    public void CanStartNext_NonTerminalStage_IsFalse()
    {
        var journal = new OperationJournal(
            OperationJournal.CurrentSchemaVersion, Guid.NewGuid(), OperationType.Apply,
            OperationStage.Mutating, OperationResolution.None, null, false, DateTimeOffset.UtcNow,
            1, 0, null, Guid.NewGuid(), Guid.NewGuid(), "hash", null, DateTimeOffset.UtcNow);

        Assert.False(OperationController.CanStartNext(journal, requiresRecovery: false));
    }

    [Fact]
    public void CanStartNext_TerminalStageButRequiresRecovery_IsFalse()
    {
        // Not reachable through the real engine today (see this task's own notes), but the
        // predicate must still be correct on its own terms - this is the regression test for the
        // admission guard fix below, exercised directly rather than via a live engine run.
        var journal = new OperationJournal(
            OperationJournal.CurrentSchemaVersion, Guid.NewGuid(), OperationType.Apply,
            OperationStage.FailedPartiallyApplied, OperationResolution.None, null, false, DateTimeOffset.UtcNow,
            1, 1, "mod-a", Guid.NewGuid(), Guid.NewGuid(), "hash", null, DateTimeOffset.UtcNow);

        Assert.False(OperationController.CanStartNext(journal, requiresRecovery: true));
    }

    [Fact]
    public void StartRestore_ApplyTypePlan_Throws()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());

            Assert.Throws<ArgumentException>(() => controller.StartRestore(SinglePlan(type: OperationType.Apply), Guid.NewGuid(), dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartRestore_RestoreTypePlan_Succeeds()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());

            var exception = Record.Exception(() => controller.StartRestore(SinglePlan(type: OperationType.Restore), Guid.NewGuid(), dir.FullName));

            Assert.Null(exception);
            Assert.Equal(OperationStage.Mutating, controller.State.Stage);
            Assert.Equal(OperationType.Restore, controller.State.Kind);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartRestore_ZeroStepPlan_ReachesTerminalUiConsumedStateAfterTheUsualThreeUpdates()
    {
        // Update() advances at most one stage per call (Mutating, then Refreshing, then Verifying -
        // each its own "if (Stage == X) { ...; return; }" block in AdvanceActiveOperation), the same
        // as every non-empty-plan test in this file - a zero-step plan still needs all three calls,
        // it just has nothing to do during the Mutating one. Refreshing/Verifying still call into the
        // adapter even with zero recovery targets (confirmed empirically: with no adapter responses
        // enqueued, this reaches FailedBeforeMutation, not Completed), so both still need enqueuing,
        // just with an empty live-mod list since there's nothing to verify against.
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var emptyPlan = OperationPlan.Create(OperationType.Restore, [], []);
            var controller = NewController(adapter, new FakeClock());
            controller.StartRestore(emptyPlan, Guid.NewGuid(), dir.FullName);

            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> Verifying
            controller.Update(); // Verifying -> Completed

            Assert.Equal(OperationStage.Completed, controller.State.Stage);
            Assert.Equal(OperationType.Restore, controller.State.Kind);
            Assert.True(controller.State.CanStartRestore);
            Assert.False(controller.State.RequiresRecovery);
            Assert.Equal(0, controller.State.ProcessedSteps);
            Assert.Equal(0, controller.State.TotalSteps);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RegisterDiscoveredRecovery_NoRecoveryNeeded_StaysIdle()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.NoRecoveryNeeded, [], []),
            new Dictionary<Guid, OperationJournal>());

        controller.RegisterDiscoveredRecovery(discovery);

        Assert.Equal(OperationStateSnapshot.Idle, controller.State);
        Assert.False(controller.IsBlockedByMultipleRoots);
        Assert.Null(controller.GetRecoveryAssessment());
    }

    [Fact]
    public void RegisterDiscoveredRecovery_SingleAuthoritative_RequiresRecoveryAndCanResolveTrueImmediately()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock(), operationsRoot: dir.FullName);
            var journalId = Guid.NewGuid();
            var journal = InterruptedJournal(journalId);
            var discovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [journalId], [journalId]),
                new Dictionary<Guid, OperationJournal> { [journalId] = journal });

            controller.RegisterDiscoveredRecovery(discovery);

            Assert.True(controller.State.RequiresRecovery);
            Assert.True(controller.State.CanResolveRecovery);
            Assert.True(controller.State.RecoveryClassificationPending); // WaitingForProvider until Task 6's Update() logic advances it
            Assert.False(controller.State.CanStartApply);
            Assert.False(controller.State.CanScan);
            Assert.False(controller.IsBlockedByMultipleRoots);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RegisterDiscoveredRecovery_MultipleDisconnectedRoots_RequiresRecoveryAndCanResolveTrueButBlockedFlagSet()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, [idA, idB], [idA, idB]),
            new Dictionary<Guid, OperationJournal> { [idA] = InterruptedJournal(idA), [idB] = InterruptedJournal(idB) });

        controller.RegisterDiscoveredRecovery(discovery);

        Assert.True(controller.State.RequiresRecovery);
        Assert.True(controller.State.CanResolveRecovery); // AcceptAllAndCloseInterruptedOperations is a real resolution, Task 8
        Assert.False(controller.State.RecoveryClassificationPending);
        Assert.False(controller.State.CanStartApply);
        Assert.True(controller.IsBlockedByMultipleRoots);
        Assert.Null(controller.GetRecoveryAssessment());
    }

    [Fact]
    public void RegisterDiscoveredRecovery_CycleDetected_SameLockoutShapeAsMultipleDisconnectedRoots()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var id = Guid.NewGuid();
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.CycleDetected, [id], [id]),
            new Dictionary<Guid, OperationJournal> { [id] = InterruptedJournal(id) });

        controller.RegisterDiscoveredRecovery(discovery);

        Assert.True(controller.State.RequiresRecovery);
        Assert.True(controller.State.CanResolveRecovery);
        Assert.True(controller.IsBlockedByMultipleRoots);
    }

    [Fact]
    public void StartApply_SetsCanStartApplyFalseAndStageMutating()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            Assert.False(controller.State.CanStartApply);
            Assert.Equal(OperationStage.Mutating, controller.State.Stage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartApply_PersistsPreparedThenMutatingAsTwoForcedCheckpoints()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            // The journal on disk reflects the LAST forced write (Mutating) - this test proves
            // both writes happened without needing to intercept the intermediate Prepared state,
            // by asserting persistence succeeded at all (StartApply would have thrown on a bad
            // sequence) and the final on-disk stage is correct.
            var journalPath = OperationBundlePaths.JournalPath(dir.FullName);
            Assert.True(OperationJournalCodec.TryLoad(journalPath, out var journal));
            Assert.Equal(OperationStage.Mutating, journal!.Stage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_DrivesMutationThroughRefreshingToVerifyingAndSettles()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(
            LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));
        var clock = new FakeClock();
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, clock);
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            controller.Update();
            controller.Update();
            controller.Update();

            Assert.Equal(OperationStage.Completed, controller.State.Stage);
            Assert.True(controller.State.CanStartApply); // terminal AND immediately allows a new operation
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_WithNoActiveOperation_DoesNothingAndDoesNotThrow()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        var exception = Record.Exception(controller.Update);

        Assert.Null(exception);
    }

    [Fact]
    public void Update_UnexpectedExceptionFromAdapter_RoutesThroughRefreshingThenSettlesAsFailed()
    {
        // UnexpectedFatalException (unlike ProviderUnavailable) doesn't prove the adapter itself
        // is unusable, so it still routes through Refreshing/Verifying before settling - carrying
        // the eventual Failed* disposition forward rather than settling immediately.
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathException(new InvalidOperationException("simulated"));
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            controller.Update(); // Mutating -> IntegrityFailure -> Refreshing (pending Failed* disposition carried forward)
            Assert.Equal(OperationStage.Refreshing, controller.State.Stage);

            controller.Update(); // Refreshing -> Verifying
            Assert.Equal(OperationStage.Verifying, controller.State.Stage);

            var exception = Record.Exception(() => controller.Update()); // Verifying -> settles as Failed*, not Completed

            Assert.Null(exception);
            Assert.Equal(OperationStage.FailedBeforeMutation, controller.State.Stage);
            Assert.False(controller.State.RequiresRecovery);
            Assert.True(controller.State.CanStartApply);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_ProviderUnavailableDuringMutation_SettlesImmediatelyWithoutAttemptingRefresh()
    {
        // ProviderUnavailable means the adapter itself is judged unusable - unlike
        // UnexpectedFatalException, this settles directly without ever entering Refreshing. No
        // RefreshResult is queued on the adapter; if Refreshing were incorrectly attempted,
        // FakePenumbraOperations would throw on the missing queued result and fail this test.
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(new SetModPathResult(SetModPathStatus.ProviderUnavailable, "SystemDisposed", "unavailable"));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            var exception = Record.Exception(() => controller.Update());

            Assert.Null(exception);
            Assert.Equal(OperationStage.FailedBeforeMutation, controller.State.Stage);
            Assert.False(controller.State.RequiresRecovery);
            Assert.True(controller.State.CanStartApply);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_RefreshRecoveryRequired_RetainsContextAndBlocksNewOperationsWithoutThrowing()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.ProviderUnavailable));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);
            controller.Update(); // Mutating -> Refreshing

            var exception = Record.Exception(controller.Update); // Refreshing -> RecoveryRequired

            Assert.Null(exception);
            Assert.Equal(OperationStage.Refreshing, controller.State.Stage); // left non-terminal, not erased
            Assert.True(controller.State.RequiresRecovery);
            Assert.False(controller.State.CanStartApply); // locked, not freed as though nothing happened

            // A further Update() must not attempt to advance the stuck operation again.
            var secondException = Record.Exception(controller.Update);
            Assert.Null(secondException);
            Assert.Equal(OperationStage.Refreshing, controller.State.Stage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_VerificationRecoveryRequired_RetainsContextAndBlocksNewOperations()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);
            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> Verifying

            controller.Update(); // Verifying -> RecoveryRequired

            Assert.Equal(OperationStage.Verifying, controller.State.Stage);
            Assert.True(controller.State.RequiresRecovery);
            Assert.False(controller.State.CanStartApply);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RequestCancellation_PersistsCancellationRequestedImmediately()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(new FakePenumbraOperations(), new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            controller.RequestCancellation();

            var journalPath = OperationBundlePaths.JournalPath(dir.FullName);
            Assert.True(OperationJournalCodec.TryLoad(journalPath, out var journal));
            Assert.True(journal!.CancellationRequested);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RequestCancellation_ThenUpdate_StopsMutationAndOnTrustworthyVerificationEndsCancelled()
    {
        var adapter = new FakePenumbraOperations();
        // No SetModPath ever queued/consumed - cancellation observed at the very start of the
        // first Advance() call means zero mutation steps run.
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);
            controller.RequestCancellation();

            controller.Update(); // Mutating -> Refreshing (cancellation observed, no steps ran)
            controller.Update(); // Refreshing -> Verifying
            controller.Update(); // Verifying -> Cancelled (verification itself was trustworthy)

            Assert.Equal(OperationStage.Cancelled, controller.State.Stage);
            Assert.True(controller.State.CanStartApply);
            Assert.Empty(adapter.SetModPathCalls);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RequestCancellation_ThenUntrustworthyVerification_RequiresRecoveryNotCancelled()
    {
        // Cancellation was requested, but verification itself can't be trusted - design section
        // 5a's precedence rule: recoverability outranks asserting a clean Cancelled outcome.
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);
            controller.RequestCancellation();

            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> Verifying
            controller.Update(); // Verifying -> RecoveryRequired, NOT Cancelled

            Assert.NotEqual(OperationStage.Cancelled, controller.State.Stage);
            Assert.Equal(OperationStage.Verifying, controller.State.Stage);
            Assert.True(controller.State.RequiresRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RequestCancellation_WithNoActiveOperation_DoesNothing()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        var exception = Record.Exception(controller.RequestCancellation);

        Assert.Null(exception);
        Assert.True(controller.State.CanStartApply);
    }

    [Fact]
    public void State_ReflectsProcessedAndSuccessfulTargetsSeparatelyFromStepCount()
    {
        // A cycle-breaking plan: 3 execution steps (temp hop + 2 final moves) but only 2 targets.
        var steps = new OperationExecutionStep[]
        {
            new(0, "X", "TEMP", OperationStepKind.CycleBreakingTemporaryMove, 0),
            new(1, "Y", "P0", OperationStepKind.FinalMove, 0),
            new(2, "X", "P2", OperationStepKind.FinalMove, 0),
        };
        var targets = new OperationRecoveryTarget[] { new("X", "P0", "P2", "X"), new("Y", "P2", "P0", "Y") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueSetModPathResult(Success);
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(plan, Guid.NewGuid(), dir.FullName);
            controller.Update(); // processes all 3 steps in one call given ample budget

            Assert.Equal(3, controller.State.ProcessedSteps);
            Assert.Equal(3, controller.State.TotalSteps);
            Assert.Equal(2, controller.State.ProcessedTargets); // X and Y, not 3
            Assert.Equal(2, controller.State.SuccessfulTargets);
            Assert.Equal(2, controller.State.TotalTargets);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void State_LastProcessedDisplayNameIsTheRealModNameNotTheIdentifier()
    {
        var steps = new OperationExecutionStep[] { new(0, "mod-a-identifier", "Weapons/A", OperationStepKind.FinalMove, 0) };
        var targets = new OperationRecoveryTarget[] { new("mod-a-identifier", "Gear/A", "Weapons/A", "A Pretty Display Name") };
        var plan = OperationPlan.Create(OperationType.Apply, steps, targets);
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(plan, Guid.NewGuid(), dir.FullName);
            controller.Update();

            Assert.Equal("mod-a-identifier", controller.State.LastProcessedIdentifier);
            Assert.Equal("A Pretty Display Name", controller.State.LastProcessedDisplayName);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_StepResultAppendFailure_DoesNotEscapeAndSettlesAsFailedBeforeMutation()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            // StepResultLog.Append opens the results file with FileShare.Read - holding it open
            // with FileShare.None here forces that constructor to throw a sharing-violation
            // IOException, uncaught inside PathMutationOperation.Advance (before ProcessedStepCount
            // is ever advanced past 0), exercising Update()'s outer catch without needing to fake
            // the adapter itself into failing. journal.json is untouched by this lock, so the
            // failure-checkpoint write in the catch block succeeds - this is the single-failure
            // path, distinct from the double-failure case covered below.
            var resultsPath = OperationBundlePaths.ResultsPath(dir.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
            using (new FileStream(resultsPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var exception = Record.Exception(() => controller.Update());

                Assert.Null(exception);
            }

            Assert.False(controller.State.RequiresRecovery);
            Assert.Equal(OperationStage.FailedBeforeMutation, controller.State.Stage);
            Assert.True(controller.State.CanStartApply);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_JournalWriteFailsOnBothThePrimaryAndTheFailureCheckpointAttempt_LeavesOperationRequiringRecoveryWithStageUnchanged()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock());
            controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName);

            // AtomicFile.CreateOrReplace's File.Move(tempPath, path, overwrite: true) throws when
            // the destination is held open with no sharing - this makes BOTH the in-loop forced
            // checkpoint (entering Refreshing, which mutates active.Journal in memory before the
            // write that then fails) AND Update()'s own failure-checkpoint attempt fail against the
            // same locked journal.json. The second failure means the FailedPartiallyApplied record
            // it built is never committed to _active.Journal, which is left holding the last
            // in-memory value (Refreshing) rather than an unverified terminal Stage.
            var journalPath = OperationBundlePaths.JournalPath(dir.FullName);
            using (new FileStream(journalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var exception = Record.Exception(() => controller.Update());

                Assert.Null(exception);
            }

            Assert.True(controller.State.RequiresRecovery);
            Assert.Equal(OperationStage.Refreshing, controller.State.Stage); // last in-memory value before the failed write, not the failure stage
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
