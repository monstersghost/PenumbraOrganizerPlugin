# Materialize on the framework thread, and leave a trail

Date: 2026-07-31
Revised: 2026-07-31 after review

## The defect

Scan and index materialization currently runs from the ImGui **draw** callback rather than the
**framework-update** callback the coordinator's own design specifies.

`MainWindow.Draw()` is Dalamud's ImGui render pass. The "Refresh mod list" button handler calls
`Plugin.RunScan()` directly from there, which calls `LibraryWorkCoordinator.Start(job)`, which calls
`job.Materialize()` synchronously before returning. `ScanJob.Materialize()` is where
`GetChangedItemAdapterDictionary(...).Invoke()` and `GetModListAdapterIpc.Invoke()` happen.

Penumbra IPC collection reads therefore occur from an execution context for which the plugin has
never established thread-affinity or mutation-safety guarantees. The plugin marshals nothing:
`RunOnFrameworkThread` appears zero times in the codebase.

This is a correctness defect regardless of whether it caused any particular crash. The coordinator's
class doc already claims the opposite of what the code does:

> Runs a library job in three phases: Materialize on the framework thread, ...

and the comment at the materialize call names the situation without recognising it as a bug:

> Materialize is the last unbounded piece of per-run work still on the render thread

That was written about latency. The execution-context consequence was missed.

## The leading hypothesis, and its limits

A user reports the game closing instantly to desktop, no error dialog, on 0.5.2.0 and on an earlier
version, unchanged after switching to the Penumbra 1.7 test build.

If Penumbra expects these adapter reads on the framework thread, or if their backing state can
change while the draw callback reads it, the current call path can race with Penumbra's own updates.
That is the leading explanation for this reporter's instant process termination. It is **not
confirmed**: there is no reproduction and no matching minidump.

What is *not* established, and is deliberately not asserted anywhere in this document: that the
adapters expose native storage, that their internals can be mutated in a way that produces an access
violation, or that `Draw` and `Framework.Update` run concurrently rather than being different
callback contexts reached from the same underlying thread.

Circumstantial support:

- **No managed exception appears in the Dalamud logs.** This makes an ordinary uncaught managed
  failure in the inspected Organizer paths less likely. It does not identify the faulting component,
  and it does not rule out other plugins, runtime or driver faults, fail-fast paths, stack overflow,
  or unrelated native failures.
- **0.5.2.0 did not fix it.** That release moved classification off the render thread and
  deliberately left `Materialize` where it was. The IPC reads never moved.
- **A Penumbra test build did not fix it.** Consistent with a defect in how the plugin calls
  Penumbra rather than in Penumbra.
- **It does not reproduce on the maintainer's libraries.** A race of this shape needs Penumbra to be
  mutating around the moment of the click. The reporter's machine has PlayerSync, Mare and
  Heliosphere writing into the mod root, Penumbra mass compaction, and a directory that provably
  disappeared mid-scan (`Could not enumerate path c:\mods\Teal Ya`, 21:16:08). A static test library
  never races.

Ruled out during investigation, recorded so they are not re-investigated:

- **Lazy or Penumbra-owned objects surviving into the background phase.** `ScanSeed` is a record of
  plain strings; `ScanJob` stores `mod.ModPath.FullName` rather than the `DirectoryInfo`, calls
  `changedItems.Keys.ToList()`, and disposes the mod-list adapter before returning.
- **A double-start race.** `EnsureAdmitted()` precedes `Start`, and both run in the draw callback, so
  two clicks cannot interleave.
- **An unhandled per-mod filesystem fault.** `ModEquipmentFileReader` catches `IOException` (covering
  `DirectoryNotFound`, `FileNotFound`, `PathTooLong`) and `UnauthorizedAccessException`, forces
  enumeration inside the `try`, and degrades one mod to "no subcategory" rather than failing the run.
- **A printf format-string hazard in `ImGui.TextColored` / `ImGui.SetTooltip` with mod names.** IL
  inspection of `Dalamud.Bindings.ImGui` shows both route through `ImGui.Text(ImU8String)`, which
  calls `ImGuiNative.TextUnformatted`. There is no printf path. The Known Issue describing this in
  the published 0.5.2.0 notes is wrong and is corrected by this work.

## Design

### Part 1: defer materialization to the framework-update callback

`Start` stops materializing. It takes ownership of the job, closes the gates, and returns. The first
coordinator `Update()` after that performs the materialize and launches the background task.

```
Draw callback (button click)          Framework-update callback
----------------------------          -------------------------
Start(job)                             Update()
  reject if disposed / running           assert framework thread
  _pendingJob = job                      capture epoch
  Phase = Materializing                  job.Materialize()      <- all IPC now here
                                         launch background task
```

