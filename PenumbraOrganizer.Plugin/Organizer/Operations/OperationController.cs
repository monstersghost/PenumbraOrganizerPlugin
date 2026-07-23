namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary> Design doc section 7b, revised in this plan's second review round: CurrentIdentifier
/// renamed LastProcessedIdentifier (it was never "current" - between frames it's the most recently
/// finished one), LastProcessedDisplayName is a real mod name lookup rather than a duplicate of the
/// identifier, and per-target progress (ProcessedTargets/SuccessfulTargets/TotalTargets) is tracked
/// separately from per-step progress since a cycle-breaking plan has more steps than targets. The
/// only thing MainWindow (a later plan) is allowed to read. Published as a whole new instance after
/// every meaningful transition, never mutated in place. </summary>
public sealed record OperationStateSnapshot(
    OperationStage? Stage,
    OperationType? Kind,
    int ProcessedSteps,
    int TotalSteps,
    int ProcessedTargets,
    int SuccessfulTargets,
    int TotalTargets,
    string? LastProcessedIdentifier,
    string? LastProcessedDisplayName,
    string? LastError,
    bool RequiresRecovery,
    bool RecoveryClassificationPending,
    bool CanStartApply,
    bool CanStartRestore,
    bool CanScan,
    bool CanIndex,
    bool CanRunFolderCleanup,
    bool CanRunFolderCleanupRollback,
    bool CanCreateBackup,
    bool CanResolveRecovery,
    bool CanRequestCancellation)
{
    public static OperationStateSnapshot Idle { get; } = new(
        Stage: null, Kind: null, ProcessedSteps: 0, TotalSteps: 0,
        ProcessedTargets: 0, SuccessfulTargets: 0, TotalTargets: 0,
        LastProcessedIdentifier: null, LastProcessedDisplayName: null, LastError: null,
        RequiresRecovery: false, RecoveryClassificationPending: false,
        CanStartApply: true, CanStartRestore: true, CanScan: true, CanIndex: true,
        CanRunFolderCleanup: true, CanRunFolderCleanupRollback: true, CanCreateBackup: true,
        CanResolveRecovery: false, CanRequestCancellation: false);
}

/// <summary>
/// Design doc sections 2, 7, 7a, revised in this plan's second review round. Owns the operation
/// state machine from Prepared onward (Preparing / plan construction is the caller's job - it
/// needs OrganizerState data this layer doesn't have). _active is never cleared when an operation
/// concludes - it is only replaced by the next StartApply call - so a terminal Stage stays visible
/// in State while CanStartApply simultaneously becomes true again (derived from
/// OperationJournal.IsTerminal). A RecoveryRequired transition sets _active.RequiresRecovery and
/// retains every field of the context rather than clearing anything.
/// </summary>
public sealed class OperationController
{
    private sealed class ActiveOperationContext
    {
        public required OperationJournal Journal { get; set; }
        public required OperationPlan Plan { get; init; }
        public required PathMutationOperation Mutation { get; init; }
        public required OperationCheckpointer Checkpointer { get; init; }
        public RefreshSettlement? Refresh { get; set; }
        public VerificationSettlement? Verification { get; set; }
        public bool RequiresRecovery { get; set; }
    }

    private readonly IPenumbraOperations _adapter;
    private readonly IElapsedTimeSource _clock;
    private readonly IDiagnosticsSink _diagnostics;
    private readonly TimeSpan _frameBudget;
    private ActiveOperationContext? _active;
    private bool _stopRequested;

    public OperationStateSnapshot State { get; private set; } = OperationStateSnapshot.Idle;

    public OperationController(IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, TimeSpan frameBudget)
    {
        _adapter = adapter;
        _clock = clock;
        _diagnostics = diagnostics;
        _frameBudget = frameBudget;
    }

