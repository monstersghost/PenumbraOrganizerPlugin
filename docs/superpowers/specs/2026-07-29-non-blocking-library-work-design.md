# Non-Blocking Library Work: Scan and Search Index

Date: 2026-07-29
Status: Drafted from a four-question brainstorming round; spec and implementation plan delivered
together per explicit user request, so the first review round covers both documents at once.
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

4. **`OrganizerState.LoadScan` (`OrganizerState.cs:44-72`) is already an atomic clear-and-rebuild.**
   It clears `_mods`, re-derives `row.Protected` from the *config* protection sets rather than from
   the incoming rows (`:51-58`), resets every `ProposedPath` to `CurrentPath` (`:59`), and recomputes
   `_knownFolders` (`:63-71`). This is the single most useful existing property for this design: an
   incremental producer can fill a scratch buffer and call `LoadScan` exactly once, and every
   invariant holds without any other component learning about partial state.

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

7. **Every helper the background phase needs is already Dalamud-free and Penumbra-free.**
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

- Clicking Scan or Build/Refresh Index never blocks a frame for longer than the framework-thread
  phase described in §3.1.
- Both show live progress and can be cancelled.
- A run that observes a concurrent Penumbra mod-list change publishes nothing and says so.
- No path can publish a partially-built result.
- Cancelling, failing, or discarding a run leaves the previously published data exactly as it was.

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

Two things make this cheap enough to do in a single frame:

- **Copying key references is not copying key data.** `changedItems.Keys` are already .NET strings
  owned by Penumbra. Materializing them into our own `List<string>` copies 8-byte references, not
  character data. A million keys is ~8 MB of references.
- **No classification happens here.** `ModTypeClassifier.Classify`, the NPC name match, the gear
  probe, and `ClassifyKeyFacet` all move to phase 2. Phase 1 is a memcpy-shaped loop.

This also *shortens* how long Penumbra's synchronized mod list is held, versus today's hold across
the entire disk walk. That is a real improvement independent of the freeze.

Phase 1 additionally captures, on the framework thread, every value phase 2 will need that only
Dalamud can supply, as plain data: the NPC name-list file path (`Plugin.cs:264`) and the embedded
seed JSON (`Plugin.cs:266-274`). Phase 2 never calls back for them.

### 3.2 Phase 2 — Compute (background `Task`, pure)

Everything else: load the NPC name list from the captured path string, build the matcher, then per
item classify, NPC-match, and where the classification says Gear, read `meta.json` and probe
Heliosphere.

This phase touches the filesystem and `System.Text.Json` and nothing else. Per §0.7, every helper it
calls is already free of Dalamud and Penumbra types.

Runs sequentially in the first version. `Process` is pure and per-item, so `Parallel.ForEach` is a
later drop-in if measurement justifies it; that is deliberately not done now.

### 3.3 Phase 3 — Publish (framework thread)

Check the staleness epoch (§6). If unchanged, hand the complete result list to the job's `Publish`,
which for Scan is one `LoadScan` plus one `RefreshOrphanedFolders`, and for Index is one assignment
to `LibraryIndex`.

`Publish` receives an already-materialized `IReadOnlyList<TResult>`. This matters: `LoadScan` clears
before it fills (`OrganizerState.cs:50-61`), so a throw part-way through its enumeration would leave
`_mods` half-populated. Enumerating a fully-built list cannot throw part-way, which is what makes the
publish step effectively atomic without any additional transaction machinery.

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

**Two constructor seams**, both of which exist so the coordinator stays Dalamud-free and its tests
stay deterministic:

- A scheduler, `Func<Func<IReadOnlyList<TResult>>, CancellationToken, Task<IReadOnlyList<TResult>>>`,
  defaulting to `Task.Run`. The coordinator schedules exactly one thing per run — the whole of phase
  2, producing the result list — so this is concretely typed rather than generic over an open
  parameter. Tests inject a controllable scheduler and drive `Update()` by hand, so no test sleeps or
  polls.
- An epoch reader, `Func<long>`. The staleness counter of §6 lives on `Plugin` next to the Penumbra
  subscribers; the coordinator only reads it through this delegate and never knows what it counts.

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
constructor parameters, and method parameter and return types for every type in that namespace. This
catches the realistic regression, which is someone adding an adapter or `IDalamudPluginInterface` as
a field or parameter. It does not catch a static call buried in a method body; catching that needs IL
inspection, which is disproportionate here given §0.7 established the call graph is already clean.

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

