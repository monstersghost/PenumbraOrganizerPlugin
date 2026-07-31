# Read Penumbra on the framework thread, and leave a trail

Date: 2026-07-31

## The defect

Every Penumbra IPC read the plugin performs during a scan or index build happens on the **render
thread**.

`MainWindow.Draw()` runs inside Dalamud's ImGui render pass. The "Refresh mod list" button handler
calls `Plugin.RunScan()` directly from there, which calls `LibraryWorkCoordinator.Start(job)`, which
calls `job.Materialize()` synchronously before returning. `ScanJob.Materialize()` is where
`GetChangedItemAdapterDictionary(...).Invoke()` and `GetModListAdapterIpc.Invoke()` happen.

Penumbra mutates the collections behind those adapters on the **framework thread**. The plugin never
marshals: `RunOnFrameworkThread` appears zero times in the codebase.

So the plugin reads Penumbra's live mod collections from one thread while Penumbra is free to write
them from another. A read that lands mid-write can dereference freed or half-updated native storage
and terminate the process with no managed exception.

The coordinator's own comment states the situation without naming it as a bug:

> Materialize is the last unbounded piece of per-run work still on the render thread

That was written about latency. The thread-affinity consequence was missed. The class doc directly
above it claims the opposite of what the code does:

> Runs a library job in three phases: Materialize on the framework thread, ...

The design was right. The implementation never matched it.

## Evidence

A user reports the game closing instantly, to desktop, with no error dialog, on 0.5.2.0 and on an
earlier version, and unchanged after switching to the Penumbra 1.7 test build.

Consistent with this root cause:

- **No managed exception anywhere in the Dalamud logs.** A native access violation inside Penumbra's
  collection internals leaves nothing for Dalamud to log. Every competing hypothesis would have left
  a stack trace.
- **0.5.2.0 did not fix it.** That release moved classification off the render thread and
  deliberately left `Materialize` where it was. The IPC reads never moved.
- **A Penumbra test build did not fix it.** This is a defect in how the plugin calls Penumbra, not in
  Penumbra.
- **It does not reproduce on the maintainer's libraries.** It needs Penumbra to be mutating at the
  instant of the click. The reporter's machine has PlayerSync, Mare and Heliosphere writing into the
  mod root, Penumbra mass compaction, and a directory that provably disappeared mid-scan
  (`Could not enumerate path c:\mods\Teal Ya`, 21:16:08). A static test library never races.

Not established: there is no minidump and no reproduction, so this is not proven to be the cause of
that specific crash. It is a real defect either way. Reading Penumbra IPC off the framework thread is
wrong on its own terms and must be fixed regardless of what the next crash report says.

Ruled out during investigation, recorded so they are not re-investigated:

- **Lazy or Penumbra-owned objects surviving into the background phase.** `ScanSeed` is a record of
  plain strings; `ScanJob` stores `mod.ModPath.FullName` rather than the `DirectoryInfo`, calls
  `changedItems.Keys.ToList()`, and disposes the mod-list adapter before returning.
- **A double-start race.** `EnsureAdmitted()` precedes `Start`, and both run on the render thread, so
  two clicks cannot interleave.
- **An unhandled per-mod filesystem fault.** `ModEquipmentFileReader` catches `IOException` (covering
  `DirectoryNotFound`, `FileNotFound`, `PathTooLong`) and `UnauthorizedAccessException`, forces
  enumeration inside the `try`, and degrades one mod to "no subcategory" rather than failing the run.
- **A printf format-string hazard in `ImGui.TextColored` / `ImGui.SetTooltip` with mod names.** IL
  inspection of `Dalamud.Bindings.ImGui` shows both route through `ImGui.Text(ImU8String)`, which
  calls `ImGuiNative.TextUnformatted`. There is no printf path. The Known Issue describing this in
  the published 0.5.2.0 notes is wrong and is removed by this work.

## Design

### Part 1: defer Materialize to the framework thread

`Start` stops materializing. It records the request and returns; the next `Update()` call, which
already runs on the framework thread once per frame, performs the materialize and launches the
background task.

