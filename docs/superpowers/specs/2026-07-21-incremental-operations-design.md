# Incremental Operations, Operation Journal, and Crash Recovery

Date: 2026-07-21
Status: Design approved, not yet implemented
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
keeps every IPC call on the thread it runs on today. See section 8.

## 2. Operation controller

A single controller owns all long-running organizer operations. It is a state machine with explicit
stages:

```
Idle -> Preparing -> Mutating -> Refreshing -> Verifying -> Completed
                                                         -> Failed
```

Stage names are user-visible and are recorded in the journal and diagnostics, so the last recorded
stage identifies where an abnormal termination occurred.

### Invariants

- **One operation at a time.** Starting an operation while one is active is rejected, not queued.
- **Conflicting controls are disabled** while an operation is active: Scan, Apply, Restore, Index,
  folder cleanup, and rollback.
- **The work list is immutable** once constructed. Underlying rows and proposed paths cannot change
  mid-operation.
- **Closing the plugin window hides the UI only.** It never disposes or cancels an active operation.
- **Plugin disposal and game shutdown do not mark the operation completed.** The journal is left in
  its last checkpointed non-terminal state, which is exactly what recovery looks for.

### Cancellation

Apply and Restore are **not cancellable once the `Mutating` stage begins**. A cancel that stops
midway leaves Penumbra partially reorganized, and rollback-on-cancel introduces a second mutation
path that can fail in the same ways as the first. Cancellation during `Preparing` is permitted
because no mutation has occurred.

## 3. Frame-budgeted execution

Mutation is spread across frames from a framework update handler rather than run in one loop.

```
Each frame, process queued items until either:
  - the queue is empty, or
  - approximately 2-4 ms of wall-clock time has elapsed this frame
```

A **time budget, not a fixed item count.** Per-call cost varies with machine, storage medium,
Penumbra state, and library size; a fixed "N per frame" either stalls slow machines or needlessly
throttles fast ones.

### Transaction sequence

```
1. Validate all proposed paths
2. Capture the rollback snapshot
3. Construct the immutable operation plan
4. Persist journal as Prepared
5. Mark journal as Mutating
6. Process the work list incrementally, recording each successful mutation
7. Refresh/reload Penumbra state once, after the queue finishes
8. Verify the resulting paths
9. Classify and report: full success, partial completion, or failure
```

Steps 4 and 5 must both precede the first `SetModPath` call. Persisting the journal only after
mutation begins would leave a crash in the first few calls with changed paths and no recovery
marker.

Per-item recording captures the mod identifier and the IPC return code. This is what makes
"did Apply complete before the crash?" answerable.

