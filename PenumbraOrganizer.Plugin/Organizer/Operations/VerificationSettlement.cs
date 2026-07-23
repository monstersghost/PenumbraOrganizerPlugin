namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary> Per-identifier disposition. Design doc section 5. Task 4 (PathMutationOperation) is
/// this type's primary producer; VerificationSettlement (this file) is its primary consumer. </summary>
public enum TargetMutationStatus
{
    NotAttempted, FinalStepSucceeded, FinalStepFailed, SkippedAfterEarlierFailure, AlreadySatisfied,
}

public enum VerificationStatus { Waiting, Settled, TimedOut, RecoveryRequired }

public enum RecoveryRequiredReason { DuplicateIdentifiers, ProviderUnavailable, InvalidData, TransientReadExhausted }

public sealed record VerificationResult(
    VerificationStatus Status,
    IReadOnlyList<string> UnsettledIdentifiers,
    RecoveryRequiredReason? Reason);

/// <summary>
/// Design doc section 6. Budgeted the same way as mutation - one read-and-compare attempt per
/// Advance() call, gated by a retry interval, never a blocking wait. Only targets whose
/// TargetMutationStatus is FinalStepSucceeded or AlreadySatisfied are expected to settle; an item
/// already recorded failed during Mutating is not waited on. Two defensive guards close gaps a real
/// caller must never be able to trigger: a target missing from mutationStatuses, and a Success
/// read carrying a null Snapshot.
/// </summary>
public sealed class VerificationSettlement
{
    private int _attemptsUsed;
    private long _lastAttemptTimestamp;
    private const int MaxAttempts = 10; // "attempts", not "retries" - avoids an off-by-one
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SlowCallThreshold = TimeSpan.FromMilliseconds(50);

    public VerificationResult Advance(
        IPenumbraOperations adapter, IElapsedTimeSource clock,
        IReadOnlyList<OperationRecoveryTarget> targets,
        IReadOnlyDictionary<string, TargetMutationStatus> mutationStatuses,
        IDiagnosticsSink diagnostics, Guid operationId)
    {
        if (targets.Any(t => !mutationStatuses.ContainsKey(t.Identifier)))
            return new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.InvalidData);

        if (_attemptsUsed > 0 && clock.GetElapsedTime(_lastAttemptTimestamp) < RetryInterval)
            return new VerificationResult(VerificationStatus.Waiting, [], null);

        _lastAttemptTimestamp = clock.GetTimestamp();
        _attemptsUsed++;

        var readStart = clock.GetTimestamp();
        var read = adapter.GetLiveMods();
        var readDuration = clock.GetElapsedTime(readStart);
        if (readDuration >= SlowCallThreshold) diagnostics.RecordSlowLiveSnapshot(operationId, readDuration);

        if (read.Status == LiveModReadStatus.ProviderUnavailable)
            return new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.ProviderUnavailable);
        if (read.Status == LiveModReadStatus.InvalidData)
            return new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.InvalidData);
        if (read.Status == LiveModReadStatus.TemporarilyUnavailable)
            return _attemptsUsed >= MaxAttempts
                ? new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.TransientReadExhausted)
                : new VerificationResult(VerificationStatus.Waiting, [], null);
        if (read.Snapshot is null)
            return new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.InvalidData);
        if (read.Snapshot.DuplicateIdentifiers.Count > 0)
            return new VerificationResult(VerificationStatus.RecoveryRequired, [], RecoveryRequiredReason.DuplicateIdentifiers);

        var expected = targets.Where(t => mutationStatuses[t.Identifier] is TargetMutationStatus.FinalStepSucceeded or TargetMutationStatus.AlreadySatisfied);
        var unsettled = expected.Where(t => !IsSettled(t, read.Snapshot)).Select(t => t.Identifier).ToList();

        if (unsettled.Count == 0) return new VerificationResult(VerificationStatus.Settled, [], null);
        return _attemptsUsed >= MaxAttempts
            ? new VerificationResult(VerificationStatus.TimedOut, unsettled, null)
            : new VerificationResult(VerificationStatus.Waiting, [], null);
    }

    private static bool IsSettled(OperationRecoveryTarget t, LiveModSnapshot live) =>
        live.Mods.TryGetValue(t.Identifier, out var mod) &&
        PenumbraPathSemantics.AreEquivalent(mod.FullPath, t.FinalRawPath, t.ModName);
}
