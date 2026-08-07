# Piece 0: split MainWindow before the UI features land in it

Date: 2026-08-07
Part of `2026-08-07-ui-overhaul-umbrella-design.md` (piece 0 of 6, built first)
Branch: `design/ui-overhaul`

## Why now, and why first

`MainWindow.cs` is **2,080 lines** with **16 draw methods** and is the largest file in the project.

**Three** of the pieces in this overhaul modify it: the sort control (piece 2), hover explanations
(piece 3) and the Help tab (piece 4). Piece 5 does not — the guided first run is a separate Dalamud
`Window` and integrates through a button on the Help tab. Splitting after the other three land means
touching them again; splitting before means they land in small focused files from the start.

This work was already on the cleanup brief, sequenced **last**. That was right when it stood alone.
It is wrong now: the value of a split is highest immediately before features are added, not after.

## The risk, stated plainly

**No test exercises MainWindow's rendering.** An earlier draft said "zero tests touch it", which is
false and matters: `PenumbraOrganizer.Plugin.Tests/Windows/WorkbookStrategyOptionsTests.cs` reads
`MainWindow`'s private static `WorkbookStrategyOptions` field **by name, via reflection**, and
`ActivityGatesTests.cs` and `EventLogBufferTests.cs` cover other `Windows` types.

Two consequences:

- **Stage A is unaffected.** A partial-class move leaves that field where it is, and the reflective
  test keeps passing.
- **Stage B is affected, and this is the warning worth carrying forward.** The moment piece 2 moves
  `WorkbookStrategyOptions` into a real `SortPanel` type, that test fails at runtime with an
  unhelpful `InvalidOperationException` from the reflection lookup, not a compile error. Piece 2 owns
  updating it.

What remains true is that **nothing renders these methods under test**, so the refactor's safety net
is the compiler, diff review, and looking at it in-game. That constrains what this piece is allowed
to be: a **mechanical move and nothing else**.

## Design

### Stage A: `partial class MainWindow`, one file per tab

`MainWindow` becomes a partial class. Draw methods move to sibling files. Nothing else changes.

| File | Moves in |
|---|---|
| `MainWindow.cs` | class declaration, fields, constructor, `Draw()`, `Dispose()`, tab-bar dispatch |
| `MainWindow.ScanTab.cs` | `DrawScanTab` |
| `MainWindow.ProtectTab.cs` | `DrawProtectTab` |
| `MainWindow.SortTab.cs` | `DrawSortTab` |
| `MainWindow.ReviewTab.cs` | `DrawReviewTab`, `DrawOrphanedFoldersSection`, `DrawOrphanCheckbox`, `DrawFolderActionResults` |
| `MainWindow.HistoryTab.cs` | `DrawHistoryTab` |
| `MainWindow.SearchTab.cs` | `DrawSearchTab` |
| `MainWindow.Recovery.cs` | `DrawRecoveryPanelIfNeeded`, `DrawArtifactLine` |
| `MainWindow.Widgets.cs` | `DrawWrappingButtonRow`, `DrawWrappingCheckboxRow`, `DrawOperationProgress`, `DrawLibraryWorkProgress`, `DrawLibraryWorkOutcome` |

**The 16 draw methods are not the whole file.** They account for roughly 1,360 of 2,080 lines. The
remaining ~720 are fields and about 28 non-drawing helpers: `CurrentGates`, `TextColoredWrapped`,
`TextDisabledWrapped`, `SlotLabel`, `FormatElapsed`, `ConsumeCompletionIfNew`,
`RefreshRecentOperations`, `RestoreSnapshot`, `ContinueRecovery`, `RestorePreviousState`,
`ResolveOneMultiRoot`, `CreateBackup`, `DeleteHistorySnapshot`, `SaveProtectionStateSafely`,
`RunScan`, `BuildChangedItemIndex`, `OnScanPublished`, `ApplyChanges`, `OpenConfigFile`,
`OpenContainingFolder`, `CreateDiagnosticDump` (~150 lines by itself), `ExportWorkbook`,
`OpenFileWithDefaultApp`, `ImportWorkbook`, `RefreshNpcNamesAsync`, `RefreshOrphanedFolders`,
`CleanUpSelectedFolders`, `RollbackFolderCleanup`.

**Those stay in `MainWindow.cs` in Stage A**, deliberately. Moving them means deciding which are
tab-specific and which are shared, which is judgement, and judgement is what this stage excludes.
The honest consequence: `MainWindow.cs` ends around 700-750 lines and is **still the largest file in
the project**. This piece makes it tractable, not small.

Two additions to the table:

| File | Moves in |
|---|---|
| `MainWindow.HelpTab.cs` | created empty by piece 4, not by this piece |

