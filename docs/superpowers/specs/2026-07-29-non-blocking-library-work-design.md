# Non-Blocking Library Work: Scan and Search Index

Date: 2026-07-29
Status: Revised after one review round covering this spec and its implementation plan together
(both were delivered at once per explicit user request). Four must-fix findings addressed: domain-level
admission control replacing UI-only gating (§5.1), a real atomic commit separated from post-commit
side effects (§3.3), cancellation honoured when it arrives after the background task completed
(§4.5), and synchronous scheduler failure no longer able to wedge the coordinator (§4.1). Four
further corrections: recursive purity enforcement covering result DTOs outside the `Pure` namespace
(§4.3), honest disposal semantics (§9), materialization instrumentation instead of an unmeasured
frame-safety claim (§3.4), and staleness coverage extended to `ModDirectoryChanged`/`Disposed` with
its remaining limit documented (§6). Verification during that round also found a regression the
review had not named — three recovery paths call `RunScan()` unguarded (§5.1).
Builds on: everything shipped on `main` through `06ab30d` (v0.5.1.0), in particular the operation
execution engine (`docs/superpowers/specs/2026-07-22-operation-controller-design.md` and Plans B1
through E), which this spec deliberately does **not** extend. See §2 for why.

Origin: one in-game crash report against the Scan tab with no diagnostic file attached. Root-cause
investigation did not confirm a crash mechanism, but it did establish that both Scan and the Search
tab's index build run their entire workload synchronously inside the ImGui draw callback, which on a
large or slow-disk library blocks the game's render thread for tens of seconds. That defect is real
and worth fixing on its own merits. **It is not confirmed to be the reported crash**, and this spec
does not claim to fix that report. See §11.

---

## 0. Grounding findings

Verified directly against `main` @ `06ab30d` before any design was written.

1. **`Plugin.RunScan()` (`Plugin.cs:142-202`) is fully synchronous on the draw thread.** It is reached
   from `MainWindow.DrawScanTab` (`MainWindow.cs:373-374`) via `MainWindow.RunScan()`
   (`MainWindow.cs:1613-1629`). Per click it performs: a whole-library changed-item IPC pull
   (`:147`), a mod-list adapter acquisition Penumbra documents as a *synchronized* list (`:149`), an
   NPC name-list file read plus three `RegexOptions.Compiled` alternation regexes built from scratch
   (`:151-154`, `NpcNameMatcher.cs:44-59`), then for **every** mod classified as Gear an open and full
   `JsonDocument.Parse` of that mod's `meta.json` (`:166-184`, `ModEquipmentFileReader.cs:13-37`),
   plus a `File.Exists` per mod for Heliosphere detection (`:193`, `HeliosphereDetector.cs:14`).
   `MainWindow.RunScan()` then calls `RefreshOrphanedFolders()` (`:1628`), which reads and parses
   `organization.json`.

2. **`Plugin.BuildChangedItemIndex()` (`Plugin.cs:204-239`) has the identical shape and is worse
   gated.** It performs the same two IPC calls (`:208-209`), the same NPC list load and regex build
   (`:215-218`), and the same per-Gear-mod `ReadEquipmentSlots` disk walk
   (`ChangedItemIndexBuilder.cs:40`). It additionally allocates one `IndexedChangedItem` per
   changed-item key across the whole library (`ChangedItemIndexBuilder.cs:24-27`). Its button
   (`MainWindow.cs:1157-1158`) has **no `BeginDisabled` gate at all**, unlike Scan's
   (`MainWindow.cs:372-375`), so it is clickable during any other operation and re-entrantly during
   its own run.

3. **`MainWindow._eventLog` is an unsynchronized `List<string>` shared across threads.** Declared at
   `MainWindow.cs:22`, written without a lock by `LogEvent` (`:98-103`), which is invoked from the
   Penumbra `ModAdded`/`ModDeleted`/`ModMoved` subscribers (`Plugin.cs:84-87`), and enumerated on the
   render thread every frame the Scan tab is open (`:391`). Penumbra.Api offers no thread guarantee
   for subscriber invocation, and a Penumbra rediscovery fires a burst of these events. `Insert(0, …)`
   racing a `foreach` yields `InvalidOperationException` at best and a torn backing array at worst.

4. **`OrganizerState.LoadScan` (`OrganizerState.cs:44-72`) is a single-call whole-state replacement,
   but it is *not* atomic.** It clears `_mods` (`:50`) and then fills, re-deriving `row.Protected`
   from the *config* protection sets rather than from the incoming rows (`:51-58`), resetting every
   `ProposedPath` to `CurrentPath` (`:59`), and recomputing `_knownFolders` (`:63-71`). The
   single-call shape is genuinely useful — a producer can fill a scratch buffer and publish once,
   with no other component learning about partial state. But anything throwing after the `Clear()`
   leaves the state half-replaced. The first draft of this spec mistook "one call" for "atomic";
   §3.3 corrects that with build-then-swap.

