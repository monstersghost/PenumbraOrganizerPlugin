# Multi-Save-Point Rollback Design

**Status:** Approved, ready for planning
**Date:** 2026-07-20

## Problem

Apply currently supports exactly one rollback point: `Plugin.cs` writes a
single `organizer-backup.json` before every Apply, and "Rollback" always
means "undo the most recent Apply." There is no history — once a second
Apply runs, the ability to go back further is gone. This mirrors an Open
Risk carried since Phase 2 and was raised again as a gap relative to the
standalone app, which supports choosing among multiple past save points.

## Scope

Applies to **Apply's rollback only**. Folder Cleanup keeps its existing
single-backup-file mechanism (`organizer-folder-backup.json`) unchanged —
it was hardened separately (write-race guard) earlier and is not part of
this change.

## Data Model & Storage

Replace `organizer-backup.json` with a history file, `organizer-history.json`,
holding a JSON array of snapshot records:

```csharp
public sealed record RollbackSnapshot(
    Guid Id,
    DateTimeOffset CreatedAt,
    string? Label,
    string AutoDescription,          // e.g. "14 mods moved"
    IReadOnlyDictionary<string, string> ModPaths); // Identifier -> FullPath, every known mod at that moment
```

Each snapshot is a **full state capture**: every mod Penumbra currently
reports, with its full path, not just the mods touched by whatever
operation triggered the snapshot. Restoring reproduces the historical
*folder placement* of mods that still exist and still resolve to the same
identifier — it does not restore mod content, version, enabled state, or
anything else about a mod, and it cannot restore a mod that was later
uninstalled. That's the scope of the guarantee; the feature doesn't claim
more than that.

`CaptureSnapshot` builds the `ModPaths` map via `ToDictionary` over
`ReadCurrentModPaths()`'s identifiers, which already throws if Penumbra
ever reports two mods under the same identifier. That's the correct
behavior — a capture must fail loudly on a duplicate identity rather than
silently keep one and drop the other. Document this as a deliberate
invariant, not an implementation accident: **capture fails if Penumbra
reports duplicate identifiers.**

Written via the same atomic-write pattern `FolderCleanupExecutor` already
uses (write to a temp file, then replace) so a crash mid-write can never
corrupt the history file.

No migration from the old `organizer-backup.json` format — the new code
never reads it. The file itself is left on disk untouched (not deleted or
overwritten) so a user who has a pending single backup at upgrade time
isn't stripped of their only rollback option; it's just an inert leftover
file at that point, not a maintained feature.

## Snapshot Lifecycle

**Creation triggers** — all funnel through one helper:

```csharp
RollbackSnapshot CaptureSnapshot(string? label, string autoDescription)
```

which reads every mod's current path via the existing
`ReadCurrentModPaths()` (Penumbra IPC — does **not** require a Scan or any
`OrganizerState`), builds the `ModPaths` map, and appends the record to the
history file. If capture throws (IPC unavailable, duplicate identifiers,
write failure) for an *automatic* pre-Apply or pre-Restore capture, the
Apply/Restore it was guarding does not proceed — the caller must not
mutate mod paths without a valid snapshot behind it. A failed *manual*
"Create Backup" simply produces no snapshot and reports the error.

Triggered automatically:
- **Before every Apply** (as today, just writing a history entry instead of
  overwriting a single backup file). `AutoDescription`: "N mods moved".
- **Before every Restore** (see below), so a Restore is itself undoable.
  `AutoDescription`: "snapshot before restoring to <target label or
  timestamp>".

Triggered manually:
- **"Create Backup" button** in the new History tab, with an optional label
  field. Works independent of Scan/Apply — the user can snapshot current
  state at any time the plugin is connected to Penumbra.

**Concurrency guard:** Apply, Restore, Create Backup, and Delete all check
and set a single `_operationInProgress` flag on `Plugin` before running and
clear it when done (mirroring how Apply already guards against re-entrant
clicks); a second trigger while one is in flight is a no-op with a status
message. ImGui draws are single-threaded on the game's render thread, so
this is about preventing double-clicks/re-entrant triggers, not real
multi-threaded races — a full lock/semaphore service is unnecessary here.

## Restore

