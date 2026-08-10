# Handoff: the 0.6.0 UI overhaul

Written 2026-08-08, updated 2026-08-11. Everything is on `main`, committed and pushed.

**The overhaul is finished and verified in-game. What is left is publishing** - see the release
checklist below. Nothing has been tagged.

## Where things stand

**1052 tests pass, production build clean with zero warnings.** Verify that before believing
anything below.

The overhaul is six pieces. Build order is fixed by real dependencies:

```
0 -> (1, 2) -> 3 -> 4 -> 5
```

| Piece | What | State |
|---|---|---|
| 0 | MainWindow split | **Done**, `aa0ad69`, in-game verified |
| 1 | NPC name lists + index matcher | **Done**, Task 5 at `7afb414`. In-game verified |
| 2 | Sort control consolidation | **Done**, Tasks 3-4 at `66ff37f` and `c9f2f37`. In-game verified |
| 3 | Hover explanations | **Done**, `3886e80` / `3e5333e` / `cad6ddb`. In-game verified |
| 4 | Help tab | **Done**, `790ae35`. In-game verified |
| 5 | Guided first run | **Done**, `43ccc6b`. In-game verified |
| — | Release prep (Task 6) | **Done**, `d17c633`. Notes written, not published |

**A full code review ran against `eb501e5..de7e9f2`** (tag 0.5.3.1 to the end of release prep) and
its findings are fixed. It found no Critical issues. The one real bug is worth knowing about because
the test that should have caught it did not:

> Every exit from `FirstRunWindow` routes through `IsOpen = false`, so Dalamud fires `OnClose`, which
> calls `FirstRunSteps.Closed()` asking for `markSeen: true`. Because `ShouldMarkSeen` or-s, that
> overwrote the `false` the Penumbra-unavailable path had just set, and the notice consumed the one
> first run - contradicting four separate pieces of user-facing text. The existing test passed
> because it drove `FirstRunSteps` in isolation and never simulated the window close that production
> always performs. `Finish` now gates on `_penumbraAvailable`, and
> `WithPenumbraUnavailable_TheRealCloseSequence_StillDoesNotMarkSeen` drives the real sequence.

**One plan decision was reversed by the maintainer:** Task 4 specified `bool?` plus a resolver so
that upgrading users would NOT see the walkthrough. Showing it on upgrade is the intent, so the flag
is a plain `bool`. Pinned by `PreExistingConfig_WithoutTheFirstRunField_ShowsTheWalkthrough` and
documented on `Configuration.FirstRunTutorialSeen`, because the plan still says otherwise.

## In-game verification status

**Complete as of 2026-08-11, against `b344da3`. Every piece has been exercised in-game and passed.**

Covered across two passes: scan and NPC classification, workbook export, Import Workbook and its
dialog, the Search index build, the whole `SortPanel` including the split checkboxes greying out and
the staleness line, Split NPC off, the Help tab, the first-run walkthrough, and the tooltip sweep
over disabled controls.

Two that were worth the trouble specifically:

- **`MigrateLegacyList`** ran on a real install. `npc-name-list.json` became
  `npc-name-list-scraped.json` with the bundled seed's 19/15/11 names intact, and 0.5.3.1's
  21,340-name `.oversized-` backup was left untouched beside it. That backup is the only real-world
  corpus available for testing the scraped list when it is enabled - do not delete it.
- **The Penumbra-disabled walkthrough path**, which had never worked in any build until the code
  review caught it. Confirmed: one explanatory step with the right message, and it offers itself
  again once Penumbra is enabled.

**Not a bug, confirmed during testing:** collisions on Review Changes after an Import. Import and
manual assignment deliberately skip `CollisionDisambiguator`; a collision made by hand surfaces as a
`Validate()` error rather than being silently renumbered. A plain **Sort** producing collisions would
be a real bug.

The step-by-step guide used for this is `docs/TESTING_GUIDE_0.6.0.md`.

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

## Release checklist for 0.6.0.0

Notes are written at `docs/RELEASE_NOTES_0.6.0.0.md` and the version is bumped. Code is complete and
verified in-game. Nothing is tagged or published.

Remaining, in order - all four are the maintainer's to do:

