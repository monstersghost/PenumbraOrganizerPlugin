# Root-only sort, new-mod prompt, chat command, unused mods, per-collection folders - Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.
> Every task is TDD: write the failing test, see it fail, implement, see it pass, commit.

**Goal.** Six features from `docs/IDEAS_2026-11-11.md`, in this order: (1) a Sort scope that sorts only the
loose mods in the library root; (2) a banner when mods are installed ("3 new mods, sort them?") that runs (1)
on just those, with an opt-in automatic mode; (3) `/porganizer sortnew` running the same path headless;
(4) an unused-mods report with size on disk and a staged move to an `Unused` folder; (5) sorting into one
root folder per Penumbra collection, then the normal sort under it; (6) help and walkthrough updates.

**Status of this document.** Combined design + plan, because the five features share one core (a scoped
sort). Revision 2 (2026-11-13) folds in a code-verified review: collection-sort idempotency, the post-Apply
rescan moving out of `Draw()`, bounded new-mod batches, counting only eligible mods, telling the user what
a rescan clears, the Rediscover reminder in chat, task ordering, test gaps, and the UI placement decisions.
If the author wants the house "spec then plan" split, the "Design" section lifts out unchanged into
`docs/superpowers/specs/2026-11-13-root-sort-and-new-mod-features-design.md`.

**Tech stack.** C# / .NET 10 (`net10.0-windows7.0`), Dalamud + `Dalamud.Bindings.ImGui`, Penumbra.Api 5.15.1,
xUnit.

---

## Settled UI decisions (from review; not open)

- **"What to sort"** is one combo on the same line as Group by in `SortPanel`: "Whole library" / "Only loose
  mods in the root". A run-time scope, never a saved protect rule, never persisted.
- **Scope line** beside the Sort button: `Will sort N of M mods: loose in root. K protected root mods stay
  put.` The same line is repeated in Review's "Apply changes?" modal so the user sees what scope produced
  the proposals they are about to apply.
- **New-mod prompt is an in-window banner** in the `_lastError` slot above the tab bar (MainWindow.cs:198).
  Not a separate window, never a popup. Bursts coalesce into one banner. With the window closed, the user
  gets one chat line instead (see Feature 2).
- **Unused mods** is a collapsed `Unused mods (N)` section on the **Scan** tab. No new tab.
- **Automatic mode** is opt-in, default off, inside a collapsed **Advanced** header on the Sort tab.
- **Walkthrough**: no new steps. One sentence in `firstrun.sort`; everything else goes in Help topics.
  `firstrun.help` is not edited.

## Open questions for the author (defaults proposed, so work is not blocked)

| # | Question | Default in this plan | Affects |
|---|---|---|---|
| Q1 | New mods: root-only filter, or sort them wherever they landed? | **Root-only, as asked.** `SortScope` carries both flags, so flipping is one line. | B3/B4 |
| Q2 | Command name. The real command is `/porganizer` (Plugin.cs `CommandName`), not `/porg`. Add an alias? | **No alias.** Subcommands on `/porganizer`. | C1/C2 |
| Q3 | Unused report: which collections count as "using" a mod? | **All collections**, each collection's OWN settings, temporary settings ignored; a combo offers "Only <player collection>" via `ActiveCollectionReader`. The section always names the collections read. | D1, D4 |
| Q4 | Unused move target. | **Keep structure** (`Unused/Gear/Top/X`). | D3 |
| Q5 | After moving to Unused, a later whole-library sort pulls those mods back out (nothing anchors them). Protect the folder? | **A checkbox "Protect the Unused folder" next to the move button, default ticked**; ticking it writes a normal saved protect rule (an explicit user action, visible on the Protect tab). | D4 |
| Q6 | Mod enabled in several collections? | **`Shared` folder** (name configurable); alternative "Leave where it is". | E2, E4 |
| Q7 | Mod enabled in no collection, with per-collection sorting on? | **The `Unused` folder** (same name as feature 4); alternative "Sort normally at the top level". | E2 |
| Q8 | Collection folder vs Heliosphere/Proteus "Sort inside" and user sort-inside folders. | **Collection outermost**: `Alice/Heliosphere/Gear/...`. | E3 |
| Q9 | Inheritance: a mod enabled in Base, which Alice and Bob inherit. | **Base** (`ignoreInheritance: true`). | D1, E2 |
| Q10 | Automatic batch cap (above it, automatic falls back to the banner). | **20 mods.** | B3 |
| Q11 | Turning per-collection sorting OFF later: should the next sort still strip the old collection folder names when finding anchors? | **Yes.** The collection folder names last used are saved (`CollectionFolderNamesInUse`), and stripping always uses that set, so `Alice/Heliosphere/x` is not mis-anchored at `Alice` after the option is switched off. | E3 |

**Still unverified (no Penumbra source on this machine):** whether "Rediscover Mods" raises `ModAdded`
once per mod. The design is safe either way: a mod counts as new only when its identifier was unknown
when the event was drained (B1), so a rediscover of known mods yields zero new mods.

---

## Ground truth checked for this plan (facts, not guesses)

