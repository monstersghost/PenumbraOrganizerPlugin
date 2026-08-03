# Move the remaining Penumbra IPC off the render thread

Date: 2026-08-03
Status: approved, not yet implemented. Revised once after an external design review — see
"Revision notes".

## Context

`docs/superpowers/specs/2026-07-31-framework-thread-materialize-design.md` established that every
Penumbra IPC read in this plugin runs from the ImGui **draw** callback rather than the
**framework-update** callback. That spec fixed two paths — Scan and the Search index build — and
shipped in the unreleased 0.5.3.0. Its own notes name the rest as a known follow-up:

> Other actions that talk to Penumbra - Restore, its preview popup, Create Backup, Apply, Folder
> Cleanup, and workbook Export/Import - still read Penumbra from the drawing work the same way.

This spec covers those seven.

### The rule, and where it comes from

Two independent sources agree, and neither was available when the original code was written.

**Penumbra's own API documentation.** The word "thread" appears zero times in `Penumbra.Api`
5.15.1's XML docs. Exactly one method carries an execution-context remark:

> `ResolvePlayerPathsAsync` — *Can be called from outside of framework. Can theoretically produce
> incoherent state when collections change during evaluation.*

A single explicit carve-out implies the rest are not carved out. `GetModListAdapter` and
`GetChangedItemAdapterDictionary` carry no such allowance, and the latter documents that it throws
`ObjectDisposedException` once Penumbra's mod storage is no longer valid.

**The reference implementation.** Mare Synchronos and its forks — including PlayerSync
(`Caraxi/PSyncClient`, whose project folder is literally `PlayerSync`) — are the heaviest Penumbra
IPC consumers in the ecosystem. Their `Interop/Ipc/IpcCallerPenumbra.cs` marshals
`CreateTemporaryCollection`, `AssignTemporaryCollection`, `RemoveTemporaryCollection`,
`SetTemporaryMods`, `SetManipulationData`, `GetCharacterData` and `Redraw` through
`await RunOnFrameworkThread(...)`. The only calls they leave unmarshalled are `ResolvePathsAsync`
and `GetMetaManipulations` — i.e. precisely the blessed resolve family.

So the ecosystem rule is: **all Penumbra IPC on the framework thread, except the documented resolve
calls.** This plugin's seven remaining sites violate it.

Note this plugin cannot copy Mare's shape verbatim. Mare `await`s because its calls originate in
async service code; ours originate in a draw callback, where awaiting or blocking is not available.
The destination is the same; the delivery mechanism must be this plugin's own.

### Why this is worth doing regardless of any crash report

Two crash reports exist for instant close-to-desktop with no error dialog on Refresh mod list. The
0.5.3.0 notes are deliberate that the fix "may" address them and that the cause is unconfirmed —
there is no reproduction and no minidump. One reporter later crashed at the main menu with a
player-sync plugin disabled, which weakens a pure timing race as the explanation for that reporter.

This spec claims no crash fix. It closes a documented deviation from the vendor's API contract and
from the pattern every comparable plugin follows. That justification stands on its own.

## Goal

No Penumbra IPC call originates from the ImGui draw callback. Every call runs on the framework
thread, with only the framework-affine portion of each action moved there — never the surrounding
file, workbook, or computation work.

## Non-goals

- **Fixing the reported crashes.** Unconfirmed cause; see above.
- **A general job scheduler.** The dispatcher introduced here carries short framework-affine entry
  points only. `LibraryWorkCoordinator` already owns long, cancellable, progress-reporting library
  work, and `OperationController` already owns operation lifecycle. Adding a third such mechanism
  is the specific trap this design avoids.
- **Changing `SetModPath` write behavior.** Writes already flow through the operation engine; this
  spec does not alter their sequencing or add retries.
- **Moving whole actions onto the framework thread.** Rejected during review: running ClosedXML
  parsing or a folder-cleanup file rewrite on the game's update loop would trade a rare crash for a
  stall every user feels.
- **Adding a new thread-guard abstraction.** One already exists; see Architecture.

## Seven-site inventory

