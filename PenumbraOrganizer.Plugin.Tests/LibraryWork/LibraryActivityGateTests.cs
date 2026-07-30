using PenumbraOrganizer.Plugin.LibraryWork;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

public class LibraryActivityGateTests
{
    private static LibraryWorkStateSnapshot Running(string name) => new(
        LibraryWorkPhase.Computing, name, 1, 10, null, null, CanCancel: true);

    private static LibraryWorkStateSnapshot Idle => LibraryWorkStateSnapshot.Idle;

    [Fact]
    public void NothingRunning_ReturnsNull()
    {
        Assert.Null(LibraryActivityGate.Reason(Idle, Idle));
    }

    [Fact]
    public void ScanRunning_ReturnsAReasonNamingTheJob()
    {
        var reason = LibraryActivityGate.Reason(Running("Scan"), Idle);

        Assert.NotNull(reason);
        Assert.Contains("Scan", reason);
    }

    [Fact]
    public void IndexRunning_ReturnsAReasonNamingTheJob()
    {
        var reason = LibraryActivityGate.Reason(Idle, Running("Search index"));

        Assert.NotNull(reason);
        Assert.Contains("Search index", reason);
    }

    [Fact]
    public void TerminalOutcomes_DoNotBlock()
    {
        var finished = new LibraryWorkStateSnapshot(
            LibraryWorkPhase.Idle, null, 10, 10, LibraryWorkOutcome.Failed, "boom", CanCancel: false);

        Assert.Null(LibraryActivityGate.Reason(finished, Idle));
    }
}