**Penumbra.Api 5.15.1**, read by reflection from
`~/.nuget/packages/penumbra.api/5.15.1/lib/net10.0-windows7.0/Penumbra.Api.dll` plus its XML docs:

- `GetCollections.Invoke()` returns `Dictionary<Guid, string>`: "all currently installed collections
  together with their display names, excluding the empty collection". **Listing every collection is possible.**
- `GetAllModSettings.Invoke(Guid collectionId, bool ignoreInheritance = false, bool ignoreTemporary = false,
  int key = 0)` returns `(PenumbraApiEc, Dictionary<string, (bool Enabled, int Priority,
  Dictionary<string, List<string>> Settings, bool Inherited, bool Temporary)>)`, keyed by mod DIRECTORY name;
  `CollectionMissing` or `Success`; "mods that have no settings at all are left out".
- `GetSettingsInAllCollections` exists but is per mod (N calls); not used. C+1 calls instead.
- `ModAdded` delivers "the base directory name of the new mod" = `OrganizerModRow.Identifier`.
- Not verified: whether `GetCollections` lists temporary collections. Temporary SETTINGS are excluded by
  `ignoreTemporary: true` either way.

**Codebase facts the design rests on (each cited):**

- `OrganizerState.Sort` touches every unprotected row (OrganizerState.cs:714); `Plugin.StartApplyOperation`
  moves every unprotected changed row (Plugin.cs:662-664). A scoped sort must reset out-of-scope proposals.
- A scan clears the session's tool unprotect overrides and every staged proposal
  (`ReplaceScanAtomically`, OrganizerState.cs:166-177). Any pipeline scan therefore discards both; the
  banner says so.
- A scan settles `StaleModList` when the mod-event epoch moves (LibraryWorkCoordinator.cs:169-171, 271-274);
  every Penumbra mod event increments it. Scanning mid-import fails, so a debounce is required.
- Penumbra events arrive on undocumented threads (EventLogBuffer.cs remarks). There are **five**
  subscribers (Plugin.cs:161-187): ModAdded, ModDeleted, ModMoved, ModDirectoryChanged, Disposed.
- The post-Apply/Restore rescan runs only inside `MainWindow.Draw()` -> `ConsumeCompletionIfNew`
  (MainWindow.cs:194, 243-264). With the window closed it never runs, so automatic mode would work once
  and then act on stale rows. **Task B0 moves it into `Plugin.OnFrameworkUpdate`.**
- The "Rediscover Mods" reminder after Apply is a modal on the Review tab (MainWindow.ReviewTab.cs:60-65,
  107-120); a headless Apply never shows it.
- Heliosphere and Proteus mods are protected by default (`ToolProtects`, OrganizerState.cs:536-547).
  A count of "new mods" that ignores protection overstates what will move.
- Anchors come from the path's root: `ToolFolders.AnchorOf` (ToolFolders.cs:30-38) takes the first
  segment; `DeepestAnchor` (ToolFolders.cs:74-88) matches chosen folders from the root; `AnchorFor` /
  `UserAnchorOf` (OrganizerState.cs:657-667) feed them `row.CurrentPath`. After a collection sort is applied
  (`Alice/Heliosphere/...`), the next sort would anchor at `Alice` and produce `Alice/Alice/...`, and user
  sort-inside folders (stored as `.Chars`) would no longer match. **E3 strips the outer segment first.**
- `CollisionDisambiguator.Disambiguate` renumbers only among rows passed in (CollisionDisambiguator.cs:11-49).
- The mod root is read only in the private `BuildInstallation` (Plugin.cs:438-447).
- `FirstRunStepsTests` pins the walkthrough at exactly 8 steps; `HelpContentTests` enforces json entry per
  constant, `HelpTopicUsage` per short, short <= 200 chars, ASCII only.

---

## Design

### Feature 1 - "Only loose mods in the root"

**A Sort scope, not protect rules.** Saved `FolderProtectionRules` are never read differently or written.

```
public sealed record SortScope(bool RootOnly, IReadOnlySet<string>? Identifiers = null)
{
    public static SortScope WholeLibrary { get; } = new(false);
    public bool Includes(OrganizerModRow row);   // !Protected && (!RootOnly || IsAtRoot) && (Identifiers?.Contains(id) ?? true)
    public static bool IsAtRoot(string currentPath); // OrganizationCleanupPlanner.GetVirtualParent(currentPath) is null
}
```

`OrganizerState.Sort(..., SortScope? scope = null)`:
- In-scope rows get a new proposal; **out-of-scope unprotected rows are reset** to `CurrentPath` (and
  `StagedBodyResolution = null`), because Apply moves every changed unprotected row.
- **Numbered layouts are built from every unprotected row**, as a whole-library sort builds them, so a
  root-only sort never invents a folder number the next full sort would renumber.
- **Untouched rows' `CurrentPath`s are reserved** in disambiguation, so a root mod is never proposed onto an
  occupied path (otherwise `Validate()` blocks Apply). Read `PenumbraPathSemantics` remarks before touching it.