1. **Review the notes.** They are deliberately explicit that the scraped-list opt-in is still off,
   which 0.5.3.1's notes had promised would return in 0.6.0. That is the one promise this release
   does not keep, and it is stated rather than omitted.
2. **Tag `0.6.0.0` - exactly that string.** `HelpTab.GuideUrl` embeds it, so `0.6.0` or `v0.6.0.0`
   ships a Help tab whose guide link 404s. No test catches this: the URL test asserts the tag is not
   `main` and that the path is right, both of which pass against a tag that does not exist. **Until
   the tag is pushed, that link is dead in every build**, including the one just tested.
3. **Publish the release.** Short user-facing note plus a link to the full notes, per the usual
   pattern.
4. **Update `repo.json`** so installed plugins see the new version.

**The version is four-part on purpose.** Every tag here is, the csproj was, and `MainWindow` renders
`Assembly.Version.ToString(4)`. The plan said `0.6.0`; following it literally would have made the
displayed version, the tag and the guide URL disagree.

## Release gates

- **The scraped-list toggle ships disabled.** It is gated on reproducing the crash and verifying the
  new matcher in-game against a full 20,115-name list. That is a release decision, not a task.
- **The NPC work must not be described as a crash fix**, in notes, comments or commit messages.
  Established: resetting the oversized list stops the observed crash. The mechanism is still unknown.
- Release notes get written but **not published**. The maintainer reviews them first and says go.
  Short user-facing note plus a link to the full notes.

## The protection model, after `261db03`

Worth knowing before touching the Protect tab, because it had two rules and now has one.

`OrganizerState.IsEffectivelyProtected` is the **single** protection rule; every path recomputes
through it. There used to be a second, `IsEffectivelyProtectedAfterIndividualToggle`, that simply
omitted the Heliosphere clause — so the answer depended on which control the user last touched, and
that is how ticking a folder came to reverse an explicit Heliosphere unprotect.

`_heliosphereUnprotectOverrides` holds the Heliosphere mods the user explicitly unprotected this
session. It is **deliberately not persisted** and is cleared by `ReplaceScanAtomically`: the
documented contract is that a scan re-protects Heliosphere mods no matter how the toggle was left,
because Heliosphere owns their location. The override survives unrelated UI actions, not a scan.

**Rule order is a decision, not an accident** (confirmed with the maintainer): an explicit mod
protection or a protected folder beats the override. Unprotecting Heliosphere mods and then
protecting a folder containing one protects that one — the newer, more specific instruction — while
leaving every Heliosphere mod outside that folder alone.

`SetAllProtection(false)` now also unticks Heliosphere rows. Before, it left them visibly ticked and
the button looked broken.

## Known debt created by pieces 1 and 2

- **`NpcNameListStore.Load` and `NpcNameListCodec.MergeAdditive` have no production callers left**,
  only tests. The piece 1 plan said explicitly to keep the 2,000-name guard on the `Load` path, so
  both were left in place rather than deleted.
- **`MainWindow.Widgets.cs`'s `DrawWrappingButtonRow` is now unused** — the sort button row was its
  last caller. Left alone in case pieces 3-5 want it.
- **Six xUnit analyzer warnings in the test project** (`ApplyPlannerTests.cs:306`,
  `NpcNameMatcherEquivalenceTests.cs:63` and `:74`) only surface on a non-incremental build. The
  production project is at zero. Only the `ApplyPlannerTests` one predates the overhaul; the two in
  `NpcNameMatcherEquivalenceTests` were introduced by piece 1 Task 2 at `56ac255` and are
  `Assert.True`/`False` over `Regex.IsMatch` where the analyzer wants `Assert.Matches`/
  `DoesNotMatch`. Worth fixing when that file is next touched — it is a temporary guard due for
  deletion once this change has settled, so it may simply go.

## Still open, unrelated to the critical path

- **`worktree-community-templates-t1`** holds a complete, unshipped Templates feature (+5,350 lines
  across 49 files: store, preview tree, planner, a Templates tab, plus fixes for filesystem errors
  escaping the draw call and untrusted template input reaching the UI). None of it is on `main`. It
  was deliberately kept when the other worktrees were pruned.
- **Community review of the static NPC list.** A Discord post was drafted and never posted.
- **Piece 0's multi-root recovery branch was not verified in-game** and will not be without a
  contrived two-root interrupted state. Everything else in piece 0 was.
