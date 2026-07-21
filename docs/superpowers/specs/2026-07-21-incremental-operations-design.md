# Incremental Operations, Operation Journal, and Crash Recovery

Date: 2026-07-21
Status: Design approved (revision 2), not yet implemented
Plugin version at time of writing: 0.4.0

## 1. Problem

Every organizer operation runs synchronously on the framework/UI thread.

Confirmed by source inspection:

- `ApplyChanges`/`Restore` drive `SetModPathIpc.Invoke(...)` in a straight loop, one call per mod
  (`Plugin.cs:515`). A 401-mod library means 401 blocking IPC calls with no yield.
- Scan calls `GetChangedItemAdapterDictionary().Invoke()` and `GetModListAdapter().Invoke()`
  synchronously (`Plugin.cs:110`, `Plugin.cs:112`).
- All of this is invoked directly from ImGui button handlers in `MainWindow`. The only `Task` in
  the plugin is the NPC wiki refresh (`Plugin.cs:266`).

This produces four distinct problems:

1. **The game freezes** for the whole duration of any substantial operation. ImGui cannot redraw
   while the calling thread is inside the loop, so no progress can be displayed and the game
   appears hung.
2. **Disconnect and watchdog risk.** Users on HDDs or with large libraries stall far longer than
   users on SSDs. A long enough stall risks a server disconnect or a framework-thread watchdog kill.
3. **Partial state is unrecoverable.** If the process terminates mid-operation, all progress
   tracking lives in memory and is lost. The next session cannot tell whether the operation
   completed, partially completed, or never began mutating.
4. **Diagnostics report no fault.** The current diagnostic dump reports zero faults even when a
   session ended abnormally, because nothing durable records that an operation was in flight.

### Non-goals and open questions

This design does **not** claim that framework-thread blocking causes the reported process
terminations. That remains an unverified hypothesis; the crash evidence available so far is a
Dalamud log from a different session that contains no organizer operations at all. The work here is
justified independently by problems 1–4, and it produces the stage boundaries and durable records
needed to actually diagnose the crash.

Penumbra IPC thread affinity is **undocumented**. The string "thread" does not appear anywhere in
the Penumbra.Api 5.15.1 XML documentation, and `SetModPath` documents only its arguments and its
return codes (`InvalidArgument`, `ModMissing`, `PathRenameFailed`, `Success`). This design therefore
keeps every IPC call on the thread it runs on today. See section 12.

## 2. Operation controller

A single controller owns all long-running organizer operations.

### States

```
Idle
Preparing
Prepared
Mutating
Refreshing
Verifying
Completed
CompletedWithItemFailures
FailedBeforeMutation
FailedPartiallyApplied
Interrupted
RecoveryRequired
Recovering
AcceptedCurrentState
```

`Prepared` is distinct from `Mutating` so that a crash after artifacts are committed but before the
first `SetModPath` is classifiable without implying mutation occurred.

`Interrupted` is normally **inferred at startup** from a persisted non-terminal journal rather than
written at crash time. See section 13.

Failure is split by whether mutation had begun: `FailedBeforeMutation` needs no recovery,
`FailedPartiallyApplied` does. `CompletedWithItemFailures` covers a run that reached the end of the
work list with one or more non-`Success` IPC results.

Stage names are user-visible and recorded in the journal and diagnostics, so the last recorded stage
identifies where an abnormal termination occurred.

### Invariants

- **One operation at a time.** Starting an operation while one is active is rejected, not queued.
- **Conflicting controls are disabled** while an operation is active: Scan, Apply, Restore, Index,
  folder cleanup, and rollback.
- **The work list is immutable** once constructed. Underlying rows and proposed paths cannot change
  mid-operation.
- **Closing the plugin window hides the UI only.** It never disposes or cancels an active operation.
- **Plugin disposal and game shutdown do not mark the operation completed.**

### Cancellation

