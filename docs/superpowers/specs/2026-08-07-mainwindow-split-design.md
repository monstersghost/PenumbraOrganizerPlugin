# Piece 0: split MainWindow before four features land in it

Date: 2026-08-07
Branch: `design/ui-overhaul`

## Why now, and why first

`MainWindow.cs` is **2,080 lines** with **16 draw methods** and is the largest file in the project.

Four of the five pieces in this overhaul add UI to it: the sort control, hover explanations, the Help
tab, and the guided first run. Splitting after they land means touching all four again. Splitting
before means they land in small focused files from the start.

This work was already on the cleanup brief, sequenced **last**. That was right when it stood alone.
It is wrong now: the value of a split is highest immediately before four features are added, not
after.

## The risk, stated plainly

**Nothing tests MainWindow's draw path.** Zero tests touch it. A refactor here has no safety net
beyond the compiler and looking at it in-game.

That constrains what this piece is allowed to be. It is a **mechanical move and nothing else**.

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

Why partial classes rather than extracting real types: a partial split **cannot change behaviour**.
Private fields stay reachable, no constructor plumbing, no new types, no state boundaries to design.
The compiler proves the move is complete. Extracting a `SortPanel` type instead would require
deciding what state it owns and how it reaches the plugin, which is design work with real failure
modes, and none of it is needed to make the file smaller.

**What is forbidden in this stage**, because each would turn a safe move into a risky one:

- Reordering any ImGui call. Moving a method between files preserves ID-stack behaviour and
  `Begin`/`End` balance; reordering calls does not.
- Renaming methods, fields or parameters.
- Changing access modifiers beyond what the move requires (nothing should need it).
- Extracting helper methods, merging duplicates, or fixing anything noticed in passing.

A reviewer must be able to confirm this stage by diffing method bodies and finding them identical.

### Stage B: real extraction, owned by the pieces that need it

Where a feature genuinely needs a component boundary, that feature's own spec designs it:

- Sort control consolidation extracts its panel.
- Help tab and guided first run are new windows and new types from the start.
- Hover explanations add no new type; the tooltip content source is its own file already.

Stage B is deliberately **not** specified here. Splitting the split is the point: Stage A is safe
because it cannot change behaviour, Stage B changes state access and belongs with the feature that
motivates it.

## Verification

No test suite covers this, so verification is explicit:

1. **Compiles with zero new warnings.**
2. **Full suite still passes** at whatever the count is when this runs. It should not move: no
   test touches these methods.
3. **Diff review**: every moved method body byte-identical to its previous form. This is the real
   gate, and it is why the forbidden list above exists.
4. **In-game pass over all six tabs**: Scan, Protect, Sort, Review Changes, History, Search. Each
   renders, each control still responds, the recovery panel still appears when an interrupted
   operation exists.

Item 4 cannot be automated and must actually be done. A partial-class move that compiles can still
be wrong if a method was dropped or duplicated during the move, and only the compiler and a human
looking at the tabs will catch it.

## What this does not do

It does not reduce total line count, improve any API, or fix anything. `MainWindow` still does the
same work in the same order. The file is smaller because the code is in more files, which is the
entire and only goal.

It also does not stop `MainWindow.cs` growing again. That is what Stage B, in the four feature
specs, addresses.
