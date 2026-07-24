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
        public OperationStage? PendingTerminalStage { get; set; }
    }

    public enum RecoveryClassificationStatus { WaitingForProvider, Classified, ClassificationUnavailable }

    private sealed class PendingRecoveryContext
    {
        public required OperationJournal Journal { get; set; }
        public required string BundleDirectory { get; init; }
        public required OperationRecoveryGraphResult Graph { get; init; }
        public ArtifactCheckStatus PlanCheckStatus { get; set; } = ArtifactCheckStatus.Unchecked;
        public OperationPlan? Plan { get; set; }
        public ArtifactCheckStatus SnapshotCheckStatus { get; set; } = ArtifactCheckStatus.Unchecked;
        public RollbackSnapshot? Snapshot { get; set; }
        public RecoveryClassificationStatus ClassificationStatus { get; set; } = RecoveryClassificationStatus.WaitingForProvider;
        public RecoveryAssessment? Assessment { get; set; }
        public long? LastClassificationAttemptTimestamp { get; set; }
    }

    private static readonly TimeSpan ClassificationRetryInterval = TimeSpan.FromSeconds(1);

    private readonly IPenumbraOperations _adapter;
    private readonly IElapsedTimeSource _clock;
    private readonly IDiagnosticsSink _diagnostics;
    private readonly TimeSpan _frameBudget;
    private readonly string _operationsRoot;
    private ActiveOperationContext? _active;
    private PendingRecoveryContext? _pendingRecovery;
    private OperationRecoveryGraphResult? _blockedMultiRootGraph;
    private bool _stopRequested;

    public OperationStateSnapshot State { get; private set; } = OperationStateSnapshot.Idle;

    public OperationController(IPenumbraOperations adapter, IElapsedTimeSource clock, IDiagnosticsSink diagnostics, TimeSpan frameBudget, string operationsRoot)
    {
        _adapter = adapter;
        _clock = clock;
        _diagnostics = diagnostics;
        _frameBudget = frameBudget;
        _operationsRoot = operationsRoot;
    }

    public void StartApply(OperationPlan plan, Guid snapshotId, string bundleDirectory) =>
        StartOperation(plan, snapshotId, bundleDirectory, OperationType.Apply);

    public void StartRestore(OperationPlan plan, Guid snapshotId, string bundleDirectory) =>
        StartOperation(plan, snapshotId, bundleDirectory, OperationType.Restore);

    // Shared by PublishState's canStartNew derivation and this admission guard, so the two can never
    // independently drift apart - previously each was written separately as its own inline boolean
    // expression. Public so it can be unit-tested directly against hand-constructed OperationJournal
    // values: the "terminal Stage co-occurring with RequiresRecovery" case this guards against is
    // not producible through the real engine today (every RequiresRecovery=true call site in this
    // class leaves Stage non-terminal), but the predicate must still be correct on its own terms,
    // not merely lucky given today's callers.
    public static bool CanStartNext(OperationJournal journal, bool requiresRecovery) =>
        journal.IsTerminal && !requiresRecovery;

    private void StartOperation(OperationPlan plan, Guid snapshotId, string bundleDirectory, OperationType expectedType)
    {
        if (plan.Type != expectedType)
            throw new ArgumentException($"This entry point requires a {expectedType}-type plan; got {plan.Type}.", nameof(plan));
        if (_active is not null && !CanStartNext(_active.Journal, _active.RequiresRecovery))
            throw new InvalidOperationException("Another organizer operation is already in progress or requires recovery.");

        var checkpointer = new OperationCheckpointer(_clock, bundleDirectory);

        // journal.OperationId and journal.PlanId both reuse plan.OperationId: a plan and the
        // journal that executes it are always constructed together when an operation is started, so there is
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

    public void RegisterDiscoveredRecovery(OperationDiscoveryResult discovery)
    {
        switch (discovery.Graph.Status)
        {
            case OperationRecoveryGraphStatus.NoRecoveryNeeded:
                return; // controller stays Idle, exactly as today

            case OperationRecoveryGraphStatus.SingleAuthoritative:
                RegisterSingleAuthoritative(discovery);
                return;

            case OperationRecoveryGraphStatus.MultipleDisconnectedRoots:
            case OperationRecoveryGraphStatus.CycleDetected:
                _blockedMultiRootGraph = discovery.Graph;
                PublishState();
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(discovery), discovery.Graph.Status, "Unhandled OperationRecoveryGraphStatus.");
        }
    }

    private void RegisterSingleAuthoritative(OperationDiscoveryResult discovery)
    {
        var authoritativeId = discovery.Graph.AuthoritativeOperationIds[0];
        if (!discovery.Journals.TryGetValue(authoritativeId, out var journal))
            return; // defensive - graph and journals dictionary are built together by RunStartupDiscovery

        var bundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, authoritativeId);
        _pendingRecovery = new PendingRecoveryContext { Journal = journal, BundleDirectory = bundleDirectory, Graph = discovery.Graph };
        PublishState();
    }

    public RecoveryAssessment? GetRecoveryAssessment() => _pendingRecovery?.Assessment;

    public bool IsBlockedByMultipleRoots => _blockedMultiRootGraph is not null;

    public enum KeepCurrentResolutionResult { ResolvedAndArchived, ResolvedArchiveDeferred }

    // Once a resolved (terminal) journal is durably saved, the caller must return success and clear
    // the recovery lock even if relocation fails - the persisted journal alone is authoritative, and
    // OperationBundleDiscovery's own startup relocation pass will finish moving any terminal journal
    // it later finds still sitting under active/.
    private KeepCurrentResolutionResult TryRelocateToCompleted(string activeBundleDirectory, OperationJournal resolvedJournal)
    {
        var completedBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: false, resolvedJournal.OperationId);
        try
        {
            if (Directory.Exists(completedBundleDirectory))
            {
                var matches = OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var existing)
                    && existing is not null
                    && existing.OperationId == resolvedJournal.OperationId
                    && existing.IsTerminal
                    && existing.Resolution == resolvedJournal.Resolution;
                if (matches)
                    return KeepCurrentResolutionResult.ResolvedAndArchived;

                Plugin.Log?.Warning($"Keep Current: completed bundle directory for {resolvedJournal.OperationId} exists but doesn't match the resolved journal - leaving both copies in place.");
                return KeepCurrentResolutionResult.ResolvedArchiveDeferred;
            }

            Directory.CreateDirectory(OperationBundlePaths.CompletedDirectory(_operationsRoot));
            Directory.Move(activeBundleDirectory, completedBundleDirectory);
            return KeepCurrentResolutionResult.ResolvedAndArchived;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Plugin.Log?.Warning(ex, $"Keep Current: journal resolved but bundle relocation failed for {resolvedJournal.OperationId}.");
            return KeepCurrentResolutionResult.ResolvedArchiveDeferred;
        }
    }

    public KeepCurrentResolutionResult ResolveKeepCurrent()
    {
        if (_pendingRecovery is not { } pending)
            throw new InvalidOperationException("No pending recovery to resolve.");

        var resolvedJournal = pending.Journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
        OperationJournalCodec.Save(OperationBundlePaths.JournalPath(pending.BundleDirectory), resolvedJournal);
        pending.Journal = resolvedJournal; // commit point - everything below is best-effort

        var result = TryRelocateToCompleted(pending.BundleDirectory, resolvedJournal);
        _pendingRecovery = null;
        PublishState();
        return result;
    }

    // Resolves every journal in the blocked graph, not only the "authoritative" leaves - an
    // unresolved non-leaf ancestor journal would recreate this exact lockout at the next startup,
    // once its (now-terminal) child drops out of the non-terminal set and the ancestor becomes its
    // own new leaf/root. Only unblocks once every journal durably persisted its resolution.
    public IReadOnlyList<Guid> AcceptAllAndCloseInterruptedOperations()
    {
        if (_blockedMultiRootGraph is not { } graph)
            throw new InvalidOperationException("No blocked multi-root recovery to resolve.");

        var unresolved = new List<Guid>();
        foreach (var operationId in graph.AllOperationIds)
        {
            var bundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, operationId);
            if (!OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDirectory), out var journal) || journal is null)
            {
                unresolved.Add(operationId);
                continue;
            }

            var resolvedJournal = journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
            try
            {
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDirectory), resolvedJournal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Plugin.Log?.Warning(ex, $"Accept all: failed to persist resolution for {operationId}.");
                unresolved.Add(operationId);
                continue;
            }

            TryRelocateToCompleted(bundleDirectory, resolvedJournal); // best-effort, same rule as ResolveKeepCurrent
        }

        if (unresolved.Count > 0)
        {
            PublishState();
            return unresolved;
        }

        _blockedMultiRootGraph = null;
        PublishState();
        return [];
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
        if (_pendingRecovery is { ClassificationStatus: RecoveryClassificationStatus.WaitingForProvider } pending)
        {
            try
            {
                TryAdvanceClassification(pending);
            }
            catch (Exception)
            {
                // Mirrors the _active operation's own exception boundary below - an unmodeled failure
                // here must not propagate out of Update() (this method has no caller-side safety net;
                // Plugin.cs's Framework.Update subscription doesn't wrap it either), and must not leave
                // classification stuck retrying the same throw every second indefinitely.
                pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;
                PublishState();
            }
        }

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

    private void TryAdvanceClassification(PendingRecoveryContext pending)
    {
        var stateChanged = false;

        if (pending.PlanCheckStatus == ArtifactCheckStatus.Unchecked)
        {
            (pending.PlanCheckStatus, pending.Plan) = ArtifactStatusChecker.CheckPlan(pending.BundleDirectory);
            stateChanged = true;
        }
        if (pending.SnapshotCheckStatus == ArtifactCheckStatus.Unchecked)
        {
            (pending.SnapshotCheckStatus, pending.Snapshot) = ArtifactStatusChecker.CheckSnapshot(pending.BundleDirectory);
            stateChanged = true;
        }

        // Classification needs a valid Plan only - a missing/invalid Snapshot does not block it.
        if (pending.PlanCheckStatus != ArtifactCheckStatus.Valid)
        {
            pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;
            PublishState();
            return;
        }

        if (pending.LastClassificationAttemptTimestamp is { } last && _clock.GetElapsedTime(last) < ClassificationRetryInterval)
        {
            if (stateChanged)
                PublishState();
            return; // throttle window not yet elapsed since the last attempt
        }

        pending.LastClassificationAttemptTimestamp = _clock.GetTimestamp(); // record this attempt regardless of outcome
        var liveResult = _adapter.GetLiveMods();

        switch (liveResult.Status)
        {
            case LiveModReadStatus.Success when liveResult.Snapshot is not null:
                pending.Assessment = RecoveryAssessmentBuilder.Build(pending.Plan!, liveResult.Snapshot);
                pending.ClassificationStatus = RecoveryClassificationStatus.Classified;
                break;

            case LiveModReadStatus.TemporarilyUnavailable:
            case LiveModReadStatus.ProviderUnavailable:
                // Retryable at startup specifically - Penumbra may simply not have finished loading
                // yet. pending.ClassificationStatus already is WaitingForProvider; nothing to change.
                break;

            case LiveModReadStatus.InvalidData:
            default:
                // A response that parsed but doesn't make sense won't be fixed by asking again.
                pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;
                break;
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
                {
                    var failStage = active.Journal.ProcessedStepCount > 0 ? OperationStage.FailedPartiallyApplied : OperationStage.FailedBeforeMutation;
                    if (result.StopReason == MutationStopReason.ProviderUnavailable)
                    {
                        // ProviderUnavailable means the adapter itself is judged unusable -
                        // attempting a refresh call against the same broken adapter would very
                        // likely also just fail, trading a clean, immediately-retryable terminal
                        // outcome for one stuck in RequiresRecovery for no benefit. This is the
                        // one deliberate exception to "Refreshing always runs exactly once":
                        // settle directly without ever entering Refreshing, and do not set
                        // RequiresRecovery merely because refresh was skipped.
                        active.Journal = active.Journal with { Stage = failStage, UpdatedAt = DateTimeOffset.UtcNow };
                        active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
                    }
                    else
                    {
                        // An unmodeled exception (UnexpectedFatalException) doesn't prove the
                        // provider is unusable, and the operation's actual post-failure state may
                        // be uncertain - still attempt the bounded refresh before settling,
                        // carrying the eventual Failed* disposition forward through
                        // Refreshing/Verifying rather than discarding it.
                        active.PendingTerminalStage = failStage;
                        active.Journal = active.Journal with { Stage = OperationStage.Refreshing, UpdatedAt = DateTimeOffset.UtcNow };
                        active.Checkpointer.CheckpointIfDue(active.Journal, force: true);
                    }
                    break;
                }
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

            // Settled or TimedOut both conclude the operation. A pending Failed* disposition
            // (carried forward from an UnexpectedFatalException during Mutating) takes
            // precedence over both a clean completion and a cancelled outcome - the operation
            // already failed, refresh/verify only ran to establish authoritative post-failure
            // state, not to redeem the outcome. A cancelled outcome is asserted only once
            // verification proved trustworthy and no such pending failure exists - design
            // section 5a's precedence rule (this is the ONLY place Cancelled is ever set).
            if (active.PendingTerminalStage is { } pendingStage)
            {
                active.Journal = active.Journal with { Stage = pendingStage, UpdatedAt = DateTimeOffset.UtcNow };
            }
            else if (active.Journal.CancellationRequested)
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
        if (_active is null && _pendingRecovery is null && _blockedMultiRootGraph is null)
        {
            State = OperationStateSnapshot.Idle;
            return;
        }

        if (_active is null && _blockedMultiRootGraph is not null)
        {
            State = OperationStateSnapshot.Idle with
            {
                RequiresRecovery = true,
                RecoveryClassificationPending = false,
                CanResolveRecovery = true, // AcceptAllAndCloseInterruptedOperations, Task 8
                CanStartApply = false, CanStartRestore = false, CanScan = false, CanIndex = false,
                CanRunFolderCleanup = false, CanRunFolderCleanupRollback = false, CanCreateBackup = false,
            };
            return;
        }

        if (_active is null) // _pendingRecovery is not null
        {
            var pending = _pendingRecovery!;
            State = OperationStateSnapshot.Idle with
            {
                RequiresRecovery = true,
                RecoveryClassificationPending = pending.ClassificationStatus == RecoveryClassificationStatus.WaitingForProvider,
                CanResolveRecovery = true, // Keep Current needs neither classification nor a valid plan/snapshot
                CanStartApply = false, CanStartRestore = false, CanScan = false, CanIndex = false,
                CanRunFolderCleanup = false, CanRunFolderCleanupRollback = false, CanCreateBackup = false,
            };
            return;
        }

        var journal = _active.Journal;
        var canStartNew = CanStartNext(journal, _active.RequiresRecovery);
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
