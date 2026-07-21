# Handoff: Folder Cleanup (organization.json orphaned-folder prune)

Merged to `main` (`09fcaeb`). This note is for whoever picks up the remaining in-game verification
or the next phase of work.

## What's on `main` now

Detects orphaned (empty) folder entries in Penumbra's `organization.json` and lets the user prune
selected ones, with byte-fidelity backup/rollback, as a separate action from mod-move Apply/Rollback.
This is the plugin's **second write target** — but plain file I/O against `organization.json`, not
an IPC call like Phase 2's `SetModPath`.

- `Organizer/OrganizationJson.cs` / `OrganizationJsonCodec.cs` — pure data model + never-throws
  status-carrying parse/serialize, `[JsonExtensionData]` on every type so unknown fields survive a
  round-trip.
- `Organizer/OrganizationCleanupPlanner.cs` — pure detection/prune logic: `GetVirtualParent`,
  `DetectOrphaned`, `Prune`.
- `Organizer/FolderCleanupExecutor.cs` — all file-I/O sequencing (cleanup + rollback), no IPC. Target
  write happens *before* backup promotion (reversed order would destroy the previous backup on a
  failed target write). Byte-fidelity backup: the *original* bytes read before pruning, never a
  reread of the post-prune file.
- `Organizer/OrganizerState.cs` — new `HasScanned` property, distinguishes "never scanned" from
  "scanned, genuinely empty library" (the latter is exactly where every persisted folder may
  legitimately be orphaned).
- `Plugin.cs` — `DetectOrphanedFolders()`/`CleanUpFolders()`/`RollbackFolderCleanup()`.
- `Windows/MainWindow.cs` — new Orphaned Folders section on the Review Changes tab: two-tier
  plain/customized checkbox list, confirm popup, a persistent "Rediscover Mods required" banner
  (no reload IPC exists — user must click Penumbra's own Rediscover Mods).

Design: `docs/superpowers/specs/2026-07-15-plugin-organizer-folder-cleanup-design.md`.
Plan: `docs/superpowers/plans/2026-07-15-plugin-organizer-folder-cleanup.md`.

171 tests pass, build clean.

## Key decisions, in case they need revisiting

- Occupancy is `CurrentPath` (detection, advisory, last-scan) plus a fresh IPC read at write time
  (enforcement) — **never `ProposedPath`**. Deliberate: `DetectOrphanedFolders` intentionally does
  NOT make an IPC call (advisory list only); only `CleanUpFolders` does (fresh `GetModListAdapter`
  read), and that's the actual safety net.
- `Folders` and `Separators` are disjoint in Penumbra's schema; pruning only ever touches `Folders`
  and carries `Separators`/unknown top-level `ExtensionData` through by reference, untouched.
- File encoding: UTF-8 **without** BOM (`new UTF8Encoding(false)`). Confirmed against a real
  install's `organization.json` during this session's in-game verification — first bytes are `7b 0a`
  (`{` + newline), no `EF BB BF` prefix. No code change was needed.

## In-game verification status — partially done, 4 items explicitly deferred

Verified in-game this session (2026-07-15), across three live cleanup runs on a real ~239-mod
library (229 → 112 → 21 folders pruned across separate runs, no corruption):

- **Item 1 (detect real pre-existing orphans):** confirmed — real orphans detected every run.
- **Item 2 (Sort without Apply excludes occupied folders):** confirmed.
- **Item 3 (clean up a plain-empty folder):** confirmed — Success message demands Rediscover Mods;
  pruned folders were confirmed to still be visible in Penumbra's own Mods folder tree until
  Rediscover Mods was clicked, then vanished.
- **Item 4 (customized-empty folder extra friction):** confirmed — separate "Empty but customized"
  section, unchecked by default, correct description (e.g. "Gear/AeAstralis (custom color)").
- **Item 5 (rollback restores folder with customization intact):** confirmed — custom color survived
  a prune + rollback cycle.
- **Item 7 (stale-selection race via Penumbra's own UI):** confirmed — the write-time fresh-IPC
  re-verification held up under real bulk runs (large cleanup batches ran clean with no incorrect
  deletions).

**Explicitly deferred to a future session (user's call, not blocked on anything):**
- **Item 6:** mod placements unaffected throughout — not diffed.
- **Item 8:** reload banner clears only via Scan, not bare Rediscover Mods — not exercised.
- **Item 9:** a 0-mod library still detects orphans via `HasScanned` (not `Mods.Count`) — not
  exercised (needs a temporarily-empty library to test).

None of these are known-broken — they're just the specific safety-net edge cases nobody has
exercised yet. See the plan's Task 7 "In-game verification" section for the exact checklist wording.

## Process note

Executed via subagent-driven-development, 7 tasks + final whole-branch review, all clean — no
worktree-boundary violations, continuing the streak since the fix documented in
`docs/HANDOFF_PHASE2_APPLY.md`'s process note.
