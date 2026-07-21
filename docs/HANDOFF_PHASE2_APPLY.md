# Handoff: Phase 2 Apply (write support)

Merged to `main` (`c4c9c1f`). This note is for whoever picks up in-game verification or the next
phase of work.

## What's on `main` now

Phase 2 turns the Review Changes tab's "Apply" button from a disabled placeholder into a real
feature. This is the plugin's **first-ever write IPC call** — everything through Phase 1f was
read-only.

- `Organizer/ApplyPlanner.cs` — new pure, static, unit-tested class: `BuildBackup`,
  `BlockingIdentifiers`, `Retain`. No live IPC, fully covered by `ApplyPlannerTests.cs`.
- `Plugin.cs` — `ApplyChanges()`, `RollbackLastApply()`, `ProtectAndSkipBlockingMods()`,
  `BackupExists`, plus the `SetModPathIpc` field and atomic backup-file read/write helpers. None of
  this is unit tested — matches the existing convention for `RunScan`/`SaveProtectionState`/
  `ExportReview`, which also touch live IPC / config-directory file I/O and have no tests either.
- `Windows/MainWindow.cs` — `DrawReviewTab()` now has a real Apply button (confirmation popup +
  result summary), a "Protect & Skip All Blocking Mods" button (visible only when `Validate()` has
  issues), and a Rollback button (visible only while a backup file exists — its summary now renders
  unconditionally, a bug caught by the final whole-branch review before merge).

Design: `docs/superpowers/specs/2026-07-15-plugin-organizer-phase2-apply-design.md` — read this
first if picking up anywhere in this area, it has the full rationale including an external design
review's findings and what was/wasn't adopted (see its "Revision notes" section at the end).
Plan: `docs/superpowers/plans/2026-07-15-plugin-organizer-phase2-apply.md`.

131 tests pass (`dotnet test PenumbraOrganizer.Plugin.Tests`), build is clean.

## The single most important technical detail

