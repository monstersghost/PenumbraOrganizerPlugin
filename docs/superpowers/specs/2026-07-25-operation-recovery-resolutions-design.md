# Operation Recovery Resolutions: Continue and Restore Previous State (Plan D2)

Date: 2026-07-25
Status: Design draft, not yet reviewed
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
rather than silently decided — see §4 for the exact revision.

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

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ContinuationPlanStatus { Ready, Blocked }

public sealed record ContinuationPlanResult(ContinuationPlanStatus Status, IReadOnlyList<NamedModMove> ResidualMoves);

/// <summary>
/// Design doc section 8a/9: computes the residual move set for Continue from an already-built
/// RecoveryAssessment - never re-classifies, never re-reads live state. AtKnownIntermediate's
/// validity is proven by attempting ApplyPlanner.OrderMovesForApply on the full candidate set, the
/// exact operation Continue would actually perform - not by hand-enumerated consistency rules.
/// </summary>
public static class ContinuationPlanner
{
    public static ContinuationPlanResult TryBuildResidualMoves(OperationPlan interruptedPlan, RecoveryAssessment assessment)
    {
        var hasBlockingClassification = assessment.Classifications.Any(c =>
            c.State is ItemRecoveryState.AtNeither or ItemRecoveryState.MissingLive);
        if (hasBlockingClassification)
            return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, []);

