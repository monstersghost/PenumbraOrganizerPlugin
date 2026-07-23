namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum RefreshSettlementStatus { Waiting, Settled, RecoveryRequired }

public sealed record RefreshSettlementResult(RefreshSettlementStatus Status);

/// <summary>
/// Design doc section 5b. Mirrors VerificationSettlement's bounded-retry shape exactly (same
/// attempt count and interval) - no separate TimedOut state, since a refresh either resolves
/// within the bound or becomes RecoveryRequired; there is no per-identifier partial-success case
/// the way verification has.
/// </summary>
public sealed class RefreshSettlement
{
    private int _attemptsUsed;
    private long _lastAttemptTimestamp;
    private const int MaxAttempts = 10;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SlowCallThreshold = TimeSpan.FromMilliseconds(50);

    public RefreshSettlementResult Advance(
        IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, Guid operationId)
    {
        if (_attemptsUsed > 0 && clock.GetElapsedTime(_lastAttemptTimestamp) < RetryInterval)
            return new RefreshSettlementResult(RefreshSettlementStatus.Waiting);

        _lastAttemptTimestamp = clock.GetTimestamp();
        _attemptsUsed++;

        var callStart = clock.GetTimestamp();
        var refresh = adapter.RequestPostMutationRefresh();
        var duration = clock.GetElapsedTime(callStart);
        if (duration >= SlowCallThreshold) diagnostics.RecordSlowRefresh(operationId, duration);

        return refresh.Status switch
        {
            RefreshStatus.Success => new RefreshSettlementResult(RefreshSettlementStatus.Settled),
            RefreshStatus.TemporarilyUnavailable => _attemptsUsed >= MaxAttempts
                ? new RefreshSettlementResult(RefreshSettlementStatus.RecoveryRequired)
                : new RefreshSettlementResult(RefreshSettlementStatus.Waiting),
            _ => new RefreshSettlementResult(RefreshSettlementStatus.RecoveryRequired), // ProviderUnavailable, InvalidState
        };
    }
}
