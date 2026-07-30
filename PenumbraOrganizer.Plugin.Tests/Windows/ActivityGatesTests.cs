using PenumbraOrganizer.Plugin.LibraryWork;
using PenumbraOrganizer.Plugin.Organizer.Operations;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class ActivityGatesTests
{
    private static LibraryWorkStateSnapshot Running => new(
        LibraryWorkPhase.Computing, "Scan", 1, 10, null, null, CanCancel: true);

    private static LibraryWorkStateSnapshot Finished(LibraryWorkOutcome outcome) => new(
        LibraryWorkPhase.Idle, null, 10, 10, outcome, null, CanCancel: false);

    private static LibraryWorkStateSnapshot Idle => LibraryWorkStateSnapshot.Idle;

    [Fact]
    public void EverythingIdle_AllowsEverything()
    {
        var gates = ActivityGates.Build(OperationStateSnapshot.Idle, Idle, Idle);

        Assert.True(gates.CanScan);
        Assert.True(gates.CanIndex);
        Assert.True(gates.CanStartApply);
        Assert.True(gates.CanStartRestore);
        Assert.True(gates.CanRunFolderCleanup);
        Assert.True(gates.CanRunFolderCleanupRollback);
        Assert.True(gates.CanCreateBackup);
        Assert.True(gates.CanStageProposals);
    }

    [Fact]
    public void ScanRunning_BlocksEverythingIncludingStaging()
    {
        var gates = ActivityGates.Build(OperationStateSnapshot.Idle, Running, Idle);

        Assert.False(gates.CanScan);
        Assert.False(gates.CanIndex);
        Assert.False(gates.CanStartApply);
        Assert.False(gates.CanStartRestore);
        Assert.False(gates.CanRunFolderCleanup);
        Assert.False(gates.CanRunFolderCleanupRollback);
        Assert.False(gates.CanCreateBackup);
        Assert.False(gates.CanStageProposals);
    }

    [Fact]
    public void IndexRunning_BlocksScan()
    {
        var gates = ActivityGates.Build(OperationStateSnapshot.Idle, Idle, Running);

        Assert.False(gates.CanScan);
        Assert.False(gates.CanIndex);
    }

    [Fact]
    public void OperationLockout_BlocksLibraryWork()
    {
        var operation = OperationStateSnapshot.Idle with
        {
            CanScan = false, CanIndex = false, CanStartApply = false, CanStartRestore = false,
            CanRunFolderCleanup = false, CanRunFolderCleanupRollback = false, CanCreateBackup = false,
        };

        var gates = ActivityGates.Build(operation, Idle, Idle);

        Assert.False(gates.CanScan);
        Assert.False(gates.CanIndex);
        Assert.False(gates.CanStartApply);
    }

    [Fact]
    public void RecoveryRequired_BlocksLibraryWork()
    {
        var operation = OperationStateSnapshot.Idle with
        {
            RequiresRecovery = true, CanScan = false, CanIndex = false,
        };

        var gates = ActivityGates.Build(operation, Idle, Idle);

        Assert.False(gates.CanScan);
        Assert.False(gates.CanIndex);
    }

    [Theory]
    [InlineData(LibraryWorkOutcome.Completed)]
    [InlineData(LibraryWorkOutcome.Cancelled)]
    [InlineData(LibraryWorkOutcome.StaleModList)]
    [InlineData(LibraryWorkOutcome.Failed)]
    public void AnyTerminalOutcome_ReleasesEveryGate(LibraryWorkOutcome outcome)
    {
        var gates = ActivityGates.Build(OperationStateSnapshot.Idle, Finished(outcome), Idle);

        Assert.True(gates.CanScan);
        Assert.True(gates.CanStartApply);
        Assert.True(gates.CanStageProposals);
    }

    [Fact]
    public void StagingIsBlockedOnlyByLibraryWork_NotByOperationLockout()
    {
        // Staging edits ProposedPath, which only a completing LoadScan clobbers. An Apply in flight
        // is already prevented from starting a second Apply by CanStartApply; it has no reason to
        // stop the user preparing the next batch.
        var operation = OperationStateSnapshot.Idle with { CanStartApply = false };

        var gates = ActivityGates.Build(operation, Idle, Idle);

        Assert.True(gates.CanStageProposals);
        Assert.False(gates.CanStartApply);
    }
}
