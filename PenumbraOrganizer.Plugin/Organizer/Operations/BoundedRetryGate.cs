namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Shared bounded-retry gating logic for VerificationSettlement and RefreshSettlement - both
/// settlement classes use the identical one-attempt-per-Advance()-call, interval-gated,
/// max-attempt-bounded shape (design doc section 5b: "reuses the same attempt-count/interval
/// shape"). Extracted so the two classes' retry bookkeeping cannot silently drift apart.
/// </summary>
internal sealed class BoundedRetryGate
{
    private int _attemptsUsed;
    private long _lastAttemptTimestamp;
    private const int MaxAttempts = 10; // "attempts", not "retries" - avoids an off-by-one
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan SlowCallThreshold = TimeSpan.FromMilliseconds(50);

    /// <summary> Returns true if a new attempt may begin now (and records it as having begun);
    /// false if the caller must return its Waiting status without touching the adapter at all. </summary>
    public bool TryBeginAttempt(IElapsedTimeSource clock)
    {
        if (_attemptsUsed > 0 && clock.GetElapsedTime(_lastAttemptTimestamp) < RetryInterval)
            return false;

        _lastAttemptTimestamp = clock.GetTimestamp();
        _attemptsUsed++;
        return true;
    }

    public bool IsExhausted => _attemptsUsed >= MaxAttempts;
}
