# Plugin organizer, Phase 2: Apply (virtual-path writes) — Design

**Status:** approved, not yet implemented. Revised 2026-07-15 after an external design review — see
"Revision notes" at the end for what changed and why.

## Context

Every phase through Phase 1f is read-only: `SortByCreator`/`SortByModType`/`SortByTypeThenCreator`/
`SortByCreatorThenType`/`AssignManual` only ever set `ProposedPath` in memory. The Review Changes
tab's "Apply" button has been disabled since Phase 1, pending an explicit scope decision — per
`docs/ROADMAP.md`, this is that decision, reached via brainstorming with the user on 2026-07-15.

Before any design work, the actual write mechanism was researched in depth, since the original
`docs/ROADMAP.md` framing ("An Apply button that can rename/move mod folders with no way back is the
single riskiest thing this plugin could ship") turned out to overstate the risk:

- `Penumbra.Api`'s `SetModPath(modDirectory, newPath, modName)` sets Penumbra's own internal virtual
  folder/sort-order path for a mod. It does not touch the physical mod directory on disk in any way —
  confirmed against the standalone app's own definitive, source-code-verified investigation
  (`docs/PROJECT_CONTEXT.md` and `docs/KNOWN_ISSUE_EMPTY_FOLDERS_AFTER_RESORT.md` in
  `C:\Repo\PenumbraOrganizer`): "Normal organization operations must not... move physical mod
  directories... The primary operation is changing supported Penumbra logical or virtual-folder
  metadata." Mod placement is read fresh from Penumbra's own `mod_data.db` (a LiteDB file, confirmed
  by inspecting its magic bytes) on every load; no physical file ever moves.
- This means Apply here is a config-value write, not a file operation — much lower blast radius than
  the original framing assumed, and backup/rollback can be correspondingly simpler: capturing each
  mod's previous path value, not a filesystem snapshot.
- A known, separate issue was investigated and explicitly ruled out of scope: Penumbra tracks folder
  *existence* (`organization.json`) independently of mod *placement* (`mod_data.db`), and nothing in
  Penumbra's own move/placement logic prunes an entry from the former when a folder becomes empty —
  confirmed structural (via reading Penumbra's own source, `Ottermandias/Luna`'s
  `BaseFileSystem.Delete`), not an artifact of any particular tool's write method. The capability to
  delete a folder exists inside Penumbra (used by its own UI) but is never exposed via any documented
  IPC. Closing this gap would require directly reading/writing `organization.json` — the same category
  of scope expansion already parked for detailed gear-slot sorting in `docs/ROADMAP.md`, with the
  added risk of a live-file write race against Penumbra's own process. Deliberately not attempted here;
  see Non-goals.

## Goal

Enable a real Apply action that writes each unprotected, changed mod's `ProposedPath` to Penumbra via
`SetModPath`, with a rolling one-Apply backup and a Rollback action, gated on `Validate()` showing no
issues, with a bulk bypass for mods blocking Apply that shouldn't be.

## Non-goals

- Any write beyond `SetModPath` — no `meta.json`/`mod_data.db` metadata edits (creator-name
  normalization, Favorite/Tags/Notes), no `organization.json` folder-structure edits. See Context.
- Fixing Penumbra's orphaned-empty-folder behavior. Explicitly out of scope, tracked as its own
  parking-lot item in `docs/ROADMAP.md` next to detailed gear-slot sorting — both are "reach past
  documented IPC into Penumbra's own internal state" decisions, evaluated separately from this spec.
- A multi-Apply rollback history. One rolling backup, covering only the most recent Apply — see
  Backup/rollback mechanics.
- A forced re-scan immediately before Apply as an additional gate. Considered during brainstorming and
  not adopted — `Validate()` showing no issues is the only gate; if a mod genuinely disappeared from
  Penumbra between scan and Apply, that surfaces as a per-mod `ModMissing` failure in the Apply result
  summary instead (see Write mechanics), which the best-effort failure handling already covers.