Apply and Restore are **not cancellable once the `Mutating` stage begins**. A cancel that stops
midway leaves Penumbra partially reorganized, and rollback-on-cancel introduces a second mutation
path that can fail in the same ways as the first. Cancellation during `Preparing` is permitted
because no mutation has occurred.

## 3. Persistence ordering and the operation plan

Recovery depends on both the rollback snapshot and the operation plan. Neither may be referenced by
a journal before it is durably committed and verified re-readable.

```
1. Validate
2. Capture and persist rollback snapshot
3. Construct operation plan
4. Persist operation plan atomically
5. Verify that snapshot and plan can be reopened and pass integrity checks
6. Persist journal as Prepared
7. Persist journal as Mutating
8. Begin mutation
```

If step 5 fails, the operation aborts in `FailedBeforeMutation` and no journal is written.

### Operation plan contents

```
Operation ID
Operation type
Creation timestamp
Plan format/schema version
Integrity hash
Ordered list of:
  - mod identifier
  - original normalized path
  - intended normalized path
  - original raw path (display)
  - intended raw path (display)
```

The full ordered target list is required: without it, "Continue Apply" cannot be reconstructed.

The plan is immutable once written. Resumption creates a new plan (section 8), never edits this one.

## 4. Frame-budgeted execution

Mutation is spread across frames from a framework update handler rather than run in one loop.

```
Each frame, while work remains:
  - always process at least one item
  - before each subsequent item, check elapsed time
  - stop when approximately 2-4 ms has elapsed this frame
```

A **time budget, not a fixed item count.** Per-call cost varies with machine, storage medium,
Penumbra state, and library size; a fixed "N per frame" either stalls slow machines or needlessly
throttles fast ones.

### Over-budget behavior

The budget limits *additional* calls after it is consumed. It cannot cap the duration of one opaque
IPC call, and no attempt is made to interrupt a call in flight. Therefore:

- Elapsed time is checked *before* beginning the next item, never mid-call.
- At least one item is processed per frame whenever work remains, guaranteeing forward progress even
  if a single call exceeds the entire budget.
- Individual call duration is recorded, and a call exceeding a slow-call threshold emits a
  diagnostic event.

The budget is an internal constant initially, not user-configurable.

### IPC failure continuation policy

Every `SetModPath` return code has a defined action:

| Result | Action |
| --- | --- |
| `Success` | Record, continue |
| `InvalidArgument` | **Stop immediately.** Indicates a plan or validation defect |
| `ModMissing` | Record failure, continue. Later items are independent under an identifier-based plan |
| `PathRenameFailed` | **Stop by default.** May indicate filesystem or collision state affecting subsequent items |
| Unexpected exception | Stop mutation immediately, enter partial-failure handling |

These mappings are provisional and must be confirmed against Penumbra's provider implementation
before implementation. The requirement the spec fixes now is that the policy is explicit and total,
not that these specific choices are final.

Stopping mid-list transitions to `FailedPartiallyApplied` and proceeds to refresh and verification
so the observed state is classified rather than assumed.

**"Full success" is defined as final-state verification matching the plan.** IPC results and
verification can disagree; verification is authoritative for outcome, IPC results are authoritative
for what the API reported. A run where every call returned `Success` but verification finds
mismatches is not a success.

## 5. Result recording

Three distinct records with distinct durability, so no durable per-item knowledge is promised that
the design does not actually persist:

| Record | When written | Durability |
| --- | --- | --- |
| In-memory item result | After every IPC call | Lost on process termination |
| Structured diagnostic event | Appended, flushed periodically | Survives if flush completed |
| Journal checkpoint | Periodic aggregate (section 6) | Survives to last checkpoint |

**Recovery truth is never derived from any of these.** It is derived from live state compared
against the snapshot and plan (section 7). The records above serve diagnostics, not classification.

## 6. Operation journal

A small, plugin-owned file written before mutation begins and checkpointed during it.