Why partial classes rather than extracting real types: a partial split **cannot change behaviour**.
Private fields stay reachable, no constructor plumbing, no new types, no state boundaries to design.
The compiler proves the move is complete. Extracting a `SortPanel` type instead would require
deciding what state it owns and how it reaches the plugin, which is design work with real failure
modes, and none of it is needed to make the file smaller.

**What is forbidden in this stage**, because each would turn a safe move into a risky one:

- Reordering any ImGui call. Moving a method between files preserves ID-stack behaviour and
  `Begin`/`End` balance; reordering calls does not.
- **Moving any field, static or instance.** This is the one that can produce a bug the diff review
  passes. Static field initializers in different partial files run in an order the C# specification
  does not guarantee, so dragging `SearchableCategories` into `MainWindow.SearchTab.cs` can make
  `_librarySearchCategories = new(SearchableCategories)` observe a null array depending on `<Compile>`
  ordering. That compiles cleanly, leaves every method body identical, and may not reproduce on the
  reviewer's machine. **All fields stay in `MainWindow.cs`.**
- Renaming methods, fields or parameters.
- Changing access modifiers beyond what the move requires (nothing should need it).
- Extracting helper methods, merging duplicates, or fixing anything noticed in passing.

A reviewer must be able to confirm this stage by diffing method bodies and finding them identical.

**One method needs naming in the review checklist.** `DrawHistoryTab` contains an `EndPopup()`
followed by `continue` inside a `foreach` inside a popup body (around lines 1145-1149). Begin/End
balance there depends on control flow, not on lexical structure, and a single dropped line produces
an ImGui assertion at runtime rather than a compile error. Diff that block character by character.

### Stage B: real extraction, owned by the pieces that need it

Where a feature genuinely needs a component boundary, that feature's own spec designs it:

- Sort control consolidation extracts `Windows/SortPanel.cs`. **This guts `MainWindow.SortTab.cs`
  almost immediately**, leaving a stub holding Import Workbook, the NPC refresh button and manual
  assignment. That is expected, not waste: piece 2 needs a clean file to move *out of*, and this
  spec's byte-identical diff gate applies to the move, not to the lifetime of the result. A reviewer
  sequencing 0 then 2 should know they are diffing a method about to be dismantled.
- Help tab is a new **tab-drawing type** (`HelpTab.cs`) inside `MainWindow`'s tab bar, not a window.
  Guided first run is a separate Dalamud `Window` (`FirstRunWindow.cs`). An earlier draft called both
  "new windows", which is wrong for Help.
- Hover explanations add no new type; the tooltip content source is its own file already.

Stage B is deliberately **not** specified here. Splitting the split is the point: Stage A is safe
because it cannot change behaviour, Stage B changes state access and belongs with the feature that
motivates it.

## Verification

No test renders this code, so verification is explicit:

1. **Compiles with zero new warnings.**
2. **Full suite still passes**, including `WorkbookStrategyOptionsTests`, which reflects over a
   `MainWindow` field and is the one existing test that could notice this piece at all.
3. **Diff review**: every moved method body byte-identical to its previous form, with `DrawHistoryTab`'s
   popup-and-`continue` block checked character by character. This is the real gate.
4. **In-game pass over all six tabs**: Scan, Protect, Sort, Review Changes, History, Search. Each
   renders and each control responds.
5. **In-game pass over every modal**, because item 4 opens none of them and popups are the highest-risk
   construct in this file. `MainWindow.cs:37-41` documents why: `BeginTabBar` pushes an ID override,
   so popup scope is coupled to where it is opened. There are eleven `BeginPopupModal` blocks. Cover
   at minimum: Apply confirm, the post-Apply reminder, per-snapshot Restore confirm, snapshot Delete,
   "Clean up folders?", and the single-root recovery path.
6. **The multi-root recovery branch (`MainWindow.cs:189-260`) will not be verified.** It is
   unreachable without a contrived interrupted-operation state spanning two roots. Stating that
   plainly is better than implying coverage that will not happen. If it is worth staging, that is a
   deliberate extra task, not something to assume.

Items 4 to 6 cannot be automated and must actually be done. Note what they are *not* for: a dropped
or duplicated method is a compile error (`CS0111`, or an unresolved call), since all 16 draw methods
are called from within the class. The genuine residual risks are field motion and popup scoping,
which is why both are called out above.

## What this does not do

It does not reduce total line count, improve any API, or fix anything. `MainWindow` still does the
same work in the same order. The file is smaller because the code is in more files, which is the
entire and only goal.

It also does not stop `MainWindow.cs` growing again. That is what Stage B, in the four feature
specs, addresses.