5. **Protection round-trips through `Configuration`, not through scan rows.** `RunScan` passes
   `Config.ProtectedModIdentifiers` and `Config.ProtectedFolderPaths` into `LoadScan`
   (`Plugin.cs:200`). This is what makes §5's decision to leave the Protect tab live during a run
   correct rather than merely convenient.

6. **`Plugin.ApplyChanges()` (`Plugin.cs:373-443`) and `Plugin.Restore(Guid)` (`Plugin.cs:608-693`)
   are dead code.** Whole-repo grep finds zero callers in production or tests; they were superseded by
   `StartApplyOperation`/`StartRestoreOperation` plus `OperationController`. `MainWindow.ApplyChanges()`
   (`MainWindow.cs:1631`) is MainWindow's own private wrapper around `StartApplyOperation` and is
   unrelated. Between them these two dead methods contain 2 of the 4 `RunScan()` call sites
   (`Plugin.cs:436`, `:686`).

7. **Three recovery paths call `RunScan()` unguarded.** `ResolveKeepCurrent` (`Plugin.cs:549`),
   `AcceptAllAndCloseInterruptedOperations` (`:590`), and `ResolveOneMultiRootOperation` (`:604`)
   call it directly with no `try`. Making `RunScan()` throw when busy therefore lets an exception
   escape a recovery resolution. The comment already at `:600-602` reasons explicitly about
   `RunScan()` being able to throw, so the codebase already models it that way. Verified during the
   first review round; drives §5.1's `TryRequestScan`.

8. **`StartApplyOperation` guards only on `_operationInProgress` and validation** (`Plugin.cs:447-452`).
   It has no knowledge of library work, so nothing but the UI stops an Apply beginning during a scan.

9. **`OrganizerState`'s four state fields are `readonly`** (`OrganizerState.cs:8-11`):  `_mods`,
   `_protectedModIdentifiers`, `_protectedFolders`, `_knownFolders`. Build-then-swap (§3.3) requires
   dropping that modifier on all four.

10. **Penumbra.Api 5.15.1 exposes mod-mutation events this plugin does not subscribe to**, confirmed
    by enumerating `IpcSubscribers` types in the package's XML documentation: `ModDirectoryChanged`,
    `Disposed`, `Initialized`, and `ModSettingChanged`. Only `ModAdded`/`ModDeleted`/`ModMoved` are
    subscribed today (`Plugin.cs:84-87`). Drives §6.

11. **Every helper the background phase needs is already Dalamud-free and Penumbra-free.**
    `ModEquipmentFileReader`, `HeliosphereDetector`, `ModTypeClassifier`, `NpcNameMatcher`,
    `NpcNameListStore`, `ChangedItemKeyParser`, and `EquipmentSlotMapper` reference only BCL types and
    `PenumbraOrganizer.Core`. §4's isolation rule therefore *pins an existing property* rather than
    requiring a refactor to establish one.

---

## 1. Problem statement

Two buttons run multi-second-to-multi-minute workloads on the game's render thread. On a large
library, a library on a network share, or one in a cloud-synced folder, the game stops painting for
the duration. Users experience this as a hang, and a long enough hang is indistinguishable from a
crash and can outlast the game's server connection.

**In scope:** making Scan and the Search index build non-blocking, and making `_eventLog`
thread-safe (a prerequisite, see §7).

**Out of scope:** caching the compiled NPC regexes across runs (tracked separately), and the
per-frame `OrganizerState.Mods` sort documented in §11.

**Success criteria:**

- The only render-thread work per run is the framework phase of §3.1, which is instrumented and
  reports its duration (§3.4). No fixed millisecond bound is claimed until that instrumentation has
  produced numbers from a real library.
- Both show live progress and can be cancelled, and a cancellation the UI accepted is always
  honoured — never silently overridden by a result that finished in the same frame.
- A run that observes a concurrent Penumbra mod-list change publishes nothing and says so.
- No path can publish a partially-built result.
- Cancelling, failing, or discarding a run leaves the previously published data exactly as it was.
  "Failing" here means failing *before* the commit point of §3.3; post-commit side effects have
  their own weaker guarantee, stated there.
- Two library runs, or a library run and an Apply/Restore/cleanup/backup, cannot overlap. This is
  enforced in the domain layer (§5.1), not only by disabling buttons.

---

## 2. Why this does not reuse the Apply operation engine

The obvious move is to port `PathMutationOperation.Advance` (`PathMutationOperation.cs:83-159`): a
cursor advanced from `Framework.Update` under a `TimeSpan` budget. Two independent reasons rule it
out.

**The arithmetic does not work.** The engine's budget is 2 ms per `Framework.Update`
(`Plugin.cs:64`). At 60 fps that is 2 ms of work per 16.7 ms, a 12% duty cycle. Sixty seconds of real
disk work becomes roughly eight minutes of wall clock. Worse, `Advance` guarantees at least one item
per call (`PathMutationOperation.cs:96`, and it must, or one slow item stalls progress permanently),
so a 30 ms `meta.json` parse still lands whole on the render thread. Two thousand Gear mods at ~20 ms
each is 2,000 consecutive frames over budget: roughly 33 seconds of visibly degraded framerate. The
budget works for Apply because each step is one fast IPC call and there are hundreds of them. It is
the wrong instrument for thousands of multi-millisecond disk reads.

