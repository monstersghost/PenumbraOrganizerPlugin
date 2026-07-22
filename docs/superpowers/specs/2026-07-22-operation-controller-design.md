# Operation Controller, Frame-Budgeted Execution, and Recovery UI

Date: 2026-07-22
Status: Design approved, not yet implemented
Builds on: `docs/superpowers/specs/2026-07-21-incremental-operations-design.md` (original design)
and the merged persistence foundations (`docs/superpowers/plans/2026-07-21-operation-persistence-foundations.md`)

## 1. Scope and relationship to prior work

The persistence foundations plan built a pure, Dalamud-free data layer: `AtomicFile`,
`OperationPlan`/`OperationPlanCodec`, `OperationJournal`/`OperationJournalCodec`,
`RecoveryClassifier`. This design covers everything deferred from that plan: the operation
controller that actually drives Apply/Restore incrementally, frame-budgeted execution against
Dalamud's framework thread, verification settlement, startup crash recovery, recovery resolutions,
and the `MainWindow`/diagnostics wiring that makes all of it visible and operable.

**This design supersedes parts of the original 2026-07-21 design doc.** Where they conflict, this
document is authoritative — in particular, `OperationPlan`'s shape (§3) and `OperationJournal`'s
shape (§6) both change from what shipped in the persistence foundations plan, because building this
controller surfaced a real gap in the original model (see §3).

## 2. File structure and dependency direction

```
MainWindow (UI)
    │  StartApply() / StartRestore() / ResolveRecovery(...) / RequestCancel()
    │  reads: OperationStateSnapshot (immutable) for progress/lockout
    ▼
OperationController                    ◄── Plugin.Framework.Update calls controller.Update()
    │  owns: state machine, exclusivity, journal/checkpoint transitions,
    │        recovery-resolution orchestration, publishing OperationStateSnapshot
    │  delegates bounded work per Update() to:
    ▼
ApplyOperation / RestoreOperation
    │  owns: current execution step, phase, frame-budgeted stepping,
    │        settlement/retry state, per-item execution results
    ▼
IPenumbraOperations (narrow adapter interface)
    ▼
PenumbraOperationsAdapter (implements IPenumbraOperations using existing IPC subscribers)
    ▼
Penumbra IPC
```

`OperationController` depends only on `IPenumbraOperations`, `IElapsedTimeSource`, and the
persistence codecs. It never depends on `MainWindow` or the concrete `Plugin` class.
`Plugin.cs` is the composition root: constructs the adapter, the clock, the controller; wires
`Framework.Update += controller.Update`; unsubscribes on `Dispose()`. It does not implement
`IPenumbraOperations` itself — that stays a dedicated adapter class so unrelated plugin
responsibilities can't leak into the operations layer.

New files under `Organizer/Operations/`:

- `OperationController.cs`
- `ApplyOperation.cs`, `RestoreOperation.cs` (share a common base for the parts that don't differ —
  frame-budget stepping, verification settlement — since both drive the same `ExecutionSteps`/
  `RecoveryTargets` model)
- `IPenumbraOperations.cs`, `PenumbraOperationsAdapter.cs`
- `IElapsedTimeSource.cs`, `StopwatchElapsedTimeSource.cs`
- `VerificationSettlement.cs`
- `RecoveryAssessment.cs`

Modified: `OperationPlan.cs`, `OperationJournal.cs`, `RecoveryClassifier.cs` (schema v2 — see §3, §6, §8).

## 3. OperationPlan v2: ExecutionSteps and RecoveryTargets

**The v1 shape (single `Items` list of `OperationPlanItem`) is insufficient** and is being replaced,
not extended. `ApplyPlanner.OrderMovesForApply` can legitimately emit two steps for the same
identifier — a cycle-breaking temporary hop, then the real target — to resolve a swap/rotation
between mods without deadlocking on Penumbra's shared path-uniqueness namespace. A single
per-identifier item can't represent that, and collapsing to "final target only" makes a crash that
lands a mod at its temporary path misclassify as `AtNeither` during recovery — exactly the case
cycle-breaking exists for.