`Restore(Guid snapshotId)`:

1. Capture a pre-restore snapshot (see above) — makes the restore
   reversible.
2. Diff the target snapshot's `ModPaths` against `ReadCurrentModPaths()`:
   - **In both, path differs** → build a `ModMove` to the snapshot's
     recorded path.
   - **In both, path same** → no move needed.
   - **Only in the target snapshot** (mod no longer installed) → skip.
     Collected into a "skipped — no longer installed" report list.
   - **Only in current state** (mod installed after the snapshot was taken)
     → build a `ModMove` targeting the Penumbra root (`FixName(row.Name)`,
     the same leaf-name convention `PenumbraPathSemantics.FixName` already
     produces elsewhere) — dropped at the top level rather than into a
     named subfolder. This sidesteps the naming/collision questions a
     dedicated folder would raise (duplicate names, an existing folder
     with that name, path-length/character edge cases): root placement has
     no folder to collide with.
   - **Currently protected or Heliosphere-managed mod** → skip regardless
     of what the snapshot says. Current protection state always wins over
     historical snapshot content — a snapshot must never be a way to move
     a mod the user has since locked. Collected into a "skipped —
     protected" report list.
3. Run the resulting moves through the existing
   `ApplyPlanner.OrderMovesForApply` → the same `ExecuteOrderedMoves` path
   Apply already uses. No new move-ordering/cycle-breaking logic is needed.
4. Report: moved count, skipped-uninstalled list, root-relocated list,
   skipped-protected list, any per-identifier IPC failures — same shape as
   Apply's existing result reporting.

Before executing, the Restore confirmation dialog shows this breakdown up
front (counts per category above) rather than a bare "are you sure" — so
the user sees the scope of the operation (including how many currently-
unlisted mods will be dropped at root) before confirming.

**Manual delete:** removes one record from the array by `Id`, rewrites the
file atomically. Snapshots are self-contained (each stores a full state,
not a diff against a neighbor), so deleting one has no effect on any other
entry.

## UI

New **"History"** tab in `MainWindow.cs`, added to the existing
`ImRaii.TabBar("MainTabs")`:

- Top: "Create Backup" button + optional label text input.
- Below: scrollable list of snapshots, newest first. Each row shows
  timestamp, label (if set) or else the auto-description, and mod count.
  Each row has "Restore" and "Delete" buttons.
- "Restore" opens a confirmation prompt showing the breakdown described
  above (moved / unchanged / skipped-uninstalled / root-relocated /
  skipped-protected counts) before the user confirms.
- After a Restore completes, show the result summary (moved /
  skipped-uninstalled / root-relocated / skipped-protected / failures), the
  same way Apply's result is currently surfaced.

## Files

**New:**
- `Organizer/RollbackHistory.cs` — `RollbackSnapshot` record,
  `CaptureSnapshot`, `LoadHistory`/`SaveHistory` (atomic write),
  `DeleteSnapshot`, `BuildRestoreMoves` (the diff logic above).
- `Organizer.Tests/RollbackHistoryTests.cs` — snapshot round-trip,
  restore-move diffing (identical/changed/uninstalled/root-relocated/
  protected cases), duplicate-identifier capture failure, delete.

**Modified:**
- `Plugin.cs` — replace single-backup `WriteBackup`/`ReadBackup`/
  `RollbackLastApply` with calls into `RollbackHistory`; Apply calls
  `CaptureSnapshot` before running.
- `Windows/MainWindow.cs` — new `DrawHistoryTab()`, wired into the tab bar.

## Testing

Unit tests cover `RollbackHistory` in isolation (snapshot persistence,
restore-diff logic for all move/unchanged/skip/relocate cases, duplicate-
identifier capture failure, delete). Reuses
`ApplyPlanner.OrderMovesForApply`/`ExecuteOrderedMoves`, which already has
its own cycle/chain test coverage — no new move-ordering tests needed.
No in-game verification plan is included here; that happens after
implementation, same as prior phases.

## Explicitly Out of Scope

- Folder Cleanup's backup mechanism (unchanged, separate system).
- Any retention cap or automatic pruning — history grows indefinitely,
  pruned only by manual delete.
- Migrating old single-backup files forward.