## 4. Operation journal

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
  "targetHash": "...",
  "updatedAt": "2026-07-21T13:28:02Z"
}
```

The journal deliberately does **not** contain the work list or the mod paths. Those live in the
rollback snapshot and the operation plan. The journal carries only enough to identify and recover
the interrupted operation, so it stays small enough to rewrite frequently.

`targetHash` is computed over **normalized** paths (see section 5), not raw strings.

### Checkpoint cadence

Do not persist after every successful `SetModPath` — on a large library that is hundreds of
filesystem writes and becomes its own performance problem on HDDs. Checkpoint on whichever comes
first:

- every 10–25 completed mutations, or
- every 500–1000 ms,

plus a forced write immediately on entering each of `Preparing`, `Mutating`, `Refreshing`,
`Verifying`, `Completed`, `Failed`.

A hard termination may therefore lose the last few progress updates. This is acceptable because
recovery does not treat `completedItems` as authoritative — see section 5.

### Atomicity and lifecycle

Journal writes use atomic temp-write-flush-replace. This applies to initial creation, checkpoint
updates, and terminal status updates.

On successful completion the journal is **marked** `Completed`, not deleted. It is retained briefly
or archived into operation history so diagnostics have a durable record of the last operation even
when nothing went wrong. A later startup or cleanup pass removes stale terminal journals.

### Separation of concerns

Four distinct artifacts with four distinct responsibilities:

| Artifact | Responsibility |
| --- | --- |
| Rollback snapshot | What the state was before the operation |
| Operation plan | What the operation intended to produce |
| Operation journal | What operation was active and how far observed execution progressed |
| Diagnostics log | Detailed timing, return codes, failures, stages |

The journal is kept separate from the existing rollback history.

## 5. Recovery

On plugin load, if a journal exists with a non-terminal status:

1. Load the referenced snapshot and operation plan.
2. Read the current live mod paths from Penumbra.
3. Compare live state against both the pre-operation snapshot and the intended target.
4. Classify the outcome.

Outcomes:

```
Completed but not finalized
Partially applied
No mutations detected
Indeterminate
```

### Path comparison must use PenumbraPathSemantics

This is a correctness requirement specific to this codebase, not a detail.

Per `Organizer/PenumbraPathSemantics.cs` (ground truth verified against Luna source 2026-07-19), a
`" (N)"` suffix on a leaf whose duplicate-marker base equals the mod's display name is **discarded
on save** and **reassigned in arbitrary enumeration order on every load**. Two paths differing only
by such a suffix are the same persisted location.

String equality during recovery would therefore classify mods as differing from both the original
and the intended state purely because Penumbra reshuffled transient tie-breaker suffixes between the
crash and the next launch. On a library with collisions this fills the `Indeterminate` bucket with
false positives and pushes users toward an unnecessary Restore — the exact outcome this design
exists to prevent.

**All three comparison legs use `PenumbraPathSemantics` equivalence:** live vs. snapshot, live vs.
target, snapshot vs. target. `targetHash` is likewise computed over normalized paths.

### Authority ordering

- **Mod identifier** (`lastCompletedIdentifier`) is authoritative for *what* was touched. It is a
  stable identifier, not a path, and is unaffected by suffix reshuffling.
- **Normalized path comparison** is authoritative for *what state resulted*.
- **`completedItems`** is a hint only, never authoritative, because checkpointing is periodic.

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

## 6. Scan

Scan is a different problem from Apply/Restore and gets a different architecture:

- Penumbra IPC reads stay on their current thread.
- Returned data is copied into plugin-owned immutable structures.
- Pure classification, path analysis, indexing, and model construction run on a worker task.
- The completed result is published back on the framework thread.

**Gated on profiling first.** Measure which phase actually consumes the time before designing the
progress model. Progress must correspond to real phases, not invented percentages.

If a single opaque Penumbra IPC call dominates the duration, granular progress inside it is
impossible without a paginated or incremental API from Penumbra. In that case the correct outcome is
a stage label and an indeterminate spinner, not a fabricated percentage.

## 7. Diagnostics

Requirements this design must satisfy:

1. Exceptions from scan, apply, restore, and index operations are always captured.
2. Operation name and current stage are recorded.
3. Relevant paths, counts, IPC return codes, and timing are recorded.
4. Fatal and non-fatal failures are distinguished.
5. Partial completion is reported clearly.
6. **Diagnostics never report "0 faults" when a journal exists in a non-terminal state.** An
   abnormally ended operation is a fault, whether or not an exception was observed.

Because remote users cannot be relied on to locate the correct Dalamud log — the log supplied with
the initial report was from a different session and contained no organizer operations — the
diagnostic dump must be self-sufficient.

## 8. Deferred

- **Off-thread IPC.** Pending verification of thread affinity against the Penumbra provider
  implementation in `xivdev/Penumbra`. Until verified, calls stay on their current thread and this
  design yields between batches rather than relocating work. If affinity is later confirmed safe,
  moving mutation to a worker becomes an evidence-based optimization.
- **Index rework.** Same incremental treatment, after profiling its individual phases.
- **Penumbra 1.6 vs. 1.7 comparison.** Requires the Penumbra version from a session that actually
  crashed. The available log shows 1.6.1.10 but covers no organizer operation.

## 9. Implementation order

1. Atomic write helper for plugin-owned files (moved earlier — sections 3 and 4 depend on it).
2. Operation controller / state machine.
3. Operation journal with checkpointing.
4. Convert Apply and Restore to frame-budgeted incremental operations.
5. Stage, item count, elapsed time, and last-successful-item logging.
6. Progress UI and conflicting-control lockout.
7. Startup recovery detection and dialog.
8. Profile Scan, then split capture from background processing.
9. Rework Index after profiling.
