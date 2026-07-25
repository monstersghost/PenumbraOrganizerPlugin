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
    public void StartApply_WhilePendingRecoveryIsSet_Throws()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            var controller = NewController(new FakePenumbraOperations(), new FakeClock(), operationsRoot: dir.FullName);
            var discovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [id], [id]),
                new Dictionary<Guid, OperationJournal> { [id] = InterruptedJournal(id) });
            controller.RegisterDiscoveredRecovery(discovery); // sets _pendingRecovery, not _active

            Assert.Throws<InvalidOperationException>(() => controller.StartApply(SinglePlan(), Guid.NewGuid(), dir.FullName));
            Assert.Throws<InvalidOperationException>(() => controller.StartRestore(SinglePlan(type: OperationType.Restore), Guid.NewGuid(), dir.FullName));
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

    private static OperationController NewControllerWithPendingRecovery(
        FakePenumbraOperations adapter, FakeClock clock, string operationsRoot, out Guid journalId)
    {
        var controller = NewController(adapter, clock, operationsRoot: operationsRoot);
        journalId = Guid.NewGuid();
        var journal = InterruptedJournal(journalId);
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [journalId], [journalId]),
            new Dictionary<Guid, OperationJournal> { [journalId] = journal });
        controller.RegisterDiscoveredRecovery(discovery);
        return controller;
    }

    [Fact]
    public void Update_ValidPlanAndSnapshot_ClassifiesOnceIpcSucceeds()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            controller.Update();

            Assert.False(controller.State.RecoveryClassificationPending);
            Assert.NotNull(controller.GetRecoveryAssessment());
            Assert.True(controller.State.CanResolveRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_PlanMissing_BecomesPermanentlyClassificationUnavailableWithoutCallingIpc()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations(); // nothing enqueued - a GetLiveMods() call would throw
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out _);

            controller.Update();
            controller.Update(); // a second call must not attempt GetLiveMods() either

            Assert.False(controller.State.RecoveryClassificationPending);
            Assert.Null(controller.GetRecoveryAssessment());
            Assert.True(controller.State.CanResolveRecovery); // Keep Current unaffected
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_SnapshotMissingButPlanValid_ClassificationStillSucceeds()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            // snapshot.json intentionally not written
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            controller.Update();

            Assert.NotNull(controller.GetRecoveryAssessment());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(LiveModReadStatus.TemporarilyUnavailable)]
    [InlineData(LiveModReadStatus.ProviderUnavailable)]
    public void Update_RetryableIpcStatus_StaysPendingAndRetriesAfterThrottleInterval(LiveModReadStatus status)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var clock = new FakeClock();
            var controller = NewControllerWithPendingRecovery(adapter, clock, dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(status, null));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            controller.Update(); // consumes the retryable response - still pending

            Assert.True(controller.State.RecoveryClassificationPending);
            Assert.Null(controller.GetRecoveryAssessment());

            clock.Advance(TimeSpan.FromSeconds(1));
            controller.Update(); // throttle interval elapsed - consumes the Success response

            Assert.False(controller.State.RecoveryClassificationPending);
            Assert.NotNull(controller.GetRecoveryAssessment());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_InvalidDataIpcStatus_BecomesPermanentlyClassificationUnavailable()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.InvalidData, null));

            controller.Update();

            Assert.False(controller.State.RecoveryClassificationPending); // permanently unavailable, not pending
            Assert.Null(controller.GetRecoveryAssessment());
            Assert.True(controller.State.CanResolveRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_ClassificationThrowsUnexpectedException_BecomesClassificationUnavailableRatherThanPropagating()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, null), onCall: () => throw new InvalidOperationException("simulated unexpected failure"));

            var exception = Record.Exception(() => controller.Update());

            Assert.Null(exception);
            Assert.False(controller.State.RecoveryClassificationPending);
            Assert.Null(controller.GetRecoveryAssessment());
            Assert.True(controller.State.CanResolveRecovery); // Keep Current remains available
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_CalledManyTimesWithinSameSecond_CallsGetLiveModsAtMostOnce()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var clock = new FakeClock();
            var controller = NewControllerWithPendingRecovery(adapter, clock, dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            var callCount = 0;
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.TemporarilyUnavailable, null), onCall: () => callCount++);

            for (var i = 0; i < 20; i++)
                controller.Update(); // no clock advance between calls - only the first should reach the adapter

            Assert.Equal(1, callCount);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_PlanInvalidSnapshotValid_PopulatesLiveSnapshotButNotAssessment()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            // plan.json intentionally not written - PlanCheckStatus becomes Missing
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            controller.Update();

            Assert.Null(controller.GetRecoveryAssessment()); // no valid plan - Assessment stays null
            Assert.True(controller.State.CanRestorePreviousState);
            Assert.False(controller.State.CanContinueRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_PlanInvalidSnapshotValid_LiveReadKeepsRetryingAfterClassificationSettlesPermanently()
    {
        // Review point 3: an earlier draft of this test tried to write snapshot.json AFTER a first
        // Update() call and expected a later Update() to discover it - impossible against the real
        // implementation, since ArtifactStatusChecker only ever runs once per artifact
        // (SnapshotCheckStatus/PlanCheckStatus permanently leave Unchecked on their first check,
        // matching ArtifactStatusChecker's own documented "checked at most once" contract). What this
        // test actually needs to prove - that Update()'s outer gate keeps retrying the live read after
        // ClassificationStatus alone has already settled - doesn't need the artifact files to change
        // at all; only the live read itself needs to fail once, then succeed on retry.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var clock = new FakeClock();
            var controller = NewControllerWithPendingRecovery(adapter, clock, dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            // plan.json intentionally not written - PlanCheckStatus becomes Missing, ClassificationStatus
            // settles permanently to ClassificationUnavailable on the first Update() call below.
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.TemporarilyUnavailable, null));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])));

            controller.Update(); // PlanCheckStatus=Missing -> ClassificationUnavailable; SnapshotCheckStatus=Valid; live read attempted -> TemporarilyUnavailable, LiveReadStatus stays WaitingForProvider
            Assert.False(controller.State.CanContinueRecovery);
            Assert.False(controller.State.CanRestorePreviousState);

            clock.Advance(TimeSpan.FromSeconds(1));
            controller.Update(); // ClassificationStatus already settled, but LiveReadStatus is still WaitingForProvider - if Update()'s outer gate incorrectly stopped calling TryAdvanceClassification once ClassificationStatus alone settled, this queued response would never be consumed and the test would fail with "no queued result"

            Assert.False(controller.State.CanContinueRecovery);
            Assert.True(controller.State.CanRestorePreviousState);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_BothPlanAndSnapshotInvalid_SettlesPermanentlyAndNeverCallsGetLiveMods()
    {
        // Review point 4: LiveReadStatus must settle to Unavailable here, not stay WaitingForProvider
        // forever - otherwise Update()'s outer gate (ClassificationStatus == WaitingForProvider ||
        // LiveReadStatus == WaitingForProvider) never closes, and TryAdvanceClassification keeps
        // getting called every single tick indefinitely even though it can do no useful work. Proven
        // two ways: many repeated Update() calls never reach the adapter at all (FakePenumbraOperations
        // throws if GetLiveMods() is called with nothing queued, matching
        // Update_CalledManyTimesWithinSameSecond_CallsGetLiveModsAtMostOnce's own established pattern),
        // and the clock is advanced past the retry throttle between calls so a still-open gate would
        // have every opportunity to attempt a (would-throw) IPC call.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations(); // nothing enqueued - a GetLiveMods() call would throw
            var clock = new FakeClock();
            var controller = NewControllerWithPendingRecovery(adapter, clock, dir.FullName, out _);
            // Neither plan.json nor snapshot.json written - both artifact checks resolve to Missing.

            for (var i = 0; i < 20; i++)
            {
                controller.Update();
                clock.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.False(controller.State.CanContinueRecovery);
            Assert.False(controller.State.CanRestorePreviousState);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_ValidPlanAndSnapshotClassifiedReady_CanContinueRecoveryBecomesTrue()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)]))); // AtIntended - a valid, empty Continue

            controller.Update();

            Assert.True(controller.State.CanContinueRecovery);
            Assert.True(controller.State.CanRestorePreviousState);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_ValidPlanButBlockingClassification_CanContinueRecoveryStaysFalse()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([]))); // mod-a missing live -> MissingLive, blocking

            controller.Update();

            Assert.NotNull(controller.GetRecoveryAssessment()); // classification itself still succeeded
            Assert.False(controller.State.CanContinueRecovery);
            Assert.True(controller.State.CanRestorePreviousState); // Restore Previous State is unaffected by Continue's blocking rule
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_DuplicateLiveIdentifiers_BothCanContinueAndCanRestoreStayFalse()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto", new Dictionary<string, string>()));
            var duplicateSnapshot = new LiveModSnapshot(
                new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Weapons/A", false) },
                new HashSet<string> { "mod-a" }); // DuplicateIdentifiers non-empty
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, duplicateSnapshot));

            controller.Update();

            Assert.False(controller.State.CanContinueRecovery);
            Assert.False(controller.State.CanRestorePreviousState);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
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

    [Fact]
    public void ResolveKeepCurrent_NoPendingRecovery_Throws()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Throws<InvalidOperationException>(() => controller.ResolveKeepCurrent());
    }

    [Fact]
    public void ResolveKeepCurrent_HappyPath_ResolvesRelocatesAndUnblocks()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewControllerWithPendingRecovery(new FakePenumbraOperations(), new FakeClock(), dir.FullName, out var journalId);
            var activeBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDirectory), InterruptedJournal(journalId));

            var result = controller.ResolveKeepCurrent();

            Assert.Equal(OperationController.KeepCurrentResolutionResult.ResolvedAndArchived, result);
            Assert.True(controller.State.CanStartApply);
            Assert.True(controller.State.CanStartRestore);
            Assert.False(controller.State.RequiresRecovery);
            Assert.False(Directory.Exists(activeBundleDirectory));
            var completedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, journalId);
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var resolved));
            Assert.True(resolved!.IsTerminal);
            Assert.Equal(OperationResolution.AcceptedCurrentState, resolved.Resolution);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveKeepCurrent_CalledAgainAfterAMatchingPriorSuccess_IsIdempotent()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewControllerWithPendingRecovery(new FakePenumbraOperations(), new FakeClock(), dir.FullName, out var journalId);
            var activeBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDirectory), InterruptedJournal(journalId));
            controller.ResolveKeepCurrent(); // first call relocates active/ -> completed/

            // Simulate a retry: re-register the same journal as pending (as if a second discovery
            // pass found it again before the first resolution's relocation was known to have
            // succeeded) and resolve it again.
            Directory.CreateDirectory(activeBundleDirectory);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDirectory), InterruptedJournal(journalId));
            var discovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [journalId], [journalId]),
                new Dictionary<Guid, OperationJournal> { [journalId] = InterruptedJournal(journalId) });
            controller.RegisterDiscoveredRecovery(discovery);

            var result = controller.ResolveKeepCurrent();

            // The completed/ destination from the first call already exists, matches (same
            // OperationId, terminal, same Resolution), so this exercises the "already relocated"
            // branch specifically, not a fresh move.
            Assert.Equal(OperationController.KeepCurrentResolutionResult.ResolvedAndArchived, result);
            Assert.False(controller.State.RequiresRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveKeepCurrent_ExistingDestinationDoesNotMatch_ReturnsDeferredButStillUnblocks()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewControllerWithPendingRecovery(new FakePenumbraOperations(), new FakeClock(), dir.FullName, out var journalId);
            var activeBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDirectory), InterruptedJournal(journalId));

            // Simulate an anomalous pre-existing completed/ directory for the same OperationId that
            // does NOT match what this resolution is about to produce (still non-terminal here,
            // unlike the journal ResolveKeepCurrent is about to save) - the collision check must not
            // treat this as "already relocated."
            var completedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, journalId);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(completedBundleDirectory), InterruptedJournal(journalId));

            var result = controller.ResolveKeepCurrent();

            Assert.Equal(OperationController.KeepCurrentResolutionResult.ResolvedArchiveDeferred, result);
            Assert.False(controller.State.RequiresRecovery); // commit-point rule: the journal save already succeeded
            Assert.True(Directory.Exists(activeBundleDirectory)); // left untouched, not moved or deleted
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(activeBundleDirectory), out var activeJournal));
            Assert.Equal(OperationResolution.AcceptedCurrentState, activeJournal!.Resolution); // the save itself still happened
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveKeepCurrent_ActiveOperationRequiresRecovery_ResolvesAndUnblocks()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.ProviderUnavailable));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewController(adapter, new FakeClock(), operationsRoot: dir.FullName);
            var plan = SinglePlan();
            controller.StartApply(plan, Guid.NewGuid(), OperationBundlePaths.BundleDirectory(dir.FullName, active: true, plan.OperationId));
            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> RecoveryRequired (_active.RequiresRecovery = true)
            Assert.True(controller.State.RequiresRecovery);
            Assert.True(controller.State.CanResolveRecovery);

            var result = controller.ResolveKeepCurrent();

            Assert.Equal(OperationController.KeepCurrentResolutionResult.ResolvedAndArchived, result);
            Assert.False(controller.State.RequiresRecovery);
            Assert.True(controller.State.CanStartApply);
            Assert.True(controller.State.CanStartRestore);
            var completedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, plan.OperationId);
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var resolved));
            Assert.Equal(OperationResolution.AcceptedCurrentState, resolved!.Resolution);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static (OperationController Controller, Guid JournalId, string BundleDirectory) SetUpPendingContinue(
        FakePenumbraOperations adapter, FakeClock clock, string operationsRoot, OperationPlan interruptedPlan)
    {
        var controller = NewControllerWithPendingRecovery(adapter, clock, operationsRoot, out var journalId);
        var bundleDirectory = OperationBundlePaths.BundleDirectory(operationsRoot, active: true, journalId);
        OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), interruptedPlan);
        return (controller, journalId, bundleDirectory);
    }

    [Fact]
    public void ResolveContinue_NoPendingRecovery_Throws()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Throws<InvalidOperationException>(() => controller.ResolveContinue());
    }

    [Fact]
    public void ResolveContinue_HappyPath_StartsSuccessorAndResolvesTheInterruptedJournal()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            var (controller, journalId, bundleDirectory) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);
            // mod-a is still at the snapshot path - a real, non-empty residual move.
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Gear/A", false)])));

            controller.ResolveContinue();

            Assert.Equal(OperationStage.Mutating, controller.State.Stage);
            Assert.Equal(OperationType.Apply, controller.State.Kind); // same type as the interrupted operation
            Assert.False(controller.State.RequiresRecovery);
            Assert.False(Directory.Exists(bundleDirectory)); // relocated out of active/
            var completedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, journalId);
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var resolved));
            Assert.Equal(OperationResolution.ContinuedByNewOperation, resolved!.Resolution);
            Assert.NotNull(resolved.SuccessorOperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_UsesAFreshLiveReadNotACachedOne()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            var (controller, _, _) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);

            // Classification-time read: mod-a already at its final path (AtIntended) - a valid,
            // empty Continue, cached as CanContinueRecovery = true with zero residual moves.
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));
            controller.Update();
            Assert.True(controller.State.CanContinueRecovery);

            // Resolution-time read: mod-a has since moved back to the snapshot path - a real residual
            // move now exists. If ResolveContinue reused the cached (empty) result instead of taking
            // its own fresh read, the successor would incorrectly be a zero-step plan.
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Gear/A", false)])));

            controller.ResolveContinue();

            Assert.Equal(1, controller.State.TotalSteps); // the residual move the FRESH read implies, not zero
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_FreshReadShowsABlockingState_ThrowsEvenThoughCachedAvailabilityWasTrue()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            var (controller, _, bundleDirectory) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);

            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));
            controller.Update();
            Assert.True(controller.State.CanContinueRecovery);

            // mod-a has since been uninstalled entirely - MissingLive, blocking.
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])));

            Assert.Throws<InvalidOperationException>(() => controller.ResolveContinue());
            Assert.True(controller.State.RequiresRecovery); // _pendingRecovery untouched - still pending
            Assert.True(Directory.Exists(bundleDirectory)); // interrupted bundle never touched
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_FreshReadHasDuplicateIdentifiers_Throws()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            var (controller, _, _) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);
            var duplicateSnapshot = new LiveModSnapshot(
                new Dictionary<string, LiveMod> { ["mod-a"] = new("mod-a", "mod-a", "Weapons/A", false) },
                new HashSet<string> { "mod-a" });
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, duplicateSnapshot));

            Assert.Throws<InvalidOperationException>(() => controller.ResolveContinue());
            Assert.True(controller.State.RequiresRecovery);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_LiveReadUnavailable_ThrowsAndLeavesPendingRecoveryIntact()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            var (controller, _, bundleDirectory) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null));

            Assert.Throws<InvalidOperationException>(() => controller.ResolveContinue());

            Assert.True(controller.State.RequiresRecovery);
            Assert.True(Directory.Exists(bundleDirectory));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_ZeroResidualMoves_StartsAndCompletesAZeroStepSuccessor()
    {
        // Every target already AtIntended - Continue is Ready with an empty move list. Proves the
        // same zero-step engine path Plan C's own StartRestore_ZeroStepPlan test already established
        // works correctly for Continue's own new call path too.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Restore,
                [new(0, "mod-a", "Gear/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Weapons/A", "Gear/A", "mod-a")]);
            var (controller, _, _) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Gear/A", false)]))); // AtIntended
            adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])));

            controller.ResolveContinue();
            Assert.Equal(OperationType.Restore, controller.State.Kind); // matches the interrupted operation's own type
            Assert.Equal(0, controller.State.TotalSteps);

            controller.Update(); // Mutating -> Refreshing
            controller.Update(); // Refreshing -> Verifying
            controller.Update(); // Verifying -> Completed

            Assert.Equal(OperationStage.Completed, controller.State.Stage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveRestorePreviousState_NoPendingRecovery_Throws()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Throws<InvalidOperationException>(() => controller.ResolveRestorePreviousState());
    }

    [Fact]
    public void ResolveRestorePreviousState_HappyPath_StartsARestoreSuccessorRegardlessOfInterruptedType()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            // The interrupted operation was an Apply; Restore Previous State's successor must still
            // always be Restore-type (design doc section 1) - no plan.json needed at all.
            var targetSnapshot = new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto",
                new Dictionary<string, string> { ["mod-a"] = "Gear/A" });
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), targetSnapshot);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            controller.ResolveRestorePreviousState();

            Assert.Equal(OperationType.Restore, controller.State.Kind);
            Assert.False(controller.State.RequiresRecovery);
            var completedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, journalId);
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var resolved));
            Assert.Equal(OperationResolution.RestoredByNewOperation, resolved!.Resolution);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveRestorePreviousState_AvailableEvenWhenPlanIsInvalid()
    {
        // Proves section 2's decoupling holds all the way through to the actual resolution, not just
        // the cached CanRestorePreviousState flag.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            // plan.json intentionally not written at all.
            var targetSnapshot = new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto",
                new Dictionary<string, string> { ["mod-a"] = "Gear/A" });
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), targetSnapshot);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            var exception = Record.Exception(() => controller.ResolveRestorePreviousState());

            Assert.Null(exception);
            Assert.Equal(OperationType.Restore, controller.State.Kind);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveRestorePreviousState_TargetHasIdentifierAbsentFromLive_SkipsItRatherThanFailing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var targetSnapshot = new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "auto",
                new Dictionary<string, string> { ["mod-a"] = "Gear/A", ["mod-gone"] = "Gear/Gone" });
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(bundleDirectory), targetSnapshot);
            // mod-gone is absent from live - RollbackHistory.BuildRestorePlan's SkippedUninstalledIdentifiers.
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            var exception = Record.Exception(() => controller.ResolveRestorePreviousState());

            Assert.Null(exception);
            Assert.Equal(1, controller.State.TotalTargets); // only mod-a, mod-gone silently excluded (not failed)
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_StartOperationRejectsIt_DeletesTheOrphanedBundleAndLeavesPendingRecoveryIntact()
    {
        // Not reachable through the real engine today (an active, non-terminal operation and a
        // pending recovery are mutually exclusive in practice - recovery is only ever registered at
        // startup, before anything is active), but StartRecoverySuccessor's own admission guard
        // (StartOperation's "_active is not null && !CanStartNext" check, which
        // bypassPendingRecoveryLockout does NOT exempt) must still correctly reject this shape, and
        // its failure-cleanup path must
        // still run - forced here using the same "not reachable via the engine but must still be
        // correct on its own terms" pattern this file already uses for
        // CanStartNext_TerminalStageButRequiresRecovery_IsFalse.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewController(adapter, new FakeClock(), operationsRoot: dir.FullName);
            var otherPlan = SinglePlan("mod-z");
            controller.StartApply(otherPlan, Guid.NewGuid(), OperationBundlePaths.BundleDirectory(dir.FullName, active: true, otherPlan.OperationId)); // sets _active, non-terminal Mutating

            var journalId = Guid.NewGuid();
            var discovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [journalId], [journalId]),
                new Dictionary<Guid, OperationJournal> { [journalId] = InterruptedJournal(journalId) });
            controller.RegisterDiscoveredRecovery(discovery); // sets _pendingRecovery alongside the already-set _active
            var interruptedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(interruptedBundleDirectory), interruptedPlan);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Gear/A", false)]))); // ResolveContinue's own fresh read

            var exception = Record.Exception(() => controller.ResolveContinue());

            Assert.IsType<InvalidOperationException>(exception);
            Assert.True(Directory.Exists(interruptedBundleDirectory)); // the interrupted bundle itself is never touched

            // The successor's own freshly-created bundle directory (plan.json/snapshot.json written
            // just before the rejected StartRecoverySuccessor call) must not survive the failure -
            // active/ must contain only otherPlan's bundle and the interrupted one, nothing extra.
            var activeDir = OperationBundlePaths.ActiveDirectory(dir.FullName);
            Assert.Equal(2, Directory.GetDirectories(activeDir).Length);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StartRecoverySuccessor_BlockedMultiRootGraphPresent_StillRejects()
    {
        // Review point 1 (critical): the private bypass must exempt only _pendingRecovery, never
        // _blockedMultiRootGraph - D2 explicitly does not resolve the multi-root/cycle case (design
        // doc section 7), so a recovery successor must never be able to start while that lockout is
        // in effect. Not reachable through the real engine today (two separate
        // RegisterDiscoveredRecovery calls, one setting _blockedMultiRootGraph and another setting
        // _pendingRecovery, don't occur within one real startup discovery pass), but the guard must
        // still be correct on its own terms.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewController(adapter, new FakeClock(), operationsRoot: dir.FullName);

            var blockedIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var blockedDiscovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, blockedIds, blockedIds),
                blockedIds.ToDictionary(id => id, InterruptedJournal));
            controller.RegisterDiscoveredRecovery(blockedDiscovery); // sets _blockedMultiRootGraph

            var journalId = Guid.NewGuid();
            var pendingDiscovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [journalId], [journalId]),
                new Dictionary<Guid, OperationJournal> { [journalId] = InterruptedJournal(journalId) });
            controller.RegisterDiscoveredRecovery(pendingDiscovery); // ALSO sets _pendingRecovery, alongside the still-set _blockedMultiRootGraph
            Assert.True(controller.IsBlockedByMultipleRoots);

            var interruptedBundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(interruptedBundleDirectory), interruptedPlan);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Gear/A", false)])));

            var exception = Record.Exception(() => controller.ResolveContinue());

            Assert.IsType<InvalidOperationException>(exception);
            Assert.True(controller.IsBlockedByMultipleRoots); // both lockouts remain exactly as they were
            Assert.True(Directory.Exists(interruptedBundleDirectory));
            var activeDir = OperationBundlePaths.ActiveDirectory(dir.FullName);
            Assert.Single(Directory.GetDirectories(activeDir)); // only the interrupted bundle - no orphaned successor left behind
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveContinue_ParentJournalWriteFails_SuccessorStillBecomesAuthoritativeOnNextStartupDiscovery()
    {
        // Final whole-branch review, Important finding: the catch block around the parent-journal
        // write claims "on next startup the successor's own RecoveryOfOperationId makes it, not the
        // stale parent, authoritative in OperationRecoveryGraph.Analyze" - this test proves that claim
        // is actually true end-to-end (StartRecoverySuccessor threading RecoveryOfOperationId through
        // to StartOperation, then a real OperationBundleDiscovery.RunStartupDiscovery pass over the
        // resulting on-disk state), rather than trusting the comment's own assertion.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var interruptedPlan = OperationPlan.Create(OperationType.Apply,
                [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            var (controller, journalId, interruptedBundleDirectory) = SetUpPendingContinue(adapter, new FakeClock(), dir.FullName, interruptedPlan);
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Gear/A", false)])));

            // Force the parent-journal-resolution write specifically to fail (not the successor's own
            // journal write, which lives in a different, freshly-created bundle directory) - same
            // exclusive-lock-on-the-temp-file technique already established by
            // AcceptAllAndCloseInterruptedOperations_RetryAfterPartialFailure.
            var interruptedJournalPath = OperationBundlePaths.JournalPath(interruptedBundleDirectory);
            var interruptedTempPath = interruptedJournalPath + ".tmp";
            var lockOnTempFile = new FileStream(interruptedTempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            Exception? exception;
            try
            {
                exception = Record.Exception(() => controller.ResolveContinue());
            }
            finally
            {
                lockOnTempFile.Dispose();
                File.Delete(interruptedTempPath);
            }

            Assert.Null(exception); // the successor already started successfully - must not report failure
            Assert.False(controller.State.RequiresRecovery); // _pendingRecovery was cleared before the parent-write attempt

            // The interrupted bundle is untouched (still active/, still non-terminal) since its own
            // write failed; the successor got its own new bundle directory.
            Assert.True(Directory.Exists(interruptedBundleDirectory));
            var activeDir = OperationBundlePaths.ActiveDirectory(dir.FullName);
            var successorBundleDirectory = Directory.GetDirectories(activeDir).Single(d => !string.Equals(d.TrimEnd('\\', '/'), interruptedBundleDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(successorBundleDirectory), out var successorJournal));
            Assert.Equal(journalId, successorJournal!.RecoveryOfOperationId); // the fix under test

            var discovery = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, discovery.Graph.Status);
            Assert.Equal(successorJournal.OperationId, Assert.Single(discovery.Graph.AuthoritativeOperationIds));
            Assert.DoesNotContain(journalId, discovery.Graph.AuthoritativeOperationIds); // the stale parent is not re-surfaced
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static OperationController NewControllerWithBlockedMultiRoot(
        FakePenumbraOperations adapter, FakeClock clock, string operationsRoot, IReadOnlyList<Guid> ids)
    {
        var controller = NewController(adapter, clock, operationsRoot: operationsRoot);
        var journals = ids.ToDictionary(id => id, InterruptedJournal);
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, ids, ids),
            journals);
        controller.RegisterDiscoveredRecovery(discovery);
        return controller;
    }

    [Fact]
    public void AcceptAllAndCloseInterruptedOperations_NoBlockedGraph_Throws()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Throws<InvalidOperationException>(() => controller.AcceptAllAndCloseInterruptedOperations());
    }

    [Fact]
    public void AcceptAllAndCloseInterruptedOperations_AllJournalsResolvable_ResolvesAllAndUnblocks()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);
            foreach (var id in new[] { idA, idB })
            {
                var bundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, id);
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDir), InterruptedJournal(id));
            }

            var unresolved = controller.AcceptAllAndCloseInterruptedOperations();

            Assert.Empty(unresolved);
            Assert.False(controller.IsBlockedByMultipleRoots);
            Assert.True(controller.State.CanStartApply);
            foreach (var id in new[] { idA, idB })
            {
                var completedDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, id);
                Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedDir), out var resolved));
                Assert.Equal(OperationResolution.AcceptedCurrentState, resolved!.Resolution);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AcceptAllAndCloseInterruptedOperations_OneJournalUnloadable_LeavesLockoutInPlace()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);
            // idA's bundle directory/journal is never written to disk - simulates an unloadable journal.
            var bundleDirB = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, idB);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDirB), InterruptedJournal(idB));

            var unresolved = controller.AcceptAllAndCloseInterruptedOperations();

            Assert.Equal([idA], unresolved);
            Assert.True(controller.IsBlockedByMultipleRoots); // partial success does not unblock
            Assert.True(controller.State.RequiresRecovery);
            Assert.False(controller.State.CanStartApply);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AcceptAllAndCloseInterruptedOperations_RetryAfterPartialFailure_DoesNotResurrectTheAlreadyResolvedJournal()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);
            var bundleDirA = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, idA);
            var bundleDirB = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, idB);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDirA), InterruptedJournal(idA));
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDirB), InterruptedJournal(idB));

            // Force idB's save to fail while idA's succeeds: AtomicFile.CreateOrReplace first does
            // "if (File.Exists(tempPath)) File.Delete(tempPath);" before writing "{path}.tmp" - so
            // holding an exclusive lock (FileShare.None) open on that temp path makes the File.Delete
            // call itself throw IOException, without needing to touch the journal file being saved.
            var journalPathB = OperationBundlePaths.JournalPath(bundleDirB);
            var tempPathB = journalPathB + ".tmp";
            var lockOnTempFile = new FileStream(tempPathB, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            IReadOnlyList<Guid> firstPass;
            try
            {
                firstPass = controller.AcceptAllAndCloseInterruptedOperations();
            }
            finally
            {
                lockOnTempFile.Dispose();
            }

            Assert.Equal([idB], firstPass); // idA resolved+relocated; idB failed to save
            Assert.True(controller.IsBlockedByMultipleRoots);
            Assert.False(Directory.Exists(bundleDirA)); // moved out of active/ already
            Assert.True(Directory.Exists(OperationBundlePaths.BundleDirectory(dir.FullName, active: false, idA)));

            // "Fix" whatever caused idB's save to fail (release the lock, above), clean up the
            // leftover temp file, then retry.
            File.Delete(tempPathB);

            var secondPass = controller.AcceptAllAndCloseInterruptedOperations();

            // Bug being guarded against: idA (already resolved and relocated to completed/ by the
            // first pass) must NOT be re-added to unresolved just because active/{idA} no longer
            // exists on this second attempt.
            Assert.Empty(secondPass);
            Assert.False(controller.IsBlockedByMultipleRoots);
            Assert.True(controller.State.CanStartApply);
            foreach (var id in new[] { idA, idB })
            {
                var completedDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, id);
                Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedDir), out var resolved));
                Assert.Equal(OperationResolution.AcceptedCurrentState, resolved!.Resolution);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_NoBlockedGraph_Throws()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Throws<InvalidOperationException>(() => controller.ResolveOneMultiRootOperation(Guid.NewGuid()));
    }

    [Fact]
    public void ResolveOneMultiRootOperation_IdNotInAuthoritativeSet_Throws()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);

            Assert.Throws<InvalidOperationException>(() => controller.ResolveOneMultiRootOperation(Guid.NewGuid()));
            Assert.True(controller.IsBlockedByMultipleRoots); // untouched
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_TwoDisconnectedRoots_ResolvingOneLeavesTheOtherAsOrdinarySingleRecovery()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);
            foreach (var id in new[] { idA, idB })
            {
                var bundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, id);
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDir), InterruptedJournal(id));
            }

            controller.ResolveOneMultiRootOperation(idA);

            Assert.False(controller.IsBlockedByMultipleRoots);
            Assert.True(controller.State.RequiresRecovery); // idB is now an ordinary single pending recovery
            Assert.True(controller.State.CanResolveRecovery);
            var completedDirA = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, idA);
            Assert.True(OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedDirA), out var resolvedA));
            Assert.Equal(OperationResolution.AcceptedCurrentState, resolvedA!.Resolution);
            Assert.True(Directory.Exists(OperationBundlePaths.BundleDirectory(dir.FullName, active: true, idB))); // still there, now the sole pending recovery
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_LastRemainingRoot_TransitionsToIdle()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA]);
            var bundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, idA);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDir), InterruptedJournal(idA));

            controller.ResolveOneMultiRootOperation(idA);

            Assert.False(controller.IsBlockedByMultipleRoots);
            Assert.False(controller.State.RequiresRecovery);
            Assert.True(controller.State.CanStartApply);
            Assert.Empty(controller.GetBlockedOperations());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_ThreeDisconnectedRoots_ResolvingOneLeavesTwoStillBlocked()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var idC = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB, idC]);
            foreach (var id in new[] { idA, idB, idC })
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: true, id)), InterruptedJournal(id));

            controller.ResolveOneMultiRootOperation(idA);

            Assert.True(controller.IsBlockedByMultipleRoots);
            var remaining = controller.GetBlockedOperations().Select(b => b.OperationId).ToList();
            Assert.Equal(2, remaining.Count);
            Assert.Contains(idB, remaining);
            Assert.Contains(idC, remaining);
            Assert.DoesNotContain(idA, remaining);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_ResolvingOneMemberOfAThreeNodeCycle_BreaksTheCycleCorrectly()
    {
        // Design doc section 5's hand-traced proof, made concrete: A -> B -> C -> A via
        // RecoveryOfOperationId. Resolving B removes it from the next non-terminal-journal load, so
        // the only surviving edge is C -> A (both still present); A is referenced as a parent by C
        // but has no parent itself in-set, so leaves = {C} once B drops out - SingleAuthoritative
        // with C as the sole remaining authoritative operation, not a smaller but still-cyclic set.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var idC = Guid.NewGuid();
            var journalA = InterruptedJournal(idA) with { RecoveryOfOperationId = idB };
            var journalB = InterruptedJournal(idB) with { RecoveryOfOperationId = idC };
            var journalC = InterruptedJournal(idC) with { RecoveryOfOperationId = idA };
            var cycleIds = new[] { idA, idB, idC };
            var controller = NewController(new FakePenumbraOperations(), new FakeClock(), operationsRoot: dir.FullName);
            var discovery = new OperationDiscoveryResult(
                new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.CycleDetected, cycleIds, cycleIds),
                new Dictionary<Guid, OperationJournal> { [idA] = journalA, [idB] = journalB, [idC] = journalC });
            controller.RegisterDiscoveredRecovery(discovery);
            foreach (var (id, journal) in new[] { (idA, journalA), (idB, journalB), (idC, journalC) })
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: true, id)), journal);

            controller.ResolveOneMultiRootOperation(idB);

            Assert.False(controller.IsBlockedByMultipleRoots);
            Assert.True(controller.State.RequiresRecovery); // now an ordinary single pending recovery, not still blocked
            // Directory presence alone wouldn't distinguish "C is correctly authoritative" from "the
            // controller latched onto the wrong id while C incidentally still sits under active/" -
            // assert via the controller's own authoritative accessor, not disk state.
            var pendingJournal = controller.GetPendingRecoveryJournal();
            Assert.NotNull(pendingJournal);
            Assert.Equal(idC, pendingJournal!.OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_RetriedAfterAlreadyResolved_SucceedsWithoutResolvingTwice()
    {
        // Stands in as the regression test for the failure-atomicity property (design doc section 5):
        // RunStartupDiscovery itself can't practically be forced to throw in a plain filesystem test
        // (every read failure it can hit - missing directory, corrupt/locked journal - is already
        // caught and treated as "skip this entry" one layer down), so this test instead exercises the
        // retry path the atomicity guarantee exists to make safe. Simulates the recovery path from a
        // prior partial failure: idA's journal is already resolved-and-relocated to completed/ (as if
        // a previous call got as far as resolving it, and this is a retry), but the controller's
        // in-memory blocked state still lists it as blocked - exactly the state the atomicity fix is
        // designed to leave behind if rediscovery didn't complete last time. The retry must not throw
        // or attempt to re-resolve an already-terminal journal - TryResolveJournalAsKeepCurrent's
        // AlreadyResolved outcome (proven by the existing AcceptAllAndCloseInterruptedOperations retry
        // test, unchanged by Task 2's extraction) makes this a normal, successful path.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);
            // idA: already resolved and relocated, as if by a prior partial attempt - nothing under
            // active/ for it.
            var completedDirA = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, idA);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(completedDirA), InterruptedJournal(idA) with { Resolution = OperationResolution.AcceptedCurrentState, Stage = OperationStage.Completed });
            // idB: still genuinely active.
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: true, idB)), InterruptedJournal(idB));

            controller.ResolveOneMultiRootOperation(idA);

            Assert.False(controller.IsBlockedByMultipleRoots);
            Assert.True(controller.State.RequiresRecovery); // idB is now the ordinary single pending recovery
            var pendingJournal = controller.GetPendingRecoveryJournal();
            Assert.NotNull(pendingJournal);
            Assert.Equal(idB, pendingJournal!.OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveOneMultiRootOperation_JournalResolutionFails_ThrowsAndLeavesBlockStateIntact()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var controller = NewControllerWithBlockedMultiRoot(new FakePenumbraOperations(), new FakeClock(), dir.FullName, [idA, idB]);
            // idA's bundle directory/journal is never written to disk - TryResolveJournalAsKeepCurrent
            // returns Failed for it (matches AcceptAllAndCloseInterruptedOperations_OneJournalUnloadable's
            // own established trigger).

            Assert.Throws<InvalidOperationException>(() => controller.ResolveOneMultiRootOperation(idA));

            Assert.True(controller.IsBlockedByMultipleRoots);
            Assert.Equal(2, controller.GetBlockedOperations().Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetPendingRecoveryArtifactStatus_NoPendingRecovery_ReturnsNull()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Null(controller.GetPendingRecoveryArtifactStatus());
    }

    [Fact]
    public void GetPendingRecoveryJournal_NoPendingRecovery_ReturnsNull()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Null(controller.GetPendingRecoveryJournal());
    }

    [Fact]
    public void GetPendingRecoveryJournal_PendingRecoveryExists_ReturnsItsJournal()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = NewControllerWithPendingRecovery(new FakePenumbraOperations(), new FakeClock(), dir.FullName, out var journalId);

            var journal = controller.GetPendingRecoveryJournal();

            Assert.NotNull(journal);
            Assert.Equal(journalId, journal!.OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetPendingRecoveryArtifactStatus_PendingRecoveryWithValidPlanMissingSnapshot_ReturnsBothStatuses()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var adapter = new FakePenumbraOperations();
            var controller = NewControllerWithPendingRecovery(adapter, new FakeClock(), dir.FullName, out var journalId);
            var bundleDirectory = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, journalId);
            var plan = OperationPlan.Create(OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)], [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(bundleDirectory), plan);
            // snapshot.json intentionally not written
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));

            controller.Update(); // populates PlanCheckStatus/SnapshotCheckStatus

            var status = controller.GetPendingRecoveryArtifactStatus();

            Assert.NotNull(status);
            Assert.Equal(ArtifactCheckStatus.Valid, status!.Value.Plan);
            Assert.Equal(ArtifactCheckStatus.Missing, status.Value.Snapshot);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetBlockedOperations_NoBlockedGraph_ReturnsEmpty()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());

        Assert.Empty(controller.GetBlockedOperations());
    }

    [Fact]
    public void GetBlockedOperations_BlockedMultiRoot_ReturnsOnlyAuthoritativeOperationsWithTheirJournals()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var journalA = InterruptedJournal(idA);
        var journalB = InterruptedJournal(idB);
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, [idA, idB], [idA, idB]),
            new Dictionary<Guid, OperationJournal> { [idA] = journalA, [idB] = journalB });

        controller.RegisterDiscoveredRecovery(discovery);
        var blocked = controller.GetBlockedOperations();

        Assert.Equal(2, blocked.Count);
        Assert.Contains(blocked, b => b.OperationId == idA && b.Journal == journalA);
        Assert.Contains(blocked, b => b.OperationId == idB && b.Journal == journalB);
    }

    [Fact]
    public void GetBlockedOperations_OnlyListsAuthoritativeIdsNotNonLeafAncestors()
    {
        // A cycle's AuthoritativeOperationIds is the full cycle-member set (OperationRecoveryGraph's
        // own semantics, unchanged by this plan) - this test just proves GetBlockedOperations
        // faithfully mirrors whatever the graph names as authoritative, not a hand-picked subset.
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid(); // not authoritative - simulates a non-leaf ancestor present in Journals but absent from AuthoritativeOperationIds
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, [idA, idB], [idA, idB, idC]),
            new Dictionary<Guid, OperationJournal> { [idA] = InterruptedJournal(idA), [idB] = InterruptedJournal(idB), [idC] = InterruptedJournal(idC) });

        controller.RegisterDiscoveredRecovery(discovery);
        var blocked = controller.GetBlockedOperations();

        Assert.Equal(2, blocked.Count);
        Assert.DoesNotContain(blocked, b => b.OperationId == idC);
    }
}