- Sort records a `SortSummary(Scope, InScope, Total, ProtectedAtRoot)`; a scan clears it. The scope line
  and the Apply modal render it via pure `SortSummaryText.Describe`.

**Interactions** (all follow from "a row in a folder is out of scope"): exclude rules are irrelevant in
root-only mode (help says so); a ticked root mod stays put; tool modes and the session override are
unchanged; sort-inside folders' mods are in a folder; templates and the workbook do not call `Sort`.

### Feature 2 - New-mod banner

**Thread affinity.** All five subscribers keep their current work and ALSO enqueue a `NewModEvent` into
`NewModTracker`'s concurrent inbox. `OnFrameworkUpdate` drains it.

**What counts as new (bounded).** The tracker holds `KnownIdentifiers`: seeded on the first framework
update from one `GetModListAdapter` read, and unioned with every published scan's identifiers. A drained
`Added(dir)` is new only if `dir` is not known at that moment (then it becomes known). `Deleted` removes from
pending; `Moved(old,new)` renames; `ModDirectoryChanged` and `Disposed` **clear pending** (and re-seed known
on the next update). A batch is ready when pending is non-empty and no mod event arrived for 5 s.

**Counting only what would move.** When a batch is ready, one `GetModListAdapter` read (no scan) builds
minimal seeds for the batch, and pure `NewModEligibility.Filter` applies: at root (Q1), and NOT protected by
the saved rules: `ProtectedModIdentifiers`, `FolderProtectionRules`, `ToolPolicy`, Heliosphere
(`KnownHeliosphereIdentifiers` or `HeliosphereDetector.IsHeliosphereManaged`, as `ReadCurrentMods` does) and
Proteus (`ProteusDetector`). This reuses the one protection rule, made public as
`ProtectionRule.IsProtected(row, ids, folderRules, policy, overrides)` (moved out of OrganizerState.cs:521-548,
behavior unchanged). Zero eligible -> no banner, one log line.

**Pipeline.** Pure `NewModSortPipeline` behind `INewModSortHost`:

```
Ready batch (eligible > 0):
  Automatic && eligible <= 20 && !HasStagedProposals && !HasSessionOverrides -> Admitting
  otherwise -> Banner (Ask), text from NewModPromptText
Admitting: AdmissionRejection() non-null -> wait, re-check each update, give up after 2 min with a message
Scanning:  StartScan(); wait for ScanGeneration to advance or the scan to settle
           StaleModList -> back to Admitting; Failed -> Failed(message)
Sorting:   SortScoped(new SortScope(RootOnly: true, Identifiers: batch)) -> changed count
           0 -> Done("nothing to move")
Applying (Sort now / automatic / command): StartApply(); wait for CompletionGeneration -> Done(summary) | Failed
Review first: after Sorting, OpenReview() -> Done
A batch arriving mid-run is queued, never merged into the running scope, never lost.
```

- The pipeline never calls `SetModPath`; Apply goes through `Plugin.StartApplyOperation` (admission,
  Validate, organization.json collision check, History snapshot).
- **Banner text** (pure `NewModPromptText`): `3 new mods are loose in the root. Sort them? [Sort now]
  [Review first] [Not now]`, plus, only when true, `Sorting rescans your library first: your unapplied sort
  will be discarded.` and `This session's unprotect choices for Heliosphere/Proteus mods will be reset.`
  "Not now" keeps them pending for `/porganizer sortnew`.
- **Window closed**: one chat line instead of the banner: `Penumbra Organizer: 3 new mods. /porganizer
  sortnew sorts them, or open /porganizer.` The banner still shows when the window opens.
- **Chat after a headless Apply** (`IChatGui`, new `[PluginService]`): `Penumbra Organizer: moved 3 new
  mods. Undo from the History tab. In Penumbra, Settings > Rediscover Mods removes any empty folders left
  behind.` (the reminder the Review modal would have shown).
- Sort settings for a headless run come from `SortSelectionFactory.FromConfig`, shared with the Sort tab.

**The rescan after Apply moves to the plugin (B0).** A pure `CompletionWatcher` (generation consumed once)
is driven from `OnFrameworkUpdate` beside `RecordOperationCompletionIfNew` (Plugin.cs:610): on an Apply or
Restore completion it calls `TryRequestScan()` (retrying on later frames if refused) and raises an event the
window consumes for UI-only effects (history cache, the Rediscover modal flag). `ConsumeCompletionIfNew`
stops calling `RunScan()`.

**Setting.** `Configuration.NewModAction` = `Ask` (default) | `SortAutomatically` | `DoNothing`, in the
Sort tab's collapsed Advanced header. Automatic is never the default.

### Feature 3 - Chat command

`/porganizer` with no argument toggles the window. Pure `CommandParser.Parse(args)`:
`sortnew [force]`, `sortroot [force]`, `help`, unknown. `sortnew` runs the pipeline with every pending
eligible mod, skipping the debounce and the cap; `sortroot` runs it with `SortScope(RootOnly: true)` over
every loose mod. Both Apply.