**Scan and Index have nothing to recover.** The journal, checkpointer, bundle directories, recovery
classifier, and recovery graph exist because Apply *mutates* Penumbra state, so a run that dies at
step 400 of 900 leaves a library nobody can reason about. Scan and Index are pure reads. A run that
dies mid-way is re-run. Attaching roughly 2,900 lines of durability machinery
(`Organizer/Operations/`) to them would defend an invariant that does not exist.

What *is* worth reusing is small and specific: the `IElapsedTimeSource` seam for deterministic tests,
the `IDiagnosticsSink` surface, the `SlowCallThreshold` idea, being driven from the existing
`OnFrameworkUpdate` subscription (`Plugin.cs:126-140`), and the `DrawOperationProgress` layout math
for a progress bar with a reserved-width Cancel button (`MainWindow.cs:620-651`).

`OperationController` itself is not modified by this work. It is 920 lines, recently shipped, and now
in-game verified; §5 coordinates with it by reading its published snapshot, never by editing it.

---

## 3. Architecture: three phases

Both jobs decompose the same way. The split is by **constraint**, not by item index.

### 3.1 Phase 1 — Materialize (framework thread, one frame)

Acquire both Penumbra adapters, copy out **strings only**, release the adapters.

For Scan, per mod: identifier, name, author, current full path, mod directory path, and the mod's
changed-item keys. For Index, the same minus the path fields it does not use.

Two things make this much cheaper than the work it replaces:

- **Copying key references is not copying key data.** `changedItems.Keys` are already .NET strings
  owned by Penumbra. Materializing them into our own `List<string>` copies 8-byte references, not
  character data, so a million keys is roughly 8 MB of backing array. That is not the same as free:
  the per-mod `List<string>` objects, their capacity growth, the resulting GC pressure, the source
  enumeration, and the IPC call itself all still cost. It is a bounded copy, not a no-op.
- **No classification happens here.** `ModTypeClassifier.Classify`, the NPC name match, the gear
  probe, and `ClassifyKeyFacet` all move to phase 2. No disk is touched at all.

This also *shortens* how long Penumbra's synchronized mod list is held, versus today's hold across
the entire disk walk. That is a real improvement independent of the freeze.

**This phase is still unbounded in principle**, and it is the one remaining piece of per-run work on
the render thread. It is expected to be orders of magnitude cheaper than the disk walk it replaces,
but "expected" is not "measured" — see §3.4.

Phase 1 additionally captures, on the framework thread, every value phase 2 will need that only
Dalamud can supply, as plain data: the NPC name-list file path (`Plugin.cs:264`) and the embedded
seed JSON (`Plugin.cs:266-274`). Phase 2 never calls back for them.

### 3.2 Phase 2 — Compute (background `Task`, pure)

Everything else: load the NPC name list from the captured path string, build the matcher, then per
item classify, NPC-match, and where the classification says Gear, read `meta.json` and probe
Heliosphere.

This phase touches the filesystem and `System.Text.Json` and nothing else. Per §0.11, every helper it
calls is already free of Dalamud and Penumbra types.

Runs sequentially in the first version. `Process` is pure and per-item, so `Parallel.ForEach` is a
later drop-in if measurement justifies it; that is deliberately not done now.

### 3.3 Phase 3 — Publish (framework thread)

Before anything else, in this order: honour a pending cancellation (§4.5), then check the staleness
epoch (§6). Only then hand the complete result list to the job's `Publish`.

Publication has two halves with **different guarantees**, and conflating them was a real defect in
this spec's first draft.

**The commit** is a single reference swap and either fully happens or does not happen at all. For
Scan that is `OrganizerState.ReplaceScanAtomically`, a new method that builds the replacement mod
dictionary and the derived `_knownFolders` list completely, and only then assigns both fields. For
Index it is the single assignment to `LibraryIndex`.

Passing an already-materialized `IReadOnlyList<TResult>` is *necessary* but not sufficient for this.
It removes deferred-enumeration risk, but today's `LoadScan` calls `_mods.Clear()` at
`OrganizerState.cs:50` and then fills, so anything throwing after that line — a protection-derivation
bug, a malformed path in `GetVirtualParent` — leaves the state half-replaced while the coordinator
reports `Failed`. Build-then-swap is what actually closes that. It requires dropping `readonly` from
`_mods`, `_protectedModIdentifiers`, `_protectedFolders`, and `_knownFolders`
(`OrganizerState.cs:8-11`). `LoadScan` keeps its current signature and delegates, so Apply, Restore,
Protect, and Folder Cleanup are unaffected.