---

## 5. UI state and gating

`MainWindow` builds a small private `ActivityGates` value once per `Draw()` from three sources:
`OperationController.State`, `_scanWork.State`, and `_indexWork.State`. Every currently gated call
site reads the merged value instead of `operationState.Can*` directly.

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

---

## 6. Staleness detection

`Plugin` owns a `long _modEventEpoch`, incremented with `Interlocked.Increment` inside each of the
three existing Penumbra subscribers (`Plugin.cs:84-87`). Lock-free, and correct regardless of which
thread Penumbra raises the event on.

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

- `LogEvent` (`MainWindow.cs:98-103`) formats the line, including its `DateTime.Now` timestamp so
  ordering reflects when the event actually fired, and enqueues to a `ConcurrentQueue<string>`.
- `Plugin.OnFrameworkUpdate` calls a new `MainWindow.DrainEventLog()`, which dequeues into
  `_eventLog` and applies the `MaxEventLogLines` trim.
- `_eventLog` becomes framework-thread-only. `Draw()` (`:391`) and `CreateDiagnosticDump()` (`:1809`)
  enumerate it with no synchronization needed, because nothing else ever touches it.

The epoch increment of §6 lives in the subscribers alongside the enqueue, so both the log line and
the staleness signal come from the same event with no ordering dependency between them.

---

## 8. Error handling

| Failure | Behavior |
|---|---|
| Phase 1 throws (Penumbra unavailable, IPC not ready, adapter disposed) | Caught by the coordinator. `LastOutcome = Failed`, message to `_lastError`. Previously published data untouched. |
| `Prepare` throws | Same as phase 1. |
| `Process` throws for one item | Aborts the run: `Failed`, previous data untouched. Matches `PathMutationOperation`'s treatment of unmodeled exceptions as integrity stops rather than item failures. Note `ReadEquipmentSlots` already absorbs *expected* filesystem exceptions itself and returns `null` (`ModEquipmentFileReader.cs:25-28, 91-94`), so reaching this path means a genuine bug or an environment failure, not a locked file. |
| `Publish` throws | Surfaced as `Failed`. §3.3 explains why the list being fully materialized keeps this from leaving half-written state. |
| Epoch changed | `StaleModList`, per §6. Distinct from `Failed` because it is expected and the user action differs: re-run, rather than report. |

Every terminal outcome returns `Phase` to `Idle`, which re-enables the gates in §5.

---

## 9. Cancellation and disposal

`RequestCancellation()` cancels the run's `CancellationTokenSource`. `Prepare` and `Process` observe
the token between items, so the observed latency is bounded by one item's work, which is a single
file read. Nothing is published. `LastOutcome = Cancelled`.

`Plugin.Dispose()` must be more careful than "cancel and move on". Dalamud unloads the plugin's
`AssemblyLoadContext` on unload; a background task still executing our code through that unload is a
real crash risk. `Dispose` therefore cancels, then waits on the task with a bounded 2 second timeout,
logging a warning if it expires. Blocking teardown for up to 2 seconds is a worthwhile trade against
a torn unload, and per-item work being short makes expiry unlikely.

`OnFrameworkUpdate` is already unsubscribed in `Dispose` (`Plugin.cs:109`), so even in the timeout
case nothing publishes into a torn-down window.

---

## 10. Testing strategy

**Coordinator** (`LibraryWorkCoordinatorTests`), against a fake job and processor, no Dalamud, no
Penumbra, deterministic via the injected scheduler of §4.1:

- Happy path: `Materialize` → `Prepare` → per-item `Process` → `Publish` once, with `State`
  transitioning `Idle → Materializing → Computing → Publishing → Idle` and `LastOutcome = Completed`.
- `Start` is rejected while a run is in flight.
- Cancellation mid-`Process`: no `Publish` call, `LastOutcome = Cancelled`.
- Epoch bumped between phase 1 and publish: no `Publish` call, `LastOutcome = StaleModList`.
- Throw from each of `Materialize`, `Prepare`, `Process`, `Publish`: `LastOutcome = Failed`, message
  captured, no partial publish.
- `ProcessedItems`/`TotalItems` progress reporting.
- `Dispose` during a run cancels and does not publish.

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
