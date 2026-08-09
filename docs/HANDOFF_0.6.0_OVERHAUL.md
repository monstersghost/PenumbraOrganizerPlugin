# Handoff: the 0.6.0 UI overhaul

Written 2026-08-08. Read this before continuing the overhaul. Everything is on `main`, committed,
nothing pushed.

## Where things stand

`main` is at `5b81ba7`. **982 tests pass, build clean, zero warnings.** Verify that before
believing anything below.

The overhaul is six pieces. Build order is fixed by real dependencies:

```
0 -> (1, 2) -> 3 -> 4 -> 5
```

| Piece | What | State |
|---|---|---|
| 0 | MainWindow split | **Done**, `aa0ad69`, in-game verified |
| 1 | NPC name lists + index matcher | Tasks 1-4 done. **Task 5 outstanding** |
| 2 | Sort control consolidation | Tasks 1-2 done. **Tasks 3-4 outstanding, Task 3 blocked on piece 1 Task 5** |
| 3 | Hover explanations | Not started |
| 4 | Help tab | Not started |
| 5 | Guided first run | Not started |

Plans live in `docs/superpowers/plans/2026-08-07-piece-*.md`, specs in
`docs/superpowers/specs/2026-08-07-*.md`. All four plans have had both an internal and an external
review pass, and the fixes from both are already folded in.

## The one non-obvious ordering constraint

**Piece 2 Task 3 cannot run before piece 1 Task 5.** `SortPanel` binds its scraped-list checkbox to
`Configuration.UseScrapedNpcNameList` and `Configuration.ScrapedNpcListFeatureEnabled`, and piece 1
Task 5 is what creates both. The piece 2 plan calls pieces 1 and 2 parallel; that is true only of
its Tasks 1 and 2, which are already done.

So the next task is **piece 1 Task 5**, then piece 2 Task 3, then Task 4, then pieces 3-5.

## What piece 1 Task 5 involves

The largest remaining unit. Read the plan section in full; the summary:

- `MigrateLegacyList(configDir)` returning `string?`, implementing the four-case table
  (legacy-only renames to `npc-name-list-scraped.json`; both-present leaves both; neither and
  scraped-only are no-ops). Needs a **production call site** in the `Plugin` constructor, before any
  library work is admitted. Without it every migration test passes while nothing ever migrates.
- `LoadForMatching(configDir, useScraped)` with every cell of the matrix defined in the plan.
- **Do not reuse `MaxSafeNameCount` (2,000) on the opted-in path.** It does not merely warn: it
  backs the file up and overwrites it with the seed. The scraped list is ~20,115 names, so reusing
  it would destroy the user's file on every opted-in load. Use a separate 25,000 ceiling that warns
  and falls back in memory only.
- Snapshot refresh semantics replacing `MergeAdditive` in `NpcNameRefreshService` — this is the
  change that stops the unbounded growth.
- `Configuration.UseScrapedNpcNameList` plus the compile-time `ScrapedNpcListFeatureEnabled = false`
  gate. Consumers read the **conjunction**, never the config value alone.
- Plumbing through `ScanProcessor`, `IndexProcessor`, `ScanJob`, `IndexJob`.

**One existing test is expected to fail and must be deleted:**
`RefreshAsync_NeverRemovesExistingNames` exists specifically to pin `MergeAdditive`. Anything else
failing means something is wrong.

## Traps already hit, so you do not hit them again

**No `InternalsVisibleTo` exists in this repo.** Verified, not assumed. Any type or member a test
touches must be `public`. This has now bitten three times: it was caught in plan review twice, and I
still wrote `internal const string ResourceName` on `Help` and had to fix it. When adding anything
the tests read, make it public and say why in a comment.

**The static NPC list source carries a UTF-8 BOM.** `NpcNameListCodec.Parse` never throws - it
returns `MalformedJson` - so a BOM does not fail loudly. It would make the bundled list silently
unavailable and leave every scan with no NPC names, reporting only a warning. The copy at
`PenumbraOrganizer.Plugin/Organizer/NpcNames/npc-name-list-static.json` has the BOM stripped and
there is a test asserting its absence. Do not re-copy from `docs/superpowers/specs/` without
stripping it again.

**Deleting the seven `SortBy*` methods breaks more than `OrganizerState`.** It broke the sort
buttons and 43 test call sites. Both are migrated; the buttons now route through
`OrganizerState.Sort(...)` via a temporary private helper in `MainWindow.SortTab.cs` that piece 2
Task 3 deletes along with the button row.

**Category order must stay the outer loop in `NpcNameMatcher.Match`.** A position-first scan flips
`"Titan Slaying Y'shtola"` from NPC to Boss. With 679 bosses against 133 NPCs in the shipped list
that would silently refile a lot of mods. Guarded by `Match_CategoryOrderBeatsPosition`.

## Behaviour changes already shipped in this work

`NpcNameMatcher` has three, each pinned by a test that fails against the old regex:

1. Separators between tokens are interchangeable - `Y-shtola` and `Y shtola` now match `Y'shtola`.
   A loosening.
2. Non-BMP letters are letters. The regex tested UTF-16 surrogates and found a word boundary inside
   a single character. A tightening.
3. `NpcNameMatch.Name` is now the list's canonical spelling, not the mod title's. Nothing reads it
   today; `ModTypeClassifier` uses only `.Kind`.

Case folding also moved from culture-sensitive to ordinal, which changes Turkish dotted/dotless I.

Sorting gained six combinations that no button ever offered - the whole `splitNpc: false` column.
NPC mods can now land in `NPC` rather than `NPC/Bosses`. The release notes must say so; nobody will
expect it.

## Release gates

- **The scraped-list toggle ships disabled.** It is gated on reproducing the crash and verifying the
  new matcher in-game against a full 20,115-name list. That is a release decision, not a task.
- **The NPC work must not be described as a crash fix**, in notes, comments or commit messages.
  Established: resetting the oversized list stops the observed crash. The mechanism is still unknown.
- Release notes get written but **not published**. The maintainer reviews them first and says go.
  Short user-facing note plus a link to the full notes.

## Still open, unrelated to the critical path

- **`worktree-community-templates-t1`** holds a complete, unshipped Templates feature (+5,350 lines
  across 49 files: store, preview tree, planner, a Templates tab, plus fixes for filesystem errors
  escaping the draw call and untrusted template input reaching the UI). None of it is on `main`. It
  was deliberately kept when the other worktrees were pruned.
- **Community review of the static NPC list.** A Discord post was drafted and never posted.
- **Piece 0's multi-root recovery branch was not verified in-game** and will not be without a
  contrived two-root interrupted state. Everything else in piece 0 was.
