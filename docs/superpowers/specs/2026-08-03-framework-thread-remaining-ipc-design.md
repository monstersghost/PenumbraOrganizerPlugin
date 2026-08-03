# Move the remaining Penumbra IPC out of the draw callback

Date: 2026-08-03
Status: **one blocker open — do not implement yet.** Direction approved. Blockers 2, 3 and 4 from
the second review are resolved in this revision; blocker 1 (the thread probe) is not, and two
wording items depend on it. See "Open blocker" below and "Revision notes" at the end.

## Open blocker

**The thread probe has never been run.** Uncommitted diagnostics already exist in `Plugin.cs` and
`MainWindow.cs` (`THREAD PROBE`), written in an earlier session to answer whether the draw callback
and the framework-update callback share a managed thread. Until the answer is recorded here, this
spec cannot state whether the dispatcher performs a genuine thread hop or a same-thread phase
deferral, and cannot confirm that `Framework.IsInFrameworkUpdateThread` distinguishes the two
contexts at all.

Record, with the Dalamud version tested: managed thread id and `IsInFrameworkUpdateThread` from
both callbacks.

| Observation | Consequence for this spec |
| --- | --- |
| Different managed ids | The dispatcher is a genuine thread hop; concurrency between the callbacks is real, and the locking in Architecture is load-bearing. |
| Same id, predicate differs | A phase deferral within one thread. The design still holds, but the crash rationale weakens sharply and the wording throughout must say "framework-update context", not "thread". |
| Same id, predicate true in both | The guard does not enforce the intended boundary. Stop; the premise under this spec *and* the merged 0.5.3.0 fix needs re-examination. |

Two wording items are deliberately left unresolved until then: this document still says "framework
thread" in places where "framework-update context" may be the only defensible phrase, and the probe
records a *managed* thread id, not an OS thread id.

## Context

`docs/superpowers/specs/2026-07-31-framework-thread-materialize-design.md` established that every
Penumbra IPC read in this plugin runs from the ImGui **draw** callback rather than the
**framework-update** callback. That spec fixed Scan and the Search index build, and shipped in the
unreleased 0.5.3.0. Its notes name the rest as follow-up: Restore, its preview popup, Create
Backup, Apply, Folder Cleanup, and workbook Export/Import. This spec covers those seven.

### The rule, and its evidential status

**Penumbra's documentation.** The word "thread" appears zero times in `Penumbra.Api` 5.15.1's XML
docs. Exactly one method carries an execution-context remark:

> `ResolvePlayerPathsAsync` — *Can be called from outside of framework. Can theoretically produce
> incoherent state when collections change during evaluation.*

That is an explicit allowance for one method, **not** an explicit prohibition for the others.
`GetModListAdapter` and `GetChangedItemAdapterDictionary` carry no allowance, and the latter
documents an `ObjectDisposedException` once Penumbra's mod storage is invalidated. The rule this
spec follows is therefore *inferred* from a documented exception, not read from a stated contract.

**The reference implementations reviewed.** Mare Synchronos and its forks — including PlayerSync
(`Caraxi/PSyncClient`, project folder `PlayerSync`) — are the heaviest Penumbra IPC consumers
examined. Their `Interop/Ipc/IpcCallerPenumbra.cs` marshals `CreateTemporaryCollection`,
`AssignTemporaryCollection`, `RemoveTemporaryCollection`, `SetTemporaryMods`,
`SetManipulationData`, `GetCharacterData` and `Redraw` through `await RunOnFrameworkThread(...)`,
and leaves only `ResolvePathsAsync` and `GetMetaManipulations` unmarshalled — precisely the blessed
resolve family. No broader ecosystem survey was performed; this is the Mare family, not a census.

This plugin cannot copy their shape: Mare `await`s because its calls originate in async service
code, while ours originate in a draw callback where neither awaiting nor blocking is available.

### What this does not claim