```json
{
  "operationId": "guid",
  "type": "Apply",
  "status": "Mutating",
  "startedAt": "2026-07-21T13:27:44Z",
  "totalItems": 401,
  "completedItems": 173,
  "lastCompletedIdentifier": "SomeModIdentifier",
  "snapshotId": "guid",
  "planId": "guid",
  "targetHash": "...",
  "recoveryOfOperationId": null,
  "updatedAt": "2026-07-21T13:28:02Z"
}
```

The journal deliberately does **not** contain the work list or the mod paths. Those live in the
rollback snapshot and the operation plan. The journal carries only enough to identify and recover
the interrupted operation, so it stays small enough to rewrite frequently.

`targetHash` is computed over **normalized** paths (section 7), not raw strings.

### Progress fields are hints, not authority

`completedItems` and `lastCompletedIdentifier` are **progress hints only**. `lastCompletedIdentifier`
means "last checkpointed sequential success" — it does not describe the set of mods touched, because
checkpointing lags mutation, failed calls may be followed by successful ones, and a single value
cannot represent a set.

Processing is strictly sequential over the plan order with no skips; this is a requirement on the
implementation, and even under it the field retains only the meaning stated above.

Authority ordering:

1. **Operation plan** — the ordered attempted work
2. **Live normalized state** — the current outcome
3. **Diagnostic event log** — observed calls and return codes, where flushed
4. **Journal progress fields** — hints for display and triage only

### Checkpoint cadence

Do not persist after every successful `SetModPath` — on a large library that is hundreds of
filesystem writes and becomes its own performance problem on HDDs. Checkpoint on whichever comes
first:

- every 10–25 completed mutations, or
- every 500–1000 ms,

plus a forced write immediately on entering each stage.

A hard termination may therefore lose the last few progress updates. This is acceptable precisely
because recovery does not treat progress fields as authoritative.

### Separation of concerns

| Artifact | Responsibility |
| --- | --- |
| Rollback snapshot | What the state was before the operation |
| Operation plan | What the operation intended to produce |
| Operation journal | What operation was active and how far observed execution progressed |
| Diagnostics log | Detailed timing, return codes, failures, stages |

## 7. Recovery

On plugin load, if a journal exists with a non-terminal status, the controller enters
`RecoveryRequired`.

1. Load the referenced snapshot and operation plan; fail to `Indeterminate` if either is unreadable
   or fails its integrity check.
2. Read current live mod paths from Penumbra.
3. Classify every planned identifier.
4. Derive the operation-level outcome.

### Path comparison must use PenumbraPathSemantics

This is a correctness requirement specific to this codebase, not a detail.

Per `Organizer/PenumbraPathSemantics.cs` (ground truth verified against Luna source 2026-07-19), a
`" (N)"` suffix on a leaf whose duplicate-marker base equals the mod's display name is **discarded
on save** and **reassigned in arbitrary enumeration order on every load**. Two paths differing only
by such a suffix are the same persisted location.

String equality during recovery would classify mods as differing from both the original and the
intended state purely because Penumbra reshuffled transient tie-breaker suffixes between the crash
and the next launch. On a library with collisions this fills the indeterminate bucket with false
positives and pushes users toward an unnecessary Restore — the exact outcome this design exists to
prevent.

**All three comparison legs use `PenumbraPathSemantics` equivalence:** live vs. snapshot, live vs.
target, snapshot vs. target. `targetHash` is likewise computed over normalized paths.

### Per-identifier classification

```
AtOriginal      live matches snapshot, not target
AtTarget        live matches target, not snapshot
AtBoth          snapshot and target normalize to the same location
AtNeither       live matches neither
MissingLive     in plan, absent from live state
MissingSnapshot in plan, absent from snapshot
MissingPlan     present live and in snapshot, absent from plan
```

`AtBoth` items are no-ops and must not inflate either the applied or the unchanged count.

### Operation-level outcomes