    public void StartApply(OperationPlan plan, Guid snapshotId, string bundleDirectory)
    {
        if (plan.Type != OperationType.Apply)
            throw new ArgumentException($"StartApply requires an Apply-type plan; got {plan.Type}.", nameof(plan));
        if (_active is not null && !_active.Journal.IsTerminal)
            throw new InvalidOperationException("Another organizer operation is already in progress.");

        var checkpointer = new OperationCheckpointer(_clock, bundleDirectory);

        // journal.OperationId and journal.PlanId both reuse plan.OperationId: a plan and the
        // journal that executes it are always constructed together at StartApply time, so there is
        // no meaningful distinction between "this operation's identity" and "this plan's identity"
        // for a freshly-started (non-resumed) operation.
        var preparedJournal = new OperationJournal(
            SchemaVersion: OperationJournal.CurrentSchemaVersion, OperationId: plan.OperationId, Type: plan.Type,
            Stage: OperationStage.Prepared, Resolution: OperationResolution.None, SuccessorOperationId: null,
            CancellationRequested: false, StartedAt: DateTimeOffset.UtcNow, TotalSteps: plan.ExecutionSteps.Count,
            ProcessedStepCount: 0, LastCompletedIdentifier: null, SnapshotId: snapshotId, PlanId: plan.OperationId,
            TargetHash: plan.IntegrityHash, RecoveryOfOperationId: null, UpdatedAt: DateTimeOffset.UtcNow);
        checkpointer.CheckpointIfDue(preparedJournal, force: true); // forced write on entering Prepared

        var mutatingJournal = preparedJournal with { Stage = OperationStage.Mutating, UpdatedAt = DateTimeOffset.UtcNow };
        checkpointer.CheckpointIfDue(mutatingJournal, force: true); // forced write on entering Mutating

        _active = new ActiveOperationContext
        {
            Journal = mutatingJournal,
            Plan = plan,
            Mutation = new PathMutationOperation(plan, _adapter, _clock, _diagnostics, bundleDirectory),
            Checkpointer = checkpointer,
        };
        _stopRequested = false;

        PublishState();
    }

    public void RequestCancellation()
    {
        if (_active is null || _active.Journal.Stage != OperationStage.Mutating)
            return;

        _stopRequested = true;
        _active.Journal = _active.Journal with { CancellationRequested = true, UpdatedAt = DateTimeOffset.UtcNow };
        try
        {
            _active.Checkpointer.CheckpointIfDue(_active.Journal, force: true);
        }
        catch (Exception)
        {
            _active.RequiresRecovery = true;
        }

        PublishState();
    }

