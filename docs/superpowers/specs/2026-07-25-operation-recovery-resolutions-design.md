# Operation Recovery Resolutions: Continue and Restore Previous State (Plan D2)

Date: 2026-07-25
Status: Revised after first review round (architecture and option 1 approved; §5's resolution methods
rebuilt around a failure-atomic successor-start path and a fresh-read-per-resolution model per that
review). Awaiting re-review before implementation planning.
Builds on: `docs/superpowers/specs/2026-07-22-operation-controller-design.md` (§8a/§9/§9a — Continue
and Restore Previous State's original design), `docs/superpowers/specs/2026-07-24-operation-recovery-classification-design.md`
(Plan D1, shipped `main` at `59ba3c2`), and Plan C
(`docs/superpowers/specs/2026-07-24-operation-restore-integration-design.md`)

## 1. Scope and relationship to prior work

Plan D1 shipped classification (`RecoveryClassifier`/`RecoveryAssessment`), startup discovery wiring,
and one resolution (**Keep Current**, plus its bulk multi-root fallback). This plan adds the other
two resolutions the original design always intended: **Continue** (finish the interrupted operation
from where it left off) and **Restore Previous State** (abandon it entirely, roll every mod back to
wherever it was *before* the interrupted operation started).

**Confirmed while grounding this plan, not assumed:** per §2 of the D1 design doc, neither resolution
needs any execution-engine change. Both start a **new** operation from a freshly-computed move set and
call the already-generalized `OperationController.StartApply`/`StartRestore` (Plan C) unchanged. Read
`OperationController.cs` in full (post-D1) to confirm this holds against the real current code, not
just the original draft — it does. This plan's new work is entirely: (a) one pure class for Continue's
residual-move computation, (b) a small generalization of an existing Plan C method, (c) two new
`OperationController` resolution methods reusing D1's already-read classification/snapshot data, (d)
`Plugin.cs`/`MainWindow` wiring.

**A real reuse finding that shapes this plan's architecture:** Restore Previous State needs almost
nothing new. `RollbackHistory.BuildRestorePlan(RollbackSnapshot target, IReadOnlyList<LiveMod>
currentMods)` (existing, unchanged since before Plan C) plus `OperationPlanBuilder.BuildNamedMoves`/
`BuildRestoreOperationPlan` (Plan C, unchanged) already do everything Restore Previous State needs —
it just sources `target` from the interrupted operation's own `PendingRecoveryContext.Snapshot`
(already read by D1's `ArtifactStatusChecker.CheckSnapshot`) and `currentMods` from the *same*
already-read `RecoveryAssessment.LiveSnapshot.Mods.Values` D1's classification used — never a second,
independent `GetLiveMods()` call (matching the original design's own "never two independent live
reads" principle at §8a). Continue is the one piece needing genuinely new logic.

## 2. A real gap found connecting D1 and D2, flagged for an explicit decision

`OperationController.TryAdvanceClassification` (D1, shipped) gates the *entire* `GetLiveMods()` call
on `pending.PlanCheckStatus == ArtifactCheckStatus.Valid` — if the interrupted operation's `plan.json`
is missing/corrupt, classification never even attempts a live read, so `RecoveryAssessment` (and its
`LiveSnapshot`) never gets populated at all.

This is exactly right for **Continue**, which genuinely cannot proceed without a valid plan (there's
nothing to classify against, nothing to finish). But per the original design's own §9a table, **Restore
Previous State's availability should depend only on snapshot validity, not plan validity** — its whole
mechanism (`RollbackHistory.BuildRestorePlan`) never reads `pending.Plan` at all. Under D1's current
code, a corrupt `plan.json` would silently also block Restore Previous State, even though nothing about
Restore Previous State actually needs that plan — because the live-mods read it needs (to know
`currentMods`) is gated behind the same plan-validity check.

**Two ways to resolve this, presented for a decision rather than assumed:**

1. **Decouple the live-mods read from plan validity in D1's `TryAdvanceClassification`.** Always attempt
   `GetLiveMods()` (once artifact checks complete, respecting the existing throttle) regardless of
   `PlanCheckStatus`; store the raw `LiveModSnapshot` on `PendingRecoveryContext` independently of
   `Assessment` (which still requires a valid plan to compute `Classifications`, and stays `null` when
   the plan is invalid). Restore Previous State reads the raw snapshot; Continue reads
   `Assessment.Classifications`. This matches the original design's §9a table exactly, at the cost of
   revising already-shipped, already-reviewed D1 code (the same category of change D1 itself made to
   Plan A2's `OperationRecoveryGraph` when it found a bug there).
2. **Accept a stricter rule than §9a**: both Continue and Restore Previous State require a valid plan
   (i.e., leave D1's code untouched, and Restore Previous State's own availability additionally checks
   `PlanCheckStatus == Valid`, even though its own mechanism doesn't need the plan's *content*). Simpler,
   no changes to shipped code, but means a corrupt plan blocks a resolution that doesn't actually depend
   on it — a real, if narrow, degradation window relative to the original design's intent (a corrupt
   plan with an intact snapshot is exactly the scenario Restore Previous State should be able to save).

This document proceeds with **option 1** as its working assumption (matching the original design's
intent precisely, and it's a small, well-contained change), but this is flagged explicitly for review
rather than silently decided — see below for the exact revision.

**Revised after review: a second, independent status is needed, not a nullable field bolted onto
`ClassificationStatus`.** `ClassificationStatus`'s three states
(`WaitingForProvider`/`Classified`/`ClassificationUnavailable`) were built assuming plan validity gates
the entire read. Once decoupled, the live read needs its own state machine — Continue and Restore
Previous State can now settle independently (a corrupt plan freezes `ClassificationStatus` at
`ClassificationUnavailable` forever, but the live read backing Restore Previous State must keep being
attempted). New enum:

```csharp
public enum RecoveryLiveReadStatus { WaitingForProvider, Available, Unavailable }
```

`PendingRecoveryContext` gains `LiveModSnapshot? LiveSnapshot` and
`RecoveryLiveReadStatus LiveReadStatus { get; set; } = RecoveryLiveReadStatus.WaitingForProvider`.

**A bug found while actually drafting the decoupling (not flagged by the review, but a direct
consequence of doing it correctly): `Update()`'s outer gate must change too.** Today it's `if
(_pendingRecovery is { ClassificationStatus: RecoveryClassificationStatus.WaitingForProvider } pending)`
— once `ClassificationStatus` settles to `ClassificationUnavailable` (plan invalid), this gate would
never fire again, so `TryAdvanceClassification` would never run again, so the live read that Restore
Previous State depends on would never be attempted past that first call. The gate must become:

```csharp
if (_pendingRecovery is { } pending &&
    (pending.ClassificationStatus == RecoveryClassificationStatus.WaitingForProvider ||
     pending.LiveReadStatus == RecoveryLiveReadStatus.WaitingForProvider))
```

so the two settle independently and the method keeps getting called until both have.

**`TryAdvanceClassification`'s revised body** (verified against the real current method,
`OperationController.cs:398-452`, not the original draft):

```csharp
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

    if (pending.PlanCheckStatus != ArtifactCheckStatus.Valid)
        pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;

    // The live read backs both Continue's classification and Restore Previous State's own
    // availability - attempt it whenever either resolution could still use it. If neither artifact
    // is valid, no resolution is reachable and the read would be wasted IPC traffic (review point 11).
    var liveReadNeeded = pending.LiveReadStatus == RecoveryLiveReadStatus.WaitingForProvider &&
        (pending.PlanCheckStatus == ArtifactCheckStatus.Valid || pending.SnapshotCheckStatus == ArtifactCheckStatus.Valid);

    if (!liveReadNeeded)
    {
        if (stateChanged)
            PublishState();
        return;
    }

    if (pending.LastClassificationAttemptTimestamp is { } last && _clock.GetElapsedTime(last) < ClassificationRetryInterval)
    {
        if (stateChanged)
            PublishState();
        return;
    }

    pending.LastClassificationAttemptTimestamp = _clock.GetTimestamp();
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
            break; // retry later - both statuses already WaitingForProvider

        case LiveModReadStatus.InvalidData:
        default:
            pending.LiveReadStatus = RecoveryLiveReadStatus.Unavailable;
            if (pending.PlanCheckStatus == ArtifactCheckStatus.Valid)
                pending.ClassificationStatus = RecoveryClassificationStatus.ClassificationUnavailable;
            break;
    }

    RecomputeResolutionAvailability(pending); // section 5 - cached once here, not per PublishState() call
    PublishState();
}
```

**Important: this cached `LiveSnapshot`/`Assessment` is now advisory only, for UI availability.** Per
the review's point 4 and 11: the actual resolution methods (§5) never consume these cached fields for
planning — they take their own fresh read at the moment the user confirms. A pending recovery can sit
unresolved for minutes; reusing a read that old to actually build moves and capture a snapshot would
resolve against state that's no longer true. The cached fields only drive whether the buttons are
enabled.

## 3. `ContinuationPlanner`: residual-move computation for Continue

Per the original design's §8a/§9 per-target rule table, reconciled against D1's actual
`ItemRecoveryState` enum (which already dropped `MissingSnapshot`/`MissingPlan` as redundant with
artifact-level status, confirmed correct in D1's own review):

| Classification | Residual action |
|---|---|
| `AtIntended`, `AtBoth` | skip — already at (or equivalent to) the final target |
| `AtSnapshot`, `AtKnownIntermediate` | queue: current live path → the original plan's `FinalRawPath` |
| `AtNeither`, `MissingLive` | block Continue entirely — not per-identifier, the whole resolution |

**"Any blocking classification present" is an all-or-nothing gate, not a per-identifier skip** — a
partial Continue could leave some mods correctly finished and others silently abandoned mid-flight,
exactly the half-completed state this whole recovery system exists to prevent. Confirmed against the
original design's own §9a table wording ("Any `AtNeither` present → Disabled"), not merely inferred.

**`AtKnownIntermediate`'s validity is proven by attempting the replan, not enumerated by hand** (§8a),
reusing `ApplyPlanner.OrderMovesForApply` exactly as it already validates ordinary Apply/Restore moves.
Concretely: `OrderMovesForApply`'s first step is `moves.ToDictionary(m => m.CurrentPath, ...)` — this
throws `ArgumentException` if two residual moves share a `CurrentPath`, which is precisely the failure
mode an inconsistent `AtKnownIntermediate` reading would produce (two mods that both currently occupy
what was meant to be one mod's temporary hop path). The replan attempt is wrapped in a try/catch; a
thrown exception means Continue is blocked, matching "otherwise treated as blocking."

**Revised after review (point 5): the original sketch threw `ArgumentException`/`KeyNotFoundException`
for malformed input via `.ToDictionary`/indexers, which is not acceptable for a `Try...`-named method —
especially now that §5 calls it from a fresh, re-derived `RecoveryAssessment` on every resolution
attempt rather than only once from a value D1 already validated. In today's shipped code, a
successfully-loaded `OperationPlan` can't actually contain duplicate `RecoveryTargets` identifiers
(`Verify()`'s integrity hash would have to be hand-recomputed to match a tampered file, and
`OperationPlan.Create` itself already rejects duplicates), so these paths are effectively unreachable
today — but making the method total is cheap, and it removes a plan-json-format assumption from a path
that's about to run more often than it did in D1.**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ContinuationPlanStatus { Ready, Blocked }

public enum ContinuationBlockReason { None, BlockingClassificationPresent, InconsistentRecoveryTargets, ReplanFailed }

public sealed record ContinuationPlanResult(
    ContinuationPlanStatus Status, IReadOnlyList<NamedModMove> ResidualMoves,
    ContinuationBlockReason Reason = ContinuationBlockReason.None);

/// <summary>
/// Design doc section 8a/9: computes the residual move set for Continue from an already-built
/// RecoveryAssessment - never re-classifies, never re-reads live state itself (the caller supplies
/// whichever assessment it wants evaluated - D1's cached one for availability, a fresh one at
/// resolution time). AtKnownIntermediate's validity is proven by attempting
/// ApplyPlanner.OrderMovesForApply on the full candidate set, the exact operation Continue would
/// actually perform - not by hand-enumerated consistency rules. Never throws for malformed input.
/// </summary>
public static class ContinuationPlanner
{
    public static ContinuationPlanResult TryBuildResidualMoves(OperationPlan interruptedPlan, RecoveryAssessment assessment)
    {
        var hasBlockingClassification = assessment.Classifications.Any(c =>
            c.State is ItemRecoveryState.AtNeither or ItemRecoveryState.MissingLive);
        if (hasBlockingClassification)
            return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.BlockingClassificationPresent);

        var targetByIdentifier = new Dictionary<string, OperationRecoveryTarget>(StringComparer.Ordinal);
        foreach (var target in interruptedPlan.RecoveryTargets)
        {
            if (!targetByIdentifier.TryAdd(target.Identifier, target))
                return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.InconsistentRecoveryTargets);
        }

        var candidateMoves = new List<NamedModMove>();
        foreach (var classification in assessment.Classifications)
        {
            if (classification.State is not (ItemRecoveryState.AtSnapshot or ItemRecoveryState.AtKnownIntermediate))
                continue; // AtIntended/AtBoth: already at the final target, nothing to queue

            if (!targetByIdentifier.TryGetValue(classification.Identifier, out var target) ||
                !assessment.LiveSnapshot.Mods.TryGetValue(classification.Identifier, out var live))
                return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.InconsistentRecoveryTargets);

            candidateMoves.Add(new NamedModMove(classification.Identifier, target.ModName, live.FullPath, target.FinalRawPath));
        }

        if (candidateMoves.Count == 0)
            return new ContinuationPlanResult(ContinuationPlanStatus.Ready, []); // every target already at its final path - a valid, empty Continue

        try
        {
            // The exact operation Continue would perform - not evaluated for its result here, only
            // for whether it throws. BuildOperationPlan (section 4) re-runs this same call for real
            // when Continue is actually resolved; this is a dry run to decide availability.
            ApplyPlanner.OrderMovesForApply(candidateMoves.Select(m => new ModMove(m.Identifier, m.CurrentPath, m.TargetPath)).ToList());
        }
        catch (ArgumentException)
        {
            return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, [], ContinuationBlockReason.ReplanFailed);
        }

        return new ContinuationPlanResult(ContinuationPlanStatus.Ready, candidateMoves);
    }
}
```

**`NamedModMove` is reused directly from Plan C** (`Organizer/Operations/OperationPlanBuilder.cs`,
`record NamedModMove(string Identifier, string ModName, string CurrentPath, string TargetPath)`) — no
new move-shape type needed, since Continue's residual moves fit the exact same shape Restore's already
use.

## 4. `OperationPlanBuilder`: generalize `BuildRestoreOperationPlan`

Continue's new plan is **not always an Apply-type plan** — it must be the *same type* as the
interrupted operation (an interrupted Apply's Continue is itself an Apply-type plan; an interrupted
Restore's Continue is a Restore-type plan). Plan C's existing `BuildRestoreOperationPlan` already
contains 100% of the logic Continue needs (`ApplyPlanner.OrderMovesForApply`, execution-step
construction, recovery-target construction) — the *only* thing hardcoded to `OperationType.Restore` is
the final `OperationPlan.Create` call. Rather than duplicate this method a third time (once for Apply's
own `BuildApplyPlan`, once for ordinary Restore, once for Continue), generalize it to take the type as
a parameter, matching this codebase's own established DRY precedent (Plan C already did the identical
generalization for `OperationController.StartApply`/`StartRestore` → `StartOperation`):

```csharp
// Existing method, renamed and given a type parameter - BuildRestoreOperationPlan(namedMoves)
// becomes BuildOperationPlan(OperationType.Restore, namedMoves) at its one existing call site
// (Plugin.StartRestoreOperation), with zero behavior change there. Continue is the second caller,
// passing whichever OperationType the interrupted operation actually was.
public static OperationPlan BuildOperationPlan(OperationType type, IReadOnlyList<NamedModMove> namedMoves)
{
    var moves = namedMoves.Select(m => new ModMove(m.Identifier, m.CurrentPath, m.TargetPath)).ToList();
    var steps = ApplyPlanner.OrderMovesForApply(moves);

    var executionSteps = steps
        .Select((s, index) => new OperationExecutionStep(
            index, s.Identifier, s.TargetPath,
            s.IsTemporary ? OperationStepKind.CycleBreakingTemporaryMove : OperationStepKind.FinalMove,
            s.GroupId))
        .ToList();

    var recoveryTargets = namedMoves
        .Select(m => new OperationRecoveryTarget(m.Identifier, m.CurrentPath, m.TargetPath, m.ModName))
        .ToList();

    return OperationPlan.Create(type, executionSteps, recoveryTargets);
}
```

`Plugin.StartRestoreOperation`'s one existing call site changes from
`OperationPlanBuilder.BuildRestoreOperationPlan(namedMoves)` to
`OperationPlanBuilder.BuildOperationPlan(OperationType.Restore, namedMoves)` — a pure rename plus one
explicit argument, no behavior change. Every existing test named `BuildRestoreOperationPlan_*` in
`OperationPlanBuilderTests.cs` needs the same mechanical rename to call the new method with
`OperationType.Restore` explicit, asserting identical behavior.

## 5. `OperationController`: `ResolveContinue`, `ResolveRestorePreviousState`

**Substantially revised after review.** The original draft cleared `_pendingRecovery` before calling
`StartApply`/`StartRestore` so their admission guard would let the successor start, then persisted the
parent's resolution afterward. The review's point 1 is right that this is unsafe: if `StartApply`/
`StartRestore` throws *after* `_pendingRecovery = null`, the recovery context is gone from memory while
the interrupted journal on disk is still unresolved — exactly the masking condition D1's admission
guard exists to prevent, reintroduced by the resolution path meant to close it. And point 4 is right
that reusing D1's classification-time `LiveSnapshot`/`Assessment` to actually build moves and capture a
snapshot resolves against state that could be minutes stale by the time the user clicks a button.

Both problems are fixed together: a dedicated, failure-atomic successor-start path that only clears
`_pendingRecovery` after the successor is confirmed durably active, and a fresh `GetLiveMods()` read
taken at the moment of resolution rather than reused from classification time. The 8-step durable
ordering (design doc §9) is unchanged in shape; what changes is exactly *when* `_pendingRecovery` gets
cleared and *which* live read feeds steps 1-2.

**`StartOperation`'s admission guard gets a private bypass, not a public one.** Verified against
`OperationController.cs:106-126`: `StartApply`/`StartRestore` are thin wrappers passing `OperationType.
Apply`/`.Restore` as `expectedType` to a shared private `StartOperation`, whose guard is `(_active is
not null && !CanStartNext(...)) || _pendingRecovery is not null || _blockedMultiRootGraph is not null`.
Recovery-successor start needs to bypass only the `_pendingRecovery` half of that guard, and only via a
path `ResolveContinue`/`ResolveRestorePreviousState` reach internally — never through the public
`StartApply`/`StartRestore` surface, which must keep rejecting while a recovery is pending:

```csharp
public void StartApply(OperationPlan plan, Guid snapshotId, string bundleDirectory) =>
    StartOperation(plan, snapshotId, bundleDirectory, OperationType.Apply);

public void StartRestore(OperationPlan plan, Guid snapshotId, string bundleDirectory) =>
    StartOperation(plan, snapshotId, bundleDirectory, OperationType.Restore);

private void StartRecoverySuccessor(OperationPlan plan, Guid snapshotId, string bundleDirectory) =>
    StartOperation(plan, snapshotId, bundleDirectory, plan.Type, bypassRecoveryLockout: true);

private void StartOperation(
    OperationPlan plan, Guid snapshotId, string bundleDirectory, OperationType expectedType,
    bool bypassRecoveryLockout = false)
{
    if (plan.Type != expectedType)
        throw new ArgumentException($"Expected a {expectedType} plan but received {plan.Type}.", nameof(plan));

    var recoveryLocked = !bypassRecoveryLockout && (_pendingRecovery is not null || _blockedMultiRootGraph is not null);
    if ((_active is not null && !CanStartNext(_active.Journal, _active.RequiresRecovery)) || recoveryLocked)
        throw new InvalidOperationException("Cannot start a new operation while another is active or pending recovery.");

    // ... unchanged body: build ActiveOperationContext, persist Prepared then Mutating checkpoints
    // (force: true, both - verified they run before this method returns), set _active.
}
```

**The failure-atomic transaction wrapper**, used by both resolution methods:

```csharp
private void StartRecoverySuccessorOrThrow(
    PendingRecoveryContext expectedPending, OperationPlan newPlan, RollbackSnapshot newSnapshot,
    OperationResolution parentResolution)
{
    // Defends the invariant, not a currently-reachable race: OperationController has no concurrent
    // entry points (same single-threaded Dalamud Update()/UI-callback model every other method here
    // already assumes). This guards a future refactor that introduces reentrancy, not a bug that
    // exists today.
    if (!ReferenceEquals(_pendingRecovery, expectedPending))
        throw new InvalidOperationException("The pending recovery changed before this resolution could start.");

    var newBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, newPlan.OperationId);
    try
    {
        OperationPlanCodec.Save(OperationBundlePaths.PlanPath(newBundleDirectory), newPlan);
        OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(newBundleDirectory), newSnapshot);
        StartRecoverySuccessor(newPlan, newSnapshot.Id, newBundleDirectory);
    }
    catch
    {
        TryDeleteBundleDirectory(newBundleDirectory);
        throw; // _pendingRecovery untouched - a failed attempt leaves recovery exactly as it was
    }

    // Reached only once the successor is durably active (StartOperation persisted Prepared and
    // Mutating checkpoints, force: true, before returning - verified against OperationController.cs,
    // not assumed). Only now does clearing _pendingRecovery become safe.
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
    catch (Exception)
    {
        // The successor is already durably running - the user's Continue/Restore request already
        // succeeded. Failing to decorate the parent journal is a housekeeping gap, not a resolution
        // failure (review point 2): on next startup the successor's own RecoveryOfOperationId makes
        // it, not the stale parent, authoritative in OperationRecoveryGraph.Analyze regardless of
        // whether this write landed - nothing is silently lost, just not yet tidied up. Must not
        // rethrow: that would report "Continue failed" for a Continue that actually started. Not
        // adding a same-session "reject or adopt an existing successor" check on top of this: once
        // _pendingRecovery only clears after success, a same-session retry has nothing left to act on
        // (there's no pending recovery to resolve again), so that failure mode isn't reachable here.
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
        // Best-effort. Verified against OperationBundleDiscovery.LoadNonTerminalActiveJournals (both
        // call sites, OperationBundleDiscovery.cs:32,61): a bundle whose journal.json is missing or
        // fails to load is skipped outright, never treated as an interrupted operation needing
        // recovery. A leftover journal-less bundle is inert disk clutter, not a correctness risk.
    }
}
```

**`ResolveContinue`** — takes its own fresh live read, re-derives the assessment from it, and rejects
duplicates before building anything:

```csharp
public void ResolveContinue()
{
    if (_pendingRecovery is not { Plan: { } plan } pending || pending.PlanCheckStatus != ArtifactCheckStatus.Valid)
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

private LiveModSnapshot ReadFreshLiveModsOrThrow()
{
    var result = _adapter.GetLiveMods();
    if (result.Status != LiveModReadStatus.Success || result.Snapshot is null)
        throw new InvalidOperationException("Live mod state is not currently available; try again shortly.");
    return result.Snapshot;
}
```

**`ResolveRestorePreviousState`** mirrors this — it never needs `ContinuationPlanner`/`pending.Plan` at
all (confirmed §1's reuse finding), and always produces a `Restore`-type plan regardless of the
interrupted operation's own type:

```csharp
public void ResolveRestorePreviousState()
{
    if (_pendingRecovery is not { Snapshot: { } targetSnapshot } pending || pending.SnapshotCheckStatus != ArtifactCheckStatus.Valid)
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
```

**Verified, not assumed (review point 7): `RollbackHistory.BuildRestorePlan` already has well-defined
missing-identifier semantics.** `RestorePlan` (`RollbackHistory.cs:16-20`) already separates
`SkippedUninstalledIdentifiers` (present in the target snapshot, absent from live — not silently
dropped) and `RootRelocatedIdentifiers` (present live, absent from the target — relocated to the
Penumbra root, not silently ignored) from `Moves`/`UnchangedIdentifiers`. This is the same machinery
Plan C's ordinary History-tab Restore already ships with. Restore Previous State inherits these exact
properties — no new completeness gate, no new result-surfacing (still deferred to Plan E, same boundary
Plan C already drew). §8 adds a test asserting this inheritance explicitly rather than leaving it
implicit.

**Verified, not assumed (review point 8): an empty Continue already has a proven terminal path.** Plan
C's own controller-level test confirmed a zero-step `OperationPlan` reaches terminal correctly through
the real engine (`RefreshResult.Success` + an empty `LiveModReadResult`, three `Update()` calls). Continue
producing zero residual moves needs no special-casing — `StartRecoverySuccessorOrThrow` with an
empty-steps plan follows the identical path. §8 adds a Continue-specific test confirming this wiring,
since Continue's own plumbing is new even though the underlying zero-step behavior isn't.

**Availability, replacing D1's placeholder single `CanResolveRecovery` boolean.** New
`OperationStateSnapshot` fields (`CanResolveRecovery` is unchanged — still "Keep Current is available"):

```csharp
bool CanContinueRecovery,
bool CanRestorePreviousState,
```

**Revised after review (point 10 and 11): computed once when classification/live-read settle, cached on
`PendingRecoveryContext`, read as-is in `PublishState()` — not recomputed on every publish.**
`PendingRecoveryContext` gains:

```csharp
public bool CanContinueRecovery { get; set; }
public bool CanRestorePreviousState { get; set; }
```

Recomputed at the end of `TryAdvanceClassification` (§2), right after `LiveSnapshot`/`Assessment`/
`ClassificationStatus`/`LiveReadStatus` are updated:

```csharp
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
```

`PublishState()`'s `_pendingRecovery`-populated branch reads `pending.CanContinueRecovery`/
`pending.CanRestorePreviousState` directly. Both default `false` and stay `false` in the
`_blockedMultiRootGraph` branch and the ordinary `Idle` branch, matching `CanResolveRecovery`'s existing
pattern in both. **These cached booleans are advisory (button-enablement) only** — per §2, the actual
resolution methods above always take their own fresh read and re-derive everything from it; a button
being enabled is a hint, not a precondition the resolution methods trust blindly. `ResolveContinue`/
`ResolveRestorePreviousState` re-check duplicate identifiers against the *fresh* read even though the
cached booleans already checked them against the *cached* one, because those two reads can disagree.

## 6. `Plugin.cs` and `MainWindow`: wiring

```csharp
internal void ResolveContinue()
{
    OperationController.ResolveContinue();
    // No RunScan() here, unlike ResolveKeepCurrent/AcceptAll - this starts a new async operation
    // (StartApply/StartRestore), which is polled to completion exactly like an ordinary Apply/Restore
    // already is (MainWindow's existing completion-detection blocks, Plan B2/C) - RunScan() belongs
    // there, not at the moment the operation merely starts.
}

internal void ResolveRestorePreviousState()
{
    OperationController.ResolveRestorePreviousState();
}
```

`MainWindow`'s crude recovery panel (D1) gains two more conditionally-enabled buttons alongside the
existing "Keep Current State," each gated on its own new capability field, each with its own
confirmation popup matching the established pattern:

```csharp
if (operationState.CanContinueRecovery && ImGui.Button("Continue"))
    ImGui.OpenPopup("Continue interrupted operation?");
// ...popup calls _plugin.ResolveContinue()...

if (operationState.CanRestorePreviousState && ImGui.Button("Restore Previous State"))
    ImGui.OpenPopup("Restore to state before the interrupted operation?");
// ...popup calls _plugin.ResolveRestorePreviousState()...
```

**Revised after review (point 12): no new completion observer needed.** The original draft argued the
crude panel needed its own `Kind`-gated completion-detection block since a Continue/Restore-Previous-
State successor is "otherwise indistinguishable from an ordinary user-initiated Apply/Restore." That's
true, but it's not a problem — the successor genuinely *is* an Apply or Restore operation from the
engine's perspective (`Kind` is one of exactly those two values, never a third). Plan C's existing
`Kind`-gated completion-detection blocks on the Apply and Restore tabs already observe it correctly,
the same way they'd observe any other Apply/Restore. Adding a second observer in the recovery panel
would just be a redundant poll of the same state. Once Continue/Restore Previous State starts the
successor, the crude panel's only remaining job is to stop showing the now-resolved recovery UI (which
it already does — `_pendingRecovery` is cleared, so `PublishState()` no longer reports a pending
recovery at all) and let the existing Apply/Restore tab pick up the successor's progress like any other
operation of that type.

## 7. What D2 does not cover

- The real recovery dialog UI (per-mod classification detail, showing *which* mods are `AtSnapshot`
  vs. `AtKnownIntermediate` vs. blocking) — Plan E, same deferral as D1's crude panel.
- Root selection for `MultipleDisconnectedRoots`/`CycleDetected` — still Plan E's job; D2 only adds
  Continue/Restore Previous State for the single-authoritative case, same scope boundary D1 drew for
  its own bulk fallback.
- Diagnostics dump changes — unrelated to this plan's scope.

## 8. Testing

Pure/xUnit-testable:
- `ContinuationPlanner.TryBuildResidualMoves`: one test per classification's residual action
  (`AtIntended`/`AtBoth` produce no move; `AtSnapshot`/`AtKnownIntermediate` produce a move from live
  path to `FinalRawPath`; `AtNeither`/`MissingLive` present anywhere blocks the whole result with
  `Reason == BlockingClassificationPresent`); a genuine `AtKnownIntermediate` collision (two residual
  moves computed to the same `CurrentPath`) produces `Blocked`/`ReplanFailed`, not a thrown exception
  escaping the method; an all-`AtIntended`/`AtBoth` plan produces `Ready` with an empty move list (a
  valid, no-op Continue); a classification whose identifier has no matching `RecoveryTarget` (or whose
  `LiveSnapshot` is missing the identifier despite a non-`MissingLive` state) produces
  `Blocked`/`InconsistentRecoveryTargets` rather than throwing — proves the method is total even though
  this input shape isn't reachable from a correctly-constructed `RecoveryAssessment` today.
- `OperationPlanBuilder.BuildOperationPlan`: existing `BuildRestoreOperationPlan_*` tests renamed and
  updated to pass `OperationType.Restore` explicitly, same assertions; one new test passing
  `OperationType.Apply` to confirm the type parameter is honored, not hardcoded.
- `TryAdvanceClassification`'s revised structure (section 2): a plan-invalid, snapshot-valid pending
  recovery still populates `LiveSnapshot`/`LiveReadStatus.Available` once IPC succeeds, `Assessment`
  stays null, `CanRestorePreviousState` becomes true while `CanContinueRecovery` stays false; a
  plan-invalid pending recovery keeps attempting the live read on subsequent `Update()` calls even
  after `ClassificationStatus` settles to `ClassificationUnavailable` (proves the revised outer gate in
  `Update()` doesn't stop polling once only one of the two statuses has settled); a pending recovery
  where *both* `PlanCheckStatus` and `SnapshotCheckStatus` are invalid never calls `GetLiveMods()` at
  all (proves the "don't read when nothing could use it" optimization).
- `OperationController.ResolveContinue`/`ResolveRestorePreviousState`, using the existing
  `FakePenumbraOperations`/`FakeClock` test doubles and the D1-established `NewControllerWithPendingRecovery`
  helper:
  - Happy path for each: new operation starts, correct `OperationType`, interrupted journal resolved
    with the right `Resolution`/`SuccessorOperationId`, best-effort relocated.
  - Each takes its own fresh `GetLiveMods()` read distinct from whatever `FakePenumbraOperations` was
    configured to return during D1's own classification pass — configure the fake to return a
    *different* live snapshot for the resolution-time call than the classification-time call, and
    assert the resolved plan/snapshot reflect the fresh one, not the cached one (proves point 4's fix).
  - `GetLiveMods()` returning `ProviderUnavailable`/`InvalidData` at resolution time throws, and
    `_pendingRecovery` is untouched (the cached `LiveSnapshot` being `Available` earlier doesn't
    guarantee it still is now).
  - Continue blocked when a fresh classification shows a blocking state even though the *cached*
    `CanContinueRecovery` was `true` (proves the fresh re-derivation, not the cached one, gates the
    actual resolution).
  - Restore Previous State available even when `PlanCheckStatus` is `Invalid` but `SnapshotCheckStatus`
    is `Valid` (proves section 2's decoupling).
  - Both throw when the fresh read's `DuplicateIdentifiers` is non-empty, even when the cached
    `CanContinueRecovery`/`CanRestorePreviousState` was `true` at the time classification last ran
    (proves the fresh duplicate check, not just the cached one).
  - Calling either with no pending recovery throws.
  - `StartOperation`/`StartApply`/`StartRestore` failing after plan/snapshot files are written (inject
    via a plan that fails `CanStartNext`, or an `_active` already populated) leaves `_pendingRecovery`
    unchanged and deletes the partially-written bundle directory (proves point 1 and point 3's fixes
    together); a forced `Directory.Delete` failure (e.g. a locked file) during that cleanup does not
    propagate — the original exception from `StartOperation` still surfaces, not the cleanup failure.
  - The parent-journal-resolution write throwing (simulate via an unwritable `interruptedBundleDirectory`)
    does not propagate out of `ResolveContinue`/`ResolveRestorePreviousState` — the successor is
    already active and the call returns normally (proves point 2's fix); a subsequent
    `OperationBundleDiscovery.RunStartupDiscovery` over that same on-disk state (both journals
    non-terminal) surfaces the successor, not the parent, as authoritative — reusing D1's own graph
    test fixtures rather than re-deriving `OperationRecoveryGraph`'s behavior here.
  - An interrupted Apply's Continue produces an Apply-type successor; an interrupted Restore's Continue
    produces a Restore-type successor; Restore Previous State always produces a Restore-type successor
    regardless of the interrupted operation's own type.
  - Continue with zero residual moves (every classification `AtIntended`/`AtBoth`) starts a zero-step
    successor and that successor reaches terminal correctly, using the same driving sequence
    (`RefreshResult.Success` + empty `LiveModReadResult`, three `Update()` calls) Plan C's own zero-step
    test already established — proves point 8's finding for this specific new call path.
  - Restore Previous State against a target snapshot containing an identifier absent from the fresh
    live read, and a fresh live read containing an identifier absent from the target snapshot, both
    complete successfully and route through `SkippedUninstalledIdentifiers`/`RootRelocatedIdentifiers`
    respectively (not silently dropped) — proves point 7's finding holds through this new call path,
    not just through `RollbackHistory`'s own existing unit tests.
  - `OperationType` values other than `Apply`/`Restore` reaching `OperationPlanBuilder.BuildOperationPlan`
    are rejected (existing `OperationPlan.Create`/`ApplyPlanner` validation, not new code — a
    regression guard, not new behavior).

Not automatable: `Plugin.cs`/`MainWindow.cs` wiring — same documented Dalamud-coupled limitation as
every prior plan.

## 9. Global constraints for the implementation plan

- `dotnet build` must introduce no new warnings/errors beyond whatever the accepted baseline is at
  worktree setup (re-verify then, per established precedent of the baseline needing a fresh check
  every time).
- No automated test may mock `IDalamudPluginInterface`/`IFramework`/Penumbra IPC.
- `PenumbraPathSemantics.AreEquivalent`/`Normalize` for every path comparison in new code (though this
  plan's new code mostly delegates to existing, already-correct comparison logic in
  `RecoveryClassifier`/`ApplyPlanner` rather than doing new comparisons itself).
- `RollbackHistory.BuildRestorePlan`/`CaptureSnapshot`, `ApplyPlanner.OrderMovesForApply`,
  `OperationController.StartApply`/`StartRestore` are out of scope for behavior changes — this plan
  consumes their existing output unchanged. `OperationPlanBuilder.BuildRestoreOperationPlan`'s rename
  to `BuildOperationPlan` is the one sanctioned exception, and it's a pure signature generalization
  with zero behavior change at its existing call site.