- Any change to `AssignManual`'s existing protected-row rejection, `CollisionDisambiguator`, or any
  sort strategy. All are reused unmodified.

## Architecture

A new pure, static class, `Organizer/ApplyPlanner.cs` — the parts of this feature that need no live
IPC and can be unit-tested, following the established pattern (`CollisionDisambiguator`,
`OrganizerExportFormatter`):

```csharp
public sealed record BackupEntry(string Identifier, string PreviousPath);
public sealed record ApplyResult(string Identifier, bool Success, string? FailureReason);

public static class ApplyPlanner
{
    public static IReadOnlyList<BackupEntry> BuildBackup(IReadOnlyList<OrganizerModRow> touchedRows);
    public static IReadOnlySet<string> BlockingIdentifiers(ReviewResult validation);
    public static IReadOnlyList<BackupEntry> Retain(
        IReadOnlyList<BackupEntry> entries, IReadOnlyList<ApplyResult> results, bool keepSuccessful);
}
```

`BuildBackup` takes exactly the rows about to change (unprotected, `ProposedPath != CurrentPath`) and
records each one's `Identifier` and its *current* `CurrentPath` (the value to restore on rollback).
Output is sorted ascending by `Identifier` (deterministic ordering for tests, diffs, and manual
inspection of the backup file) and de-duplicated by `Identifier` if the input somehow contains repeats
(shouldn't happen given `OrganizerState`'s existing invariants, but `BuildBackup` guards it rather than
writing a backup that could rollback-write the same mod twice).

`BlockingIdentifiers` takes a `ReviewResult` (already produced by `OrganizerState.Validate()`) and
returns the union of every `Identifier` appearing in `ProtectedViolations` or anywhere in
`PathCollisions`' identifier lists — the exact set the bulk bypass action needs to revert-and-protect.

`Retain` is the one small piece of pure logic shared by both directions of the backup-shrinking flow
(see Backup/rollback mechanics): given the entries just attempted and their `ApplyResult`s, it returns
only the entries whose outcome matches `keepSuccessful`. Called with `keepSuccessful: true` after a
forward Apply (keep only entries that actually wrote — those are the valid rollback candidates), and
with `keepSuccessful: false` after a Rollback (keep only entries that failed to restore — retained for
a future rollback attempt).

`Plugin.cs` gets the IPC-touching methods that consume `ApplyPlanner`'s output:

```csharp
internal IReadOnlyList<ApplyResult> ApplyChanges();
internal IReadOnlyList<ApplyResult> RollbackLastApply();
internal void ProtectAndSkipBlockingMods();
```

`RollbackLastApply` returns the same `ApplyResult` shape as `ApplyChanges` — both are batch operations
over a list of identifiers, and the UI renders both summaries the same way (see UI). If no backup file
exists, it returns an empty list (nothing was attempted) rather than a `bool false`.

**Confirmed against the real assembly (2026-07-15):** the original draft inferred
`SetModPath(string modDirectory, string modName, string newPath)` from the XML doc comment's prose
order — this was **wrong**. Reflecting directly against the referenced `Penumbra.Api` 5.15.1 DLL (via a
throwaway console project targeting the same TFM) gives the actual signature:

```csharp
PenumbraApiEc SetModPath.Invoke(string modDirectory, string newPath, string modName)
```

`newPath` is the second parameter, `modName` the third — the reverse of what the doc comment's
paramref-mention order suggested. The call this spec needs is therefore
`SetModPath(Identifier, ProposedPath, "")` (forward) / `SetModPath(Identifier, PreviousPath, "")`
(rollback) — `modName` passed empty, matching `ModWrapper.Identifier`'s confirmed doc comment ("the
unique identifier (directory name) of the mod") as the correct value for `modDirectory`.
`PenumbraApiEc`'s full member list was also confirmed by reflection: `Success`, `ModMissing`,
`InvalidArgument`, and `PathRenameFailed` all exist as named members, matching the four outcomes
`SetModPath`'s doc comment promises. This closes Open Risk #1 from the original draft before any
implementation work begins.