    public void Update()
    {
        if (_active is null || _active.RequiresRecovery)
            return;

        try
        {
            AdvanceActiveOperation();
        }
        catch (Exception)
        {
            var failedJournal = _active.Journal with
            {
                Stage = _active.Journal.ProcessedStepCount > 0 ? OperationStage.FailedPartiallyApplied : OperationStage.FailedBeforeMutation,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            try
            {
                _active.Checkpointer.CheckpointIfDue(failedJournal, force: true);
                _active.Journal = failedJournal;
            }
            catch (Exception)
            {
                // Cannot prove the terminal record was persisted - leave the operation locked as
                // requiring recovery rather than claiming a terminal outcome that isn't backed up.
                _active.RequiresRecovery = true;
            }
        }

        PublishState();
    }

    private void AdvanceActiveOperation()
    {
        var active = _active!;

        if (active.Journal.Stage == OperationStage.Mutating)
        {
            var result = active.Mutation.Advance(active.Journal, _frameBudget, _stopRequested, j => active.Checkpointer.CheckpointIfDue(j));
            active.Journal = result.Journal;

            switch (result.Status)
            {
                case MutationAdvanceStatus.MutationFinished:
                    active.Journal = active.Journal with { Stage = OperationStage.Refreshing, UpdatedAt = DateTimeOffset.UtcNow };
                    active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
                    break;
                case MutationAdvanceStatus.CancellationObserved:
                    // Refreshing still runs once even for a cancelled operation, so Verifying can
                    // report on whatever DID complete before the cancellation was observed.
                    active.Journal = active.Journal with { Stage = OperationStage.Refreshing, UpdatedAt = DateTimeOffset.UtcNow };
                    active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
                    break;
                case MutationAdvanceStatus.IntegrityFailure:
                    var stage = active.Journal.ProcessedStepCount > 0 ? OperationStage.FailedPartiallyApplied : OperationStage.FailedBeforeMutation;
                    active.Journal = active.Journal with { Stage = stage, UpdatedAt = DateTimeOffset.UtcNow };
                    active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
                    break;
                // Working: nothing more to do this tick.
            }

            return;
        }

        if (active.Journal.Stage == OperationStage.Refreshing)
        {
            active.Refresh ??= new RefreshSettlement();
            var result = active.Refresh.Advance(_adapter, _clock, _diagnostics, active.Journal.OperationId);

            if (result.Status == RefreshSettlementStatus.Settled)
            {
                active.Journal = active.Journal with { Stage = OperationStage.Verifying, UpdatedAt = DateTimeOffset.UtcNow };
                active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
            }
            else if (result.Status == RefreshSettlementStatus.RecoveryRequired)
            {
                active.RequiresRecovery = true; // journal stays non-terminal (still Refreshing), context retained
            }

            return;
        }

        if (active.Journal.Stage == OperationStage.Verifying)
        {
            active.Verification ??= new VerificationSettlement();
            var result = active.Verification.Advance(
                _adapter, _clock, active.Plan.RecoveryTargets, active.Mutation.MutationStatusByIdentifier, _diagnostics, active.Journal.OperationId);

            if (result.Status == VerificationStatus.RecoveryRequired)
            {
                active.RequiresRecovery = true; // journal stays non-terminal (still Verifying), context retained
                return;
            }

            if (result.Status == VerificationStatus.Waiting)
                return;

            // Settled or TimedOut both conclude the operation. A cancelled outcome is only
            // asserted here, once verification proved trustworthy - design section 5a's
            // precedence rule (this is the ONLY place Cancelled is ever set).
            if (active.Journal.CancellationRequested)
            {
                active.Journal = active.Journal with { Stage = OperationStage.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
            }
            else
            {
                var hasFailures = result.Status == VerificationStatus.TimedOut || active.Mutation.MutationStatusByIdentifier.Values
                    .Any(s => s is TargetMutationStatus.FinalStepFailed or TargetMutationStatus.SkippedAfterEarlierFailure);
                active.Journal = active.Journal with
                {
                    Stage = hasFailures ? OperationStage.CompletedWithItemFailures : OperationStage.Completed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }

            active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
        }
    }

    private void PublishState()
    {
        if (_active is null)
        {
            State = OperationStateSnapshot.Idle;
            return;
        }

        var journal = _active.Journal;
        var canStartNew = journal.IsTerminal && !_active.RequiresRecovery;
        var modNameByIdentifier = _active.Plan.RecoveryTargets.ToDictionary(t => t.Identifier, t => t.ModName, StringComparer.Ordinal);
        var statuses = _active.Mutation.MutationStatusByIdentifier;
        var processedTargets = statuses.Values.Count(s => s != TargetMutationStatus.NotAttempted);
        var successfulTargets = statuses.Values.Count(s => s is TargetMutationStatus.FinalStepSucceeded or TargetMutationStatus.AlreadySatisfied);

        State = new OperationStateSnapshot(
            Stage: journal.Stage, Kind: journal.Type,
            ProcessedSteps: journal.ProcessedStepCount, TotalSteps: journal.TotalSteps,
            ProcessedTargets: processedTargets, SuccessfulTargets: successfulTargets, TotalTargets: _active.Plan.RecoveryTargets.Count,
            LastProcessedIdentifier: journal.LastCompletedIdentifier,
            LastProcessedDisplayName: journal.LastCompletedIdentifier is { } id ? modNameByIdentifier.GetValueOrDefault(id) : null,
            LastError: null,
            RequiresRecovery: _active.RequiresRecovery, RecoveryClassificationPending: false,
            CanStartApply: canStartNew, CanStartRestore: canStartNew, CanScan: canStartNew, CanIndex: canStartNew,
            CanRunFolderCleanup: canStartNew, CanRunFolderCleanupRollback: canStartNew, CanCreateBackup: canStartNew,
            CanResolveRecovery: _active.RequiresRecovery,
            CanRequestCancellation: journal.Stage == OperationStage.Mutating && !_active.RequiresRecovery);
    }
}