**Post-commit side effects** are `SaveProtectionState()` and the orphaned-folder refresh. These run
*after* the commit point and cannot be rolled back. A failure in either is logged as a warning and
leaves the run's outcome `Completed`; it must never be reported as `Failed`, because the new data is
already live and calling the run failed would be a lie the UI acts on. This is the one place where
the §1 criterion is deliberately weaker, and it is stated rather than hidden.

The orphaned-folder refresh is a consequence of new scan data, so it belongs to the domain, not to a
window. The first draft routed it through a `Plugin` → `MainWindow` callback inside job publication;
that inverted the dependency and helped disguise the atomicity problem. `Plugin` owns it now, and
`MainWindow` reads the resulting state like any other.

### 3.4 Materialization instrumentation

Because §3.1 is the last unbounded render-thread step and render-thread latency is the entire point
of this work, the coordinator times it and, when it exceeds a threshold, logs a warning naming the
job, the item count, and the elapsed milliseconds. It uses the coordinator's existing warning
delegate rather than `IDiagnosticsSink`, which lives in `Organizer.Operations` and would couple this
type to the operation engine for no benefit.

Threshold: 100 ms, chosen as roughly six frames at 60 fps — long enough not to fire on a healthy
library, short enough that a hitch a user would notice gets recorded. This is a starting value to be
revised once real numbers exist, not a claim about what is achievable.

---

## 4. Components

### 4.1 `LibraryWorkCoordinator<TSeed, TResult>`

New, in `PenumbraOrganizer.Plugin/LibraryWork/`. Owns the lifecycle and nothing domain-specific.

```csharp
public enum LibraryWorkPhase { Idle, Materializing, Computing, Publishing }

public enum LibraryWorkOutcome { Completed, Cancelled, StaleModList, Failed }

public sealed record LibraryWorkStateSnapshot(
    LibraryWorkPhase Phase,
    string? JobDisplayName,
    int ProcessedItems,
    int TotalItems,
    LibraryWorkOutcome? LastOutcome,
    string? LastError,
    bool CanCancel)
{
    public bool IsRunning => Phase != LibraryWorkPhase.Idle;

    public static LibraryWorkStateSnapshot Idle { get; } = new(
        LibraryWorkPhase.Idle, JobDisplayName: null, ProcessedItems: 0, TotalItems: 0,
        LastOutcome: null, LastError: null, CanCancel: false);
}
```