Production call chain, stated so it can be checked:

```
Plugin.OnFrameworkUpdate
    -> LibraryWorkCoordinator.Update()
    -> pendingJob.Materialize()
```

Phase becomes `Materializing` on the click frame, not on the next update. Every admission gate keys
off `Phase != Idle`, so the scan button, the sort buttons, Apply, Restore and the rest must all close
in the same frame as the click. Deferring the phase change would leave a window in which a second
click is admitted.

#### Epoch capture

The epoch is captured on the framework thread **immediately before** `Materialize()`, and compared
against the current epoch at publish time. Anything that changes the mod list during or after
materialization invalidates the run.

This resolves a contradiction in the previous draft, which said both "read at materialize time" and
"a change between the click and the materialize should invalidate the run". Those cannot both hold.

The review proposed capturing the epoch *after* a successful materialize, with a before/after
stability check only if reentrant changes during IPC are possible. **Capture-before is adopted
instead**, because it is strictly stronger and simpler:

- Capturing *after* makes any change that occurred during materialization part of the new baseline.
  A snapshot that spans two logical Penumbra states would then publish as valid. That is the exact
  interval the crash hypothesis concerns, so it is the last interval to stop watching.
- Capturing *before* and comparing at publish already rejects changes during **and** after
  materialization, in one comparison, with no separate stability check. It is also what the current
  code does, so this is a move of *where* the capture happens, not a change to what it means.

The behavioural consequence is explicit: a mod-list change between `Start` and `Update` does **not**
invalidate the run, because the epoch has not been captured yet. The snapshot is taken after that
change and correctly represents the newer state.

An optional early-out — comparing the epoch again right after `Materialize` returns and settling
`StaleModList` immediately rather than running a worker whose result is already doomed — is a pure
efficiency gain. It is included because it is three lines and turns a wasted multi-second scan into
an immediate, accurate message.

#### Framework-thread enforcement

Sequencing tests prove that `Materialize` is deferred. They do not prove it runs on the right thread:
a future caller could invoke `Update()` from anywhere and every test would still pass. The code would
be relying on a convention that has already failed once.

The coordinator takes an injected predicate, in the same style as its existing `Func<long> readEpoch`
so it stays free of Dalamud references:

```csharp
public LibraryWorkCoordinator(
    Func<long> readEpoch,
    Func<bool> isFrameworkThread,
    ...)
```

`Update()` asserts before materializing:

```csharp
if (!_isFrameworkThread())
    throw new InvalidOperationException(
        "Library job materialization must run on the framework thread.");
```

Production wires this to `Dalamud.Plugin.Services.IFramework.IsInFrameworkUpdateThread`, which exists
on the API level this plugin targets (verified against `Dalamud.dll`). Tests inject a predicate they
control, which is what makes the negative case testable at all.

The assertion throws rather than settling `Failed`: a wrong-thread call is a programming error in
plugin code, not a runtime condition a user can cause or recover from.

#### Pending-job ownership

`Start` takes ownership of the job the moment it is called. The rules, in full:

| Event while pending | Behaviour |
|---|---|
| Cancel requested | Do not materialize. Settle `Cancelled`. Release the pending job. |
| Coordinator disposed | Do not materialize. Release the pending job. Publish nothing. |
| `Materialize` throws | Clear pending state. Settle `Failed`, preserving exception type and message. |
| `Update` called twice | Materialize exactly once. The second call sees no pending job. |
| `Update` never called again (shutdown) | Dispose releases the pending job. No worker was ever launched, so there is nothing to await. |
| Epoch changed before materialize | Not stale. See epoch capture above. |
| Materialize succeeds | Clear pending state, record the captured epoch, launch exactly one worker. |

`ILibraryWorkJob` is not disposable today. The ownership rules are codified anyway so that adding a
disposable job later does not require rediscovering them.

#### The latency trade, stated accurately

Materialization moves off the draw callback. It does **not** become incremental. A sufficiently
expensive materialize can still stall a framework update, which is a different stall in a different
place, not the absence of one. This is accepted as a correctness-first fix; incremental
materialization is a separate performance concern and is not in scope.

The scheduling delay is one normal framework-update interval under regular game execution, not a
fixed 16ms — frame duration is not constant, and depending on callback ordering the update may occur
later in the same frame or in the following one.

The existing duration warning stays and becomes honest: its message already says "held the framework
thread", which was untrue when written and is now correct. The surrounding comment is corrected.

### Part 2: checkpoint logging

A successful scan currently writes nothing to the log. That is why none of the reporter's files can
establish whether a scan ran at all in the session that died. Every future report is undiagnosable
until this changes.

