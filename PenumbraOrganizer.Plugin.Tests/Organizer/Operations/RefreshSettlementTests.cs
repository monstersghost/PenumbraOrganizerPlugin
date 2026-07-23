using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class RefreshSettlementTests
{
    private sealed class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    [Fact]
    public void Advance_Success_ReturnsSettled()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));

        var result = new RefreshSettlement().Advance(adapter, new FakeClock(), new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.Settled, result.Status);
    }

    [Fact]
    public void Advance_ProviderUnavailable_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.ProviderUnavailable));

        var result = new RefreshSettlement().Advance(adapter, new FakeClock(), new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.RecoveryRequired, result.Status);
    }

    [Fact]
    public void Advance_InvalidState_RecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.InvalidState));

        var result = new RefreshSettlement().Advance(adapter, new FakeClock(), new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.RecoveryRequired, result.Status);
    }

    [Fact]
    public void Advance_TemporarilyUnavailableUntilBoundExhausted_ThenRecoveryRequired()
    {
        var adapter = new FakePenumbraOperations();
        for (var i = 0; i < 10; i++)
            adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.TemporarilyUnavailable));
        var clock = new FakeClock();

        var settlement = new RefreshSettlement();
        RefreshSettlementResult result = new(RefreshSettlementStatus.Waiting);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            result = settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(RefreshSettlementStatus.RecoveryRequired, result.Status);
    }

    [Fact]
    public void Advance_TemporarilyUnavailableThenSuccess_EventuallySettles()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.TemporarilyUnavailable));
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        var clock = new FakeClock();

        var settlement = new RefreshSettlement();
        var first = settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var second = settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.Waiting, first.Status);
        Assert.Equal(RefreshSettlementStatus.Settled, second.Status);
    }

    [Fact]
    public void Advance_SecondCallWithinRetryInterval_DoesNotCallAdapterAgain()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        var clock = new FakeClock();

        var settlement = new RefreshSettlement();
        settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());
        var second = settlement.Advance(adapter, clock, new NoOpDiagnosticsSink(), Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.Waiting, second.Status);
        // No exception from an empty queue proves RequestPostMutationRefresh was not called again.
    }

    [Fact]
    public void Advance_FastRefresh_DoesNotRecordADiagnosticEvent()
    {
        var adapter = new FakePenumbraOperations();
        adapter.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));
        var diagnostics = new RecordingDiagnosticsSink();

        var result = new RefreshSettlement().Advance(adapter, new FakeClock(), diagnostics, Guid.NewGuid());

        Assert.Equal(RefreshSettlementStatus.Settled, result.Status);
        Assert.Empty(diagnostics.SlowRefreshes);
    }

    [Fact]
    public void Advance_SlowRefresh_RecordsADiagnosticEvent()
    {
        var adapter = new FakePenumbraOperations();
        var clock = new FakeClock();
        adapter.EnqueueRefreshResult(
            new RefreshResult(RefreshStatus.Success),
            onCall: () => clock.Advance(TimeSpan.FromMilliseconds(80))); // over the 50ms SlowCallThreshold
        var diagnostics = new RecordingDiagnosticsSink();
        var operationId = Guid.NewGuid();

        var result = new RefreshSettlement().Advance(adapter, clock, diagnostics, operationId);

        Assert.Equal(RefreshSettlementStatus.Settled, result.Status);
        var single = Assert.Single(diagnostics.SlowRefreshes);
        Assert.Equal(operationId, single.OperationId);
        Assert.True(single.Duration >= TimeSpan.FromMilliseconds(80));
    }
}