Public surface: `State` (published as a whole new instance after every transition, never mutated in
place, matching `OperationStateSnapshot`'s established convention), `Start(job)`, `Update()` called
from `OnFrameworkUpdate`, `RequestCancellation()`, and `Dispose()`.

`Update()` polls the in-flight `Task` for completion rather than marshalling through
`IFramework.RunOnFrameworkThread`. This keeps the coordinator free of any Dalamud dependency, which
is what makes it unit-testable without a game process.

**Constructor seams**, which exist so the coordinator stays Dalamud-free and its tests stay
deterministic and fast:

- A scheduler, `Func<Func<IReadOnlyList<TResult>>, CancellationToken, Task<IReadOnlyList<TResult>>>`,
  defaulting to `Task.Run`. The coordinator schedules exactly one thing per run — the whole of phase
  2, producing the result list — so this is concretely typed rather than generic over an open
  parameter. Tests inject a controllable scheduler and drive `Update()` by hand, so no test sleeps or
  polls.
- An epoch reader, `Func<long>`. The staleness counter of §6 lives on `Plugin` next to the Penumbra
  subscribers; the coordinator only reads it through this delegate and never knows what it counts.
- A dispose timeout, defaulting to 2 seconds. Injectable so tests do not pay a real two-second wait
  per run; a test that specifically covers the timeout warning sets it explicitly.
- A warning logger, `Action<string>?`. The coordinator is not in the `Pure` namespace, so this is
  allowed to be a Dalamud-backed delegate; it is only ever invoked on the framework thread.

**Three invariants the first draft got wrong**, each of which is a test in §10:

1. **The scheduler call is inside a `try`.** A scheduler that throws synchronously, or returns null,
   would otherwise leave `Phase == Computing` with `_task == null` — a state `Update()` can never
   settle, permanently gating Scan, Index, Apply, Restore, cleanup, and backup with no recovery short
   of reloading the plugin. Unreachable in practice with `Task.Run`, but the scheduler is an
   explicitly injectable boundary, and the failure is a total wedge rather than a degraded run.
2. **A `_disposed` flag rejects `Start` and short-circuits `Update` after disposal.** Without it,
   anything calling `RunScan` during teardown schedules fresh background work into a plugin that is
   going away.
3. **Cancellation is checked at the top of the settle path**, not merely inside the background loop.
   See §4.5.

### 4.2 `ILibraryWorkJob<TSeed, TResult>` and `ILibraryWorkProcessor<TSeed, TResult>`

The two-interface split is what makes phase 2's isolation structural rather than a comment.

```csharp
// Framework-thread side. May touch Dalamud and Penumbra freely.
public interface ILibraryWorkJob<TSeed, TResult>
{
    string DisplayName { get; }

    // Phase 1. Returns the plain-data items AND the processor that will run against them.
    LibraryWorkBatch<TSeed, TResult> Materialize();

    // Phase 3.
    void Publish(IReadOnlyList<TResult> results);
}

public sealed record LibraryWorkBatch<TSeed, TResult>(
    IReadOnlyList<TSeed> Items,
    ILibraryWorkProcessor<TSeed, TResult> Processor);

// Phase 2 side. Implementations live in LibraryWork/Pure/ and may NOT reference
// Dalamud or Penumbra types. Constructed on the framework thread from plain data,
// executed on the background thread.
public interface ILibraryWorkProcessor<TSeed, TResult>
{
    void Prepare(CancellationToken ct);                  // e.g. load NPC list, build matcher
    TResult? Process(TSeed item, CancellationToken ct);  // null means "exclude from results"
}
```

`Process` returning `TResult?` covers Index's existing rule that mods with zero changed items are
excluded from the browsable index (`ChangedItemIndexBuilder.cs:21-22`) without a second interface.

### 4.3 The isolation rule, and how it is enforced

**Rule:** no type in `PenumbraOrganizer.Plugin.LibraryWork.Pure`, and no `TSeed`/`TResult` used with
the coordinator, may reference a type from a `Dalamud.*` or `Penumbra.*` assembly.

**Enforcement:** a reflection-based architecture test asserts this over field types, property types,
constructor parameters, and method parameter and return types.

The roots it walks are **not** just the `Pure` namespace. `OrganizerModRow` and `IndexedMod` are
`TResult` types that cross the thread boundary but live outside it, so a namespace-only check would
let a Penumbra-typed field land on either without failing — exactly the regression the rule exists to
stop. The test therefore seeds from every type in `Pure` **plus** `ScanSeed`, `IndexSeed`,
`OrganizerModRow`, and `IndexedMod`, and recurses through their member types with a visited set,
stopping at BCL types.

It does not catch a static call buried in a method body; catching that needs IL inspection, which is
disproportionate here given §0.11 established the call graph is already clean. The DTO gap, unlike the
method-body gap, was avoidable, so it is closed.

A concrete consequence worth stating: `TSeed` carries the mod directory as a **`string` path**, not
the `DirectoryInfo` the Penumbra adapter hands out. Phase 2 constructs its own `DirectoryInfo`. This
severs any object identity with adapter-owned state, so a stale adapter cannot be reached through a
seed even by accident.

### 4.4 `ScanJob` and `IndexJob`

Two thin classes in `LibraryWork/`, holding the framework-thread halves of what `Plugin.RunScan()`
and `Plugin.BuildChangedItemIndex()` do today. Their processors, `ScanProcessor` and `IndexProcessor`,
live in `LibraryWork/Pure/` and hold the rest.

`Plugin` keeps `RunScan()` and `BuildChangedItemIndex()` as public entry points, re-implemented as
`_scanWork.Start(new ScanJob(…))` and `_indexWork.Start(new IndexJob(…))`. Two closed generic
coordinator instances, since the two jobs have different seed and result types; both expose the same
non-generic `LibraryWorkStateSnapshot`, so the UI treats them uniformly.

### 4.5 Cancellation ordering

`CanCancel` is true for the whole of `Computing`, which includes `Prepare`. That is intended —
`Prepare` loads the NPC name list and compiles three alternation regexes, which is real work a user
may want out of — so the processor observes the token during preparation, not only between items.

The subtle case is a cancellation that arrives *after* the background task has already completed but
*before* `Update()` observes it. The task is `RanToCompletion`, so a naive `Update()` publishes a
result the UI already told the user it was cancelling. The window is one frame, but it is a window
the user can hit by clicking Cancel exactly as a run lands.

`Update()` therefore checks `IsCancellationRequested` immediately after taking the completed task and
before both the epoch check and `Publish`. Discarding a finished, valid result is safe precisely
because these runs are read-only: the cost of honouring a cancel that arrived late is one wasted
scan, and the cost of ignoring it is the UI lying.

Note `ProcessedItems` counts items whose `Process` call *returned*. An item cancelled or thrown out
of mid-call is not counted. It measures completed processing attempts, not items visited.

---

## 5. Admission control and UI gating

### 5.1 Mutual exclusion is a domain invariant, not a UI behaviour

Two library coordinators and `OperationController` are three independent state machines. Each of the
coordinators only knows its own `IsRunning`, and `StartApplyOperation` guards only on
`_operationInProgress` and validation (`Plugin.cs:447-452`). Nothing at the domain layer prevents:

```csharp
plugin.RunScan();
plugin.BuildChangedItemIndex();   // both now running, both walking the same library
```

Disabling buttons does not fix this. It is presentation-layer prevention of a domain-layer hazard,
and it fails for anything that is not a button: a slash command, a test hook, a future callback, or
an existing code path that predates the gate.

**There is already such a path.** `ResolveKeepCurrent` (`Plugin.cs:549`),
`AcceptAllAndCloseInterruptedOperations` (`:590`), and `ResolveOneMultiRootOperation` (`:604`) all
call `RunScan()` directly, and none catches. Once `RunScan()` can throw, a recovery resolution can
throw out of the middle of a recovery.

**The rule:** a shared `EnsureNoConflictingActivity()` predicate on `Plugin`, checked by every
starting wrapper — `RunScan`, `BuildChangedItemIndex`, `StartApplyOperation`, `StartRestoreOperation`,
`CleanUpFolders`, `RollbackFolderCleanup`, `CreateBackup`. Chosen over an acquire/release token type
because the invariant is a boolean, and a token adds a lifetime to leak.

**The recovery paths get a different entry point.** They use `TryRequestScan()`, which returns
`false` and logs rather than throwing. Their scan is a best-effort refresh after resolution, not a
correctness requirement — the user can always press Refresh — so a rejected request must not abort a
recovery that has already committed. The existing comment at `Plugin.cs:600-602` already reasons
about `RunScan()` being throwable, so this matches the model the code was written against.

### 5.2 UI gating

`ActivityGates` is a **pure static policy** with its own tests, not a private helper inside
`MainWindow`. `MainWindow` computes one per `Draw()` and every gated call site reads it:

```csharp
internal static ActivityGates Build(
    OperationStateSnapshot operation,
    LibraryWorkStateSnapshot scan,
    LibraryWorkStateSnapshot index);
```

The first draft buried this as a private struct method, which left the whole lockout matrix verified
only by "the UI compiles". Extracting it means the intended rules are pinned by tests across the
cross-product of idle, scan running, index running, operation running, recovery required, and
completed-with-each-outcome.

**Disabled while either coordinator is running:** Scan, Build/Refresh Index (which gains the
`BeginDisabled` gate it has never had, per §0.2), Apply, Restore, Folder Cleanup, Folder Cleanup
Rollback, Create Backup, and the Sort tab's proposal-staging controls.

The Sort tab is included because a completing scan calls `LoadScan`, which resets every
`ProposedPath` to `CurrentPath` (`OrganizerState.cs:59`). Today the scan is synchronous so a user
cannot be mid-work when it lands; once it spans seconds, they can. Disabling staging during a run is
the option that needs no merge logic and matches how Apply already behaves.

**Left enabled: the Protect tab.** Per §0.5, protection is stored in `Configuration` and re-derived by
`LoadScan` from the config sets, not carried on the incoming rows. A protect toggle made during a run
therefore survives the publish correctly. Disabling it would be defensive noise, not safety.

**Progress and cancel** reuse the layout approach of `DrawOperationProgress` (`MainWindow.cs:620-651`)
— reserve the Cancel button's width, then give the bar the remainder — but against
`LibraryWorkStateSnapshot` rather than `OperationStateSnapshot`. The fraction is
`ProcessedItems / TotalItems`. `TotalItems` is known exactly at the end of phase 1, so the bar is
determinate for all of phase 2.

**Outcome presentation has exactly one path.** A single helper maps `LastOutcome` to UI status:
`Failed` shows `LastError`; `StaleModList` shows an actionable re-run message; `Cancelled` shows a
cancellation notice that says previous results are intact; `Completed` shows completion. `RunScan`
must not clear `_lastError` at start and leave unrelated rendering code to rediscover coordinator
failures — the two error channels would drift and contradict each other on screen.

---

## 6. Staleness detection

`Plugin` owns a `ModEventEpoch`, incremented with `Interlocked.Increment` from every Penumbra
subscriber. Lock-free, and correct regardless of which thread Penumbra raises the event on.

**Three subscribers is not enough.** The first draft used only the existing
`ModAdded`/`ModDeleted`/`ModMoved` (`Plugin.cs:84-87`). Penumbra.Api 5.15.1 also exposes
`ModDirectoryChanged`, `Disposed`, `Initialized`, and `ModSettingChanged`, none of which this plugin
subscribes to. Two of those matter here:

- **`ModDirectoryChanged`** is the serious one. `ScanSeed`/`IndexSeed` carry **absolute**
  `ModDirectoryPath` strings. If the mod root moves mid-run, phase 2 reads paths that no longer
  exist and every Gear mod resolves to `DirectoryMissing` — a wrong-but-plausible published result,
  which is strictly worse than a failure because nothing looks broken.
- **`Disposed`** signals Penumbra unloading. Its own API docs for `GetChangedItemAdapterDictionary`
  say to clear the dictionary on this event.

Both bump the same epoch. `Initialized` does not need to: a run cannot have started before Penumbra
existed, and a run in flight when Penumbra initialises has already been invalidated by the `Disposed`
that preceded it.

**Documented limit, not verified either way:** whether `ModSettingChanged` can alter
`GetChangedItems` output is unknown. Penumbra's changed items appear to be derived per-mod across all
options, which would make settings irrelevant, but that was not confirmed against Penumbra's source
and is not claimed here. If it turns out settings do affect changed items, the epoch should bump on
that event too. What this design detects is **observed structural mod-list and mod-root changes**,
not guaranteed whole-snapshot consistency.

The coordinator reads the epoch through the `Func<long>` seam of §4.1: once at the start of phase 1,
and again immediately before phase 3. Any difference discards the result, sets
`LastOutcome = StaleModList`, and surfaces "the mod list changed during the scan, please run it
again."

This is conservative by construction: it never publishes a result known to be built from a stale mod
list. The accepted cost is that a run started while Penumbra is actively rediscovering may need
retrying. The alternative, publishing anyway, means a mod deleted mid-run appears in results and a
later Apply fails against it.

---

## 7. `_eventLog` thread safety

In scope because §3 makes the existing race materially more likely: spreading work across hundreds of
frames widens the window in which Penumbra's callbacks interleave with `Draw()` enumerating that
list.

One mechanism resolves it:

- `LogEvent` (`MainWindow.cs:98-103`) formats the line, capturing `DateTime.Now` at callback time,
  and enqueues to a `ConcurrentQueue<string>`.
- `Plugin.OnFrameworkUpdate` calls a new `MainWindow.DrainEventLog()`, which dequeues into the
  framework-thread list and applies the line cap.
- The list becomes framework-thread-only. `Draw()` (`:391`) and `CreateDiagnosticDump()` (`:1809`)
  enumerate it with no synchronization needed, because nothing else ever touches it.

Two precision points. **Timestamps are captured at callback time; display order is queue arrival
order.** Those are not the same thing — callbacks on different threads have no meaningful universal
chronological order, so the display is honest about arrival, not about a total ordering that does not
exist. And the drain does one `InsertRange` of a reversed batch rather than repeated `Insert(0, …)`,
which is O(n) per line; harmless at a 200-line cap, but the batch form is simpler to read anyway.

The epoch increment of §6 lives in the subscribers alongside the enqueue, so both the log line and
the staleness signal come from the same event with no ordering dependency between them.

---

## 8. Error handling

| Failure | Behavior |
|---|---|
| A conflicting activity is already running | `Start` throws `InvalidOperationException` before any state changes; the coordinator stays `Idle`. Recovery paths use `TryRequestScan` and get `false` instead (§5.1). |
| Phase 1 throws (Penumbra unavailable, IPC not ready, adapter disposed) | Caught by the coordinator. `LastOutcome = Failed`, message retained. Previously published data untouched. |
| Scheduler throws synchronously or returns null | `Failed`, same as phase 1. Explicitly handled so the coordinator can never be left `Computing` with no task to settle. |
| `Prepare` throws | Same as phase 1. |
| `Process` throws for one item | Aborts the run: `Failed`, previous data untouched. Matches `PathMutationOperation`'s treatment of unmodeled exceptions as integrity stops rather than item failures. Note `ReadEquipmentSlots` already absorbs *expected* filesystem exceptions itself and returns `null` (`ModEquipmentFileReader.cs:25-28, 91-94`), so reaching this path means a genuine bug or an environment failure, not a locked file. |
| Cancellation requested at any point before the commit | `Cancelled`, nothing published — including when the task already finished successfully (§4.5). |
| Epoch changed | `StaleModList`, per §6. Distinct from `Failed` because it is expected and the user action differs: re-run, rather than report. |
| The commit throws (`ReplaceScanAtomically`, `LibraryIndex` assignment) | `Failed`, and because the commit is build-then-swap, no partial state was installed (§3.3). |
| A post-commit side effect throws (`SaveProtectionState`, orphan refresh) | Logged as a warning. Outcome stays `Completed` — the new data is live, and reporting `Failed` would make the UI act on a lie. This is the one deliberate weakening of the §1 criterion. |

Every terminal outcome returns `Phase` to `Idle`, which re-enables the gates in §5.

---

## 9. Cancellation and disposal

`RequestCancellation()` cancels the run's `CancellationTokenSource`. `Prepare` and `Process` observe
the token, so the observed latency is bounded by one item's work, which is a single file read.
Nothing is published. `LastOutcome = Cancelled`. See §4.5 for the late-cancellation ordering.

**Disposal reduces the teardown hazard; it does not eliminate it, and the first draft implied it
did.** Dalamud unloads the plugin's `AssemblyLoadContext`, and a background task still executing our
code through that unload is a genuine crash risk. `Dispose` cancels and then waits on the task with a
bounded timeout — but if the timeout expires, the task is still running, still holding its batch and
processor, and still executing plugin assembly code. Clearing the coordinator's fields does not stop
it. The bounded wait makes that outcome unlikely, not impossible; a synchronous filesystem call
blocked on an unresponsive network share cannot be interrupted at all.

What the design does about it, in order:

1. `Plugin.Dispose` unsubscribes `Framework.Update` **before** disposing the coordinators
   (`Plugin.cs:109` already unsubscribes; the ordering requirement is new), so no publish can be
   driven into a half-torn-down plugin.
2. Coordinators are disposed before any service their jobs depend on.
3. A `_disposed` flag rejects further `Start` calls and makes `Update` a no-op.
4. `Process` and `Prepare` are cancellation-aware at every boundary the BCL allows.
5. Timeout expiry is logged as a **teardown integrity warning**, distinct from ordinary run
   warnings, because it means unmanaged risk was accepted rather than a run merely being slow.

Deliberately not done: attaching a continuation to observe the abandoned task's exception. Since
.NET 4.5 an unobserved task exception does not escalate — `TaskScheduler.UnobservedTaskException`
fires and the default is to ignore it — so there is no process-kill risk to defend against, and
`Update()` already observes faults on every non-disposal path.

---

## 10. Testing strategy

**Coordinator** (`LibraryWorkCoordinatorTests`), against a fake job and processor, no Dalamud, no
Penumbra, deterministic via the injected scheduler of §4.1:

- Happy path: `Materialize` → `Prepare` → per-item `Process` → `Publish` once, with `State`
  transitioning `Idle → Materializing → Computing → Publishing → Idle` and `LastOutcome = Completed`.
- `Start` is rejected while a run is in flight.
- Cancellation mid-`Process`: no `Publish` call, `LastOutcome = Cancelled`.
- **Cancellation requested after the task completed but before `Update()`**: no `Publish` call,
  `LastOutcome = Cancelled` (§4.5). The first draft had no test here and the code would have
  published.
- Epoch bumped between phase 1 and publish: no `Publish` call, `LastOutcome = StaleModList`.
- Throw from each of `Materialize`, `Prepare`, `Process`, `Publish`: `LastOutcome = Failed`, message
  captured, no partial publish.
- **Scheduler throws synchronously**, and **scheduler returns null**: `Failed`, and `Start` works
  again afterwards rather than being wedged.
- **Empty batch**: zero items publishes an empty result and completes on the next `Update()`.
- `ProcessedItems`/`TotalItems` progress reporting.
- `Dispose` during a run cancels and does not publish; `Start` after `Dispose` is rejected; `Update`
  after `Dispose` is a no-op. These use an injected zero dispose timeout so the suite does not pay a
  real two-second wait, with one separate test that sets a real timeout to cover the warning.

**Admission control** (`PluginActivityTests` or equivalent on the shared predicate): Index cannot
start during Scan; Scan cannot start during Index; Apply cannot start during either; Scan and Index
cannot start while an operation is active or recovery is required; `TryRequestScan` returns `false`
rather than throwing when blocked.

**`ActivityGates`** (`ActivityGatesTests`): the cross-product of idle, scan running, index running,
operation running, recovery required, and each completed outcome, asserting exactly which capability
flags are true. This is what stops a missed call site from being invisible.

**`OrganizerState.ReplaceScanAtomically`**: a throw during derivation leaves the previous mods,
protection sets, and known folders completely unchanged.

**Processors**, against temp directories, extending the existing `ModEquipmentFileReaderTests`
conventions: a Gear mod resolving to one slot, to several, to none, a `meta.json` that fails to parse,
a missing directory, and the `null` result meaning "exclude" for Index.

**Architecture test** (`LibraryWorkPurityTests`): the §4.3 reflection check over
`LibraryWork.Pure`.

**Event log** (`EventLogBufferTests`, against the buffer extracted out of `MainWindow` so it is
testable at all): queued lines are invisible until drained, drain orders newest-first, the line cap
is enforced, and concurrent writes from several threads followed by a drain leave a full, well-formed
window. Note the cap makes "every line exactly once" unassertable once the write count exceeds it, so
the concurrency test asserts survival and well-formedness rather than an exact set.

**Not covered by automated tests, requires in-game verification:** that a large-library scan actually
holds framerate, and that the Penumbra adapters behave as expected when acquired and released within
a single frame. Both go on the manual verification list, since the existing test suite has no game
process.

---

## 11. Explicitly out of scope

- **This does not claim to fix the reported crash.** Investigation identified the render-thread block
  as the most likely explanation for a report describing a crash with no diagnostic file, but three
  other candidates were not ruled out: the `_eventLog` race (§7 fixes this one), the absence of any
  `Penumbra.Api` `Disposed`/`Initialized` subscription, and `ImGui.TextColored` being a printf-family
  function receiving user-controlled mod names at `PathTreeView.cs:71`. Those are tracked separately.
- **NPC matcher caching.** The three `RegexOptions.Compiled` regexes are still rebuilt per run. This
  design moves that cost off the render thread, which is enough for this spec; caching it behind the
  name-list file's write time is a separate, smaller change.
- **`OrganizerState.Mods` sorting per access.** The property does an `OrderBy` plus `ToList` on every
  read (`OrganizerState.cs:13-14`) and `PathTreeView` reads it every frame (`MainWindow.cs:384`). On a
  large library this is a real per-frame cost, but it is a rendering concern independent of this work.
- **Parallelism within phase 2**, per §3.2.
- **Any change to `OperationController`**, per §2.