| # | Site | IPC called | Framework-affine part | Remaining work | Settlement owner | Pattern |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `ExportWorkbook` | `GetModDirectory` | none, after caching | `ToScanInventory`, ClosedXML export, `File.Move` | unchanged (returns path) | **D** |
| 2 | `ImportWorkbook` | `GetModDirectory` | none, after caching | ClosedXML import, `ApplyImportResult` | unchanged (returns result) | **D** |
| 3 | `CreateBackup` | `GetModListAdapter` | `ReadCurrentMods` | `CaptureSnapshot`, history JSON append | none (void) | **B** |
| 4 | `StartApplyOperation` | `GetModListAdapter` (inside prepare) | the whole `TryStart` call | — | `OperationController` | **C** |
| 5 | `StartRestoreOperation` | `GetModListAdapter` (inside prepare) | the whole `TryStart` call | — | `OperationController` | **C** |
| 6 | `PreviewRestore` | `GetModListAdapter` | `ReadCurrentMods` | history load, `BuildRestorePlan` | UI popup request | **B** |
| 7 | `CleanUpFolders` | `GetModListAdapter` | mod list read → occupied-folder set | `FolderCleanupExecutor.Execute` file I/O | UI result request | **B** |

`ReadCurrentMods()` is already a correct capture boundary: it invokes the adapter, projects each
entry into an immutable `LiveMod` record of plain strings and a bool, disposes the adapter via
`using`, and returns a `List`. Nothing Penumbra-owned escapes it. Every site that uses it cuts
there.

## Execution patterns

**Pattern D — eliminate the call.** Sites 1 and 2 need one string: Penumbra's mod root.
`ModDirectoryChanged` is already subscribed (`Plugin.cs:129`), where it currently only increments
the mod epoch and logs. Cache the directory there and the IPC disappears from these paths entirely
— no deferral, no latency change, no UI change.

**Pattern B — framework capture, then existing background work.** Sites 3, 6 and 7 capture on the
framework tick and hand the rest to `BackgroundScheduler`, the delegate `LibraryWorkCoordinator`
already uses (`(work, ct) => Task.Run(work, ct)`), then settle.

**Pattern C — framework-thread admission.** Sites 4 and 5 defer the `OperationController.TryStart`
call itself. Their IPC lives inside `TryStart`'s preparation delegate, so deferring the call moves
the read without the dispatcher knowing anything about operations.

## Architecture

### `PenumbraIpcDispatcher`

New file `PenumbraOrganizer.Plugin/LibraryWork/PenumbraIpcDispatcher.cs`. Named for its constraint:
it exists to give Penumbra calls the framework thread, and is documented as **not** a general job
scheduler.

```csharp
public sealed class PenumbraIpcDispatcher(
    Func<bool> isFrameworkThread,
    Action<string>? logWarning = null,
    Action<string>? logInfo = null)
{
    public const int MaxQueueDepth = 32;
    public static readonly TimeSpan ExecutionWarningThreshold = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan QueueDelayWarningThreshold = TimeSpan.FromMilliseconds(500);

    public bool TryEnqueue(string name, IDispatchRequest request);
    public void Drain();
    public void Dispose();
}
```

`IDispatchRequest` is how a request owns its own state — the dispatcher never touches UI fields:

```csharp
public interface IDispatchRequest
{
    /// Re-checked on the framework thread immediately before Execute. Returning a reason string
    /// settles the request as rejected; returning null proceeds.
    string? RevalidateOrReject();

    /// Runs on the framework thread. Captures Penumbra state and nothing more, except where the
    /// inventory records the whole action as framework-affine.
    void Execute();

    /// Called exactly once, on the framework thread, whatever the outcome. For a Pattern B
    /// request this reports the outcome of the CAPTURE only; the request then owns its own
    /// background continuation and final state transition (see "How Pattern B publishes").
    void Settle(DispatchOutcome outcome, Exception? failure);
}

public enum DispatchOutcome { Succeeded, Failed, Rejected, Cancelled }
```

**Thread affinity.** `isFrameworkThread` is the same injected `Func<bool>` shape
`LibraryWorkCoordinator` already takes (`Plugin.cs:86` and `:91` pass
`() => Framework.IsInFrameworkUpdateThread`). No new interface is introduced. `Drain()` throws
`InvalidOperationException` when the predicate is false, matching
`LibraryWorkCoordinator.Update()`'s existing behavior at line 223. That throw is safe here for the
same reason it is safe there: `OnFrameworkUpdate` already wraps each component in its own try/catch
with an abandonment latch, and the two adjacent components must not disagree about off-thread
policy. A dispatcher that logged and continued would leave the violation invisible.

**Reentrancy.** `Drain()` snapshots the queue, executes only that snapshot, and leaves anything
enqueued during the drain for the next tick. A request cannot monopolise a frame by enqueuing more
work.