Two crash reports exist for instant close-to-desktop with no error dialog on Refresh mod list. The
cause is unconfirmed — no reproduction, no minidump — and one reporter later crashed at the main
menu with their sync plugin disabled, which weakens a pure timing race for that reporter. **This
spec claims no crash fix.** It closes an inferred execution-context deviation, which justifies
itself.

## Goal

No Penumbra IPC call originates from the ImGui draw callback. Only the IPC and its immediate
projection move; surrounding file, workbook and computation work stays where it already runs.

## Non-goals

- **Fixing the reported crashes.**
- **A general job scheduler.** `LibraryWorkCoordinator` owns long cancellable library work;
  `OperationController` owns operation lifecycle. A third such mechanism is the trap this avoids.
- **Changing `SetModPath` write behavior.**
- **Moving whole actions off the draw callback.** Rejected twice in review, and confirmed against
  the code: it would put ClosedXML parsing, `organization.json` rewrites and history-file writes on
  the game's update loop.
- **Relocating work that is merely slow.** Preparation that runs on the draw callback today and
  touches no Penumbra IPC stays on the draw callback. Its latency is pre-existing and out of scope.
- **A new thread-guard abstraction.** One already exists; see Architecture.

## Seven-site inventory

| # | Site | IPC called | Moves to framework context | Stays where it is | State owner | Pattern |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `ExportWorkbook` | `GetModDirectory` | nothing, after caching | all of it | unchanged | **1** |
| 2 | `ImportWorkbook` | `GetModDirectory` | nothing, after caching | all of it | unchanged | **1** |
| 3 | `CreateBackup` | `GetModListAdapter` | `ReadCurrentMods` | snapshot capture, history append | `RequestState<Unit>` | **2** |
| 4 | `StartApplyOperation` | `GetModListAdapter` | `ReadCurrentMods` only | the entire `TryStart` call | `OperationController` | **3** |
| 5 | `StartRestoreOperation` | `GetModListAdapter` | `ReadCurrentMods` only | the entire `TryStart` call | `OperationController` | **3** |
| 6 | `PreviewRestore` | `GetModListAdapter` | `ReadCurrentMods` | history load, `BuildRestorePlan` | `RequestState<RestorePlan>` | **2** |
| 7 | `CleanUpFolders` | `GetModListAdapter` | read → occupied-folder set | `FolderCleanupExecutor` file I/O | `RequestState<FolderCleanupResult>` | **2** |
| — | **Infrastructure** | `GetModDirectory` | mod-root seed on first framework tick | — | `ModRootState` | — |

The infrastructure row exists so an audit counting Penumbra call sites finds eight and none look
missed: patterns 1 removes two call sites and adds one.

`ReadCurrentMods()` is already a correct capture boundary — it invokes the adapter, projects each
entry into an immutable `LiveMod` of plain strings and a bool, disposes the adapter via `using`,
and returns a list. Nothing Penumbra-owned escapes. Its return type changes to
`IReadOnlyList<LiveMod>` so a captured list cannot be mutated after it crosses a boundary.

## The three patterns

Numbered rather than lettered, because an earlier draft's "Pattern A — run the whole action on the
framework thread" was rejected and no longer exists.

**Pattern 1 — eliminate the call.** Sites 1 and 2 need one string. `ModDirectoryChanged` is already
subscribed (`Plugin.cs:129`), where it currently only increments the mod epoch and logs. Cache the
directory there and the IPC leaves these paths entirely: no dispatcher, no deferral, no latency
change, no UI change.

**Pattern 2 — framework capture, then background.** Sites 3, 6 and 7 capture on the framework tick
and hand pure captured input to `BackgroundScheduler`, the delegate `LibraryWorkCoordinator`
already uses (`(work, ct) => Task.Run(work, ct)`), then publish one final state.

**Pattern 3 — framework capture, then the existing preparation, unmoved.** Sites 4 and 5 capture
the mod list on the framework tick and hand it to `OperationController.TryStart`'s existing
preparation delegate, which continues to run exactly where it runs today.