```csharp
internal enum OperationStepKind { FinalMove, CycleBreakingTemporaryMove }

internal sealed record OperationExecutionStep(
    int StepIndex, string Identifier, string TargetRawPath, OperationStepKind Kind);

internal sealed record OperationRecoveryTarget(
    string Identifier, string SnapshotRawPath, string FinalRawPath, string ModName);

internal sealed record OperationPlan(
    int SchemaVersion,
    Guid OperationId,
    OperationType Type,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OperationExecutionStep> ExecutionSteps,
    IReadOnlyList<OperationRecoveryTarget> RecoveryTargets,
    string IntegrityHash);
```

`ExecutionSteps` is exactly what `Advance()` iterates, duplicates (per identifier) allowed.
`RecoveryTargets` is one entry per identifier — the desired semantic outcome, carrying both the
original snapshot path and the final target so recovery never has to infer the snapshot side from
execution steps.

**Validated invariants**, checked in `OperationPlan.Create(...)` before the integrity hash is
computed (construction throws if any fail — a plan must never be persisted in a state it would
reject on reload, same principle as v1):

- Every execution-step identifier has exactly one recovery target.
- Every recovery target has at least one execution step.
- The final execution step for each identifier targets its `FinalRawPath`.
- A `CycleBreakingTemporaryMove` step is never the last step for its identifier.
- Recovery target identifiers are unique.
- Execution-step indices are contiguous, starting at 0, strictly ordered.

**Integrity hash**: `\0`-delimited (not a plain space, and not the unseparated concatenation v1
shipped with) — `Identifier + '\0' + Normalize(TargetRawPath, ModName)` per execution step, steps
`'\0'`-joined in `StepIndex` order, plus the same construction over `RecoveryTargets` ordered by
`Identifier`. `\0` cannot appear in a mod identifier or a Penumbra virtual path, closing the
theoretical boundary-shift collision the v1 hash had.

**Construction** (shared by Apply and Restore — both already produce a `IReadOnlyList<ModMove>`
before hitting `ApplyPlanner.OrderMovesForApply`, confirmed identical for both call sites):

```csharp
var orderedSteps = ApplyPlanner.OrderMovesForApply(moves); // ApplyStep needs an IsTemporary flag added
var executionSteps = orderedSteps.Select((step, index) => new OperationExecutionStep(
    index, step.Identifier, step.TargetPath,
    step.IsTemporary ? OperationStepKind.CycleBreakingTemporaryMove : OperationStepKind.FinalMove)).ToList();
var recoveryTargets = moves.Select(m => new OperationRecoveryTarget(
    m.Identifier, m.CurrentPath, m.TargetPath, namesByIdentifier[m.Identifier])).ToList();
var plan = OperationPlan.Create(operationType, executionSteps, recoveryTargets);
```