Chat answers (defined, tested in `NewModSortPipelineTests` / `CommandParserTests`):
- nothing pending: `No new mods since the plugin loaded. /porganizer sortroot sorts every loose mod in the root.`
- staged proposals or session overrides exist and no `force`: `You have an unapplied sort (or unprotect
  choices) that a rescan would discard. Apply or clear it on the Review tab, or run /porganizer sortnew force.`
- engine busy: the engine's own rejection reason, then `Try again when it finishes.`
- success / nothing to move / failure: the same lines as the banner path.

### Feature 4 - Unused mods report

- `CollectionSettingsReader` (IPC adapter, not unit-tested): `GetCollections()`, then
  `GetAllModSettings(id, ignoreInheritance: true, ignoreTemporary: true)` per collection -> pure
  `CollectionUsage` (Id, Name, enabled directories; `Unreadable` list). Null on total failure, never throws.
- Pure `UnusedModsReport.Build(rows, usedDirectories)`; protected rows flagged "protected - will not move".
- Size: `Path.Combine(ModDirectory, identifier)` via `ModFolderSize.Measure` on a cancellable `Task.Run`,
  results in a `ConcurrentDictionary`; read-only, so no activity gate. `ByteSize.Format`.
  `Plugin` gains `internal string? ModDirectory` (wraps `GetModDirectory`, null on failure).
- `OrganizerState.StageMoveUnder(identifiers, "Unused")` -> `Unused/<CurrentPath>`; skips protected and
  already-under rows; additive (keeps other staged proposals); same collision tail.
