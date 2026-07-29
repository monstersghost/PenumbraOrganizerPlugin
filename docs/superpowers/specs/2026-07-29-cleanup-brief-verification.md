# Verification of the Code Cleanliness Remediation Brief

Date: 2026-07-29
Subject: `PenumbraOrganizerPlugin-Code-Cleanup-Remediation.md` (external brief, 14 items)
Verified against: `main` @ `06ab30d` (v0.5.1.0)

The brief's own workflow section asks for exactly this pass: "Report any item that is already fixed,
no longer applicable, or named differently." Six of its fourteen items describe work that already
exists, and two more are overstated. This document records what was checked and what was found, so
nobody plans against the stale parts.

---

## Summary

| # | Item | Verdict |
|---|---|---|
| 1 | `MainWindow.cs` too large | **Genuine.** 1,956 lines. |
| 2 | `Plugin.cs` owns too many workflows | **Genuine.** 837 lines. Partly addressed by the in-flight library-work plan. |
| 3 | Duplicated operation state | **Genuine, and larger than described.** See below. |
| 4 | Repeated admission boilerplate | **Genuine.** Same root cause as 3. |
| 5 | Obsolete synchronous paths | **Confirmed dead; already scheduled for removal.** |
| 6 | UI/render-loop filesystem work | **Overstated.** Mostly already handled; one small instance remains. |
| 7 | Stale history cache | **Does not reproduce as an independent defect.** Symptom of 8. |
| 8 | Completion handling fragmented | **Genuine.** Its proposed fix needs a prerequisite the brief does not mention. |
| 9 | Recovery successor admission | **Already implemented.** |
| 10 | Live-state coupled to plan validity | **Already implemented.** |
| 11 | Direct infrastructure calls | **Largely already implemented.** |
| 12 | Duplicate identifier handling | **Already implemented.** |
| 13 | Continuation planner validation | **Half implemented.** Typed result exists; granularity is coarser than proposed. |
| 14 | Framework exception containment | **Already implemented** for operations. One new gap is being introduced elsewhere. |

---

## Already implemented

**5 — Obsolete synchronous paths.** `Plugin.ApplyChanges()` (`Plugin.cs:373-443`) and
`Plugin.Restore(Guid)` (`:608-693`) have zero callers in production or tests, confirmed by
whole-repo grep. `MainWindow.ApplyChanges()` (`MainWindow.cs:1631`) is an unrelated private wrapper
around `StartApplyOperation` and is live. Removal is already Task 9 of
`docs/superpowers/plans/2026-07-29-non-blocking-library-work.md`. No tests validate the dead
behaviour, so the brief's "remove tests that validate dead behavior" step is a no-op.

**9 — Recovery successor admission.** The brief requires that recovery state is never cleared before
successor startup is guaranteed. That is already how it works. `ResolveContinue`
(`OperationController.cs:323-347`) and `ResolveRestorePreviousState` (`:349-371`) perform every
fallible step — artifact check, fresh live read, duplicate-identifier check, continuation planning,
plan construction, snapshot capture — *before* calling `StartRecoverySuccessorOrThrow`. A dedicated
successor path already exists, with a `bypassPendingRecoveryLockout` parameter whose comment
(`:132-137`) documents that only recovery resolution may use it. `Keep Current` already has its own
resolution path and does not impersonate Apply or Restore.

**10 — Live-state reading coupled to plan validity.** Already separate. `TryAdvanceClassification`
checks plan and snapshot artifacts independently of `_adapter.GetLiveMods()`, and the comment at
`:654-658` states explicitly that a missing or invalid snapshot does not block classification.

**11 — Direct infrastructure calls.** `IPenumbraOperations`, `IDiagnosticsSink`,
`IElapsedTimeSource`, and `AtomicFile` all exist. The brief's candidate list is largely a
description of the current design.

**12 — Duplicate identifier handling.** `LiveModSnapshot.cs` is
`(IReadOnlyDictionary<string, LiveMod> Mods, IReadOnlySet<string> DuplicateIdentifiers)` — the
brief's proposed record, already present, with a doc comment explaining why the read side is
non-throwing while `RollbackHistory.CaptureSnapshot`'s write side deliberately throws.

**14 — Framework exception containment.** `OperationController.Update()` has two explicit catch
boundaries (`:593-607` for classification, `:612-634` for active operations), each with a comment
noting the framework callback has no caller-side net.

**13 — half.** `ContinuationPlanResult` with `ContinuationPlanStatus { Ready, Blocked }` already
replaced incidental exceptions with a typed result. The brief asks for four statuses
(`Success`/`ValidationFailure`/`UnsupportedState`/`NoWorkRequired`) where there are two. That is a
granularity refinement for diagnostics, not the control-flow defect described.

---

## Overstated

**6 — UI/render-loop filesystem work.** Three of the brief's four named concerns are already
handled:

- Recovery artifact probing is throttled to once per second by `ClassificationRetryInterval`
  (`OperationController.cs:92`, checked at `:687`), not once per frame.
