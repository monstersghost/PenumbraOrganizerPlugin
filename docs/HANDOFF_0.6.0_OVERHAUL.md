# Handoff: the 0.6.0 UI overhaul

Written 2026-08-08, updated 2026-08-10. Read this before continuing the overhaul. Everything is on
`main`, committed and pushed.

## Where things stand

`main` is at `de7e9f2` plus the post-review fixes. **1049 tests pass, production build clean with
zero warnings.** Verify that before believing anything below.

The overhaul is six pieces. Build order is fixed by real dependencies:

```
0 -> (1, 2) -> 3 -> 4 -> 5
```

| Piece | What | State |
|---|---|---|
| 0 | MainWindow split | **Done**, `aa0ad69`, in-game verified |
| 1 | NPC name lists + index matcher | **Done**, Task 5 at `7afb414`. Partly in-game verified |
| 2 | Sort control consolidation | **Done**, Tasks 3-4 at `66ff37f` and `c9f2f37`. Partly in-game verified |
| 3 | Hover explanations | **Done**, `3886e80` / `3e5333e` / `cad6ddb`. In-game verified |
| 4 | Help tab | **Done**, `790ae35`. Not in-game verified |
| 5 | Guided first run | **Done**, `43ccc6b`. Not in-game verified |
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

All six pieces are closed and release prep is done, so every ordering constraint in the plans is
spent. What remains is in-game verification and publishing - see the release checklist below.

## In-game verification status

A first in-game pass ran on 2026-08-10 against `ef62b67`.

**Confirmed working:** Scan completes (so `ScanJob.Materialize` with the new arguments, and
`LoadForMatching` reading the embedded static list off the framework thread, both work). Workbook
export. Import Workbook, including its dialog — which matters, because piece 2 moved it out of the
old button row's disabled scope. **Split NPC off**, the combination no button ever offered.

**Still unverified, and worth doing on the next pass:**

- **The `MigrateLegacyList` call site** in the `Plugin` constructor. This is the one that operates on
  a real user file, so it is the highest-value remaining check: a pre-0.6.0 `npc-name-list.json`
  should become `npc-name-list-scraped.json` with contents intact. Back up the config directory
  first. Every migration test passes against a temp directory; that the constructor reaches it at
  all is still unproven, which is the exact trap the piece 1 plan called out.
- **`IndexJob`** — the Search index build was not exercised.
- **`SortPanel` details** — the two split checkboxes greying out under Creator, and the staleness
  line appearing after a selection change.

**One bug found and fixed:** ticking or unticking any folder on the Protect tab reversed an explicit
"Toggle Heliosphere protection" unprotect, for every Heliosphere mod at once. Fixed at `261db03`.
Pre-existing since `91446a3`, not a regression from this overhaul — see "Known debt" below for what
the fix changed.

**Not a bug:** collisions shown on Review Changes after an Import. Import and manual assignment
deliberately skip `CollisionDisambiguator`; a collision created by hand surfaces as a `Validate()`
error rather than being silently renumbered. If a plain **Sort** ever produces collisions, that is a
real bug — disambiguation should have renumbered them.

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

Notes are written at `docs/RELEASE_NOTES_0.6.0.0.md` and the version is bumped. Nothing is tagged or
published. In order:

1. **Verify `MigrateLegacyList` in-game.** Still the only unverified thing that touches a real user
   file. Back up the plugin config directory, then confirm a pre-0.6.0 `npc-name-list.json` becomes
   `npc-name-list-scraped.json` with its contents intact.
2. **Verify pieces 4 and 5 in-game.** For the walkthrough specifically: a fresh config shows it,
   dismissing stops it returning, closing with the X counts as dismissing, and with Penumbra
   disabled it shows one step and offers itself again next time.
3. **Review the notes.** They are deliberately explicit that the scraped-list opt-in is still off,
   which 0.5.3.1's notes had promised would return in 0.6.0.
4. **Tag `0.6.0.0` - exactly that string.** `HelpTab.GuideUrl` embeds it, so `0.6.0` or `v0.6.0.0`
   ships a Help tab whose guide link 404s. No test catches this: the URL test asserts the tag is not
   `main` and that the path is right, both of which pass against a tag that does not exist.
5. Release, then update `repo.json`.

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

## Requested, not yet built

**A "Join the Discord" button on the Help tab.** Requested 2026-08-10, deliberately deferred rather
than guessed at.

**It needs the invite URL from the maintainer — there is none anywhere in the repo.** The only
mentions of a support Discord are in the unshipped `worktree-community-templates-t1` docs, and none
of them carries a link. Do not invent one, and do not scrape one from a release page.

When the URL arrives, it goes beside `HelpTab.GuideUrl` and reuses `HelpTab.OpenUrl`, which already
handles the browser launch and swallows a failure rather than tearing down the draw call. Two things
to decide at that point, neither obvious:

- **Whether it needs a tooltip.** The two existing Help-tab buttons deliberately have none, because
  their labels say exactly what they do. "Join the Discord" probably qualifies too, but a note that
  it opens an external browser and leaves the game may be worth it.
- **Whether an invite that can expire belongs in a shipped binary.** A dead invite in a released
  build cannot be fixed without a new release. A vanity URL or a redirect the maintainer controls
  avoids that; a raw `discord.gg/<code>` does not.

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
