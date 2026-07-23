namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum MutationAdvanceStatus { Working, MutationFinished, CancellationObserved, IntegrityFailure }

public enum MutationStopReason { None, UserCancellation, ProviderUnavailable, JournalWriteFailed, PlanCorrupt, UnexpectedFatalException }

public sealed record MutationAdvanceResult(OperationJournal Journal, MutationAdvanceStatus Status, MutationStopReason StopReason);

/// <summary>
/// Design doc section 5, revised in this plan's second review round. Drives only the Mutating
/// stage - it signals the caller via MutationAdvanceStatus rather than ever setting journal.Stage
/// itself. Cancellation is checked once, at Advance's entry, before any step of that call begins:
/// a call made with stopRequested already true processes zero new steps. The frame budget's "always
/// process at least one step" guarantee applies only once cancellation has already been ruled out.
/// MutationStatusByIdentifier is computed from each identifier's LAST execution step's durable
/// disposition on every access, never a dictionary mutated opportunistically mid-loop - this means
/// a temp hop's outcome can never leak into the reported status once the final step has its own
/// recorded disposition.
/// </summary>
public sealed class PathMutationOperation
{
    private readonly OperationPlan _plan;
    private readonly IPenumbraOperations _adapter;
    private readonly IElapsedTimeSource _clock;
    private readonly IDiagnosticsSink _diagnostics;
    private readonly string _bundleDirectory;
    private readonly Dictionary<int, OperationStepDisposition> _stepDispositions = new();
    private readonly Dictionary<string, int> _lastStepIndexByIdentifier;

    private static readonly TimeSpan SlowCallThreshold = TimeSpan.FromMilliseconds(50);

