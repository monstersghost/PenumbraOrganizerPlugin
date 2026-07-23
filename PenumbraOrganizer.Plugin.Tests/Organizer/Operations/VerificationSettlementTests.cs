using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class VerificationSettlementTests
{
    private static OperationRecoveryTarget Target(string id, string finalPath) => new(id, "Gear/" + id, finalPath, id);

    private static LiveModSnapshot Snapshot(params (string Id, string Path)[] mods) =>
        LiveModSnapshotBuilder.Build(mods.Select(m => new LiveMod(m.Id, m.Id, m.Path, HeliosphereManaged: false)));

    private sealed class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    private static readonly Dictionary<string, TargetMutationStatus> NoTargets = new();

    [Fact]
    public void Advance_AllTargetsSettled_ReturnsSettled()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot(("mod-a", "Weapons/A"))));
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") };
        var statuses = new Dictionary<string, TargetMutationStatus> { ["mod-a"] = TargetMutationStatus.FinalStepSucceeded };

        var result = new VerificationSettlement().Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.Settled, result.Status);
    }

    [Fact]
    public void Advance_TargetNeverSettles_TimesOutAfterMaxAttempts()
    {
        var adapter = new FakePenumbraOperations();
        for (var i = 0; i < 10; i++)
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot(("mod-a", "Gear/A"))));
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") }; // live never matches this
        var statuses = new Dictionary<string, TargetMutationStatus> { ["mod-a"] = TargetMutationStatus.FinalStepSucceeded };

        var settlement = new VerificationSettlement();
        VerificationResult result = new(VerificationStatus.Waiting, [], null);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            result = settlement.Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(VerificationStatus.TimedOut, result.Status);
        Assert.Equal(["mod-a"], result.UnsettledIdentifiers);
    }

    [Fact]
    public void Advance_ItemFailedDuringMutation_IsNotWaitedOn()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot()));
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") };
        var statuses = new Dictionary<string, TargetMutationStatus> { ["mod-a"] = TargetMutationStatus.FinalStepFailed };

        var result = new VerificationSettlement().Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.Settled, result.Status);
    }

    [Fact]
    public void Advance_DuplicateLiveIdentifiers_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        var duplicateSnapshot = LiveModSnapshotBuilder.Build(
        [
            new LiveMod("mod-a", "mod-a", "Gear/First", HeliosphereManaged: false),
            new LiveMod("mod-a", "mod-a", "Gear/Second", HeliosphereManaged: false),
        ]);
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, duplicateSnapshot));
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") };
        var statuses = new Dictionary<string, TargetMutationStatus> { ["mod-a"] = TargetMutationStatus.FinalStepSucceeded };

        var result = new VerificationSettlement().Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.DuplicateIdentifiers, result.Reason);
    }

    [Fact]
    public void Advance_ProviderUnavailable_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null));
        var clock = new FakeClock();

        var result = new VerificationSettlement().Advance(adapter, clock, [], NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.ProviderUnavailable, result.Reason);
    }

    [Fact]
    public void Advance_InvalidData_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.InvalidData, null));
        var clock = new FakeClock();

        var result = new VerificationSettlement().Advance(adapter, clock, [], NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.InvalidData, result.Reason);
    }

    [Fact]
    public void Advance_TemporarilyUnavailableForAllAttempts_RecoveryRequiredWithTransientReadExhausted()
    {
        var adapter = new FakePenumbraOperations();
        for (var i = 0; i < 10; i++)
            adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.TemporarilyUnavailable, null));
        var clock = new FakeClock();

        var settlement = new VerificationSettlement();
        VerificationResult result = new(VerificationStatus.Waiting, [], null);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            result = settlement.Advance(adapter, clock, [], NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid());
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.TransientReadExhausted, result.Reason);
    }

    [Fact]
    public void Advance_SuccessStatusWithNullSnapshot_RecoveryRequiredRatherThanThrowing()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, null)); // malformed adapter result
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") };
        var statuses = new Dictionary<string, TargetMutationStatus> { ["mod-a"] = TargetMutationStatus.FinalStepSucceeded };

        var result = new VerificationSettlement().Advance(adapter, clock, targets, statuses, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.InvalidData, result.Reason);
    }

    [Fact]
    public void Advance_TargetMissingFromMutationStatuses_RecoveryRequiredRatherThanSilentlySettled()
    {
        var adapter = new FakePenumbraOperations(); // no read queued - must never be reached
        var clock = new FakeClock();
        var targets = new[] { Target("mod-a", "Weapons/A") };

        var result = new VerificationSettlement().Advance(adapter, clock, targets, NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(VerificationStatus.RecoveryRequired, result.Status);
        Assert.Equal(RecoveryRequiredReason.InvalidData, result.Reason);
    }

    [Fact]
    public void Advance_SecondCallWithinRetryInterval_ReturnsWaitingWithoutReadingAgain()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot()));
        var clock = new FakeClock();

        var settlement = new VerificationSettlement();
        settlement.Advance(adapter, clock, [], NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid()); // consumes the one queued read
        var second = settlement.Advance(adapter, clock, [], NoTargets, new NoOpDiagnosticsSink(), Guid.NewGuid()); // no time advanced

        Assert.Equal(VerificationStatus.Waiting, second.Status);
        // No exception from an empty queue proves GetLiveMods was not called a second time.
    }

    [Fact]
    public void Advance_FastLiveRead_DoesNotRecordADiagnosticEvent()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, Snapshot()));
        var clock = new FakeClock();
        var diagnostics = new RecordingDiagnosticsSink();

        var result = new VerificationSettlement().Advance(adapter, clock, [], NoTargets, diagnostics, Guid.NewGuid());

        Assert.Equal(VerificationStatus.Settled, result.Status);
        Assert.Empty(diagnostics.SlowLiveSnapshots);
    }

    [Fact]
    public void Advance_SlowLiveRead_RecordsADiagnosticEvent()
    {
        var adapter = new FakePenumbraOperations();
        var clock = new FakeClock();
        adapter.EnqueueLiveModRead(
            new LiveModReadResult(LiveModReadStatus.Success, Snapshot()),
            onCall: () => clock.Advance(TimeSpan.FromMilliseconds(80))); // over the 50ms SlowCallThreshold
        var diagnostics = new RecordingDiagnosticsSink();
        var operationId = Guid.NewGuid();

        var result = new VerificationSettlement().Advance(adapter, clock, [], NoTargets, diagnostics, operationId);

        Assert.Equal(VerificationStatus.Settled, result.Status);
        var single = Assert.Single(diagnostics.SlowLiveSnapshots);
        Assert.Equal(operationId, single.OperationId);
        Assert.True(single.Duration >= TimeSpan.FromMilliseconds(80));
    }
}