### Why Pattern 3 is not "defer the whole `TryStart` call"

The first revision proposed deferring the entire call, on the assumption that a controller
preparation delegate would be thin. Verified 2026-08-03 against `Plugin.cs:431-468` — it is not.
Before the IPC it runs `OrganizerState.Validate()` and a per-row `AreEquivalent` pass, then
`ReadExistingOrganizationFolderPaths()`, which reads and parses `organization.json`. After the IPC
it runs `CaptureSnapshot`, `AppendSnapshot` (rewriting the history JSON), `BuildApplyPlan`, and two
`Save` calls writing the plan and snapshot files. `StartRestoreOperation` is comparable and also
loads history. Deferring the whole call would move one file read, three file writes and three
O(mods) passes onto the update loop.

The review's alternative — split preparation and build the plan on a worker — is also rejected, for
a different reason: `touchedRows` derives from `OrganizerState.Mods`, and `OrganizerState` is
mutated from the draw callback by every sort action. Moving plan construction to a worker would
introduce concurrent access to mutable plugin state, which finding I7 of the same review correctly
warns against. Trading a documented execution-context deviation for an undocumented data race is a
bad trade.

So Pattern 3 moves the minimum: `ReadCurrentMods()` comes out of the preparation delegate and is
passed into it as an already-captured `IReadOnlyList<LiveMod>`. Everything else — admission,
validation, file I/O, plan construction — stays on the draw callback, unchanged and unmoved,
exactly as today. The click frame captures; a later draw frame runs the unchanged preparation with
the captured list. Admission therefore still evaluates after the click, which satisfies
execution-time revalidation without a separate mechanism.

`OperationController` is not modified. Once an operation starts it remains the sole owner of
lifecycle, progress, cancellation and recovery.

## Architecture

### `PenumbraIpcDispatcher`

New file `PenumbraOrganizer.Plugin/LibraryWork/PenumbraIpcDispatcher.cs`. Named for its constraint
and documented as **not** a general job scheduler.

```csharp
public enum EnqueueResult { Accepted, QueueFull, Disposed }

public readonly record struct RevalidationResult(bool IsValid, string? Code, string? Message)
{
    public static RevalidationResult Valid => new(true, null, null);
    public static RevalidationResult Reject(string code, string message) => new(false, code, message);
}

public abstract record FrameworkStepResult
{
    /// The framework-context step finished. For Pattern 2 this means the CAPTURE finished, not
    /// the user's action; for Pattern 3 it means the mod list was captured.
    public sealed record Completed : FrameworkStepResult;
    public sealed record Rejected(string Code, string Message) : FrameworkStepResult;
    public sealed record Failed(Exception Exception) : FrameworkStepResult;
    public sealed record Cancelled : FrameworkStepResult;
}

public interface IDispatchRequest
{
    /// Re-checked in the framework-update context immediately before Execute.
    RevalidationResult Revalidate();

    /// Runs in the framework-update context. Performs the Penumbra call and its projection,
    /// and nothing else.
    void Execute(CancellationToken lifetime);

    /// Always invoked from Drain(), in the framework-update context, exactly once per accepted
    /// request. MUST NOT THROW - it is a small state assignment. See "Settlement must not throw".
    void OnFrameworkStepCompleted(FrameworkStepResult result);

    /// Invoked by Dispose() on the disposing thread. Thread-safe state invalidation only:
    /// no UI work, no scheduling, no publication.
    void OnDiscarded();
}

public sealed record DispatchLogEntry(
    string Name,
    int QueueDepthAtAdmission,
    TimeSpan QueueDelay,
    TimeSpan ExecutionDuration,
    FrameworkStepResult Result);

public sealed class PenumbraIpcDispatcher(
    Func<bool> isFrameworkContext,
    Action<DispatchLogEntry>? log = null)
{
    public const int MaxQueueDepth = 32;
    public static readonly TimeSpan ExecutionWarningThreshold = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan QueueDelayWarningThreshold = TimeSpan.FromMilliseconds(100);

    public EnqueueResult TryEnqueue(string name, IDispatchRequest request);
    public void Drain(CancellationToken lifetime);
    public void Dispose();
}
```