    public PathMutationOperation(
        OperationPlan plan, IPenumbraOperations adapter, IElapsedTimeSource clock,
        IDiagnosticsSink diagnostics, string bundleDirectory)
    {
        _plan = plan;
        _adapter = adapter;
        _clock = clock;
        _diagnostics = diagnostics;
        _bundleDirectory = bundleDirectory;

        _lastStepIndexByIdentifier = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in plan.ExecutionSteps) // steps are index-ordered, so the last write per identifier wins
            _lastStepIndexByIdentifier[step.Identifier] = step.StepIndex;
    }

    public IReadOnlyDictionary<string, TargetMutationStatus> MutationStatusByIdentifier =>
        _plan.RecoveryTargets.ToDictionary(
            t => t.Identifier,
            t => FindLastExecutedStatus(t.Identifier),
            StringComparer.Ordinal);

    private TargetMutationStatus FindLastExecutedStatus(string identifier)
    {
        // Find the last EXECUTED (not skipped) step for this identifier. A step index with no
        // entry in _stepDispositions was never attempted - TryGetValue (not GetValueOrDefault)
        // is essential here, since OperationStepDisposition.Succeeded is the enum's default (0)
        // value and GetValueOrDefault would silently misreport an unattempted step as succeeded.
        OperationStepDisposition? lastSkippedDisposition = null;

        for (var i = _plan.ExecutionSteps.Count - 1; i >= 0; i--)
        {
            if (_plan.ExecutionSteps[i].Identifier != identifier)
                continue;

            var stepIndex = _plan.ExecutionSteps[i].StepIndex;
            if (!_stepDispositions.TryGetValue(stepIndex, out var disposition))
                continue; // not yet attempted - keep scanning earlier steps for this identifier

            // Remember the last skipped disposition as a fallback
            if ((disposition == OperationStepDisposition.SkippedAfterEarlierFailure ||
                 disposition == OperationStepDisposition.SkippedAlreadySatisfied) &&
                lastSkippedDisposition == null)
            {
                lastSkippedDisposition = disposition;
                continue;
            }

            // Found an executed step (not skipped)
            if (disposition != OperationStepDisposition.SkippedAfterEarlierFailure &&
                disposition != OperationStepDisposition.SkippedAlreadySatisfied)
            {
                return ToTargetStatus(disposition);
            }
        }

        // No executed step found; if we saw a skipped step, report that
        if (lastSkippedDisposition.HasValue)
            return ToTargetStatus(lastSkippedDisposition.Value);

        // No recorded step at all, return NotAttempted
        return TargetMutationStatus.NotAttempted;
    }

    private static TargetMutationStatus ToTargetStatus(OperationStepDisposition disposition) => disposition switch
    {
        OperationStepDisposition.Succeeded => TargetMutationStatus.FinalStepSucceeded,
        OperationStepDisposition.Failed => TargetMutationStatus.FinalStepFailed,
        OperationStepDisposition.SkippedAfterEarlierFailure => TargetMutationStatus.SkippedAfterEarlierFailure,
        OperationStepDisposition.SkippedAlreadySatisfied => TargetMutationStatus.AlreadySatisfied,
        _ => TargetMutationStatus.NotAttempted,
    };

    public MutationAdvanceResult Advance(
        OperationJournal journal, TimeSpan budget, bool stopRequested, Action<OperationJournal> checkpointIfDue)
    {
        if (stopRequested)
            return new MutationAdvanceResult(journal, MutationAdvanceStatus.CancellationObserved, MutationStopReason.UserCancellation);

        var start = _clock.GetTimestamp();
        var index = journal.ProcessedStepCount;
        var lastIdentifier = journal.LastCompletedIdentifier;
        var processedAnyThisCall = false;

        while (index < _plan.ExecutionSteps.Count)
        {
            if (processedAnyThisCall && _clock.GetElapsedTime(start) >= budget)
                break;

            var step = _plan.ExecutionSteps[index];
            var callStart = _clock.GetTimestamp();
            SetModPathResult ipcResult;
            try
            {
                ipcResult = _adapter.SetModPath(step.Identifier, step.TargetRawPath);
            }
            catch (Exception)
            {
                // Unmodeled exception: cannot prove the IPC boundary is still usable, so this is
                // an operation-integrity stop, not an item failure - the conservative-by-default
                // reading of design section 5's "unexpected exception" case.
                journal = journal with { ProcessedStepCount = index, LastCompletedIdentifier = lastIdentifier, UpdatedAt = DateTimeOffset.UtcNow };
                checkpointIfDue(journal);
                return new MutationAdvanceResult(journal, MutationAdvanceStatus.IntegrityFailure, MutationStopReason.UnexpectedFatalException);
            }

            var callDuration = _clock.GetElapsedTime(callStart);
            if (callDuration >= SlowCallThreshold)
                _diagnostics.RecordSlowCall(journal.OperationId, step.Identifier, callDuration);

            if (ipcResult.Status == SetModPathStatus.ProviderUnavailable)
            {
                journal = journal with { ProcessedStepCount = index, LastCompletedIdentifier = lastIdentifier, UpdatedAt = DateTimeOffset.UtcNow };
                checkpointIfDue(journal);
                return new MutationAdvanceResult(journal, MutationAdvanceStatus.IntegrityFailure, MutationStopReason.ProviderUnavailable);
            }

            var succeeded = ipcResult.Status is SetModPathStatus.Success or SetModPathStatus.NothingChanged;

            if (succeeded)
            {
                RecordDisposition(step, OperationStepDisposition.Succeeded, ipcResult, callDuration);
                lastIdentifier = step.Identifier;
                index++;
            }
            else
            {
                RecordDisposition(step, OperationStepDisposition.Failed, ipcResult, callDuration);

                var groupId = step.GroupId;
                var cascadeIndex = index + 1;
                while (cascadeIndex < _plan.ExecutionSteps.Count && _plan.ExecutionSteps[cascadeIndex].GroupId == groupId)
                {
                    var cascadeStep = _plan.ExecutionSteps[cascadeIndex];
                    RecordDisposition(cascadeStep, OperationStepDisposition.SkippedAfterEarlierFailure, null, null);
                    lastIdentifier = cascadeStep.Identifier;
                    cascadeIndex++;
                }

                index = cascadeIndex;
            }

            processedAnyThisCall = true;
            journal = journal with { ProcessedStepCount = index, LastCompletedIdentifier = lastIdentifier, UpdatedAt = DateTimeOffset.UtcNow };
            checkpointIfDue(journal);
        }

        var status = index >= _plan.ExecutionSteps.Count ? MutationAdvanceStatus.MutationFinished : MutationAdvanceStatus.Working;
        return new MutationAdvanceResult(journal, status, MutationStopReason.None);
    }

    private void RecordDisposition(
        OperationExecutionStep step, OperationStepDisposition disposition, SetModPathResult? ipcResult, TimeSpan? duration)
    {
        _stepDispositions[step.StepIndex] = disposition;
        StepResultLog.Append(OperationBundlePaths.ResultsPath(_bundleDirectory), new OperationStepResult(
            step.StepIndex, step.Identifier, disposition,
            ipcResult?.Status.ToString(), ipcResult?.Diagnostic,
            DateTimeOffset.UtcNow, duration is null ? null : (long)duration.Value.TotalMilliseconds));
    }
}