```
Render thread (button click)          Framework thread (next Update)
-----------------------------          ------------------------------
Start(job)                             Update()
  validate not disposed, not running     read epoch
  _job = job                             job.Materialize()      <- all IPC now here
  _pendingStart = true                   launch background task
  Phase = Materializing
```

Phase becomes `Materializing` on the click frame, not on the next Update. This matters: every
admission gate keys off `Phase != Idle`, so the button, the sort buttons, Apply, Restore and the
rest must all close in the same frame as the click. Deferring the phase change would leave a
one-frame window where a second click is admitted.

The epoch is read at materialize time, not request time. A mod-list change between the click and the
materialize should invalidate that run, and reading the epoch next to the IPC calls is what makes
that true.

`Materialize` throwing is handled exactly as today, by settling the run to `Failed` with the
exception message. The only difference is which frame that happens on.

The existing 100ms warning stays and becomes honest: its message already says "held the framework
thread", which was untrue when written and is now correct. The surrounding comment is corrected.

Cost: one frame, roughly 16ms, between click and IPC read. Irrelevant against a scan measured in
seconds.

### Part 2: checkpoint logging

A successful scan currently writes nothing to the log. That is why none of the reporter's files can
establish whether a scan ran at all in the session that died. Every future report is undiagnosable
until this changes.

Log at each boundary through `Plugin.Log.Information`, so the lines land in `dalamud.log`, which
reporters already know how to send:

```
Scan requested
Scan materialize begin
Scan materialize complete: N mods, M ms
Scan worker started
Scan worker complete: N results
Scan publish begin
Scan publish complete
```

plus the existing outcome paths (cancelled, stale, failed) which already carry a reason.

The last line written before the log goes silent identifies the phase that died. If the crash is the
race described above, the trail stops between "materialize begin" and "materialize complete" and the
diagnosis is settled.

Deliberately not a separate file with forced flushes. A dedicated file with `Flush(true)` per line
would survive a hard kill more reliably, but it is a new artifact reporters do not know to send, and
it costs a synchronous disk write per boundary. `dalamud.log` is the file people already attach.
Accepted risk: a hard native kill may lose the last buffered line or two, so treat the final logged
checkpoint as "at least this far", not "exactly this far".

Per-mod logging is not included. 300 lines per scan buys precision that only matters if the crash
turns out to be inside one specific mod's disk read, which the fault isolation in
`ModEquipmentFileReader` already argues against. If the checkpoint trail points there, per-mod
logging is the obvious follow-up.

### What is not changed

- `Publish` already runs on the framework thread via `Update()`. Untouched.
- The background phase is already free of Dalamud and Penumbra types, enforced by an architecture
  test. Untouched.
- Penumbra's `Disposed` event still only bumps the epoch rather than cancelling an in-flight run.
  The run's results are discarded as stale, and its seeds are plain strings, so nothing reaches back
  into unloaded Penumbra state. Wasted work, not a hazard. Out of scope here.

## Testing

The coordinator is unit-testable without a game, and its tests already drive `Update()` directly.

- Existing coordinator tests that call `Start` and assert on the result of materializing must now
  call `Update()` first. This is a real change to the test surface and every affected test is
  updated rather than weakened.
- New: `Start_DoesNotMaterialize_UntilUpdate`. A job whose `Materialize` records that it ran; assert
  it has not run after `Start`, and has run after one `Update`. This is the test that would have
  caught the defect.
- New: `Start_ClosesTheGateImmediately`. After `Start` and before any `Update`, `State.Phase` is
  `Materializing` and `State.IsRunning` is true, so a second `Start` throws.
- New: `Materialize_ThrowingOnUpdate_SettlesFailedWithTheMessage`. Confirms the failure path
  survives the move.
- New: `Epoch_ChangedBetweenStartAndUpdate_InvalidatesTheRun`. Pins the decision to read the epoch at
  materialize time.

Checkpoint logging is verified through the coordinator's existing injectable log delegate rather than
by asserting on Dalamud's logger.

## Release

This is the substantive fix 0.5.2.0 was meant to be. It ships as 0.5.3.0 with the corrected Known
Issues section, and the release notes state plainly that the earlier hazard entry was wrong.