Evaluated over *changed* items — those where snapshot and target differ, i.e. excluding `AtBoth`:

| Outcome | Rule |
| --- | --- |
| No mutations detected | All changed items are `AtOriginal` |
| Completed but not finalized | All changed items are `AtTarget` |
| Partially applied | At least one `AtOriginal`, at least one `AtTarget`, none `AtNeither` |
| Indeterminate | Any relevant item is `AtNeither`, unexpectedly missing, or structurally inconsistent |

### Recovery dialog

```
The previous Apply operation was interrupted.

Current state:
173 of 401 intended paths appear applied.
228 paths remain unchanged.
0 paths differ from both the original and intended state.

Choose:
- Continue Apply
- Restore Previous State
- Keep Current State
- View Details
```

The dialog reports observed state and offers four choices rather than assuming failure and pushing
rollback. If the operation had in fact completed and the process died during refresh or
verification, the user must not be told it failed.

## 8. Recovery resolutions

Each resolution is an auditable transition, never a silent dismissal. All three mark the interrupted
journal terminal and prevent the dialog from reappearing for that operation.

### Continue Apply

Does **not** resume from `completedItems + 1`. A residual plan is rebuilt from live state:

- `AtTarget` — skip
- `AtOriginal` — queue
- `AtNeither` — block Continue unless explicitly resolved by the user
- Missing — block or exclude per defined policy

Continuation creates a **new operation** with a new operation ID, a new plan, and a **new rollback
snapshot capturing the current partial state**. Without the new snapshot, a failure during the
resumed Apply would leave only the pre-first-attempt snapshot, which is not necessarily the safest
undo point.

The new journal records `recoveryOfOperationId` pointing at the interrupted operation, preserving an
auditable chain and keeping journal lifecycle simple.

### Restore Previous State

Creates a new restore operation with its own plan and journal, linked by `recoveryOfOperationId`. It
never mutates under the old interrupted journal.

### Keep Current State

Marks the interrupted journal `AcceptedCurrentState`, and:

- records that the user accepted the observed partial state
- preserves the original snapshot for the retention period
- archives the old operation plan
- triggers a fresh Scan / reload of organizer state
- prevents the dialog from reappearing

## 9. Atomic persistence helper

Two operations, because reading safely matters as much as writing safely:

```
AtomicCreateOrReplace
AtomicReadValidated
```

Implementation requirements:

- temp file in the **same directory** as the destination
- serialized bytes fully written before replacement
- durable flush (`Flush(true)` or equivalent) where appropriate
- **first-write behavior when no destination exists** — `File.Replace` does not behave identically
  for creation and replacement, and must not be assumed to
- defined Windows replacement semantics
- cleanup of orphaned temp files
- defined recovery when both destination and temp exist
- checksum/schema-version validation on read
- defined backup behavior if replacement itself fails

## 10. Framework update handler lifecycle

The update callback is where an escaping exception would recreate the exact class of failure this
design exists to diagnose. It must therefore have a top-level exception boundary that:

1. records stage and current item
2. stops further queue processing
3. checkpoints the journal when possible
4. transitions to a failure state
5. never marks success

Also required: subscribed exactly once, removed on plugin disposal, guarded against reentrancy, with
defined behavior when a UI action and an update tick touch controller state concurrently.

## 11. Disposal

Disposal must not be relied on to write a final `Interrupted` checkpoint. During a crash or hard
shutdown it may never run, or may not have time.

**The persisted non-terminal checkpoint is itself sufficient evidence of interruption.** Any
dispose-time write is best effort only.

Disposal must not start rollback, refresh, verification, or substantial synchronous work.

## 12. Scan

Scan is a different problem from Apply/Restore and gets a different architecture:

- Penumbra IPC reads stay on their current thread.
- Returned data is copied into plugin-owned immutable structures.
- Pure classification, path analysis, indexing, and model construction run on a worker task.
- The completed result is published back on the framework thread.