`ApplyPlanner.ApplyStep`/`OrderMovesForApply` need an `IsTemporary` flag added at the source —
do not infer it later from path-naming conventions (the temp path factory already produces an
identifiable-looking name, but that's an implementation detail of path generation, not a contract).

## 4. OperationJournal v2

```csharp
internal enum OperationStage
{
    Preparing, Prepared, Mutating, Refreshing, Verifying,
    Completed, CompletedWithItemFailures,
    FailedBeforeMutation, FailedPartiallyApplied,
    Cancelled,
}

internal enum OperationResolution { None, AcceptedCurrentState, ContinuedByNewOperation, RestoredByNewOperation }

internal sealed record OperationJournal(
    int SchemaVersion,
    Guid OperationId,
    OperationType Type,
    OperationStage Stage,
    OperationResolution Resolution,
    Guid? SuccessorOperationId,      // set only when Resolution is Continued/RestoredByNewOperation
    DateTimeOffset StartedAt,
    int TotalSteps,
    int CompletedStepCount,          // authoritative resume marker — see rationale below
    string? LastCompletedIdentifier, // diagnostic/UI only, never drives resume logic
    Guid SnapshotId,
    Guid PlanId,
    string TargetHash,
    Guid? RecoveryOfOperationId,
    DateTimeOffset UpdatedAt)
{
    public bool IsTerminal =>
        Resolution != OperationResolution.None ||
        Stage is OperationStage.Completed or OperationStage.CompletedWithItemFailures
            or OperationStage.FailedBeforeMutation or OperationStage.FailedPartiallyApplied
            or OperationStage.Cancelled;
}
```

**`CompletedStepCount` replaces `LastCompletedIdentifier` as the authoritative resume marker.**
Once an identifier can appear in `ExecutionSteps` twice (temporary hop, then final move),
"last completed identifier was ModA" can't distinguish step 12 (ModA's temp hop) from step 14
(ModA's final move) in a sequence like: *step 12: ModA→temp, step 13: ModB→ModA's former path,
step 14: ModA→final*. `CompletedStepCount` is unambiguous: `0` means nothing done, the next step is
`ExecutionSteps[CompletedStepCount]`, completion is `CompletedStepCount == ExecutionSteps.Count`, no
`-1` sentinel needed. `LastCompletedIdentifier` is kept for the UI's "currently applying: X" display
and for human-readable diagnostics, but nothing in the recovery/resume path reads it.

**`Stage` and `Resolution` are deliberately separate axes.** `Stage` records what execution actually
did — it can stay frozen at a non-terminal value like `Mutating` forever if the process died there,
and that's the truth, not a bug. `Resolution` records a later human/system decision applied on top
of that frozen record. `IsTerminal` is `Resolution != None` OR `Stage` reached a terminal execution
outcome — either is sufficient, independently. This lets a superseded journal keep an honest,
unmodified historical `Stage` while still being correctly excluded from future recovery prompts via
`Resolution`.

**Persisted enums are strings** (`JsonStringEnumConverter`, per the fix already applied to v1) —
carried forward unchanged, this was already the right call and stays.

## 5. Frame-budgeted execution (`Advance`)

**`IDiagnosticsSink`**: a small logging abstraction (wrapping `IPluginLog`/the existing
`PluginLogAdapter`) that records slow-call events and item-failure detail for §10's diagnostics
dump — introduced in this plan, not carried over from anywhere. Its exact member list is an
implementation detail for the sequencing plan; conceptually it needs `RecordSlowCall(identifier,
duration)` and `RecordSlowLiveSnapshot(duration)` at minimum.

**`SlowCallThreshold`**: provisional constant, `TimeSpan.FromMilliseconds(50)` — picked the same way
`CheckpointPolicy`'s 10-item/500ms and the verification settlement's 10-attempt/100ms were picked:
a defensible starting value with no profiling data yet, cheap to retune once real telemetry exists.
A `SetModPath` call taking 50ms+ is already well outside the 2-4ms frame budget and worth flagging;
this is not claimed to be a tuned value.

```csharp
internal enum TargetMutationStatus
{
    NotAttempted, FinalStepSucceeded, FinalStepFailed, SkippedAfterEarlierFailure, AlreadySatisfied,
}
```

`ApplyOperation`/`RestoreOperation.Advance(TimeSpan budget, bool stopRequested)`:

1. Restart the elapsed-time measurement at entry.
2. Loop: always attempt at least one eligible step. Before starting each *subsequent* step, check
   whether the budget is exhausted **or** `stopRequested` is true — if so, stop; the currently
   in-flight step (if any) always finishes, one `SetModPath` call is never split across two
   `Advance` calls.
3. Record each step's IPC result and its call duration; a duration past the slow-call threshold
   emits a diagnostic event (§9).
4. Update `TargetMutationStatus` for the step's identifier: `FinalStepSucceeded`/`FinalStepFailed`
   on a `FinalMove` step's result; a `CycleBreakingTemporaryMove` failure marks that identifier
   `SkippedAfterEarlierFailure` and its later final-move step is never attempted.
5. Checkpoint the journal (`CheckpointPolicy.IsDue`, unchanged from v1) after each step, using
   `CompletedStepCount`, not identifier.

**IPC failure continuation policy** — item-level failures never stop the batch; only
operation-integrity conditions do:

| Result | Action |
|---|---|
| `Success` | record, continue |
| `ModMissing` | record item failure, continue |
| `InvalidArgument` | record item failure, continue |
| `PathRenameFailed` | record item failure, continue |
| Unexpected exception, IPC boundary still usable | record item failure, continue |
| IPC unavailable/disposed | stop — operation-integrity failure |
| Journal/checkpoint write failure | stop — operation-integrity failure |
| Duplicate live identifiers detected | stop — operation-integrity failure |
| Plan/identifier mapping corrupt | stop — operation-integrity failure |

An item failure must still advance `CompletedStepCount`/the step cursor — it does not retry the same
step forever.

**Exception boundaries**: an outer boundary around the entire `Advance()` call (in
`OperationController.Update()`) catches anything escaping and fails the operation safely rather than
letting it escape the framework update callback; an inner boundary around each individual step
decides, via `CanSafelyContinue(exception)`, whether to record an item failure and continue or stop
the operation entirely.

**Cancellation**: `stopRequested` is only observed between steps (never mid-call), the in-flight
call's result is always recorded, a due checkpoint is persisted, no further step begins, and the
operation still proceeds through `Refreshing`/`Verifying` for whatever succeeded — so the user gets
an accurate per-item report even for a cancelled run. Final `Stage = Cancelled` regardless of
what verification finds; `MutationOccurred` (for display purposes) is derived from
`CompletedStepCount > 0`, not persisted separately. The Stop control is only shown/enabled during
`Mutating` — during `Preparing` there's nothing mid-step to interrupt (abort cleanly, no
refresh/verification needed), and during `Refreshing`/`Verifying` mutation has already stopped, so a
press there can only mean "hide the progress UI," which isn't the same action and isn't offered.