        var targetByIdentifier = interruptedPlan.RecoveryTargets.ToDictionary(t => t.Identifier, StringComparer.Ordinal);
        var candidateMoves = new List<NamedModMove>();
        foreach (var classification in assessment.Classifications)
        {
            if (classification.State is not (ItemRecoveryState.AtSnapshot or ItemRecoveryState.AtKnownIntermediate))
                continue; // AtIntended/AtBoth: already at the final target, nothing to queue

            var target = targetByIdentifier[classification.Identifier]; // guaranteed present - Classify iterates interruptedPlan.RecoveryTargets itself
            var live = assessment.LiveSnapshot.Mods[classification.Identifier]; // guaranteed present - MissingLive already excluded above
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
            return new ContinuationPlanResult(ContinuationPlanStatus.Blocked, []);
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

Both follow the original design's identical 8-step durable ordering (§9), reusing D1's
`PendingRecoveryContext` fields directly (never re-classifying, never re-reading live state
independently) and Plan C's `StartApply`/`StartRestore` unchanged:

1. Use the already-built `RecoveryAssessment` (D1) — never re-read live state.
2. Capture a **new** `RollbackSnapshot` from that same live-state generation (`RollbackHistory.CaptureSnapshot`
   on the identical `LiveSnapshot.Mods.Values` already read for classification — no new IPC call).
3. Build and validate the continuation/restore `OperationPlan` (new `OperationId` via `OperationPlan.Create`,
   fresh `ExecutionSteps`/`RecoveryTargets`).
4. Persist the new plan.
5. Persist the new snapshot.
6. Persist the new non-terminal journal via `StartApply`/`StartRestore` itself, with
   `RecoveryOfOperationId` pointing at the interrupted operation.
7. Only now, mark the *original* journal `Resolution = ContinuedByNewOperation`/`RestoredByNewOperation`,
   `SuccessorOperationId = <new operation's id>`.
8. The new operation is already active as of step 6 — nothing further to activate.

**A crash between steps 6 and 7 leaves both journals non-terminal simultaneously** — already tolerated
by the existing `OperationRecoveryGraph.Analyze` (a child's `RecoveryOfOperationId` makes it
authoritative within its component regardless of whether the parent's own resolution write landed;
`OperationBundleRetention`'s transitive-closure retention already accounts for this same relationship).
No new discovery/retention logic needed — D1/A2's existing machinery already handles this shape.

**Availability, replacing D1's placeholder single `CanResolveRecovery` boolean** (D1's own design doc
explicitly flagged this: "D1 temporarily defines this single boolean as 'Keep Current is available' -
D2 will need to split this... since those three have genuinely different availability rules"). New
`OperationStateSnapshot` fields:

```csharp
bool CanContinueRecovery,
bool CanRestorePreviousState,
```

(`CanResolveRecovery` stays as the "Keep Current is available" boolean D1 already defined — Keep
Current's own availability doesn't change in this plan.) Computed in the `_pendingRecovery`-populated
branch of `PublishState()`:

```csharp
CanContinueRecovery = pending.ClassificationStatus == RecoveryClassificationStatus.Classified
    && pending.Assessment is not null
    && ContinuationPlanner.TryBuildResidualMoves(pending.Plan!, pending.Assessment).Status == ContinuationPlanStatus.Ready,
CanRestorePreviousState = pending.SnapshotCheckStatus == ArtifactCheckStatus.Valid
    && pending.LiveSnapshot is not null, // section 2's decoupling - see below
```

Both are `false` in the `_blockedMultiRootGraph` branch and the ordinary `Idle` branch (matching
`CanResolveRecovery`'s existing pattern in both).

**Section 2's decoupling, applied**: `PendingRecoveryContext` gains a new field,
`public LiveModSnapshot? LiveSnapshot { get; set; }`, populated by `TryAdvanceClassification`
independently of plan validity. The method's structure changes from "check plan valid, else
permanently unavailable, else attempt IPC" to: always attempt the (already-throttled) IPC read once
artifact checks complete, regardless of plan validity; store the raw snapshot unconditionally on
success; only additionally compute `Assessment`/`Classifications` when `pending.Plan is not null`.
`ClassificationStatus` semantics are otherwise unchanged (`WaitingForProvider`/`Classified`/
`ClassificationUnavailable` still exist, still gate `RecoveryClassificationPending` the same way) — this
is additive, not a rewrite of D1's state machine.

**A real ordering dependency, resolved before writing the code below (not left implicit)**:
`StartApply`/`StartRestore`'s existing admission guard (`OperationController.cs:126`) throws if
`_pendingRecovery is not null` — added during D1's final review specifically to close a masking hole.
This means `ResolveContinue`/`ResolveRestorePreviousState` cannot call `StartApply`/`StartRestore`
while `_pendingRecovery` is still set, but per the 8-step ordering, the *original* journal's resolution
(step 7) must be persisted *after* the new operation is already active (step 6). So `_pendingRecovery`
must be cleared *before* calling `StartApply`/`StartRestore`, with the original journal's resolution
write happening afterward as its own explicit step — captured in local variables first so clearing
`_pendingRecovery` doesn't lose access to the interrupted journal/bundle directory:

```csharp
public void ResolveContinue()
{
    if (_pendingRecovery is not { Assessment: { } assessment, Plan: { } plan } pending)
        throw new InvalidOperationException("No pending recovery with a valid classification to continue.");

    var result = ContinuationPlanner.TryBuildResidualMoves(plan, assessment);
    if (result.Status != ContinuationPlanStatus.Ready)
        throw new InvalidOperationException("Continue is not available for the current recovery state.");

    var newPlan = OperationPlanBuilder.BuildOperationPlan(plan.Type, result.ResidualMoves);
    var newSnapshot = RollbackHistory.CaptureSnapshot(
        assessment.LiveSnapshot.Mods.Values.ToList(), label: null,
        autoDescription: $"Snapshot before continuing interrupted operation {pending.Journal.OperationId}");

    var newBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, newPlan.OperationId);
    OperationPlanCodec.Save(OperationBundlePaths.PlanPath(newBundleDirectory), newPlan);
    OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(newBundleDirectory), newSnapshot);

    var interruptedJournal = pending.Journal;
    var interruptedBundleDirectory = pending.BundleDirectory;
    _pendingRecovery = null; // clear before StartApply/StartRestore - their admission guard requires this

    if (plan.Type == OperationType.Apply)
        StartApply(newPlan, newSnapshot.Id, newBundleDirectory);
    else
        StartRestore(newPlan, newSnapshot.Id, newBundleDirectory);

    // Step 7: only now, after the new operation is durably active (StartApply/StartRestore already
    // persisted Prepared and Mutating checkpoints before returning), mark the interrupted journal
    // resolved. A crash here leaves both non-terminal - tolerated, see this section's own note above.
    var resolvedInterruptedJournal = interruptedJournal with
    {
        Resolution = OperationResolution.ContinuedByNewOperation,
        SuccessorOperationId = newPlan.OperationId,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
    OperationJournalCodec.Save(OperationBundlePaths.JournalPath(interruptedBundleDirectory), resolvedInterruptedJournal);
    TryRelocateToCompleted(interruptedBundleDirectory, resolvedInterruptedJournal); // best-effort, same rule as Keep Current
}
```

`ResolveRestorePreviousState()` mirrors this exactly, with two differences: it doesn't need
`ContinuationPlanner`/`pending.Plan` at all (confirmed §1's reuse finding), and it always produces a
`Restore`-type plan regardless of the interrupted operation's own type (Restore Previous State means
"go back to the snapshot," independent of whether the interrupted operation was itself an Apply or a
Restore):

```csharp
public void ResolveRestorePreviousState()
{
    if (_pendingRecovery is not { LiveSnapshot: { } liveSnapshot, Snapshot: { } targetSnapshot } pending)
        throw new InvalidOperationException("No pending recovery with a valid snapshot to restore.");

    var currentMods = liveSnapshot.Mods.Values.ToList();
    var restorePlan = RollbackHistory.BuildRestorePlan(targetSnapshot, currentMods);
    var namedMoves = OperationPlanBuilder.BuildNamedMoves(restorePlan.Moves, currentMods);
    var newPlan = OperationPlanBuilder.BuildOperationPlan(OperationType.Restore, namedMoves);
    var newSnapshot = RollbackHistory.CaptureSnapshot(
        currentMods, label: null,
        autoDescription: $"Snapshot before restoring interrupted operation {pending.Journal.OperationId} to its prior state");

    var newBundleDirectory = OperationBundlePaths.BundleDirectory(_operationsRoot, active: true, newPlan.OperationId);
    OperationPlanCodec.Save(OperationBundlePaths.PlanPath(newBundleDirectory), newPlan);
    OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(newBundleDirectory), newSnapshot);

    var interruptedJournal = pending.Journal;
    var interruptedBundleDirectory = pending.BundleDirectory;
    _pendingRecovery = null;

    StartRestore(newPlan, newSnapshot.Id, newBundleDirectory);

    var resolvedInterruptedJournal = interruptedJournal with
    {
        Resolution = OperationResolution.RestoredByNewOperation,
        SuccessorOperationId = newPlan.OperationId,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
    OperationJournalCodec.Save(OperationBundlePaths.JournalPath(interruptedBundleDirectory), resolvedInterruptedJournal);
    TryRelocateToCompleted(interruptedBundleDirectory, resolvedInterruptedJournal);
}
```

**Duplicate live identifiers**: per §9a, both Continue and Restore Previous State should be `Disabled`
when `assessment.LiveSnapshot.DuplicateIdentifiers` is non-empty (only Keep Current tolerates this,
per D1). `CanContinueRecovery`/`CanRestorePreviousState`'s computed expressions above need this check
added explicitly — `pending.LiveSnapshot.DuplicateIdentifiers.Count == 0` as an additional conjunct on
both. Not shown in the sketches above to keep them focused on the primary logic; called out here so it
isn't silently dropped when this becomes the actual implementation.

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

Since Continue/Restore Previous State both start a **new async operation** rather than resolving
synchronously (unlike Keep Current), the panel needs its own completion-detection block — matching the
exact `Kind`-gated pattern Plan C's MainWindow work established for Apply/Restore's own tabs, since a
Continue/Restore-Previous-State operation is otherwise indistinguishable in `OperationController.State`
from an ordinary user-initiated Apply/Restore. This plan's crude panel does not attempt a polished
progress display (that's Plan E's job, same deferral as everywhere else) — just enough to avoid the
panel silently vanishing mid-operation with no feedback, then reappearing confusingly if the new
operation itself hits `RequiresRecovery`.

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
  path to `FinalRawPath`; `AtNeither`/`MissingLive` present anywhere blocks the whole result); a
  genuine `AtKnownIntermediate` collision (two residual moves computed to the same `CurrentPath`)
  produces `Blocked`, not a thrown exception escaping the method; an all-`AtIntended`/`AtBoth` plan
  produces `Ready` with an empty move list (a valid, no-op Continue).
- `OperationPlanBuilder.BuildOperationPlan`: existing `BuildRestoreOperationPlan_*` tests renamed and
  updated to pass `OperationType.Restore` explicitly, same assertions; one new test passing
  `OperationType.Apply` to confirm the type parameter is honored, not hardcoded.
- `OperationController.ResolveContinue`/`ResolveRestorePreviousState`, using the existing
  `FakePenumbraOperations`/`FakeClock` test doubles and the D1-established `NewControllerWithPendingRecovery`
  helper: happy path for each (new operation starts, correct `OperationType`, interrupted journal
  resolved with the right `Resolution`/`SuccessorOperationId`, best-effort relocated); Continue blocked
  when classification shows a blocking state (throws, `_pendingRecovery` untouched); Restore Previous
  State available even when `PlanCheckStatus` is `Invalid` but `SnapshotCheckStatus` is `Valid` (proves
  section 2's decoupling); both disabled when `DuplicateIdentifiers` is non-empty; calling either with
  no pending recovery throws.
- `TryAdvanceClassification`'s revised structure (section 2): a plan-invalid, snapshot-valid pending
  recovery still populates `LiveSnapshot` once IPC succeeds, `Assessment` stays null,
  `CanRestorePreviousState` becomes true while `CanContinueRecovery` stays false.

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
