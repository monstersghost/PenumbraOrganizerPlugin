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
    bool CanContinueRecovery,
    bool CanRestorePreviousState,
    bool CanRequestCancellation)
{
    public static OperationStateSnapshot Idle { get; } = new(
        Stage: null, Kind: null, ProcessedSteps: 0, TotalSteps: 0,
        ProcessedTargets: 0, SuccessfulTargets: 0, TotalTargets: 0,
        LastProcessedIdentifier: null, LastProcessedDisplayName: null, LastError: null,
        RequiresRecovery: false, RecoveryClassificationPending: false,
        CanStartApply: true, CanStartRestore: true, CanScan: true, CanIndex: true,
        CanRunFolderCleanup: true, CanRunFolderCleanupRollback: true, CanCreateBackup: true,
        CanResolveRecovery: false, CanContinueRecovery: false, CanRestorePreviousState: false,
        CanRequestCancellation: false);
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
        public required string BundleDirectory { get; init; }
        public RefreshSettlement? Refresh { get; set; }
        public VerificationSettlement? Verification { get; set; }
        public bool RequiresRecovery { get; set; }
        public OperationStage? PendingTerminalStage { get; set; }
    }

    public enum RecoveryClassificationStatus { WaitingForProvider, Classified, ClassificationUnavailable }

    public enum RecoveryLiveReadStatus { WaitingForProvider, Available, Unavailable }

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
        public LiveModSnapshot? LiveSnapshot { get; set; }
        public RecoveryLiveReadStatus LiveReadStatus { get; set; } = RecoveryLiveReadStatus.WaitingForProvider;
        public bool CanContinueRecovery { get; set; }
        public bool CanRestorePreviousState { get; set; }
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
    private IReadOnlyDictionary<Guid, OperationJournal>? _blockedMultiRootJournals;
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

    // bypassPendingRecoveryLockout is intentionally unreachable from StartApply/StartRestore's public
    // surface - only StartRecoverySuccessor (below) ever passes true, and only Task 4's
    // ResolveContinue/ResolveRestorePreviousState ever call that. An ordinary Apply/Restore must keep
    // being rejected while a recovery is pending; only the controlled recovery-resolution path itself
    // is allowed to bypass that lockout. Note this bypasses ONLY the _pendingRecovery half of the
    // guard - _blockedMultiRootGraph is never bypassable, by anything: D2 explicitly does not resolve
    // the multi-root/cycle case (design doc section 7), so a recovery successor must never be able to
    // start while that lockout is in effect.
    private void StartOperation(
        OperationPlan plan, Guid snapshotId, string bundleDirectory, OperationType expectedType,
        bool bypassPendingRecoveryLockout = false, Guid? recoveryOfOperationId = null)
    {
        if (plan.Type != expectedType)
            throw new ArgumentException($"This entry point requires a {expectedType}-type plan; got {plan.Type}.", nameof(plan));

        var pendingRecoveryLocked = !bypassPendingRecoveryLockout && _pendingRecovery is not null;
        var blockedGraphLocked = _blockedMultiRootGraph is not null;
        if ((_active is not null && !CanStartNext(_active.Journal, _active.RequiresRecovery)) || pendingRecoveryLocked || blockedGraphLocked)
            throw new InvalidOperationException("Another organizer operation is already in progress or requires recovery.");

        var checkpointer = new OperationCheckpointer(_clock, bundleDirectory);

        // journal.OperationId and journal.PlanId both reuse plan.OperationId: a plan and the
        // journal that executes it are always constructed together when an operation is started, so there is
        // no meaningful distinction between "this operation's identity" and "this plan's identity"
        // for a freshly-started (non-resumed) operation. RecoveryOfOperationId is null for an ordinary
        // StartApply/StartRestore and set only by StartRecoverySuccessor - it is what actually makes
        // OperationRecoveryGraph.Analyze treat a recovery successor as authoritative over its stale
        // parent on a later startup (design doc section 5's stated safety net for a failed parent-
        // journal write; this parameter is what makes that guarantee true, not merely asserted).
        var preparedJournal = new OperationJournal(
            SchemaVersion: OperationJournal.CurrentSchemaVersion, OperationId: plan.OperationId, Type: plan.Type,
            Stage: OperationStage.Prepared, Resolution: OperationResolution.None, SuccessorOperationId: null,
            CancellationRequested: false, StartedAt: DateTimeOffset.UtcNow, TotalSteps: plan.ExecutionSteps.Count,
            ProcessedStepCount: 0, LastCompletedIdentifier: null, SnapshotId: snapshotId, PlanId: plan.OperationId,
            TargetHash: plan.IntegrityHash, RecoveryOfOperationId: recoveryOfOperationId, UpdatedAt: DateTimeOffset.UtcNow);
        checkpointer.CheckpointIfDue(preparedJournal, force: true); // forced write on entering Prepared

        var mutatingJournal = preparedJournal with { Stage = OperationStage.Mutating, UpdatedAt = DateTimeOffset.UtcNow };
        checkpointer.CheckpointIfDue(mutatingJournal, force: true); // forced write on entering Mutating

        _active = new ActiveOperationContext
        {
            Journal = mutatingJournal,
            Plan = plan,
            Mutation = new PathMutationOperation(plan, _adapter, _clock, _diagnostics, bundleDirectory),
            Checkpointer = checkpointer,
            BundleDirectory = bundleDirectory,
        };
        _stopRequested = false;

        PublishState();
    }

    private void StartRecoverySuccessor(OperationPlan plan, Guid snapshotId, string bundleDirectory, Guid recoveryOfOperationId) =>
        StartOperation(plan, snapshotId, bundleDirectory, plan.Type, bypassPendingRecoveryLockout: true, recoveryOfOperationId: recoveryOfOperationId);

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
                _blockedMultiRootJournals = discovery.Journals;
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

    public (ArtifactCheckStatus Plan, ArtifactCheckStatus Snapshot)? GetPendingRecoveryArtifactStatus() =>
        _pendingRecovery is { } pending ? (pending.PlanCheckStatus, pending.SnapshotCheckStatus) : null;

    public OperationJournal? GetPendingRecoveryJournal() => _pendingRecovery?.Journal;

    // Only AuthoritativeOperationIds (the ones actually independently resolvable - for disconnected
    // roots these are literal graph leaves, but for a cycle every member is authoritative), not
    // AllOperationIds - a non-authoritative ancestor isn't independently actionable; it gets folded in
    // automatically once its authoritative descendant resolves and discovery re-runs (Task 3).
    public IReadOnlyList<(Guid OperationId, OperationJournal Journal)> GetBlockedOperations() =>
        _blockedMultiRootGraph is not { } graph || _blockedMultiRootJournals is not { } journals
            ? []
            : graph.AuthoritativeOperationIds
                .Where(journals.ContainsKey)
                .Select(id => (id, journals[id]))
                .ToList();

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
        if (_pendingRecovery is { } pending)
        {
            var resolvedJournal = pending.Journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(pending.BundleDirectory), resolvedJournal);
            pending.Journal = resolvedJournal; // commit point - everything below is best-effort

            var result = TryRelocateToCompleted(pending.BundleDirectory, resolvedJournal);
            _pendingRecovery = null;
            PublishState();
            return result;
        }

        // Live, in-session checkpoint-write failure on an Apply/Restore currently in progress
        // (RequestCancellation, Update's catch block, AdvanceActiveOperation's Refreshing/Verifying
        // branches all set this) - independent from the startup-discovered _pendingRecovery case
        // above, but resolved the same way: commit the resolution to the journal, then best-effort
        // relocate. Clearing _active afterward is deliberate - the operation is being abandoned, not
        // continued, so there is nothing left to advance, and PublishState() should fall through to
        // ordinary Idle (or whatever _pendingRecovery/_blockedMultiRootGraph state remains) rather
        // than keeping a resolved-but-still-RequiresRecovery-looking context around.
        if (_active is { RequiresRecovery: true } active)
        {
            var resolvedJournal = active.Journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(active.BundleDirectory), resolvedJournal);
            active.Journal = resolvedJournal; // commit point - everything below is best-effort

            var result = TryRelocateToCompleted(active.BundleDirectory, resolvedJournal);
            _active = null;
            PublishState();
            return result;
        }

        throw new InvalidOperationException("No pending recovery to resolve.");
    }

    // Note the difference from a naive first draft: these re-check ArtifactStatusChecker.CheckPlan/
    // CheckSnapshot directly, rather than reading pending.PlanCheckStatus/pending.Plan (which are
    // only populated once TryAdvanceClassification's async loop has run at least once). This removes
    // any dependency on classification having already advanced - matching the same "revalidate
    // fresh, don't trust a cache" principle already applied to the live-mods read below, and it's
    // cheap (a small, synchronous, side-effect-free file read).
    public void ResolveContinue()
    {
        if (_pendingRecovery is not { } pending)
            throw new InvalidOperationException("No pending recovery to continue.");

        var (planStatus, plan) = ArtifactStatusChecker.CheckPlan(pending.BundleDirectory);
        if (planStatus != ArtifactCheckStatus.Valid || plan is null)
            throw new InvalidOperationException("No pending recovery with a valid plan to continue.");

        var freshSnapshot = ReadFreshLiveModsOrThrow();
        if (freshSnapshot.DuplicateIdentifiers.Count > 0)
            throw new InvalidOperationException("Continue is not available - live state has duplicate identifiers.");

        var freshAssessment = RecoveryAssessmentBuilder.Build(plan, freshSnapshot);
        var result = ContinuationPlanner.TryBuildResidualMoves(plan, freshAssessment);
        if (result.Status != ContinuationPlanStatus.Ready)
            throw new InvalidOperationException("Continue is not available for the current live state.");

        var newPlan = OperationPlanBuilder.BuildOperationPlan(plan.Type, result.ResidualMoves);
        var newSnapshot = RollbackHistory.CaptureSnapshot(
            freshSnapshot.Mods.Values.ToList(), label: null,
            autoDescription: $"Snapshot before continuing interrupted operation {pending.Journal.OperationId}");

        StartRecoverySuccessorOrThrow(pending, newPlan, newSnapshot, OperationResolution.ContinuedByNewOperation);
    }

    public void ResolveRestorePreviousState()
    {
        if (_pendingRecovery is not { } pending)
            throw new InvalidOperationException("No pending recovery to restore.");

        var (snapshotStatus, targetSnapshot) = ArtifactStatusChecker.CheckSnapshot(pending.BundleDirectory);
        if (snapshotStatus != ArtifactCheckStatus.Valid || targetSnapshot is null)
            throw new InvalidOperationException("No pending recovery with a valid snapshot to restore.");

        var freshSnapshot = ReadFreshLiveModsOrThrow();
        if (freshSnapshot.DuplicateIdentifiers.Count > 0)
            throw new InvalidOperationException("Restore Previous State is not available - live state has duplicate identifiers.");

        var currentMods = freshSnapshot.Mods.Values.ToList();
        var restorePlan = RollbackHistory.BuildRestorePlan(targetSnapshot, currentMods);
        var namedMoves = OperationPlanBuilder.BuildNamedMoves(restorePlan.Moves, currentMods);
        var newPlan = OperationPlanBuilder.BuildOperationPlan(OperationType.Restore, namedMoves);
        var newSnapshot = RollbackHistory.CaptureSnapshot(
            currentMods, label: null,
            autoDescription: $"Snapshot before restoring interrupted operation {pending.Journal.OperationId} to its prior state");

        StartRecoverySuccessorOrThrow(pending, newPlan, newSnapshot, OperationResolution.RestoredByNewOperation);
    }

    private LiveModSnapshot ReadFreshLiveModsOrThrow()
    {
        var result = _adapter.GetLiveMods();
        if (result.Status != LiveModReadStatus.Success || result.Snapshot is null)
            throw new InvalidOperationException("Live mod state is not currently available; try again shortly.");
        return result.Snapshot;
    }

    // Design doc section 5: the failure-atomic recovery-successor transaction. _pendingRecovery is
    // cleared only after StartRecoverySuccessor has durably activated the new operation - if anything
    // in the try block throws, _pendingRecovery is untouched and the interrupted operation is exactly
    // as recoverable as it was before this call.
    private void StartRecoverySuccessorOrThrow(
        PendingRecoveryContext expectedPending, OperationPlan newPlan, RollbackSnapshot newSnapshot,
        OperationResolution parentResolution)
    {
        // Defends the invariant, not a currently-reachable race: OperationController has no concurrent
        // entry points (same single-threaded Dalamud Update()/UI-callback model every other method
        // here already assumes). Guards a future refactor that introduces reentrancy.
        if (!ReferenceEquals(_pendingRecovery, expectedPending))
            throw new InvalidOperationException("The pending recovery changed before this resolution could start.");

        var newBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, newPlan.OperationId);
        try
        {
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(newBundleDirectory), newPlan);
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(newBundleDirectory), newSnapshot);
            StartRecoverySuccessor(newPlan, newSnapshot.Id, newBundleDirectory, expectedPending.Journal.OperationId);
        }
        catch
        {
            TryDeleteBundleDirectory(newBundleDirectory);
            throw; // _pendingRecovery untouched - a failed attempt leaves recovery exactly as it was
        }

        // Reached only once the successor is durably active (StartOperation persisted Prepared and
        // Mutating checkpoints, force: true, before returning). Only now does clearing
        // _pendingRecovery become safe.
        var interruptedJournal = expectedPending.Journal;
        var interruptedBundleDirectory = expectedPending.BundleDirectory;
        _pendingRecovery = null;

        try
        {
            var resolvedInterruptedJournal = interruptedJournal with
            {
                Resolution = parentResolution,
                SuccessorOperationId = newPlan.OperationId,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(interruptedBundleDirectory), resolvedInterruptedJournal);
            TryRelocateToCompleted(interruptedBundleDirectory, resolvedInterruptedJournal);
        }
        catch (Exception ex)
        {
            // The successor is already durably running - the user's Continue/Restore request already
            // succeeded. Failing to decorate the parent journal is a housekeeping gap, not a
            // resolution failure: on next startup the successor's own RecoveryOfOperationId makes it,
            // not the stale parent, authoritative in OperationRecoveryGraph.Analyze regardless of
            // whether this write landed - nothing is silently lost, just not yet tidied up. Must not
            // rethrow: that would report "Continue failed" for a Continue that actually started.
            // Must not stay completely silent either though (review point 7) - logged via the same
            // Plugin.Log?.Warning pattern TryRelocateToCompleted already uses for its own best-effort
            // failures, below.
            Plugin.Log?.Warning(ex,
                $"{parentResolution} successor {newPlan.OperationId} started, but resolving the interrupted " +
                $"journal {interruptedJournal.OperationId} failed. It will be correctly picked up on next " +
                "startup via the successor's own RecoveryOfOperationId.");
        }
    }

    private static void TryDeleteBundleDirectory(string bundleDirectory)
    {
        try
        {
            if (Directory.Exists(bundleDirectory))
                Directory.Delete(bundleDirectory, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort. A bundle whose journal.json is missing or fails to load is skipped
            // outright by OperationBundleDiscovery.LoadNonTerminalActiveJournals (both call sites),
            // never treated as an interrupted operation needing recovery - a leftover journal-less
            // bundle here is inert disk clutter, not a correctness risk.
        }
    }

    private enum JournalResolutionOutcome { Resolved, AlreadyResolved, Failed }

    // Extracted from AcceptAllAndCloseInterruptedOperations (this method's own logic is unchanged,
    // just no longer duplicated for Task 3's per-root resolution). Resolves one journal via
    // Keep-Current semantics: persists the resolution, best-effort relocates to completed/. Treats
    // "already resolved and relocated by a prior partial attempt" as its own outcome, not Failed -
    // a retry must not resurrect an already-successfully-resolved journal (see the existing
    // AcceptAllAndCloseInterruptedOperations_RetryAfterPartialFailure test, unchanged by this
    // extraction).
    private JournalResolutionOutcome TryResolveJournalAsKeepCurrent(Guid operationId)
    {
        var activeBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, operationId);
        if (!Directory.Exists(activeBundleDirectory))
        {
            var completedBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: false, operationId);
            var alreadyResolved = OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(completedBundleDirectory), out var existing)
                && existing is not null
                && existing.OperationId == operationId
                && existing.IsTerminal
                && existing.Resolution == OperationResolution.AcceptedCurrentState;
            return alreadyResolved ? JournalResolutionOutcome.AlreadyResolved : JournalResolutionOutcome.Failed;
        }

        if (!OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(activeBundleDirectory), out var journal) || journal is null)
            return JournalResolutionOutcome.Failed;

        var resolvedJournal = journal with { Resolution = OperationResolution.AcceptedCurrentState, UpdatedAt = DateTimeOffset.UtcNow };
        try
        {
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDirectory), resolvedJournal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Plugin.Log?.Warning(ex, $"Failed to persist Keep-Current resolution for {operationId}.");
            return JournalResolutionOutcome.Failed;
        }

        TryRelocateToCompleted(activeBundleDirectory, resolvedJournal); // best-effort, same rule as ResolveKeepCurrent
        return JournalResolutionOutcome.Resolved;
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
            if (TryResolveJournalAsKeepCurrent(operationId) == JournalResolutionOutcome.Failed)
                unresolved.Add(operationId);
        }

        if (unresolved.Count > 0)
        {
            PublishState();
            return unresolved;
        }

        _blockedMultiRootGraph = null;
        _blockedMultiRootJournals = null;
        PublishState();
        return [];
    }

    // Clears every recovery-related field and re-registers a fresh discovery result in one place, so
    // a multi-root-to-single-root or multi-root-to-none transition can't leave a stale field from the
    // previous state behind. Called only once RunStartupDiscovery has already succeeded (see
    // ResolveOneMultiRootOperation below) - never call this before a fresh OperationDiscoveryResult is
    // in hand.
    private void ReplaceDiscoveredRecovery(OperationDiscoveryResult discovery)
    {
        _pendingRecovery = null;
        _blockedMultiRootGraph = null;
        _blockedMultiRootJournals = null;
        RegisterDiscoveredRecovery(discovery);

        // RegisterDiscoveredRecovery's NoRecoveryNeeded branch returns without calling PublishState()
        // (correct at startup, where State already defaults to Idle) - here we may be transitioning
        // OUT of a non-Idle blocked state, so publish unconditionally regardless of which branch fired.
        PublishState();
    }

    public void ResolveOneMultiRootOperation(Guid operationId)
    {
        if (_blockedMultiRootGraph is not { } graph)
            throw new InvalidOperationException("No blocked multi-root recovery to resolve.");
        if (!graph.AuthoritativeOperationIds.Contains(operationId))
            throw new InvalidOperationException("The requested operation is not an independently resolvable root of the blocked recovery graph.");

        if (TryResolveJournalAsKeepCurrent(operationId) == JournalResolutionOutcome.Failed)
            throw new InvalidOperationException($"Failed to resolve {operationId} - see the plugin log.");

        // Re-run discovery over whatever remains on disk now that operationId has dropped out (either
        // just resolved above, or already resolved by a prior partial attempt) - the same startup
        // discovery path Plugin.cs's constructor uses, reused here rather than hand-rolling a second
        // graph derivation. Deliberately NOT cleared before this call: if RunStartupDiscovery throws,
        // the old _blockedMultiRootGraph/_blockedMultiRootJournals stay exactly as they were rather
        // than being discarded with nothing to replace them - the journal we just resolved is already
        // durably terminal regardless of whether this line succeeds, so a retry is always safe.
        var discovery = OperationBundleDiscovery.RunStartupDiscovery(_operationsRoot);
        ReplaceDiscoveredRecovery(discovery);
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
        if (_pendingRecovery is { } pending &&
            (pending.ClassificationStatus == RecoveryClassificationStatus.WaitingForProvider ||
             pending.LiveReadStatus == RecoveryLiveReadStatus.WaitingForProvider))
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
                pending.LiveReadStatus = RecoveryLiveReadStatus.Unavailable;
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

        // Classification (Continue) needs a valid Plan only - a missing/invalid Snapshot does not
        // block it. Restore Previous State's own availability depends only on the live read below,
        // never on plan validity (design doc section 2).
        if (pending.PlanCheckStatus != ArtifactCheckStatus.Valid)
            pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;

        // If NEITHER artifact is valid, no resolution could ever consume a live read - settle
        // LiveReadStatus permanently here too (mirroring ClassificationStatus's own permanent settle
        // just above), rather than leaving it WaitingForProvider forever. Without this, Update()'s
        // outer gate (ClassificationStatus == WaitingForProvider || LiveReadStatus ==
        // WaitingForProvider) would stay true indefinitely once ClassificationStatus alone had
        // already settled, calling this method every tick forever for no purpose (review point 4).
        var anyLiveConsumerAvailable = pending.PlanCheckStatus == ArtifactCheckStatus.Valid || pending.SnapshotCheckStatus == ArtifactCheckStatus.Valid;
        if (!anyLiveConsumerAvailable)
        {
            pending.LiveReadStatus = RecoveryLiveReadStatus.Unavailable;
            RecomputeResolutionAvailability(pending);
            if (stateChanged)
                PublishState();
            return;
        }

        // The live read backs both Continue's classification and Restore Previous State's own
        // availability - attempt it whenever either resolution could still use it, but only if it
        // hasn't already settled (a prior attempt may have resolved it to Available/Unavailable while
        // ClassificationStatus was still the one field keeping Update()'s gate open).
        if (pending.LiveReadStatus != RecoveryLiveReadStatus.WaitingForProvider)
        {
            if (stateChanged)
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
                pending.LiveSnapshot = liveResult.Snapshot;
                pending.LiveReadStatus = RecoveryLiveReadStatus.Available;
                if (pending.PlanCheckStatus == ArtifactCheckStatus.Valid)
                {
                    pending.Assessment = RecoveryAssessmentBuilder.Build(pending.Plan!, liveResult.Snapshot);
                    pending.ClassificationStatus = RecoveryClassificationStatus.Classified;
                }
                break;

            case LiveModReadStatus.TemporarilyUnavailable:
            case LiveModReadStatus.ProviderUnavailable:
                // Retryable at startup specifically - Penumbra may simply not have finished loading
                // yet. Both statuses already WaitingForProvider; nothing to change.
                break;

            case LiveModReadStatus.InvalidData:
            default:
                // A response that parsed but doesn't make sense won't be fixed by asking again.
                pending.LiveReadStatus = RecoveryLiveReadStatus.Unavailable;
                if (pending.PlanCheckStatus == ArtifactCheckStatus.Valid)
                    pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;
                break;
        }

        RecomputeResolutionAvailability(pending);
        PublishState();
    }

    // Cached once here (called only when classification/live-read state actually changes), not
    // recomputed on every PublishState() call - design doc section 5, review points 10/11. These
    // booleans are advisory for UI button-enablement only: ResolveContinue/ResolveRestorePreviousState
    // (Task 4) always take their own fresh read and re-derive everything from it before committing.
    private static void RecomputeResolutionAvailability(PendingRecoveryContext pending)
    {
        pending.CanContinueRecovery = pending.ClassificationStatus == RecoveryClassificationStatus.Classified
            && pending.Assessment is not null
            && pending.LiveSnapshot!.DuplicateIdentifiers.Count == 0
            && ContinuationPlanner.TryBuildResidualMoves(pending.Plan!, pending.Assessment).Status == ContinuationPlanStatus.Ready;

        pending.CanRestorePreviousState = pending.SnapshotCheckStatus == ArtifactCheckStatus.Valid
            && pending.LiveReadStatus == RecoveryLiveReadStatus.Available
            && pending.LiveSnapshot!.DuplicateIdentifiers.Count == 0;
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
                CanContinueRecovery = pending.CanContinueRecovery,
                CanRestorePreviousState = pending.CanRestorePreviousState,
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
            CanContinueRecovery: false, CanRestorePreviousState: false,
            CanRequestCancellation: journal.Stage == OperationStage.Mutating && !_active.RequiresRecovery);
    }
}