- Recent operations load on a section-open transition (`MainWindow.cs:1099-1101`), not per frame.
- History is cached behind `_historyCache ??=` (`:969`, `:1714`).

What remains is one instance: `Plugin.FolderBackupExists` is a bare `File.Exists` (`Plugin.cs:370`)
evaluated during `Draw` at `MainWindow.cs:1500` and `:1550`. Worth fixing, but it is a one-line
cache, not the systemic problem the brief describes.

Separately, and not mentioned by the brief: `OrganizerState.Mods` performs an `OrderBy` plus
`ToList` on *every access* (`OrganizerState.cs:13-14`), and `PathTreeView` reads it every frame
(`MainWindow.cs:384`). On a large library that is a real per-frame cost — not filesystem work, but
the same category of concern.

**7 — Stale history cache.** The brief calls this "a confirmed defect". It does not reproduce as an
independent one. All four live history-mutation sites already invalidate:

| Mutation | Invalidation |
|---|---|
| `CreateBackup` → `AppendSnapshot` (`Plugin.cs:331`) | `MainWindow.cs:949` |
| `DeleteHistorySnapshot` (`:346`) | `MainWindow.cs:1376` / `:1393` |
| `StartApplyOperation` → `AppendSnapshot` (`:470-471`) | `MainWindow.cs:853` |
| `StartRestoreOperation` → `AppendSnapshot` (`:532`) | `MainWindow.cs:949` |

The remaining two `AppendSnapshot` calls (`:401`, `:626`) are inside the dead methods from item 5.

**However**, every one of those invalidations sits *inside* a completion-latch block — `:853` inside
`if (_applyOperationActive && Kind == Apply && CanStartApply)`, `:949` inside the Restore
equivalent. So a missed or double-fired latch takes the cache invalidation with it. Item 7 is a
symptom of item 8, not a separate bug, and planning them as two work items means fixing the same
thing twice.

---

## Genuine

**3 — Duplicated operation state.** `_operationInProgress` appears at 29 sites in `Plugin.cs`. The
brief is right that it duplicates controller state, but understates why removing it is a design
decision rather than a deletion:

- The flag exists to cover a window the controller genuinely cannot see. `StartApplyOperation`
  builds the plan, reads live mods, captures a rollback snapshot, and appends it to history *before*
  `OperationController.StartApply` is ever called (`Plugin.cs:445-489`). During that window the
  controller is legitimately Idle while real work is in flight.
- The duplication is deliberate. The comment at `:494` reads "Defense-in-depth alongside
  `_operationInProgress`, not a replacement for it."
- The clearing paths are asymmetric: failure clears via `catch { _operationInProgress = false; throw; }`,
  but success clears via an inference latch in `OnFrameworkUpdate` (`:129-139`) that watches
  controller state. That asymmetry is the fragile part.

Useful discount: **6 of the 29 sites are inside the dead methods** (`:375`, `:441`, `:610`, `:612`,
`:691`, and the `finally` at `:689-692`). Removing dead code first drops this to 23.

**4 — Repeated admission boilerplate.** Same root cause. Eight entry points repeat check/reject/
set-flag/try/catch-clear/rethrow.

**8 — Completion handling fragmented.** Three latches exist: `_operationInProgress` (`Plugin.cs:39`),
`_applyOperationActive` and `_restoreOperationActive` (`MainWindow.cs:36-37`). Completion is inferred
from `Kind` plus a `Can*` flag at `MainWindow.cs:850` and `:946` — precisely the pattern the brief
warns against.

**The brief's recommended fix has an unstated prerequisite.** It proposes "a monotonically increasing
operation ID or completion generation". `OperationStateSnapshot` (`OperationController.cs:10-33`) has
neither — no operation identifier of any kind. Adding one means changing the shipped, in-game-verified
controller, so this is a larger change than the brief's framing suggests.

**1 and 2** are accurate: `MainWindow.cs` is 1,956 lines, `Plugin.cs` is 837.

---

## Sequencing consequences

1. **Items 3, 4, 7, and 8 are one work item.** They share a root cause; 7 falls out of 8 for free.
   See `2026-07-29-operation-state-authority-design.md`.
2. **Remove the dead methods before starting item 3.** It eliminates a fifth of the call sites and
   both dead `AppendSnapshot` calls.
3. **The `MainWindow.cs` split (item 1) goes last**, after the in-flight library-work plan stops
   adding UI to the Scan and Search tabs.
4. **The library-work plan's Task 8 must be rewritten**, not built and then consolidated. Its
   `ActivityAdmission` type would otherwise be a third admission mechanism that item 3 immediately
   deletes.
5. **A new item-14 gap is being introduced by the library-work plan**: `ScanWork.Update()` and
   `IndexWork.Update()` are added to `OnFrameworkUpdate` with no catch boundary, while
   `OperationController.Update()` has one. Fix it there rather than rediscovering it in a later
   cleanup pass.