**Admission failure is not a dispatch outcome.** `TryEnqueue` returns a typed `EnqueueResult`. A
`QueueFull` or `Disposed` result is handled by the caller, synchronously, in the draw callback: it
releases the gate it just took and publishes its own failure state. `OnFrameworkStepCompleted` is
reached only by requests that were accepted, and therefore always runs from `Drain()` in the
framework-update context. This removes the first revision's contradiction, where a rejected enqueue
would have "settled on the framework thread" from a draw callback.

**Disposal uses a separate callback for the same reason.** `OnDiscarded()` may run on whichever
thread unloads the plugin, so it is specified as thread-safe invalidation only — never UI work and
never publication.

**Synchronization.** One lock, `_sync`, guarding a `Queue<QueuedRequest>` and a `_disposed` flag.
The first revision assumed enqueue, drain and dispose could not overlap, which the open blocker has
not established, and disposal in particular may arrive from another context regardless.

- `TryEnqueue`: lock; return `Disposed` if disposed; return `QueueFull` if count is
  `MaxQueueDepth`; enqueue with its admission timestamp and the depth at admission; unlock.
- `Drain`: verify `isFrameworkContext()` first; lock; move the current items into a local list and
  clear exactly those; unlock; then revalidate, execute and complete each **outside the lock**.
- `Dispose`: lock; set `_disposed`; move queued items to a local list and clear; unlock; then call
  `OnDiscarded()` on each **outside the lock**.

Arbitrary request code is never invoked while the lock is held. Items enqueued during a drain are
not in that drain's snapshot and wait for the next tick, so a request cannot monopolise a frame by
enqueuing more work.

**Context affinity.** `isFrameworkContext` is the same injected `Func<bool>` shape
`LibraryWorkCoordinator` already takes — `Plugin.cs:86` and `:91` pass
`() => Framework.IsInFrameworkUpdateThread`. No new interface. `Drain()` throws
`InvalidOperationException` when the predicate is false, matching
`LibraryWorkCoordinator.Update()`'s behavior at line 223, and safe for the same reason:
`OnFrameworkUpdate` already wraps each component in its own try/catch with an abandonment latch.
Two adjacent components must not disagree about off-context policy, and a logged-and-ignored
violation is invisible in exactly the case that matters.

**Bounding.** `MaxQueueDepth` is a corruption alarm, not capacity management. Seven single-flight
gated features cannot legitimately produce 32 pending captures; reaching it means gating has
already failed. Rejection is logged at warning severity.

**Thresholds.** Execution warns at 100ms, reusing `LibraryWorkCoordinator.MaterializeWarningThreshold`'s
precedent. Queue delay warns at 100ms as well, chosen because normal delay is a single framework
tick (~16ms at 60fps); 100ms is roughly six missed ticks, which indicates a stalled update callback
rather than a busy one. Both are constants with stated reasoning rather than arbitrary numbers.

**Settlement must not throw.** `OnFrameworkStepCompleted` is contractually a small state
assignment. The dispatcher still wraps it defensively and logs, but a throwing settlement is a
lifecycle failure the dispatcher cannot repair — it cannot know what request-owned state was left
half-written. Implementations perform one `Volatile.Write` and nothing else.

### Request state

```csharp
public abstract record RequestState<T>
{
    private RequestState() { }
    public sealed record Idle : RequestState<T>;
    public sealed record Capturing : RequestState<T>;
    public sealed record Processing : RequestState<T>;
    public sealed record Succeeded(T Result) : RequestState<T>;
    public sealed record Failed(string Code, string Message) : RequestState<T>;
}

public readonly record struct Unit;
```