`Penumbra.Api`'s `SetModPath` signature is **`SetModPath.Invoke(string modDirectory, string newPath,
string modName)`** — `newPath` is the SECOND argument, `modName` the THIRD. This was confirmed by
reflection against the real `Penumbra.Api` 5.15.1 assembly (a throwaway console project referencing
the same NuGet package) before writing the implementation plan — the spec's first draft had
inferred `(modDirectory, modName, newPath)` from the XML doc comment's prose order, which was
**wrong**. If you ever touch `SetModPath` call sites (`Plugin.cs`'s `ApplyChanges`/
`RollbackLastApply`), double-check the argument order hasn't drifted — `modName` is always passed
`""`, `OrganizerModRow.Identifier` is `modDirectory`.

If a future Penumbra.Api version bump changes this signature, re-verify by reflection rather than
trusting the doc comment — see the spec's Architecture section for the exact throwaway-project
technique used.

## Update, 2026-07-15 — first real Apply run revealed a real, undiagnosed bug

An Apply run happened incidentally this session (while verifying the unrelated Folder Cleanup
feature) on a real ~239-mod library: **9 succeeded, 11 failed**, every failure reported
`PathRenameFailed` (`Penumbra.Api.Enums.PenumbraApiEc.PathRenameFailed` — Penumbra's own generic
"could not be set" rejection from `SetModPath`, no further detail).

**The failure pattern is not random — every failure came in a matching pair:** both the plain name
and its Phase 1d collision-disambiguated `(2)`/`(3)` suffix sibling failed together. Confirmed
examples: `Bibo+ Medieval (Penumbra)_1_1_0` + `Bibo+ Medieval (Penumbra)_1_1_0 (2)`; same for
Galateah, Heart Sweater by Hatsu, the "When that face 1 au ra keeps talking to you (Expression)"
mod, and Yet Another Slouchy Fox.

**Not yet investigated.** Root cause unknown — could be a Penumbra-side path validation rule (e.g.
around duplicate installs, path length, or specific characters), or could relate to how
`CollisionDisambiguator`'s `(N)` suffix naming interacts with `SetModPath`'s `newPath` argument
specifically. Whoever picks this up should start by reproducing on just one of the paired mods in
isolation (Apply only that one mod, not the full batch) to rule out a batch-ordering or
partial-state issue, then compare its exact `ProposedPath` string against Penumbra's own accepted
path syntax.

## What's NOT done yet — the critical next step

**Full in-game verification per the checklist below has still not happened** — the run above was
incidental (a byproduct of testing a different feature), not the deliberate walkthrough this
section describes. Do not treat "tests pass" as "safe to trust" — none of the actual `SetModPath`
behavior is exercised by the test suite, and the one real run so far surfaced a live bug (see above).
Before relying on this feature for real mod libraries, walk through the plan's final checklist
(`docs/superpowers/plans/2026-07-15-plugin-organizer-phase2-apply.md`, "Final in-game verification"):

1. Apply on a small, deliberately-chosen set of mods — confirm they actually move in Penumbra's own
   UI (not just this plugin's `CurrentPath` display).
2. Rollback afterward — confirm they return to their original folders.
3. Force an Apply failure (protect a mod mid-flight, or disable one mid-batch) — confirm the summary
   correctly reports it as failed while the rest succeed.
4. Force a Rollback failure (disable a mod between Apply and Rollback so its restore fails) —
   confirm the backup file is retained (not deleted), the Rollback button stays visible, and a
   second click only retries the still-pending entry.
5. Trigger `Validate().HasIssues` — confirm Apply is disabled, "Protect & Skip All Blocking Mods"
   appears, and clicking it makes `Validate()` clean again.
6. Confirm no physical mod directory moves on disk during any of the above — Apply/Rollback are
   virtual-path writes only (see the spec's Context section for why: `SetModPath` only touches
   Penumbra's `mod_data.db`, never the mod folder itself).

## Update, 2026-07-16 — root-cause investigation into the PathRenameFailed bug (game unavailable, static analysis only)

Investigated the `PathRenameFailed` bug above via Penumbra/OtterGui's real source (`xivdev/Penumbra`,
`Ottermandias/OtterGui`, `Ottermandias/Penumbra.Api` on GitHub) plus the leftover
`organizer-export.txt`/`organizer-backup.json`/Penumbra's real `organization.json` still on disk from
that session. Could not reproduce live — the game was unavailable this session.

**Ruled out:** the `SetModPath` argument order (already correct, per the existing note above); a bug
in `CollisionDisambiguator` itself (its uniqueness logic is sound — traced through the source, the
`(2)`/`(3)` suffixes on the specific failing mods, e.g. `Bibo+ Medieval (Penumbra)_1_1_0`, are baked
into Penumbra's own on-disk duplicate-install directory names, not something `CollisionDisambiguator`
generated for this trio, so the original "collision-disambiguated pairs" framing above was a
mischaracterization); and directory-lookup ambiguity in `TryGetMod` (confirmed an exact
`OrdinalIgnoreCase` match against the real `ModStorage.TryGetMod` source, no prefix confusion).

**Leading hypothesis, not yet live-confirmed:** Penumbra's own `FileSystem<T>` (which its ModFileSystem
sort-order tree is built on) gives folders and mod leaves the *same name-uniqueness namespace* —
`RenameAndMove`/`MoveChild` throws (caught generically as `PathRenameFailed`) if the target full path
already belongs to an existing child, and that child can be a **folder**, including an orphaned/empty
one that Penumbra never auto-prunes (the exact problem the separate Folder Cleanup feature targets).
Our own `Validate()` only checks collisions among mod rows it knows about — it has no visibility into
bare folder entries in `organization.json`. Timeline support: `organizer-export.txt` (16:59, pre-Apply,
"Collisions: 0") showed the failing mods with clean distinct proposed paths; `organizer-backup.json`
(22:17, after three folder-cleanup passes pruned hundreds of orphaned folders) showed those same mods
present as *successful*. Consistent with a stale orphaned folder occupying the target leaf name on the
first attempt, cleared by folder cleanup before a later attempt. The exact orphaned-folder state at the
moment of the original failure is gone now (overwritten by later cleanup passes) — this can't be
confirmed further without live reproduction.

**What shipped as a defensive measure (not a confirmed fix):** `ApplyPlanner.FolderPathCollisions`
(pure, unit-tested) plus a new check in `Plugin.ApplyChanges()` that reads `organization.json` before
Apply and throws a clear, actionable error — naming the affected mods and pointing at Folder Cleanup —
instead of letting Penumbra fail with the opaque `PathRenameFailed`. This turns a mystery failure into
an actionable one regardless of whether it's the *complete* root cause, but does not prove the
hypothesis. 187 tests pass, build clean.

**Next step when the game is available again:** confirm live whether this check actually fires before
a real Apply that would otherwise hit `PathRenameFailed`, and if it does, whether running Folder
Cleanup and retrying then succeeds cleanly.

## Update, 2026-07-19 — the orphaned-folder hypothesis above was wrong; real root cause found and fixed

A real Apply run on a ~113-mod library (106 succeeded, 7 failed, all `PathRenameFailed`) gave the
first live evidence since the incidental 2026-07-15 run. The `FolderPathCollisions` defensive check
did **not** fire for any of the 7 failures - confirming the orphaned-folder theory above doesn't
explain this. The `organizer-export.txt` taken immediately after the run showed the real pattern:
every failing group is a **direct path swap or rotation** among mods that already carry Phase 1d's
`(2)`/`(3)` duplicate-install suffixes. Concrete examples pulled straight from the export:

- `Bibo+ Medieval (Penumbra)_1_1_0 (2)`: `CurrentPath = Gear/…_1_1_0`, `ProposedPath = Gear/…_1_1_0 (2)`
- `Bibo+ Medieval (Penumbra)_1_1_0`: `CurrentPath = Gear/…_1_1_0 (2)`, `ProposedPath = Gear/…_1_1_0`

These two want to trade slots. A 3-way version of the same pattern (`X→Y→Z→X`) explained the
`When that face 1 au ra keeps talking to you (Expression)` failures. Root cause: `ApplyChanges`
(and `RollbackLastApply`, which has the identical bug in reverse) processed the touched-row set in
one naive sequential pass with no cycle awareness - so at the moment each swap member's `SetModPath`
call ran, its target path was still occupied by another not-yet-moved member of the same swap, and
every member of the cycle failed deterministically, every time.

**Fixed** via a new pure, unit-tested `Organizer.ApplyPlanner.OrderMovesForApply` (plus `ModMove`/
`ApplyStep` records): the move set (`CurrentPath → TargetPath` per mod) always decomposes into
disjoint chains and disjoint cycles, since `ProposedPath` is already guaranteed unique and each
mod's own `CurrentPath` is inherently unique. Chains are processed in reverse so each target is
vacated before something moves into it; cycles are broken by parking one member at a temporary path
first, draining the rest of the cycle, then completing that mod's move into its real target once the
slot is free. `Plugin.cs` gained a shared `ExecuteOrderedMoves` helper used by both `ApplyChanges`
and `RollbackLastApply` (rollback also needed a fresh live current-path read via
`ReadCurrentModPaths`/`GetModListAdapter`, since the backup file can outlive a session and cached
`OrganizerState.Mods` may be stale). The temporary path briefly shows up in Penumbra's own internal
state between a swapped mod's two `SetModPath` calls, but per the user this is a non-concern -
Penumbra's own UI tree doesn't visually refresh until "Rediscover Mods" is clicked, so it's never
actually seen. 307 tests pass (7 new), build clean.

The `FolderPathCollisions` defensive check from the earlier (wrong) hypothesis is left in place -
harmless, and folder/mod name-namespace collisions are a real Penumbra behavior even if they weren't
the cause of this particular incident.

**Not yet in-game verified** - this was root-caused and fixed via static analysis of a real export
file, not a live reproduction-then-fix cycle (systematic-debugging's Phase 1-2 evidence was strong
enough - three independent failing groups all matching the exact swap/rotation pattern - to treat as
confirmed without further live experimentation). Next session with the game available should re-run
Apply on a library known to contain Phase 1d duplicate-suffix pairs/rotations and confirm all
members now succeed. **In-game verified later the same day: 32 succeeded, 0 failed on the same
library.** But see the next section - the deeper question of WHY swaps kept appearing every scan
was answered afterward, and the answer supersedes part of this section's framing.

## Update, 2026-07-19 (later) — WHY the swaps existed at all: Penumbra discards " (N)" suffixes on save

After the cycle fix verified clean, a heavy Apply/Rediscover/Folder-Cleanup session ended in a
persistent, 100%-reproducible stack-overflow game crash on every Scan (`0xc00000fd` in
`Dalamud.Game.Framework.HandleFrameworkUpdate`, dump analyzed via WinDbg/SOS; plus a caught
`ArgumentOutOfRangeException` in `ModCollection.GetActualSettings` during Penumbra's own mod
import - both Penumbra-side, resolved only by resetting Penumbra's config). Investigating what our
writes could have contributed surfaced the real generator, confirmed line-by-line in Luna/Penumbra
source (`Ottermandias/Luna`, `xivdev/Penumbra`, `testing` branch, 2026-07-19):

- `Luna.FileSystemUtility.FixName`: every node name is whitespace-trimmed, `/` becomes `\`.
- `Luna.FileSystemUtility.IsDuplicateName`: any name ending in `" (uint)"` is a duplicate marker.
- `Luna.DataPath.UpdateByNode`: only `Folder` + `SortName` persist. A leaf whose duplicate-marker
  base equals the mod's display name persists `SortName = null` - **the " (N)" suffix is discarded
  on save**; the duplicate branch strips the marker even for custom leaves (`SortName = baseName`).
- `Penumbra.ModFileSystemSaver.CreateDataNodes`: on every load/Rediscover, nodes are recreated from
  `SortName ?? mod.Name` and suffixes re-dealt by `CreateDuplicateDataNode` in **enumeration
  order** - not the order we assigned.

So `CollisionDisambiguator`'s " (N)" suffixes were never persistable identity: every Apply's suffix
assignment was thrown away on save and re-dealt arbitrarily on reload, making the same mods appear
"swapped" on every scan - an endless churn loop of no-op writes (the earlier framing "baked into
Penumbra's own on-disk duplicate-install directory names" conflated mod *directory* names with
filesystem *leaf* names). A second churn generator: mods with trailing-space names (real case:
`Vespucci `) never compare equal to their Penumbra-trimmed `CurrentPath`.

**Fixed** via `Organizer/PenumbraPathSemantics.cs` - a pure, tested mirror of the exact Luna
semantics above (`FixName`, `IsDuplicateName`, `AreEquivalent`): two paths are equivalent when
their folders match and their leaves match after reducing transient duplicate markers. Wired in
three places: (1) every sort strategy pins a row's proposal back to `CurrentPath` when equivalent
(`OrganizerState.FinishProposals`, running BEFORE disambiguation so retained suffixed paths are
reserved); (2) `CollisionDisambiguator` now prefers the already-in-place row as canonical; (3)
`ApplyChanges`' touched-row predicate uses `AreEquivalent` instead of raw string equality.
`BuildPath` also FixNames the leaf and creator segments (trailing spaces trimmed, `/` in names
escaped as `\` - a raw `/` in a mod name would have split into a bogus folder level). Result:
duplicate installs already in the right folder count as in place regardless of which suffix
Penumbra dealt them today. 341 tests pass, build clean. **Not yet in-game verified.**

Penumbra-side flags worth reporting upstream (not ours to fix): the `GetActualSettings`
index-out-of-range during `AddMod` (`ModFileSystem.OnModPathChange`'s own comment: "event
spaghetti... possibly break. Untangling the events is hard."), and the unguarded state that produced
the stack-overflow kill on bulk enumeration.

## Known limitations, not fixed here

- **No fix for Penumbra's orphaned-empty-folder behavior.** Folder *existence*
  (`organization.json`) is tracked independently of mod *placement* (`mod_data.db`), and nothing in
  Penumbra's own logic prunes an empty folder entry after a move. No IPC exposes this. Deliberately
  out of scope — tracked in `docs/ROADMAP.md` next to detailed gear-slot sorting, both needing their
  own future scope-expansion decision.
- **Single rolling backup, not a history.** "Rollback" only ever means "undo the most recently
  completed Apply." Considered and explicitly not adopted during brainstorming.
- **No `IModPathWriter`-style abstraction for testing the IPC orchestration.** Considered during the
  external design review and declined — would be a bigger architectural shift than this feature
  itself, and inconsistent with every other IPC-touching method in this codebase.

## Process note

This phase's subagent-driven-development execution (3 tasks + one final whole-branch review) ran
clean — no worktree-boundary violations, unlike Phase 1f's execution (an implementer subagent twice
committed directly onto `main` instead of its assigned worktree; recovered cleanly since neither
commit had been pushed, and the fix — a mandatory first-step directory-verification check in every
dispatch prompt — has held for every phase since, including this one). The final whole-branch review
(dispatched on the most capable model) did catch one real
Important bug purely from looking at the whole branch together: the Rollback result summary was
nested inside `if (_plugin.BackupExists)`, so a fully successful rollback (which deletes the backup)
silently hid its own confirmation. Fixed in `c4c9c1f` before merge. Worth remembering: per-task
reviews can all pass clean while a cross-task integration bug still slips through — the final
whole-branch review earned its keep here.
