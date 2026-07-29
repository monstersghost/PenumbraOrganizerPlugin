using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

/// <summary>
/// Pins the admission and completion behaviour that exists BEFORE the state-authority refactor.
/// Every one of these must still pass, unchanged, after it. A test here that needs editing during
/// the refactor is a behaviour change and must be raised rather than edited.
/// </summary>
public class OperationAdmissionCharacterizationTests
{
    // Mirrors the helpers from OperationControllerTests.cs exactly.

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

    // Directory.CreateTempSubdirectory() is the operationsRoot pattern every test in
    // OperationControllerTests.cs uses when it needs a real directory for StartApply/StartRestore.
    private static string NewBundleDirectory() => Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public void SecondOperation_CannotStartWhileOneIsActive()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        controller.StartApply(SinglePlan(), Guid.NewGuid(), NewBundleDirectory());

        Assert.False(controller.State.CanStartApply);
        Assert.False(controller.State.CanStartRestore);
    }

    [Fact]
    public void NormalOperation_CannotStartWhileRecoveryIsRequired()
    {
        var controller = NewController(new FakePenumbraOperations(), new FakeClock());
        var id = Guid.NewGuid();
        var discovery = new OperationDiscoveryResult(
            new OperationRecoveryGraphResult(OperationRecoveryGraphStatus.SingleAuthoritative, [id], [id]),
            new Dictionary<Guid, OperationJournal> { [id] = InterruptedJournal(id) });
        controller.RegisterDiscoveredRecovery(discovery);

        Assert.True(controller.State.RequiresRecovery);
        Assert.False(controller.State.CanStartApply);
        Assert.False(controller.State.CanScan);
    }

    [Fact]
    public void CanStartNext_RequiresBothTerminalAndNoRecovery()
    {
        // OperationStage's terminal success member is Completed - there is no "Succeeded" stage
        // (the enum: Preparing/Prepared/Mutating/Refreshing/Verifying/Completed/
        // CompletedWithItemFailures/FailedBeforeMutation/FailedPartiallyApplied/Cancelled).
        var terminal = InterruptedJournal(Guid.NewGuid()) with { Stage = OperationStage.Completed };

        Assert.True(OperationController.CanStartNext(terminal, requiresRecovery: false));
        Assert.False(OperationController.CanStartNext(terminal, requiresRecovery: true));
    }

    // Mirrors OperationControllerTests.Update_DrivesMutationThroughRefreshingToVerifyingAndSettles -
    // the established sequence for driving a single Apply operation to a terminal Completed state.
    private static OperationController RunSingleOperationToTerminal()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueSetModPathResult(Success);
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        adapter.EnqueueLiveModRead(new LiveModReadResult(
            LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([new LiveMod("mod-a", "mod-a", "Weapons/A", false)])));
        var controller = NewController(adapter, new FakeClock());
        controller.StartApply(SinglePlan(), Guid.NewGuid(), NewBundleDirectory());

        controller.Update(); // Mutating -> Refreshing
        controller.Update(); // Refreshing -> Verifying
        controller.Update(); // Verifying -> Completed

        return controller;
    }

    [Fact]
    public void TerminalState_IsRetainedAndCanStartBecomesTrueAgain()
    {
        // _active is never cleared when an operation concludes - a terminal Stage stays visible in
        // State while CanStartApply simultaneously becomes true again. Pin both halves.
        var controller = RunSingleOperationToTerminal();

        Assert.NotNull(controller.State.Stage);
        Assert.True(controller.State.CanStartApply);
    }

    [Fact]
    public void ReRunningUpdateOnATerminalOperation_DoesNotChangeTheSnapshot()
    {
        var controller = RunSingleOperationToTerminal();
        var before = controller.State;

        controller.Update();
        controller.Update();

        Assert.Equal(before, controller.State); // OperationStateSnapshot is a record; value equality
    }
}