## Backup/rollback mechanics

Before any `SetModPath` call in `ApplyChanges()`:

1. Compute the touched-row set (unprotected, `ProposedPath != CurrentPath`) from the current
   `OrganizerState`.
2. Call `ApplyPlanner.BuildBackup` on it.
3. Write the result to `organizer-backup.json` in the plugin's config directory: serialize to
   `organizer-backup.json.tmp`, then replace the real file with it (`File.Replace`, or
   write-then-`File.Move` with overwrite — implementation detail for the plan). Fixed filename,
   matches the Export feature's established fixed-filename convention, but unlike Export this file has
   operational meaning, not just a human-readable snapshot, so a half-written file from a crash mid-save
   must never be mistaken for a valid backup. If this write fails for any reason (e.g. disk full), abort
   Apply before any `SetModPath` call — see Error handling.
4. Proceed to the actual `SetModPath` calls (Write mechanics, below).
5. **After the full batch completes** (regardless of any individual failures): call
   `ApplyPlanner.Retain(backupEntries, applyResults, keepSuccessful: true)` and atomically rewrite
   `organizer-backup.json` with just that filtered list — entries for mods that never actually wrote
   are dropped, since there's nothing to roll back for them. If the filtered list is empty (nothing
   succeeded), delete the file instead of writing an empty one.

This means the backup file always reflects *what actually changed*, not merely what Apply intended to
change — fixing a scenario the original draft missed: if mod A succeeds and mod B fails during Apply,
and the user separately re-organizes mod B by hand afterward, a later Rollback must not overwrite that
legitimate change by replaying mod B's stale pre-Apply path. Since Rollback only ever restores entries
that are still in the (now-filtered) backup, and B was dropped from it after the failed forward write,
this can't happen.

`RollbackLastApply()`:

1. If `organizer-backup.json` doesn't exist, return an empty result list immediately (nothing to roll
   back) — the Rollback button is only enabled in the UI when the file exists, but the method itself is
   defensive regardless of UI state.
2. Deserialize the backup entries.
3. For each entry, call `SetModPath(Identifier, PreviousPath, "")` — same best-effort-continue
   semantics as a forward Apply (see Write mechanics); a mod that's since disappeared just fails that
   one entry.
4. Call `ApplyPlanner.Retain(backupEntries, rollbackResults, keepSuccessful: false)` and atomically
   rewrite `organizer-backup.json` with just that filtered list — entries that restored successfully are
   dropped (nothing left to roll back for them); entries that failed to restore are kept, so a second
   Rollback click can retry just those instead of silently having nothing left to recover. If the
   filtered list is empty (everything restored), delete the file.
5. Trigger a fresh `RunScan()`, same as after a forward Apply.
6. Return the per-entry `ApplyResult` list for the UI to render as a summary (see UI).

This is intentionally a single rolling backup, not a history — considered and explicitly not adopted
during brainstorming (see Non-goals). "Rollback" always means "undo the most recently completed Apply,"
where "the backup" now always means "whatever from that Apply hasn't been successfully rolled back yet."

## Apply gating and bypass

The existing disabled `ImGui.Button("Apply (disabled in Phase 1)")` in `DrawReviewTab()` becomes a real,
conditionally-enabled button: `ImGui.BeginDisabled()` wraps it exactly when
`_plugin.OrganizerState.Validate().HasIssues` is `true`. This is the only gate — no forced re-scan (see
Non-goals).

A new **"Protect & Skip All Blocking Mods"** button appears only when `Validate().HasIssues` is `true`
(no reason to show it otherwise). Clicking it calls `_plugin.ProtectAndSkipBlockingMods()`, which:

```csharp
var rowsById = OrganizerState.Mods.ToDictionary(m => m.Identifier);
foreach (var identifier in ApplyPlanner.BlockingIdentifiers(OrganizerState.Validate()))
{
    if (!rowsById.TryGetValue(identifier, out var mod))
        continue; // identifier no longer present (e.g. mod removed since scan) — nothing to protect
    OrganizerState.AssignManual(identifier, mod.CurrentPath);  // revert while still unprotected
    OrganizerState.SetProtected(identifier, true);              // then protect
}
SaveProtectionState();
```

Order matters: `AssignManual` rejects already-protected rows (`OrganizerState.cs`'s existing check), so
the revert must happen before the protect, not after. This makes every currently-blocking mod
permanently protected (same persistence as manually checking a box in the Protect tab — no special
"temporary" protection concept), consistent with how protection already works everywhere else in this
plugin. The dictionary lookup (rather than `.First(...)`, which throws) means a blocking identifier
that's vanished from the current scan between `Validate()` and this loop is silently skipped instead of
crashing the whole bulk action.

## Write mechanics

`ApplyChanges()`:

0. **Validate internally before doing anything else:** call `OrganizerState.Validate()` and throw
   `InvalidOperationException` if `HasIssues` is `true`. The Review tab's Apply button is already
   disabled whenever this is the case (see Apply gating and bypass), so reaching this in normal use
   means the UI's own gate was bypassed — a programming-error guard, not an expected user-facing
   outcome, so it's proportionate to throw here rather than invent a `Blocked` result type solely to
   cover a state the UI itself prevents. This is the one invariant the command method enforces itself
   rather than trusting the caller.
1. Compute the touched-row set and write the backup (see Backup/rollback mechanics).
2. For every row where `!Protected && ProposedPath != CurrentPath` (the same touched-row definition the
   backup used): call `SetModPath(Identifier, ProposedPath, "")`.
3. Continue through every row regardless of individual failures — explicitly chosen during
   brainstorming over stop-on-first-failure, since the backup already covers full recovery either way,
   and best-effort maximizes forward progress for what is fundamentally a batch operation.
4. Record each attempt as an `ApplyResult` (`Identifier`, `Success`, and `FailureReason` derived from
   `SetModPath`'s return value when not `Success` — `InvalidArgument`/`ModMissing`/`PathRenameFailed`).
5. Rewrite the backup to keep only the entries that actually succeeded (see Backup/rollback mechanics,
   step 5).
6. Call `RunScan()` to refresh `OrganizerState` from Penumbra's actual live state — a mod that succeeded
   now shows its new path as `CurrentPath`; a mod that failed still shows its old one, reflecting what
   actually happened rather than trusting the write blindly.
7. Return the `ApplyResult` list for the UI to render as a summary.

## UI

Review Changes tab (`DrawReviewTab()`):

- **Apply** button: disabled when `Validate().HasIssues`. Clicking it (when enabled) opens a
  confirmation step — "Apply changes to N mods?" (N = the touched-row count) — before calling
  `ApplyChanges()`. After it returns, render a summary: count succeeded / count failed, with failed
  identifiers and their reasons listed.
- **Protect & Skip All Blocking Mods** button: visible only when `Validate().HasIssues`.
- **Rollback** button: visible only when `organizer-backup.json` exists on disk (a simple
  `File.Exists` check at draw time, consistent with how `MainWindow` already checks `_lastExportPath`
  for the Export feature's path display). Clicking it calls `RollbackLastApply()` and shows the same
  count-succeeded/count-failed summary shape as Apply. If any entries failed to restore, the summary
  also notes that the backup file was retained and the button remains visible so the user can retry —
  avoid wording that implies rollback is all-or-nothing (e.g. don't say "Rollback complete" when some
  entries are still pending).

## Data flow

Same overall shape as every prior phase: `OrganizerState` holds the in-memory model, sort strategies
and `AssignManual` only ever set `ProposedPath`, `Validate()` is a pure read over that state. Apply is
the first feature that also reads/writes a persistent file with *operational* meaning (the backup) and
calls a write IPC. Nothing about `OrganizerState`'s own API changes — `ApplyChanges`/`RollbackLastApply`/
`ProtectAndSkipBlockingMods` all live on `Plugin`, consuming `OrganizerState`'s existing public surface
(`Mods`, `Validate()`, `AssignManual`, `SetProtected`) exactly as `RunScan`/`SaveProtectionState`
already do.

## Error handling

`SetModPath` failures are expected, ordinary outcomes (a mod removed between scan and Apply, an
invalid path), not exceptional conditions — handled via the `ApplyResult` list and best-effort
continuation, not exceptions. IPC-unavailable (Penumbra not running) at Apply time surfaces the same
way `RunScan`'s existing try/catch already handles it for scanning — an `ApplyChanges()` call that
can't reach Penumbra at all should throw and be caught at the same `MainWindow` call-site pattern
already established for `RunScan()`, rather than silently producing an all-`ModMissing` result list.
Backup-file write failure (e.g. disk full) should abort the Apply *before* any `SetModPath` call is
made — writing changes with no backup in place would defeat the entire safety mechanism this spec
exists to provide.

## Testing

`ApplyPlanner`'s three functions are pure and get direct unit tests, same pattern as
`CollisionDisambiguator`/`OrganizerExportFormatter`:

- `BuildBackup`: empty touched-row list; a single row; multiple rows; confirms `PreviousPath` is each
  row's `CurrentPath` (not `ProposedPath`); confirms output is sorted ascending by `Identifier`;
  confirms a duplicate `Identifier` in the input is de-duplicated to one entry.
- `BlockingIdentifiers`: empty `ReviewResult` → empty set; a `ProtectedViolations`-only result; a
  `PathCollisions`-only result with multiple identifiers at one colliding path; both present at once,
  confirming the union (no duplicates if the same identifier somehow appears in both).
- `Retain`: `keepSuccessful: true` with a mix of successful/failed results returns only the successful
  entries; `keepSuccessful: false` returns only the failed ones; an entry with no matching result is
  excluded either way (defensive — shouldn't happen since results are always produced from the same
  entry list, but `Retain` shouldn't silently keep an entry it can't confirm an outcome for); empty
  input → empty output.

`ApplyChanges`/`RollbackLastApply`/`ProtectAndSkipBlockingMods` and the backup file's actual
read/write round-trip are only verifiable in-game (real `SetModPath` IPC, real file I/O) — this is the
plugin's first write IPC call, so in-game verification carries more weight here than for any prior
phase. At minimum: Apply on a small, deliberately-chosen set of mods, confirm they move in Penumbra's
own UI; Rollback afterward, confirm they return to their original folders; a forced failure case (e.g.
protect a mod mid-flight, or disable a mod that's part of the batch) to confirm the best-effort
continuation and summary reporting actually work, not just the happy path; and specifically, a forced
*rollback* failure (e.g. disable a mod between Apply and Rollback so its restore fails) to confirm the
backup file is retained afterward (not deleted) and a second Rollback click only retries the pending
entry rather than replaying everything.

## Open risks

1. ~~`SetModPath`'s exact parameter order is inferred, not yet confirmed against the real assembly.`~~
   **Resolved 2026-07-15** — confirmed by reflection against the real `Penumbra.Api` 5.15.1 assembly
   before writing the implementation plan; see Architecture.
2. **Concurrent modification during a long Apply batch.** If the user (or Penumbra itself) changes
   something mid-batch — e.g. disables a mod that's queued later in the same Apply — the best-effort
   continuation should simply record that mod's failure and move on. Not expected to be common given
   this plugin's own UI is the only thing driving `SetModPath` calls during an Apply, but worth naming
   since nothing prevents alt-tabbing to Penumbra's own UI mid-Apply.
3. **The orphaned-folder limitation (see Context) is real and will be visible to users of this
   feature.** Documented here and in the handoff/roadmap docs once implemented, not silently absorbed —
   consistent with how every other IPC gap in this project has been handled.

## Revision notes

The first draft of this spec went through an external design review before implementation started.
Adopted, folded into the sections above:

- Rollback used to delete `organizer-backup.json` unconditionally, which could permanently lose the
  recovery record for entries that failed to restore. Fixed: the backup is rewritten after both the
  forward Apply and the Rollback to keep only the entries still needing action, and deleted only when
  empty (Backup/rollback mechanics, `ApplyPlanner.Retain`).
- The backup used to record every touched row regardless of whether its forward write actually
  succeeded, which could let a Rollback overwrite an unrelated legitimate change to a mod whose Apply
  had failed. Fixed by the same rewrite-after-batch mechanism above — a mod is only ever a rollback
  candidate if its forward write is confirmed to have succeeded.
- `ApplyChanges()` relied solely on the UI disabling the Apply button when `Validate()` had issues.
  Fixed: the method now checks this itself and throws if bypassed (Write mechanics, step 0).
- `RollbackLastApply()` returned a bare `bool`, which can't carry a per-mod success/failure summary the
  way `ApplyChanges()`'s `ApplyResult` list can. Fixed: both now return the same `ApplyResult` shape.
- `ProtectAndSkipBlockingMods()`'s `.First(...)` lookup would throw if a blocking identifier had since
  disappeared from the scan. Fixed: safe dictionary lookup that skips missing identifiers.
- `BuildBackup` didn't guarantee ordering or reject duplicate identifiers. Fixed: sorted output,
  de-duplicated input.
- The backup file write was a plain overwrite, risking a truncated/corrupt file if the process died
  mid-write. Fixed: write-to-temp-then-replace (Backup/rollback mechanics, step 3).

Separately, while preparing to write the implementation plan, `SetModPath`'s parameter order was
confirmed by reflection against the real `Penumbra.Api` 5.15.1 assembly (Architecture). The order the
first draft inferred from the doc comment's prose (`modDirectory, modName, newPath`) was wrong — the
real signature is `(modDirectory, newPath, modName)`. All call sites in this spec have been corrected.
This is exactly the kind of gap Open Risk #1 existed to catch before implementation.

Considered and deliberately not adopted, to keep this phase proportionate to a solo-maintained,
single-user, synchronously-executed ImGui plugin:

- **A fully versioned backup document with a 5-state enum (Prepared/Applying/Applied/RollingBack/
  PartiallyRolledBack) and per-write incremental persistence.** The simpler "rewrite the backup once
  after the batch completes" approach above closes the same two real gaps (stale-intent backups,
  unconditional deletion) without a state machine. The remaining gap this leaves — a process crash
  mid-batch, before the post-batch rewrite — is real but narrow, and not worth the added complexity for
  a manually-triggered, button-click-driven batch operation.
- **An `IModPathWriter` abstraction so `ApplyChanges`/`RollbackLastApply`'s orchestration logic could
  run against a fake in unit tests.** Every prior phase's IPC-touching code (`RunScan`,
  `SaveProtectionState`, `ExportReview`) is verified in-game only, by design; introducing a DI/interface
  layer here would be a larger architectural shift than the feature itself and inconsistent with the
  rest of the codebase. `ApplyPlanner`'s pure functions (including the new `Retain`) already carry all
  the logic that can meaningfully be unit-tested without live IPC.
- **Stale/invalid-backup state detection, a separate "discard recovery record" action, an explicit
  operation-busy lock, and snapshotting the Apply plan before the confirmation dialog.** All reasonable
  hardening, but ImGui's single-threaded draw loop already rules out the concurrent-operation scenarios
  these guard against, and a corrupt/unreadable backup file is an edge case rare enough to handle if it
  actually comes up rather than design for now.
