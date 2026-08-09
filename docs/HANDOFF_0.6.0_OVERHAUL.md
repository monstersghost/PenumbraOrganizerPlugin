# Handoff: the 0.6.0 UI overhaul

Written 2026-08-08, updated 2026-08-10. Read this before continuing the overhaul. Everything is on
`main`, committed, nothing pushed.

## Where things stand

`main` is at `c9f2f37`. **1011 tests pass, production build clean with zero warnings.** Verify that
before believing anything below.

The overhaul is six pieces. Build order is fixed by real dependencies:

```
0 -> (1, 2) -> 3 -> 4 -> 5
```

| Piece | What | State |
|---|---|---|
| 0 | MainWindow split | **Done**, `aa0ad69`, in-game verified |
| 1 | NPC name lists + index matcher | **Done**, Task 5 at `7afb414`. Not in-game verified |
| 2 | Sort control consolidation | **Done**, Tasks 3-4 at `66ff37f` and `c9f2f37`. Not in-game verified |
| 3 | Hover explanations | Not started — **next** |
| 4 | Help tab | Not started |
| 5 | Guided first run | Not started |

Pieces 1 and 2 are closed, so the ordering constraint below is spent. The next task is piece 3.

## What is not verified in-game

Penumbra's IPC only resolves inside the game, so nothing that touches it has been exercised. From
pieces 1 and 2 specifically:

- **The `MigrateLegacyList` call site** in the `Plugin` constructor. Every migration test passes
  against a temp directory; that the constructor actually reaches it is unproven. This is the exact
  trap the piece 1 plan called out.
- **`ScanJob` / `IndexJob`** passing the new `(configDirectory, useScraped)` arguments — their
  `Materialize()` bodies call `GetModListAdapter` and `GetChangedItemAdapterDictionary`.
- **The whole `SortPanel`**, since ImGui only draws in a game frame. Worth checking specifically:
  the two split checkboxes greying out under Creator, the staleness line appearing after a
  selection change, and Import Workbook still opening its dialog now that it no longer shares the
  old button row's disabled scope.

## Three deliberate deviations from the plans, already reviewed and accepted

- **`AddedCount` was renamed `NameCount`**, not merely redefined. The piece 1 plan said to change
  its meaning and keep the name; a field called "Added" holding a snapshot total is a trap.
- **`Configuration.ScrapedNpcListFeatureEnabled` is `public`**, not `internal` as the plan wrote.
  No `InternalsVisibleTo` — the test asserting the feature ships disabled would not compile.
- **`NpcNameRefreshService.RefreshAsync` kept its `embeddedSeedJson` parameter**, now used only for
  default exclusions. The plan's "fall back to empty" would have broken a second existing test
  beyond the one deletion it predicted. The bundled seed ships `"Excluded": []`, so production
  behaviour is exactly what the plan specified.

## One behaviour added that no plan specified

Snapshot refresh replaces a category **only when its scrape completed cleanly**. `NpcWikiScraper`
returns the names it gathered alongside a `FailureReason`, so replacing from a run that stopped
early would delete every name past the failure point — a timeout on page 3 of 50 would silently
discard 47 pages. A failed category now keeps what was on disk. Pinned by
`Refresh_FailedCategory_KeepsWhatWasAlreadyOnDisk`.

Plans live in `docs/superpowers/plans/2026-08-07-piece-*.md`, specs in
`docs/superpowers/specs/2026-08-07-*.md`. All four plans have had both an internal and an external
review pass, and the fixes from both are already folded in.

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
buttons and 43 test call sites. Both were migrated, and the temporary helper that carried the
buttons through the gap is gone along with the button row — `SortPanel` calls
`OrganizerState.Sort(...)` directly now.

**`Help.Tooltip` takes a `disabledReason` parameter; use it rather than a second `SetTooltip`.**
ImGui binds `IsItemHovered` to the last submitted item, so two tooltip calls against one widget in
one frame fight over the same window. Every control in `SortPanel` passes the reason through the
parameter instead. The tooltip must still be called immediately after its widget and after any
inner `EndDisabled`, which submits no item of its own.

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

## Known debt created by pieces 1 and 2

- **`docs/HOW_SORTING_WORKS.md` is stale in three places** and was not in either plan's scope:
  it still says you pick a strategy by clicking a button, still describes `NpcNameMatcher` as one
  compiled alternation per category, and still points at `OrganizerState.SortBy*`. `USER_GUIDE.md`
  was updated; this one was not.
- **`NpcNameListStore.Load` and `NpcNameListCodec.MergeAdditive` have no production callers left**,
  only tests. The piece 1 plan said explicitly to keep the 2,000-name guard on the `Load` path, so
  both were left in place rather than deleted.
- **`MainWindow.Widgets.cs`'s `DrawWrappingButtonRow` is now unused** — the sort button row was its
  last caller. Left alone in case pieces 3-5 want it.
- **Three xUnit analyzer warnings in the test project** (`ApplyPlannerTests.cs:306`,
  `NpcNameMatcherEquivalenceTests.cs:63` and `:74`) predate this work and only surface on a
  non-incremental build. The production project is at zero.

## Still open, unrelated to the critical path

- **`worktree-community-templates-t1`** holds a complete, unshipped Templates feature (+5,350 lines
  across 49 files: store, preview tree, planner, a Templates tab, plus fixes for filesystem errors
  escaping the draw call and untrusted template input reaching the UI). None of it is on `main`. It
  was deliberately kept when the other worktrees were pruned.
- **Community review of the static NPC list.** A Discord post was drafted and never posted.
- **Piece 0's multi-root recovery branch was not verified in-game** and will not be without a
  contrived two-root interrupted state. Everything else in piece 0 was.