Generic, so a consumer never recovers `T` by runtime pattern matching, and closed by a private
constructor so the state set cannot be extended accidentally. Each site owns exactly one field:

```csharp
private volatile RequestState<Unit> _backupState = new RequestState<Unit>.Idle();
private volatile RequestState<Organizer.RestorePlan> _restorePreviewState = ...;
private volatile RequestState<Organizer.FolderCleanupResult> _cleanupState = ...;
```

On admission the site's transient output is replaced by `Capturing`, so a previous result is never
displayed beside a newer in-flight request.

**Publication policy.** Every transition is a single `Volatile.Write` of a fully constructed
immutable record. Consumers `Volatile.Read` once into a local and render from that local — never
re-reading the field mid-render, and never combining it with a second field. Any additional value a
site needs goes *inside* the record. The result graph is never mutated after publication, and all
contained collections are immutable or privately owned.

**Pattern 2 failures publish only into `RequestState<T>.Failed`.** They do not also write
`_lastError`. Two independent writes from a worker cannot be observed consistently by the draw
callback, and `_lastError` is not safe to write off the draw callback. `_lastError` remains for
synchronous, draw-callback failures only.

### Plugin lifetime

`Plugin` gains `private readonly CancellationTokenSource _lifetimeCts = new()`. Every Pattern 2
background continuation receives `_lifetimeCts.Token`, and `Drain` receives it so a request
cancelled between admission and execution completes as `Cancelled` without touching Penumbra.

`Dispose()` cancels the token first, then disposes the dispatcher, then the rest.

A cancelled continuation **publishes nothing**. During plugin unload there is no consumer left, and
writing `Idle` or `Cancelled` into state owned by a disposing object is the hazard, not the
remedy. Cancellation is checked immediately before the single publication `Volatile.Write`.

An explicit invariant, because cancellation is weaker than it looks: **cancellation prevents
subsequent phases and prevents publication; it does not interrupt a non-cancellable library call
already in progress.** ClosedXML, `System.Text.Json` serialization and `File.Replace` all run to
completion once entered. Persistence integrity for the history file and `organization.json`
therefore continues to depend on their existing atomic write-then-replace behavior, not on
cancellation.

### Background work takes pure inputs

Pattern 2 continuations are static functions over captured immutable input. They do not close over
`Plugin`, `MainWindow`, `OrganizerState`, `Config`, `OperationController`, or any Penumbra service:

```csharp
static BackupOutcome CreateBackup(
    IReadOnlyList<Organizer.LiveMod> captured, string historyPath, string? label, CancellationToken ct);

static Organizer.RestorePlan BuildPreview(
    IReadOnlyList<Organizer.LiveMod> captured, string historyPath, Guid snapshotId, CancellationToken ct);

static Organizer.FolderCleanupResult RunCleanup(
    IReadOnlySet<string> occupied, string organizationJsonPath, string backupPath,
    IReadOnlySet<string> selectedPaths, CancellationToken ct);
```

Anything a continuation needs is a parameter. `Config.LastFolderCleanup`, which `CleanUpFolders`
writes today, is applied by the site when it publishes, in the draw callback — not by the worker.

### Cross-feature exclusion

Per-button gating is not sufficient: the dispatcher serialises only the short captures, and Pattern
2 continuations can overlap each other and Pattern 3's preparation. Two shared resources make that
unsafe:

- `organization.json` — Folder Cleanup rewrites it; Apply's preparation reads it through
  `ReadExistingOrganizationFolderPaths()`.
- the history JSON — Create Backup appends; Restore preview and Restore load it; Apply appends.

The existing `EnsureAdmitted()` / `LibraryActivityGate` admission already serialises against
library work and operations. This spec extends it to cover Pattern 2 continuations: a site's gate
is held from admission until its final publication, not merely until its capture completes.