**Bounding.** `TryEnqueue` returns false once depth reaches `MaxQueueDepth`; the caller settles the
request as rejected and surfaces it. Every one of these actions is user-initiated and gated in the
UI, so reaching 32 pending means something is already wrong and silently growing is worse.

**Disposal.** `Dispose()` stops accepting work and settles every queued request as `Cancelled` so
no pending flag survives plugin unload. `TryEnqueue` returns false after disposal begins.

**Logging.** Each request records enqueue time, start time, queue delay, execution duration,
outcome, and queue depth at admission. Queue delay and execution duration warn on separate
thresholds, because a 5ms action that waited ten seconds is a different failure from a slow one.
This matches the run-tagged checkpoint logging 0.5.3.0 added to `LibraryWorkCoordinator`.

### Request state, in the UI

Each result-producing site owns a small state value rather than a bool plus a stale result field:

```csharp
public abstract record RequestState
{
    public sealed record Idle : RequestState;
    public sealed record Pending : RequestState;
    public sealed record Succeeded<T>(T Result) : RequestState;
    public sealed record Failed(string Message) : RequestState;
}
```

When a new request is admitted, that site's transient output is cleared immediately and replaced by
`Pending`. A previous result is never left on screen next to a newer in-flight request, which is
the ambiguity a bool plus a retained field creates.

**Latest-request-wins.** Sites 6 and 7 carry a generation counter incremented at admission; on
settle, a request whose generation no longer matches discards its result instead of publishing it.
This prevents a slow earlier preview from overwriting a newer one.

**How Pattern B publishes its final result.** For sites 3, 6 and 7 the dispatcher's `Settle` marks
only that the *capture* succeeded — the user-visible result arrives later, from the background
step. So a Pattern B request's own state machine is `Pending(capturing)` →
`Pending(processing)` → `Succeeded` or `Failed`, and the background continuation performs that last
transition itself.

That transition is a single reference assignment to one `volatile RequestState` field per site.
Reference assignment is atomic, so the draw thread either sees the old state or the new one and
never a half-written result — which is why the state is one field holding a record rather than a
bool plus a separate result field. No consumer may read two fields and combine them; if a site ever
needs more than one value, it goes inside the record.

Deliberately not routed back through a framework tick to publish: that would need a second pump,
and there is nothing here for the framework thread to do with the result. The framework thread is
required for *touching Penumbra*, which the background step never does — it works only on the
immutable `LiveMod` list captured during `Execute`.

**Two-stage validation.** Admission is checked at click time so the button disables immediately,
and `RevalidateOrReject()` re-checks on the framework thread before executing, because another
subsystem may have changed state in between. A failed revalidation settles as `Rejected` with a
reason — it never throws and never leaves the request pending. For sites 4 and 5 this is
structural rather than additional: `TryStart` *is* the admission check, and deferring the call
means it evaluates at execution time by construction.

## Per-site design

**1 and 2, workbook Export/Import.** `Plugin` gains `private string? _penumbraModRoot`, seeded on
the first `OnFrameworkUpdate` tick and updated by the existing `ModDirectoryChanged` subscriber,
which already receives the new directory as its first argument. `BuildInstallation()` reads the
cached value. If it is still null — possible only in the window between plugin load and the first
framework tick, before the window can be interacted with — both actions surface "Penumbra's mod
directory is not known yet; try again in a moment" and do nothing. No dispatcher involvement, no
behavior change.

**3, Create Backup.** The click enqueues a request whose `Execute` calls `ReadCurrentMods()` and
nothing else. `Settle` hands the captured list to the background scheduler, which runs
`CaptureSnapshot` and `AppendSnapshot` — the history file rewrite scales with history length and
does not belong on the update loop. Void to the user except on failure, which sets `_lastError`.

**4 and 5, Apply and Restore.** The click closes the button gate and enqueues a request whose
`Execute` calls `OperationController.TryStart(...)` with the existing preparation delegate
unchanged. If `result.Started` is false, the request settles as `Rejected` carrying
`result.RejectionReason` — replacing today's `throw new InvalidOperationException(...)`, which
would otherwise escape onto the framework thread. Once started, `OperationController` remains the
sole owner of lifecycle, progress, cancellation and recovery. The dispatcher provides thread
affinity and nothing else.

**6, Restore preview.** `Execute` calls `ReadCurrentMods()`. `Settle` schedules
`RollbackHistory.Load` plus `BuildRestorePlan` in the background and publishes into the popup's
request state, subject to the generation check. `PreviewRestore` stops returning a value.