- UI: `Unused mods (N)` collapsed section on the Scan tab, naming the collections read ("Checked 3
  collections: Alice, Base, Bob. Temporary settings from other plugins are ignored."), table with sizes,
  select all, "Move selected to Unused" + "Protect the Unused folder" checkbox (Q5), then a hint to open Review.

### Feature 5 - One folder per collection

- Pure `CollectionFolderNames.Build(names, reserved)`: `FixName`; sorter-folder or Shared/Unused clash ->
  " (collection)"; duplicates -> " (2)" in GUID order; blank -> "Collection".
- Pure `CollectionFolderAssigner.Assign(rows, usage, names, rule, sharedName, unusedName)` -> identifier ->
  outer folder, or null (LeaveInPlace rows are removed from the sort scope, so they do not move).
- **Idempotency (review item 1).** Pure `OuterFolders.Strip(currentPath, outerNames)` removes a leading
  segment that is a collection folder, `Shared` or `Unused` (case-insensitive; `outerNames` =
  `CollectionFolderNamesInUse` from config + the two configured names). `AnchorFor`, `UserAnchorOf`,
  `ToolModeOf` and the root test use the **stripped** path; `ToolFolders` itself is unchanged.
  `SortInsideFolderChoices.Candidates` strips too, so a chosen `.Chars` matches `Alice/.Chars/x`.
  The final proposal is `outer + "/" + ToolAdjustedPath(...)`, so re-sorting an applied layout yields the
  same path and `FinishProposals`' equivalence check turns it into zero changes.
- Layout group key `(outer ?? "", anchor)`, so each collection is numbered on its own.
- UI: Sort tab "Collections" group: checkbox, shared-rule combo, shared folder name; collections are read on
  the Sort click (C+1 IPC calls); "Read 3 collections: ..." under the scope line; on read failure, sorts
  without collection folders and says so.

### Feature 6 - Help and walkthrough (minimum)

Each UI task adds its own tooltip topics (json + `HelpTopics` + `HelpTopicUsage` + `HelpTab.Sections`), so
`HelpContentTests` stays green per commit. F1: `help.tab-sort` body (scope, collections, Advanced/new mods),
`help.tab-scan` body (Unused mods), one new Help section `help.new-mods` (banner, settings, the three
commands, undo via History, the Rediscover note), and **one sentence** added to `firstrun.sort`
("Use What to sort to sort only the loose mods in the root."). No new step; `firstrun.help` unchanged.
F2: `docs/USER_GUIDE.md`, `docs/HOW_SORTING_WORKS.md`, `docs/ui-mock/sort.html` + `scan.html`, rebuilt
`docs/USER_GUIDE.pdf` (`python docs/build-pdf.py`), `docs/ROADMAP.md`.

---

## Global constraints

- Branch `feat/root-sort-and-new-mods` from `main` in `C:\Repo\PenumbraOrganizer.Plugin`. Verify the full
  path on every file operation (worktree drift has happened before).
- Build: `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj -c Debug`.
  Test: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter
  "FullyQualifiedName~<Name>"`. Record the passing baseline in X0.
- No `InternalsVisibleTo`: anything a test reaches is `public`. Pure logic in `Organizer/` or non-ImGui
  `Windows/*` files; ImGui code stays thin. `LibraryWorkPurityTests` rules apply to `LibraryWork/Pure`.
- Every `Begin*` has its `End*`; `Help.Tooltip` right after its widget.
- Plain ASCII everywhere, including test strings.
- Commit trailer: `Co-Authored-By: <implementing model> <noreply@anthropic.com>`.
- No release, no version bump, no tag, no repo.json. Screenshots are retaken in game by the author.

## File structure

| File | Change | Tasks |
|---|---|---|
| `Organizer/SortScope.cs`, `Organizer/SortSummary.cs` (+ `SortSummaryText`) | create | A1 |
| `Organizer/ProtectionRule.cs` | create: the protection rule moved out of OrganizerState, unchanged | A0 |
| `Organizer/CollisionDisambiguator.cs` | `reservedPaths` overload | A2 |
| `Organizer/OrganizerState.cs` | use ProtectionRule; scope + summary; `StageMoveUnder`; outer folders + strip; `HasSessionOverrides` | A0, A2, D3, E3 (serial) |
| `Windows/SortSelectionFactory.cs` | create: `FromConfig`, `SortRunner.Run`; later collection settings | A3, E3, E4 (serial) |
| `Windows/SortPanel.cs` | `RootOnly` on `SortSelection`; What to sort combo; scope line; Advanced header; Collections group | A3, A4, B5, E4 (serial) |
| `Windows/MainWindow.cs`, `MainWindow.Tutorial.cs` | completion consumer stops rescanning; `RequestTab`; banner slot | B0, B4, B5 (serial) |
| `Windows/MainWindow.ReviewTab.cs` | scope line in "Apply changes?" modal | A4 |
| `Windows/MainWindow.ScanTab.cs`, `Windows/UnusedModsSection.cs` | Unused mods section | D4 |
| `Configuration.cs` | `NewModAction`, `UnusedFolderName`, `SortByCollection`, `CollectionSharedRule`, `CollectionSharedFolderName`, `CollectionUnusedRule`, `CollectionFolderNamesInUse` | X1 |
| `Organizer/Operations/CompletionWatcher.cs` | create | B0 |
| `Organizer/NewMods/NewModTracker.cs`, `NewModEligibility.cs`, `NewModSortPipeline.cs`, `INewModSortHost.cs`, `NewModPromptText.cs` | create | B1, B2, B3, B5 |
| `Organizer/CommandParser.cs` | create | C1 |
| `Plugin.cs` | B0 rescan; tracker feed; pipeline; host adapter; `IChatGui`; `ModDirectory`; commands; `CollectionSettingsReader` property | B0, B4, D4a, C2 (serial) |
| `Organizer/Collections/*` (`CollectionUsage`, `CollectionSettingsReader`, `UnusedModsReport`, `ModFolderSize`, `ByteSize`, `CollectionFolderNames`, `CollectionFolderAssigner`, `OuterFolders`, `CollectionRules`) | create | X1, D1, D2, E1, E2, E3 |
| `Organizer/SortInsideFolderChoices.cs` | strip outer segment | E3 |
| `Resources/help-content.json`, `Windows/HelpTopics.cs`, `HelpTopicUsage.cs`, `HelpTab.cs` | topics | A4, B5, D4, E4, F1 (serial) |
| `docs/USER_GUIDE.md`, `HOW_SORTING_WORKS.md`, `ui-mock/sort.html`, `ui-mock/scan.html`, `USER_GUIDE.pdf`, `ROADMAP.md` | prose | F2 |

## Test list (Tests project)

- `Organizer/ProtectionRuleTests`: the moved rule gives the same answers as before (re-run
  `OrganizerStateToolPolicyTests`, `OrganizerStateTests` unchanged).
- `Organizer/SortScopeTests`: whole library; root-only excludes `A/x`; protected root excluded; identifier
  filter; both flags intersect.
- `Organizer/SortSummaryTextTests`: "Will sort N of M mods: loose in root. K protected root mods stay put.";
  whole-library wording; K = 0 omits the second sentence; ASCII.
- `Organizer/OrganizerStateScopedSortTests`: root-only moves only root rows; out-of-scope rows reset after a
  staged full sort; protected root row untouched; excluded-folder rows untouched; sort-inside rows untouched;
  numbering equals a full sort's; a root row whose path equals an untouched row's current path is renumbered;
  `Validate()` clean; summary recorded and cleared by a scan; null scope = today (`OrganizerStateSortTests`
  unchanged).
- `Organizer/CollisionDisambiguatorTests`: reserved path forces a suffix; reserved paths never renamed.
- `Windows/SortSelectionFactoryTests`: `FromConfig` equals the panel's selection for the same config;
  unknown strategy -> default.
- `Windows/SortPanelPreferencesTests` (existing file, new cases): first-frame load through the factory gives
  the same grouping index, splits, hair race and body priority the old inline load gave, for a default
  config and a fully customised one; `RootOnly` always starts false.
- `Organizer/Operations/CompletionWatcherTests` (review item 2): a generation is consumed once; Apply and
  Restore request a rescan, other kinds do not; a refused rescan is retried on the next update until it
  starts; works with no window (no UI dependency).
- `Organizer/NewMods/NewModTrackerTests`: seeded known set; add of a known id is not new; add of an unknown
  id is new and becomes known; scan publish extends known; not ready before 5 s; a new event restarts the
  clock; delete removes; move renames; ModDirectoryChanged and Disposed clear pending; `TakeAll` skips the
  debounce; duplicates collapse; concurrent enqueue loses nothing.
- `Organizer/NewMods/NewModEligibilityTests`: folder mod excluded; protected-by-id, by-folder-rule,
  Heliosphere (known id and detector), Proteus-made excluded; Heliosphere in Normal mode included; an exclude
  rule does not matter for a root mod.
- `Organizer/NewMods/NewModSortPipelineTests` (fake host): Ask -> Banner; Automatic -> scans; Automatic with
  21 eligible -> Banner; Automatic with staged proposals or session overrides -> Banner with the warning
  lines; gated -> waits, then proceeds; gate never clears -> times out; StaleModList -> re-admits; scan Failed
  -> Failed; zero changed -> Done, no Apply; Sort now -> StartApply once -> Done on next completion; Apply
  rejection -> Failed with the engine's reason; Review first -> OpenReview, no Apply; missing ids counted;
  a batch mid-run is queued; **two automatic batches in a row with the window closed both apply** (with
  B0's rescan in the fake host); command paths: nothing pending, staged without force, staged with force,
  busy - each produces the chat line defined above.
- `Organizer/NewMods/NewModPromptTextTests`: 1 vs N; warning lines only when true; window-closed chat line;
  post-Apply line includes the Rediscover note; ASCII.
- `Organizer/CommandParserTests`: empty -> Toggle; `sortnew`, ` SortNew `, `sortnew force`, `sortroot`,
  `help`; unknown.
- `Organizer/Collections/CollectionUsageTests`, `UnusedModsReportTests`, `ModFolderSizeTests` (temp dir),
  `ByteSizeTests`, `CollectionFolderNamesTests`, `CollectionFolderAssignerTests`, `OuterFoldersTests`
  (strip only a known leading segment; case-insensitive; never strips a sorter folder or a deeper segment).
- `Organizer/OrganizerStateStageMoveTests`: `Unused/<CurrentPath>`; protected skipped; already-under skipped;
  other proposals kept; collision renumbered.
- `Organizer/OrganizerStateCollectionSortTests`: prefix; per-collection numbering; Heliosphere and Proteus
  SortInside under the collection; user sort-inside under the collection; protected untouched; with root-only.
  **Sort -> apply (LoadScan with CurrentPath = ProposedPath) -> sort again gives zero changes**, once per
  anchor mode: plain, Heliosphere SortInside, Proteus SortInside, user sort-inside (`.Chars` and `.Chars/Alice`),
  Shared, Unused, and with root-only; plus option switched off after applying (Q11) gives no `Alice/Alice`.
- `Organizer/SortInsideFolderChoicesTests`: candidates under a collection folder are offered stripped.
- `ConfigurationTests`: an old config without the new fields reads the defaults.

---

## Tasks

Difficulty easy / medium / hard. Model: haiku (mechanical, new isolated file), sonnet (medium, touches
existing code), opus (hard: invariants across sort, numbering, Apply or threading).

### X0 - Branch and baseline  (easy, haiku)
- [ ] `git checkout main && git status --short`; `git checkout -b feat/root-sort-and-new-mods`; full test run,
  record the pass count.

### X1 - Configuration fields  (easy, haiku)
Files: `Configuration.cs`, `Organizer/Collections/CollectionRules.cs`, `Tests/ConfigurationTests.cs`.
- [ ] Defaults test; add `NewModAction { Ask, SortAutomatically, DoNothing }`, `CollectionSharedRule
  { SharedFolder, LeaveInPlace }`, `CollectionUnusedRule { UnusedFolder, SortNormally }` and the properties
  (`UnusedFolderName = "Unused"`, `CollectionSharedFolderName = "Shared"`, `CollectionFolderNamesInUse = []`).
  Additive, no version bump. Commit.

### A0 - Extract the protection rule  (medium, sonnet)
Files: create `Organizer/ProtectionRule.cs`; `Organizer/OrganizerState.cs`; tests.
- [ ] Move `IsEffectivelyProtected` + `ToolProtects` (OrganizerState.cs:521-548) verbatim into public static
  `ProtectionRule`; OrganizerState calls it. Add `public bool HasSessionOverrides`. Whole suite green. Commit.

### A1 - `SortScope`, `SortSummary`  (easy, haiku)
Files: create `Organizer/SortScope.cs`, `Organizer/SortSummary.cs`, tests. Commit.

### A2 - Scoped sort in `OrganizerState`  (hard, opus)
Files: `Organizer/OrganizerState.cs`, `Organizer/CollisionDisambiguator.cs`, tests. After A0, A1.
- [ ] Failing tests; `Disambiguate(rows, reservedPaths = null)`; `Sort(..., SortScope? scope = null)` on the
  long overload; private tail touches `scope.Includes` rows, resets other unprotected rows, reserves
  untouched current paths; records `LastSortSummary` (cleared in `ReplaceScanAtomically`). Whole
  `OrganizerState*` suite. Commit.

### A3 - Headless sort settings  (medium, sonnet)  - after A2 (uses the scope signature)
Files: create `Windows/SortSelectionFactory.cs`; `Windows/SortPanel.cs` (`bool RootOnly = false` on
`SortSelection`; first-frame load via the factory; Sort button calls `SortRunner.Run`);
`Tests/Windows/SortSelectionFactoryTests.cs`, `Tests/Windows/SortPanelPreferencesTests.cs`.
- [ ] `FromConfig(Configuration, bodyLabels)`, `SortRunner.Run(state, selection, canonicalize, scope,
  outerFolders = null)` - the ONE call to `state.Sort`. Body priority via `BodyPriority.Repair`, no save. Commit.

### A4 - What to sort combo and scope line  (medium, sonnet)
Files: `Windows/SortPanel.cs`, `Windows/MainWindow.ReviewTab.cs`, help files. After A3.
- [ ] Combo on the Group by line (session-only); scope line beside Sort from `SortSummaryText`; the same
  line under "Apply changes to N mods?" in the modal when a summary exists. Topics `sort.what-to-sort`,
  `sort.scope-line`. Commit.

### B0 - Post-Apply rescan without the window  (medium, sonnet)
Files: create `Organizer/Operations/CompletionWatcher.cs` (+ tests); `Plugin.cs` (drive it in
`OnFrameworkUpdate` beside `RecordOperationCompletionIfNew`, own try/catch + latch, `TryRequestScan` with
retry); `Windows/MainWindow.cs` (`ConsumeCompletionIfNew` keeps history-cache and reminder flag, drops
`RunScan()`). Independent of A-tasks.
- [ ] Tests from the list. In game: Apply with the window open still rescans exactly once. Commit.

### B1 - `NewModTracker`  (medium, sonnet)
Files: create `Organizer/NewMods/NewModTracker.cs`, tests. Independent. Commit.

### B2 - `NewModEligibility`  (easy, sonnet)
Files: create `Organizer/NewMods/NewModEligibility.cs`, tests. After A0. Commit.

### B3 - `NewModSortPipeline` and `NewModPromptText`  (hard, opus)
Files: create `Organizer/NewMods/INewModSortHost.cs`, `NewModSortPipeline.cs`, `NewModPromptText.cs`, tests.
After A1 (types).
- [ ] Host port: `AdmissionRejection()`, `HasStagedProposals`, `HasSessionOverrides`, `StartScan()`,
  `ScanGeneration`, `ScanState`, `SortScoped(SortScope)`, `StartApply()`, `OperationCompletionGeneration`,
  `OperationState`, `OpenReview()`, `IsWindowOpen`, `Chat(string)`, `Setting`. Pipeline API: `Offer(batch,
  eligibleCount)`, `UserChose(SortNow|ReviewFirst|NotNow)`, `RunCommand(kind, force)`, `Update(now)`,
  `Banner` (text model or null). Tests from the list. Commit.

### B4 - Plugin wiring  (medium, sonnet)
Files: `Plugin.cs`, `Windows/MainWindow.cs`, `Windows/MainWindow.Tutorial.cs`. After B0-B3, A3.
- [ ] `[PluginService] IChatGui`; all five subscribers also enqueue; seed known ids on first update; scan
  publish extends known (in `RunPostScanSideEffects`); on ready batch: one mod-list read + `NewModEligibility`
  + `pipeline.Offer`; `pipeline.Update(now)` in its own try/catch + latch; host adapter (`OrganizerState`,
  `ScanWork.State`, `OperationController`, `SortRunner` + `SortSelectionFactory.FromConfig`,
  `StartApplyOperation`, chat). `MainWindow.RequestTab(HighlightTarget)` (one-frame `SetSelected`, reusing
  the tutorial mechanism). Commit.

### B5 - Banner and Advanced setting  (medium, sonnet)
Files: `Windows/MainWindow.cs` (banner in the `_lastError` slot: text + three buttons when
`pipeline.Banner` is non-null, error line still shown below it), `Windows/SortPanel.cs` (collapsed
"Advanced" header: "When new mods are installed: Ask / Sort automatically / Do nothing", saved at once),
help files. After B4, A4.
- [ ] Topics `newmods.banner`, `sort.new-mod-action`. In game: install 2 mods -> one banner after ~5 s;
  install 25 with automatic on -> banner, not an automatic run. Commit.

### C1 - `CommandParser`  (easy, haiku)
Files: create `Organizer/CommandParser.cs`, tests. Independent. Commit.

### D1 - Collection usage model and reader  (medium, sonnet)
Files: create `Organizer/Collections/CollectionUsage.cs` (+ tests), `CollectionSettingsReader.cs`.
Independent (the `Plugin.cs` property comes in D4a). Commit.

### D2 - Report, size, byte formatting  (easy, haiku)
Files: create `Organizer/Collections/UnusedModsReport.cs`, `ModFolderSize.cs`, `ByteSize.cs`, tests. After D1. Commit.

### D3 - `StageMoveUnder`  (medium, sonnet)
Files: `Organizer/OrganizerState.cs`, tests. After A2. Commit.

### D4a - Plugin accessors  (easy, haiku)  - after B4, before C2
Files: `Plugin.cs`.
- [ ] `internal string? ModDirectory` (wraps `GetModDirectory`, catches, null on failure; `BuildInstallation`
  reuses it) and `public CollectionSettingsReader CollectionSettingsReader { get; }`. Commit.

### C2 - Command dispatch  (easy, sonnet)  - after D4a
Files: `Plugin.cs`. After B4, C1.
- [ ] `OnCommand` -> parser -> toggle / `pipeline.RunCommand(...)` / help line; `HelpMessage` lists the
  subcommands. Commit.

### D4 - Unused mods section  (medium, sonnet)
Files: create `Windows/UnusedModsSection.cs`; `Windows/MainWindow.ScanTab.cs` (one call, collapsed header
`Unused mods (N)###scan-unused`); help files. After D1-D3, D4a, B5 (help files).
- [ ] As in the Design; size task cancelled when a scan publishes or the plugin unloads; "Protect the Unused
  folder" ticked by default writes the rule via `SetFolderProtectionRule` + `SaveProtectionState`. Topics
  `unused.check`, `unused.move`, `unused.protect-folder`. Commit.

### E1 - `CollectionFolderNames`, `OuterFolders`  (easy, haiku)
Files: create `Organizer/Collections/CollectionFolderNames.cs`, `OuterFolders.cs`, tests. Independent. Commit.

### E2 - `CollectionFolderAssigner`  (medium, sonnet)
Files: create `Organizer/Collections/CollectionFolderAssigner.cs`, tests. After D1, E1, X1. Commit.

### E3 - Outer folders in `Sort`, idempotent  (hard, opus)
Files: `Organizer/OrganizerState.cs`, `Organizer/SortInsideFolderChoices.cs`, `Windows/SortSelectionFactory.cs`
(pass outer folders and the strip set through `SortRunner`), tests. After A2, A3, D3, E1, E2.
- [ ] `Sort(..., IReadOnlyDictionary<string,string>? outerFolders = null, IReadOnlySet<string>? outerNames = null)`;
  anchors and the root test read `OuterFolders.Strip(row.CurrentPath, outerNames)`; layout key
  `(outer, anchor)`; proposal `outer + "/" + ToolAdjustedPath(...)`; LeaveInPlace rows out of scope.
  All zero-change round-trip tests from the list. Whole `OrganizerState*` suite. Commit.

### E4 - Collections UI  (medium, sonnet)
Files: `Windows/SortPanel.cs`, `Windows/SortSelectionFactory.cs`, `Configuration` use (save
`CollectionFolderNamesInUse` after a collection sort), help files. After E3, D4.
- [ ] As in the Design. Topics `sort.by-collection`, `sort.collection-shared`. Commit.

### F1 - Help prose and walkthrough sentence  (easy, haiku)
Files: `Resources/help-content.json`, `Windows/HelpTopics.cs`, `Windows/HelpTab.cs`. After E4.
- [ ] As in Feature 6. Run `HelpContentTests`, `HelpTabTests`, `FirstRunStepsTests`. Commit.

### F2 - Guide, sorting doc, mocks, PDF, roadmap  (easy, haiku)
Files: docs only (see File structure). After F1. Mark Sort and Scan screenshots for an in-game retake. Commit.

### F3 - In-game verification (author)
Root-only on a real library; install 3 mods -> one banner, Sort now, History undo; the same with the window
closed twice in a row (automatic); 25 mods with automatic -> banner; `/porganizer sortnew` with a staged sort
(refused) and with `force`; Rediscover Mods: does it raise a banner (it should not); unused report names
collections, sizes appear; collection sort applied twice -> second run proposes nothing; large-library timing.

---

## Order and parallelism

Serial spines (shared files):
- `OrganizerState.cs`: A0 -> A2 -> D3 -> E3
- `Plugin.cs`: B0 -> B4 -> D4a -> C2
- `MainWindow.cs`: B0 -> B4 -> B5
- `SortPanel.cs` / `SortSelectionFactory.cs`: A3 -> A4 -> B5 -> E3 -> E4
- help files: A4 -> B5 -> D4 -> E4 -> F1

| Wave | Tasks (parallel within a wave; no shared files) |
|---|---|
| 0 | X0 |
| 1 | X1, A0, A1, B1, C1, D1, E1 |
| 2 | A2, B0, B2, B3, D2, E2 |
| 3 | A3, D3 |
| 4 | A4, B4 |
| 5 | B5, D4a |
| 6 | C2, D4 |
| 7 | E3 |
| 8 | E4 |
| 9 | F1, then F2 |
| 10 | F3 (author, in game) |

(B0 touches `Plugin.cs` and `MainWindow.cs` and A2 touches only `OrganizerState.cs`/the disambiguator, so
wave 2 has no shared file.)

Model summary: opus for A2, B3, E3; sonnet for A0, A3, A4, B0, B1, B2, B4, B5, C2, D1, D3, D4, E2, E4;
haiku for X0, X1, A1, C1, D2, D4a, E1, F1, F2. Each opus task gets a final review against the "Codebase
facts" list above.