**Honest responsiveness claim** (for the design doc and any user-facing documentation — do not
overstate this): *this eliminates the known whole-library blocking loop and keeps the game
responsive under normal per-item costs; it cannot bound a single pathological IPC call, since the
budget is checked between calls, not during one.* Per-frame runtime is approximately the budget plus
the duration of whatever call was already in flight when the budget was exceeded.

## 6. Verification settlement

Budgeted the same way as mutation — one read-and-compare attempt per `Update()` tick, gated by a
retry interval, never a blocking synchronous wait:

```csharp
internal enum LiveModReadStatus { Success, TemporarilyUnavailable, ProviderUnavailable, InvalidData }
internal sealed record LiveModReadResult(LiveModReadStatus Status, LiveModSnapshot? Snapshot);
internal enum VerificationStepResult { Waiting, Settled, TimedOut, RecoveryRequired }

internal sealed class VerificationSettlement
{
    private int _attemptsUsed;
    private long _lastAttemptTimestamp;
    private const int MaxAttempts = 10; // "attempts", not "retries" — avoids an off-by-one
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

    public VerificationStepResult Advance(
        IPenumbraOperations adapter, IElapsedTimeSource clock,
        IReadOnlyList<OperationRecoveryTarget> targets,
        IReadOnlyDictionary<string, TargetMutationStatus> mutationStatuses,
        IDiagnosticsSink diagnostics)
    {
        if (_attemptsUsed > 0 && clock.GetElapsedTime(_lastAttemptTimestamp) < RetryInterval)
            return VerificationStepResult.Waiting;

        _lastAttemptTimestamp = clock.GetTimestamp();
        _attemptsUsed++;

        var readStart = clock.GetTimestamp();
        var read = adapter.GetLiveMods();
        var readDuration = clock.GetElapsedTime(readStart);
        if (readDuration >= SlowCallThreshold) diagnostics.RecordSlowLiveSnapshot(readDuration);

        if (read.Status is LiveModReadStatus.ProviderUnavailable or LiveModReadStatus.InvalidData)
            return VerificationStepResult.RecoveryRequired;
        if (read.Status == LiveModReadStatus.TemporarilyUnavailable)
            return _attemptsUsed >= MaxAttempts ? VerificationStepResult.RecoveryRequired : VerificationStepResult.Waiting;
        if (read.Snapshot!.DuplicateIdentifiers.Count > 0)
            return VerificationStepResult.RecoveryRequired;

        var expected = targets.Where(t => mutationStatuses[t.Identifier]
            is TargetMutationStatus.FinalStepSucceeded or TargetMutationStatus.AlreadySatisfied);
        var unsettled = expected.Where(t => !IsSettled(t, read.Snapshot)).ToList();

        if (unsettled.Count == 0) return VerificationStepResult.Settled;
        return _attemptsUsed >= MaxAttempts ? VerificationStepResult.TimedOut : VerificationStepResult.Waiting;
    }

    private static bool IsSettled(OperationRecoveryTarget t, LiveModSnapshot live) =>
        live.Mods.TryGetValue(t.Identifier, out var mod) &&
        PenumbraPathSemantics.AreEquivalent(mod.FullPath, t.FinalRawPath, t.ModName);
}
```