| Action | May overlap Apply/Restore | May overlap Cleanup | May overlap Backup |
| --- | --- | --- | --- |
| Create Backup | No — both append history | No | No — single-flight |
| Restore preview | No — reads history mid-append | Yes | No |
| Folder Cleanup | No — organization.json | No — single-flight | No |
| Workbook Import | No — mutates OrganizerState | No | Yes |
| Workbook Export | Yes — read-only | Yes | Yes |

**Folder Cleanup is single-flight, not latest-request-wins.** It mutates the filesystem; an
older cleanup must never continue rewriting while a newer one supersedes its result. A second
request while one is in flight is rejected at admission with a stated reason. Latest-request-wins
applies only to Restore preview, which is a replaceable read-only computation.

Restore preview's generation counter therefore also documents *why* two generations can coexist:
its gate reopens when the popup closes, so a user can close and reopen the popup while an earlier
capture is still processing.

## Error handling

An accepted request reaches exactly one `FrameworkStepResult`. `Completed` proceeds to the
pattern's continuation. `Rejected` carries a stable code and a message; the code is what tests
assert on, the message is what the UI shows. `Failed` carries the exception. `Cancelled` publishes
nothing.

Rejection sources stay distinguishable rather than collapsing into one generic outcome:
`EnqueueResult.QueueFull`/`Disposed` are dispatcher admission failures handled in the draw
callback; `RevalidationResult` rejections are request-level; and an `OperationController` rejection
is neither — the dispatch succeeded, the operation was refused, so it is reported by the Apply or
Restore site from `TryStart(...).RejectionReason` rather than as a dispatch rejection.

A request throwing inside `Execute` does not prevent later requests in the same drain from running.

## Testing

`PenumbraIpcDispatcher` is unit-tested through the injected predicate and log callback:

- FIFO ordering; items enqueued during a drain wait for the next drain.
- One request throwing does not prevent later requests executing; it completes as `Failed`.
- A rejecting `Revalidate` completes as `Rejected` and `Execute` never runs.
- `Drain` with the predicate false throws and executes nothing.
- `TryEnqueue` returns `QueueFull` at `MaxQueueDepth` and `Disposed` after disposal.
- `Dispose` calls `OnDiscarded` on queued requests and never `OnFrameworkStepCompleted`.
- A cancelled lifetime token completes queued requests as `Cancelled` without executing.
- Queue delay and execution duration are recorded independently in `DispatchLogEntry`.
- Concurrent enqueue / drain / dispose from separate threads loses no request and double-settles
  none.
- A throwing `OnFrameworkStepCompleted` is caught and does not abort the drain.

Per-pattern request objects are extracted as testable units rather than left inline in draw
methods, because their behavior is not trivial glue:

- The gate closes before enqueue, and an `EnqueueResult` other than `Accepted` releases it.
- Pattern 2 transitions `Capturing` → `Processing` → `Succeeded`/`Failed`.
- A cancelled lifetime publishes nothing.
- A stale generation discards its result (preview), and a second in-flight request is rejected
  (cleanup).
- Apply/Restore report an `OperationController` rejection rather than throwing.
- `ModRootState` transitions Unknown → Available → Unavailable and back.

Only the final wiring in `Plugin.cs`/`MainWindow.cs` is left to in-game verification.

In-game: each of the seven actions produces its correct result; a double-click admits one request;
Apply rejected by the controller shows the rejection rather than throwing; the mod root survives
changing Penumbra's directory mid-session; unloading the plugin during a cleanup does not throw.

## Mod-root caching

```csharp
public abstract record ModRootState
{
    private ModRootState() { }
    public sealed record Unknown : ModRootState;
    public sealed record Available(string Path) : ModRootState;
    public sealed record Unavailable(string Reason) : ModRootState;
}
```

1. The first `OnFrameworkUpdate` tick calls `GetModDirectory(PluginInterface).Invoke()` and
   publishes `Available` with the returned string copied into an owned instance, or `Unavailable`
   if the call throws.