### Worker isolation

The background task must **not** receive live Penumbra collections, mutable plugin UI models, IPC
wrappers, or references to services requiring framework-thread access.

### Stale result handling

The library can change while Scan runs.

```
Capture generation N
Process generation N
Publish only if N is still the active generation
```

This holds even though Scan cannot currently overlap another operation — it prevents a superseded or
disposed task from publishing later.

### Profiling gate

Measure which phase consumes the time before designing the progress model. Progress must correspond
to real phases, not invented percentages. If a single opaque IPC call dominates, the correct outcome
is a stage label and an indeterminate spinner, not a fabricated percentage.

## 13. Verification

Verification runs after refresh and requires a bounded settlement window, because observable state
may lag the final mutation and a single immediate read could falsely report failure.

```
After refresh:
1. Read live state
2. Compare every planned identifier under normalized equivalence
3. If mismatches exist, yield and retry within a bounded count or duration
4. Classify only after the verification window expires
```

Contract:

- **Every mod in the plan** is re-read. Unrelated mods are not checked, except that identifiers
  present live but absent from the plan are classified `MissingPlan` for diagnostics.
- Mods missing from live state are `MissingLive` and count toward `Indeterminate`, not `Failed`.
- Normalized equivalence alone is sufficient for a match.
- Duplicate identifiers or a library changed mid-operation force `Indeterminate`.

Retry bounds are to be set from measured Penumbra refresh behavior; the requirement fixed now is
that a bounded window exists.

## 14. Diagnostics

1. **All managed exceptions crossing organizer-controlled operation boundaries are captured and
   recorded where process state still permits.** This guarantee cannot extend to native crashes,
   process termination, stack overflow, fail-fast, or power loss.
2. **Non-terminal journals provide evidence of abnormal termination even when no exception was
   captured.** This is the stronger diagnostic guarantee and does not depend on managed exception
   handling running at all.
3. Operation name and current stage are recorded.
4. Relevant paths, counts, IPC return codes, and timing are recorded.
5. Fatal and non-fatal failures are distinguished.
6. Partial completion is reported clearly.
7. **Diagnostics never report "0 faults" when a journal exists in a non-terminal state.**

Because remote users cannot be relied on to locate the correct Dalamud log — the log supplied with
the initial report was from a different session and contained no organizer operations — the
diagnostic dump must be self-sufficient.

## 15. Storage layout and retention

```
%LocalAppData%\PenumbraOrganizer\Operations\
    active-operation.json
    plans\
        {operationId}.json
    completed\
        {operationId}.json
```

Rollback snapshots remain in the existing history location.

Retention:

- active non-terminal journal: **retain indefinitely until resolved**
- completed/failed/accepted journals: 30 days
- operation plans: same period as their journal
- rollback snapshots: existing history policy, but **never delete one still referenced by an
  unresolved journal**
- completed operation records capped by both age and count (e.g. 30 days or 50 operations)

The unresolved-reference protection rule matters more than the exact durations.

## 16. Deferred

- **Off-thread IPC.** Pending verification of thread affinity against the Penumbra provider
  implementation in `xivdev/Penumbra`. Until verified, calls stay on their current thread and this
  design yields between batches rather than relocating work.
- **Index rework.** Same incremental treatment, after profiling its phases.
- **Penumbra 1.6 vs. 1.7 comparison.** Requires the Penumbra version from a session that actually
  crashed. The available log shows 1.6.1.10 but covers no organizer operation.

## 17. Implementation phases

Split so that a conditional Scan design does not contaminate the concrete Apply/Restore plan.

**Phase A** — shared atomic persistence, operation controller, plan/journal, frame-budgeted
Apply/Restore, recovery and resolutions, progress UI and control lockout, diagnostics.

**Phase B** — Scan profiling spike.

**Phase C** — concrete Scan implementation derived from Phase B measurements.

**Phase D** — Index profiling and rework.