Only targets whose `TargetMutationStatus` is `FinalStepSucceeded` or `AlreadySatisfied` are expected
to settle — an item already recorded as failed during Mutating isn't waited on.
`IsSettled` checks the live path directly; there is no separate "snapshot equals target" shortcut,
because that check never inspected live state at all and would false-positive for a missing or
misplaced mod.

**Outcome mapping**:

| Condition | Outcome |
|---|---|
| All required targets settle, no item failures | `Completed` |
| All required targets settle, some item failures during Mutating | `CompletedWithItemFailures` |
| Some uniquely-identifiable targets time out | `CompletedWithItemFailures` (reason: `VerificationTimeout`) |
| Duplicate identifiers / unreadable live snapshot / provider unavailable | `RecoveryRequired` — journal stays non-terminal |
| Journal/plan durability fails during verification | `FailedPartiallyApplied` or `FailedBeforeMutation`, per whether mutation occurred |

A verification timeout is item-level (the operation still concludes with a clear report); an
inability to trust the live-state read at all is operation-level (the journal is left recoverable
rather than asserting a terminal outcome it can't actually support).

**Persistence discipline**: only `DateTimeOffset.UtcNow` wall-clock values go in the journal.
`Stopwatch`/`GetTimestamp()` values are process-relative and meaningless after a restart — they stay
in-process only, used solely for in-process interval decisions. Checkpoint on entering `Verifying`,
on each actual attempt (not on every `Waiting` tick that didn't yet reach the retry interval), and on
the terminal result.

## 7. Non-reentrancy and conflicting-control lockout

`OperationController` holds a single `_active` field; `StartApply`/`StartRestore` reject if it's
already set. **Scan, Index, Folder Cleanup, and Folder Cleanup Rollback stay outside the controller
in this plan** — their own incremental treatment is explicitly deferred (original design §12/§16,
pending profiling) — but each gains a guard so they can't run concurrently with an active
Apply/Restore: `if (!snapshot.CanScan) return;` (non-throwing — see §7a).

**"Rollback" and "Restore" are confirmed to be two unrelated features** (`RollbackFolderCleanup`
undoes `organization.json` folder structure from its own backup file via `FolderCleanupExecutor`;
`Restore` moves mod paths via `SetModPath`/`RollbackHistory`) — no naming collision to resolve, no
shared controller ownership needed; Folder Cleanup Rollback gets its own `CanRunFolderCleanupRollback`
guard, not `StartRestore`.

### 7a. Non-throwing UI guards

Every `MainWindow` entry point checks a capability flag and returns early — it does not throw.
Throwing for an expected user action (a stale button click, or a future code path) risks an
unnecessary Dalamud error boundary:

```csharp
private void OnApplyClicked()
{
    if (!_plugin.OperationController.State.CanStartApply) return; // button should already be disabled
    _plugin.OperationController.StartApply(...);
}
```

## 8. RecoveryClassifier v2

```csharp
internal enum ItemRecoveryState
{
    AtSnapshot, AtIntended, AtBoth, AtKnownIntermediate, AtNeither,
    MissingLive, MissingSnapshot, MissingPlan,
}
```

`AtBoth`: snapshot and final target are equivalent (a no-op move) — checked and matched against
live state directly (not inferred), same fix as `IsSettled` above.
`AtKnownIntermediate`: live path matches a `CycleBreakingTemporaryMove` step's target for this
identifier. This state alone does **not** mean safe-to-continue — it's cross-checked in §8a.

Artifact-level degradation is tracked separately from per-item state, since a missing snapshot file
is not a property of any individual mod:

```csharp
internal enum ArtifactStatus { Valid, PlanMissing, PlanInvalid, SnapshotMissing, SnapshotInvalid, JournalInvalid }
```

### 8a. RecoveryAssessment — one atomic read feeding both classification and replanning

```csharp
internal sealed record RecoveryAssessment(
    LiveModSnapshot LiveSnapshot, IReadOnlyList<ItemRecoveryClassification> Classifications, string SnapshotGenerationHash);
```

Built from a single `GetLiveMods()` call. Both the recovery dialog's classification and Continue
Apply's residual-plan construction read from the *same* `RecoveryAssessment` — never two independent
live reads, which could disagree if the library changed between them.

**`AtKnownIntermediate` validity is proven by attempting to replan, not by hand-enumerated rules.**
The residual move set for Continue is built from *current live paths* to *original final targets*
(`new ModMove(id, live.Mods[id].FullPath, target.FinalRawPath)` — never from the original snapshot
path, which for an `AtKnownIntermediate` target is stale), then run through
`ApplyPlanner.OrderMovesForApply` again. If that succeeds, the intermediate state was consistent; if
it fails, Continue is blocked for those identifiers. This is a stronger check than trying to
enumerate consistency conditions by hand, because it's the exact operation Continue would actually
perform — replanning against reality is the real test of whether reality is resumable.

## 9. Recovery resolutions and startup wiring

**Startup** (`Plugin.cs` constructor): load durable artifacts (journal/plan/snapshot) synchronously —
this is just file I/O, safe at construction time. **Do not call Penumbra IPC for live classification
in the constructor** — the provider may not be ready yet. If a non-terminal journal is found, enter
`RecoveryRequiredPendingClassification`; `MainWindow` shows *"Interrupted operation detected. Waiting
for Penumbra state to become available…"*. Once `Framework.Update` confirms IPC is available,
attempt the `RecoveryAssessment` read and publish the real `RecoveryRequired` state with
classification results. This avoids treating normal startup ordering as a false indeterminate.

**Continue Apply** — durable ordering, crash-safe in both directions:

1. Build `RecoveryAssessment` (read + classify).
2. Capture a **new** `RollbackSnapshot` from that same live-state generation.
3. Build and validate the continuation `OperationPlan` (new `OperationId`, fresh `ExecutionSteps`
   from replanning residual moves, fresh `RecoveryTargets`).
4. Persist the new plan.
5. Persist the new snapshot.
6. Persist the new non-terminal journal, `RecoveryOfOperationId` pointing at the interrupted operation.
7. Only now, mark the original journal `Resolution = ContinuedByNewOperation`,
   `SuccessorOperationId = <new operation's id>`.
8. Activate the new operation.

A crash between steps 6 and 7 leaves both journals non-terminal simultaneously — this is expected
and must be tolerated: **a valid non-terminal journal whose `RecoveryOfOperationId` references
another journal supersedes that parent operationally**, even if the parent's own terminalization
(step 7) never completed. Startup recovery detection follows this chain and treats the newest child
as authoritative rather than presenting two separate recovery prompts. Restore Previous State follows
the identical ordering with `Resolution = RestoredByNewOperation`.

Per-target residual rule for Continue:

| Classification | Residual action |
|---|---|
| `AtIntended`, `AtBoth` | skip |
| `AtSnapshot` | queue (from current live path — see §8a) |
| `AtKnownIntermediate` | queue if replanning succeeds; otherwise treated as blocking |
| `AtNeither`, `MissingLive`, `MissingSnapshot` | block Continue; dialog offers View Details instead |

**Keep Current State**: `Resolution = AcceptedCurrentState` on the interrupted journal, archive its
plan, preserve its snapshot for the retention window, trigger a fresh `RunScan()`.

### 9a. Recovery dialog option availability

Not all three choices are always enabled — availability depends on artifact/classification state:

| Condition | Continue | Restore | Keep Current |
|---|---|---|---|
| Plan and classification valid, no blockers | Enabled | Enabled if snapshot valid | Enabled |
| Plan invalid | Disabled | Enabled if snapshot usable | Enabled |
| Snapshot missing | Enabled if plan valid | Disabled | Enabled |
| Duplicate live identifiers | Disabled | Disabled | Enabled, with a warning |
| IPC unavailable | Pending (not yet decidable) | Pending | Pending — can't scan current state to accept it |
| Any `AtNeither` present | Disabled | Enabled if snapshot valid | Enabled |

Keep Current remains available in most degraded cases, but the dialog makes explicit that it accepts
an incompletely-understood live state when classification itself was degraded.

## 10. Diagnostics and retention

**Diagnostics dump** stops relying solely on `Config.LastApply`/`LastRestore`:

- A non-terminal journal at dump time produces an explicit section regardless of the config summary:
  last `Stage`, N/M steps, last checkpoint time, count of recorded slow calls — this is the exact
  case the original bug report needed and the old dump couldn't show.
- Recent terminal journals (from `completed/`, not just the single rolling summary) are listed with
  `Stage`/`Resolution`/timing.
- Slow-call events get their own section (count past threshold, worst offenders by identifier) — the
  concrete evidence needed to distinguish "this machine's storage is slow" from "something hung."

**Retention/cleanup**: runs once, synchronously, at plugin construction (after recovery detection,
non-blocking to it) — a directory listing and a handful of deletes needs no incremental treatment.
Active non-terminal journal: retained indefinitely. Terminal journals/plans: 30 days or 50-operation
cap, whichever is hit first. A snapshot is never deleted while any non-terminal journal's
`RecoveryOfOperationId` chain (walked transitively, not just direct references) still points to it.

## 11. Small deferred items folded into this plan

- `AtomicFile.TryReadValidated` gains an `IOException` catch (locked file), returning `false` —
  matches the `Try` contract callers already assume.
- `OperationPlanItem.OriginalRawPath`'s "document its future purpose" gap is moot — that record no
  longer exists; `RecoveryTargets.SnapshotRawPath`/`FinalRawPath` are explicit, load-bearing fields
  from the start.
- The missing `AtBoth`-with-live-at-neither-location test case is added as part of rewriting the
  classifier for the v2 state set, not as a separate follow-up.
- The `\`-vs-`/` test literal fix (persistence foundations' Task 2 and Task 5 tests) ships as its
  own small, independent task — unrelated to everything else in this plan, no reason to entangle it.

## 12. What this design does not cover

- Scan/Index's own incremental split — still gated on profiling, per the original design.
- Off-thread IPC — still pending verification of Penumbra's actual thread-affinity guarantees.
- Penumbra 1.6 vs 1.7 compatibility comparison — still requires a crash log from a session that
  actually crashed.

## 13. Implementation sequencing (for the follow-on plan(s))

Expected to split into multiple sequenced implementation plans, mirroring how the persistence
foundations work was split from this:

1. Schema v2 migration: `OperationPlan`/`OperationJournal`/`RecoveryClassifier` rewrite, adapter
   interface + `PenumbraOperationsAdapter`, `IElapsedTimeSource`, duplicate-identifier guard,
   `ApplyPlanner.ApplyStep.IsTemporary`, hash delimiter fix, `AtomicFile` `IOException` catch,
   `\`-vs-`/` test literal fix.
2. `OperationController` + `ApplyOperation` + frame-budgeted execution + verification settlement,
   wired to Apply only.
3. `RestoreOperation` reusing the same controller/execution machinery.
4. Recovery classification, `RecoveryAssessment`, startup wiring, the three resolutions, retention
   cleanup.
5. `MainWindow` UI wiring (progress display, capability-gated buttons, Stop control, recovery
   dialog) and diagnostics dump changes.