Each run gets a short monotonically increasing id, and every line names the job so Scan and Index
runs stay distinguishable in a shared logger:

```
[Organizer.Scan:42] requested
[Organizer.Scan:42] materialize begin
[Organizer.Scan:42] materialize complete mods=293 elapsedMs=84 epoch=17
[Organizer.Scan:42] worker started
[Organizer.Scan:42] worker complete results=293 elapsedMs=1842
[Organizer.Scan:42] publish begin capturedEpoch=17 currentEpoch=17
[Organizer.Scan:42] publish complete
```

Terminal outcomes log their reason, the captured and current epoch, and the **exception type as well
as its message** — a bare message loses the distinction between, say, an `IOException` and an
`InvalidOperationException` with similar wording. The plugin version is logged once at startup so a
report's log identifies its own build.

Written through `Plugin.Log.Information`, so the lines land in `dalamud.log`, which reporters already
know how to send. Deliberately not a separate file with forced flushes: that would survive a hard
kill more reliably, but it is a new artifact nobody sends and it costs a synchronous disk write per
boundary. Accepted risk: a hard native kill may lose the last buffered line or two, so a trail is
evidence of "at least this far", never "exactly this far".

What a truncated trail would and would not establish: a trail ending during materialization would
strongly localize the failure and justify targeted IPC and thread-affinity investigation. It would
**not** prove the mechanism. It could still be an IPC implementation fault, another callback invoked
synchronously by the IPC call, invalid external state, or an unrelated native failure during that
interval.

Per-mod logging is not included. 300 lines per scan buys precision that only matters if the failure
is inside one specific mod's disk read, which the fault isolation in `ModEquipmentFileReader` argues
against. If the trail points there, per-mod logging is the obvious follow-up.

### What is not changed

- `Publish` already runs on the framework thread via `Update()`. Untouched.
- The background phase is already free of Dalamud and Penumbra types, enforced by an architecture
  test. Untouched.
- Penumbra's `Disposed` event still only bumps the epoch rather than cancelling an in-flight run.
  The run's results are discarded as stale and its seeds are plain strings, so nothing reaches back
  into unloaded Penumbra state. Wasted work, not a hazard. Out of scope.

## Testing

The coordinator is unit-testable without a game and its tests already drive `Update()` directly.

Existing coordinator tests that call `Start` and assert on materialized results must now call
`Update()` first. Every affected test is updated to do so rather than weakened, and every one is
re-run to confirm it still fails against the pre-change behaviour it was written for.

New tests:

| Test | What it pins |
|---|---|
| `Start_DoesNotMaterialize_UntilUpdate` | The deferral itself. A job recording whether `Materialize` ran. |
| `Start_ClosesTheGateImmediately` | `Phase == Materializing` and `IsRunning` before any `Update`, so a second `Start` throws. |
| `Update_OffTheFrameworkThread_Throws` | The injected predicate returns false; `Update` throws rather than materializing. The negative case the guard exists for. |
| `Update_Twice_MaterializesOnlyOnce` | Pending state is cleared on the first materialize. |
| `CancelBeforeUpdate_DoesNotMaterialize` | Settles `Cancelled`, releases the job, never calls `Materialize`. |
| `DisposeBeforeUpdate_DoesNotMaterialize` | Same, for disposal, and publishes nothing. |
| `MaterializeFailure_ClearsPendingJob_AndSettlesFailed` | Failure path survives the move; a subsequent `Start` is accepted. |
| `Epoch_ChangedBetweenStartAndUpdate_DoesNotInvalidateTheRun` | The corrected semantic. The snapshot post-dates the change. |
| `Epoch_ChangedDuringMaterialize_InvalidatesTheRun` | Capture-before catches the span-two-states case the review raised. |
| `Epoch_ChangedAfterMaterialize_InvalidatesTheRun` | The existing staleness behaviour is preserved. |

Checkpoint logging is verified through the coordinator's existing injectable log delegate rather than
by asserting against Dalamud's logger.

## Release

Ships as 0.5.3.0. The notes describe what was actually done and what remains unknown:

> Moves Penumbra scan and index IPC reads from the UI draw callback to the framework-update callback,
> matching the coordinator's intended execution contract, and adds scan checkpoints to the log for
> future diagnosis. This addresses a suspected race associated with reports of the game closing
> instantly. Because that crash could not be reproduced and no matching dump was available, its
> specific cause remains unconfirmed.

The 0.5.2.0 Known Issue describing an `ImGui` format-string hazard was wrong. The correction is
stated explicitly rather than the entry being quietly dropped.