2. The existing `ModDirectoryChanged` subscriber replaces it with the directory it already
   receives as its first argument.
3. The `Disposed` subscriber — already present for the changed-item adapter — publishes
   `Unavailable("Penumbra unloaded")`.
4. An empty or whitespace path is `Unavailable`, never `Available`.
5. Reads are a single `Volatile.Read` into a local, matching the publication policy above.

`BuildInstallation()` reads the cached state. On anything but `Available`, Export and Import do
nothing and report "Penumbra's mod directory is not known yet". Both are the only callers of
`BuildInstallation()`, confirmed by grep, so this changes nothing else.

This is a real behavior change, stated plainly: Export and Import consume the most recently
observed mod root rather than querying it at action time. The value changes only when Penumbra
raises `ModDirectoryChanged`, which this plugin already trusts for scan invalidation.

## Open risks

1. **The mod-root cache depends on `ModDirectoryChanged` firing for every change.** A miss would
   already corrupt scan invalidation today, but this adds a second consumer of that assumption.
2. **Pattern 2 introduces real background concurrency** into paths that had none. It is confined by
   pure inputs and single-point publication, but it is new.
3. **The execution-context rule is inferred, not stated.** Penumbra documents one allowance, not a
   contract. This design is documented-consistent, not provably correct.
4. **Blocker 1 is unresolved.** If the probe shows the predicate is true in both callbacks, this
   design's guard does not enforce what it claims and the premise needs revisiting.

## Revision notes

### First revision, after review 1

Rejected "defer all seven whole actions and measure". Built the seven-site inventory, which showed
three distinct shapes and that two sites need no dispatcher once the mod root is cached. Added
reentrancy snapshotting, bounding, explicit request state, generation tokens, and separate
queue-delay logging.

### This revision, after review 2

Blockers 2, 3 and 4 resolved; blocker 1 and its two wording items left open.

- **Blocker 2.** Verified that `TryStart`'s preparation is not thin, and reduced Pattern 3 to
  moving the IPC alone. The review's proposed split — plan construction on a worker — was **not
  adopted**: `touchedRows` derives from `OrganizerState`, which the draw callback mutates, so that
  split would trade an execution-context deviation for a data race, contradicting the same
  review's finding I7.
- **Blocker 3.** Plugin-lifetime `CancellationTokenSource`, threaded into every continuation and
  into `Drain`. Cancelled work publishes nothing. Added the invariant that cancellation does not
  interrupt a non-cancellable call already running.
- **Blocker 4.** One lock with stated ordering for enqueue/drain/dispose, request code never
  invoked under it, and admission failure separated from dispatch completion via `EnqueueResult`
  and a separate `OnDiscarded` for disposal.
- Also adopted: `FrameworkStepResult` naming so a capture is not logged as the action succeeding
  (I1); generic `RequestState<T>` with `Capturing`/`Processing` and a `Unit` (I2); Pattern 2
  failures no longer write `_lastError` (I3); Create Backup gets an explicit state owner (I4);
  cross-feature exclusion matrix (I6); pure-input continuations (I7); `ModRootState` with seeding,
  invalidation and thread-safe reads (I8); the infrastructure inventory row (I9); distinguishable
  rejection sources (I10); explicit `Volatile.Read`/`Write` and read-once-into-a-local (M2);
  `IReadOnlyList` for captured collections (M3); structured `DispatchLogEntry` (M4); stated
  threshold reasoning (M5); depth-as-alarm (M6); non-throwing settlement contract (M7); typed
  revalidation with stable codes (M8); extracted testable request objects (M9); numbered patterns
  with the rejected one removed (N1); narrowed "Mare family reviewed" wording (N3); and "inferred
  from a documented exception" rather than "documented contract" (N4).
- **M1 merged into I6.** Single-flight for cleanup is necessary but not sufficient, because Apply's
  preparation reads the same `organization.json` that cleanup rewrites. Handled as cross-feature
  exclusion rather than as a per-feature policy.