**7, Folder Cleanup.** `Execute` invokes the adapter and projects it straight into the occupied
folder set — the projection must happen on the framework thread because it reads `FullPath` off
adapter entries. `Settle` schedules `FolderCleanupExecutor.Execute`, which reads and rewrites
`organization.json` and the backup file, then publishes the result. `CleanUpFolders` stops
returning a value. The existing comment explaining why this read is deliberately fresh rather than
taken from `OrganizerState` stays true and stays put.

## Error handling

Every request settles exactly once, on the framework thread, through one of four outcomes.
`Succeeded` publishes its result. `Failed` records the exception message into that site's state and
`_lastError`. `Rejected` records the reason without an exception. `Cancelled` clears state
silently, since it only happens at disposal. In all four the pending flag clears — a request that
throws must never leave its feature permanently disabled, which is the failure mode exception
isolation alone does not prevent.

A request that throws inside `Execute` does not prevent later requests in the same drain from
running. A request that throws inside `Settle` is caught, logged, and does not abort the drain.

## Testing

`PenumbraIpcDispatcher` is pure apart from the injected predicate and is unit-tested:

- FIFO ordering.
- Items enqueued during a drain wait for the next drain.
- One request throwing does not prevent later requests executing.
- A throwing request settles as `Failed` and clears pending.
- `RevalidateOrReject` returning a reason settles as `Rejected`, and `Execute` never runs.
- `Drain()` with the predicate false throws and executes nothing.
- `TryEnqueue` returns false at `MaxQueueDepth` and after disposal.
- Disposal settles queued requests as `Cancelled`.
- Queue delay and execution duration are recorded independently, and each warns on its own
  threshold.
- A stale generation discards its result rather than publishing it.

The seven call-site rewires are `Plugin.cs` and `MainWindow.cs` glue, untested by this repo's
convention and verified in-game — which is why all decision logic lives in the dispatcher and in
request objects rather than in the draw methods.

In-game verification: each of the seven actions still produces its correct result; a double-click
admits exactly one request; Apply rejected by the controller shows the rejection rather than
throwing; the mod root survives changing Penumbra's directory mid-session.

## Open risks

1. **The mod-root cache can go stale if `ModDirectoryChanged` does not fire in some path that
   changes it.** The event is Penumbra's own and already drives the mod epoch, so a miss would
   already corrupt scan invalidation today — but this makes a second consumer depend on it.
2. **Pattern B moves work to a background thread that previously ran inline.** `CaptureSnapshot`,
   `BuildRestorePlan` and `FolderCleanupExecutor` become concurrent with the UI reading the state
   they settle into. Each publishes through the request's `Settle` on the framework thread, so the
   handoff is single-point, but this is new concurrency in paths that had none.
3. **This does not make the plugin's Penumbra usage provably correct** — only documented-correct.
   Penumbra states no thread contract beyond the one resolve remark; the rule here is inferred from
   that remark plus the reference implementation.

## Revision notes

The first draft proposed deferring all seven whole actions to the next framework tick and
"shipping uniformly and measuring". An external review rejected that, correctly. Adopted:

- **Seven sites are not equivalent.** The draft grouped them by "touches IPC". Building the
  inventory showed three distinct shapes, and that two sites need no dispatcher at all once the mod
  root is cached from an event already subscribed.
- **Running whole actions on the framework thread was unjustified.** ClosedXML parsing and the
  cleanup file rewrite would stall game logic for every user. Only the capture step moves.
- Explicit reentrancy, bounding, disposal and settlement rules, rather than "enqueue and drain".
- Settlement on both success and failure, so a throw cannot leave a feature disabled.
- Explicit request state over a bool plus a retained result field, with stale output cleared at
  admission.
- Generation tokens for latest-request-wins.
- Queue delay logged separately from execution duration.
- A name that resists misuse.
- The dispatcher does not touch `MainWindow` state; requests settle themselves.

Not adopted, with reasoning:

- **A new `IFrameworkThreadGuard` interface.** The codebase already injects `Func<bool>
  isFrameworkThread` into `LibraryWorkCoordinator`; adding an interface would be a second idiom for
  a solved problem.
- **Log-and-drain-nothing on an off-thread `Drain()`.** `LibraryWorkCoordinator.Update()` throws in
  the same situation and `OnFrameworkUpdate` already contains that throw per component. Two
  adjacent components disagreeing on this would be worse than either policy, and a logged-and-
  ignored violation is invisible in exactly the case that matters.
- **Execution-time revalidation as a separate mechanism for Apply and Restore.** Deferring the
  `TryStart` call makes admission evaluate at execution time by construction.
