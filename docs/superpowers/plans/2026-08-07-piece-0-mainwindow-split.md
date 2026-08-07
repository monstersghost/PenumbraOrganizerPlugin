# Piece 0: MainWindow Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split `MainWindow.cs` (2,080 lines) into partial-class files, one per tab, so the three UI features that follow land in focused files instead of compounding an existing problem.

**Architecture:** `MainWindow` becomes a `partial class`. The 16 draw methods move to sibling files. Fields, constructor, `Draw()`, `Dispose()`, tab dispatch and all ~28 non-drawing helpers stay in `MainWindow.cs`. Nothing else changes — this is a mechanical move whose correctness is provable by diffing method bodies.

**Tech Stack:** C# / .NET 10, Dalamud plugin (API level 15), Dear ImGui, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-07-mainwindow-split-design.md`. Read it before Task 1.

## Global Constraints

- **This is a mechanical move and nothing else.** A reviewer must be able to confirm it by diffing method bodies and finding them byte-identical.
- **Do not move any field, static or instance. All fields stay in `MainWindow.cs`.** The reason is not that a specific field would break today — no static-to-static initializer dependency exists in this file, and the one an earlier draft cited (`_librarySearchCategories = new(SearchableCategories)`) provably cannot fail: `_librarySearchCategories` is an *instance* field, so its initializer runs in the constructor, necessarily after type initialization. The rule stands because **ordering among static initializers across partial declaration parts is unspecified in C#**, and such a dependency is invisible in a diff. Introducing one later, after fields have been scattered, would produce a null that compiles cleanly, passes review, and may not reproduce on the reviewer's machine. Keeping every field in one file makes the hazard impossible to create by accident.
- **Do not reorder any ImGui call.** Moving a method between files preserves ID-stack behaviour and `Begin`/`End` balance; reordering calls does not.
- **Do not rename anything, change any access modifier, extract any helper, merge any duplicate, or fix anything noticed in passing.** If you spot a bug, write it down and leave it.
- No test renders this code. The safety net is the compiler, diff review, and an in-game pass. That is why the rules above are absolute rather than advisory.
- Baseline: **912 tests passing** on `main` at `2044227` (verified 2026-08-08, after the docs merge). Diff against that commit, not `263682b`: 0.5.3.1 changed `MainWindow.cs` in between, and including that change would obscure the byte-identical check. This piece adds none and must not change the count.

---

### Task 1: Convert to a partial class and move the six tab methods

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`
- Create: `PenumbraOrganizer.Plugin/Windows/MainWindow.ScanTab.cs`
- Create: `PenumbraOrganizer.Plugin/Windows/MainWindow.ProtectTab.cs`
- Create: `PenumbraOrganizer.Plugin/Windows/MainWindow.SortTab.cs`
- Create: `PenumbraOrganizer.Plugin/Windows/MainWindow.ReviewTab.cs`
- Create: `PenumbraOrganizer.Plugin/Windows/MainWindow.HistoryTab.cs`
- Create: `PenumbraOrganizer.Plugin/Windows/MainWindow.SearchTab.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `MainWindow` is `partial`. Task 2 adds two more partial files.

- [ ] **Step 1: Make the class partial**

In `MainWindow.cs`, change the declaration:

```csharp
public sealed partial class MainWindow : Window, IDisposable
```

Build. Expect zero errors, zero new warnings — a partial class with one file is legal and identical.

- [ ] **Step 2: Create the six tab files with the correct scaffold**

Each new file uses this exact scaffold. Copy the `using` directives from the top of `MainWindow.cs` verbatim into each new file; unused ones are harmless and removing them counts as "fixing something in passing", which is forbidden.

```csharp
// <the same using directives as MainWindow.cs, verbatim>

namespace PenumbraOrganizer.Plugin.Windows;

public sealed partial class MainWindow
{
    // moved methods go here
}
```

- [ ] **Step 3: Move one method at a time, building after each**

Move exactly these, one per build:

| Destination file | Method(s) |
|---|---|
| `MainWindow.ScanTab.cs` | `DrawScanTab` |
| `MainWindow.ProtectTab.cs` | `DrawProtectTab` |
| `MainWindow.SortTab.cs` | `DrawSortTab` |
| `MainWindow.ReviewTab.cs` | `DrawReviewTab`, `DrawOrphanedFoldersSection`, `DrawOrphanCheckbox`, `DrawFolderActionResults` |
| `MainWindow.HistoryTab.cs` | `DrawHistoryTab` |
| `MainWindow.SearchTab.cs` | `DrawSearchTab` |

Cut the whole method including its XML doc comment and any comment block immediately above it. Paste it unchanged. Build. Move to the next.

Building after each move means a mistake is attributable to one method rather than six.

**`DrawHistoryTab` needs care.** It contains an `EndPopup()` followed by `continue` inside a `foreach` inside a popup body, at lines 1147-1148. Begin/End balance there depends on control flow rather than lexical structure, and one dropped line produces an ImGui assertion at runtime, not a compile error. Move it whole, then diff that block character by character before moving on.

- [ ] **Step 4: Verify no field moved**

```bash
git diff --stat PenumbraOrganizer.Plugin/Windows/MainWindow.cs
```

The only deletions in `MainWindow.cs` should be the moved method bodies. If any `private`/`static` field declaration appears in the deletion set, put it back — see the Global Constraints.

- [ ] **Step 5: Build and run the full suite**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

Expected: 912 passing, zero new warnings. `WorkbookStrategyOptionsTests` reflects over a `MainWindow` field by name and is the one test that could notice this piece; it must still pass, which it will because no field moved.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow*.cs
git commit -m "refactor: move MainWindow tab draw methods to partial files"
```

---

### Task 2: Move the recovery panel and shared widgets

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`
- Create: `PenumbraOrganizer.Plugin/Windows/MainWindow.Recovery.cs`
- Create: `PenumbraOrganizer.Plugin/Windows/MainWindow.Widgets.cs`

**Interfaces:**
- Consumes: `MainWindow` is `partial` (Task 1).
- Produces: the split is complete. No later task in this plan depends on it.

- [ ] **Step 1: Create both files with the Task 1 scaffold**

Same scaffold, same `using` directives.

- [ ] **Step 2: Move, one method per build**

| Destination file | Method(s) |
|---|---|
| `MainWindow.Recovery.cs` | `DrawRecoveryPanelIfNeeded`, `DrawArtifactLine` |
| `MainWindow.Widgets.cs` | `DrawWrappingButtonRow`, `DrawWrappingCheckboxRow`, `DrawOperationProgress`, `DrawLibraryWorkProgress`, `DrawLibraryWorkOutcome` |

`DrawRecoveryPanelIfNeeded` contains the multi-root recovery branch (around lines 189-260) with several `BeginPopupModal` blocks. Move it whole and diff carefully; it is the branch that cannot be verified in-game (see Task 3, step 4).

- [ ] **Step 3: Confirm what remains**

```bash
wc -l PenumbraOrganizer.Plugin/Windows/MainWindow*.cs
```

Expected: `MainWindow.cs` around 700-750 lines, still the largest of the set. That is the designed outcome, not a shortfall: the ~28 non-drawing helpers (`CreateDiagnosticDump`, `ApplyChanges`, `ImportWorkbook`, `RestoreSnapshot` and the rest) deliberately stay, because sorting them into tab-specific and shared requires judgement and judgement is what this piece excludes.

If `MainWindow.cs` is much smaller than 700 lines, something moved that should not have. Check for moved fields.

- [ ] **Step 4: Run the full suite and commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow*.cs
git commit -m "refactor: move recovery panel and shared widgets to partial files"
```

---

### Task 3: Verification

**Files:** none modified. This task is a gate, not a change.

**Interfaces:** none.

- [ ] **Step 1: Diff every moved method body**

```bash
git diff 2044227..HEAD -- PenumbraOrganizer.Plugin/Windows/
```

Every moved method must be byte-identical to its previous form. This is the real gate and the reason the forbidden list exists. Read it; do not skim it.

Pay particular attention to `DrawHistoryTab`'s `EndPopup()`/`continue` block.

- [ ] **Step 2: Confirm the build is clean**

```bash
dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj -c Debug
```

Expected: zero errors, **zero new warnings**. A new warning means something changed beyond a move.

- [ ] **Step 3: In-game pass over all six tabs**

Build Debug, reload the plugin in-game, and confirm each of Scan, Protect, Sort, Review Changes, History and Search renders and that its controls respond. Six tabs, because this piece runs before piece 4 adds Help.

- [ ] **Step 4: In-game pass over the modals**

Step 3 opens no popups, and popups are the highest-risk construct in this file — `MainWindow.cs:36-40` documents that `BeginTabBar` pushes an ID override, so popup scope is coupled to where it is opened. There are nine `BeginPopupModal` blocks (lines 223, 241, 274, 295, 313, 995, 1011, 1136, 1573); the checklist below covers six of them.

Cover at minimum:

- Apply confirmation
- the post-Apply "Rediscover Mods" reminder
- per-snapshot Restore confirmation on History
- snapshot Delete confirmation
- "Clean up folders?" on Review Changes
- the single-root recovery path, if an interrupted operation can be staged

**The multi-root recovery branch will not be verified.** It needs a contrived interrupted-operation state spanning two roots. Stating that is better than implying coverage that will not happen. If it is worth staging, that is a separate deliberate task.

- [ ] **Step 5: Record the result**

Write what you actually did in the task report: which tabs, which modals, what you could not reach. "Looks fine" is not a verification record.

---

## Self-review notes

- **Spec coverage:** partial-class conversion, the file partition, the forbidden list including the field rule, the honest statement that `MainWindow.cs` stays ~700 lines, the `DrawHistoryTab` popup warning, the corrected test claim, and the six-part verification are all covered by Tasks 1-3.
- **The field rule is the one constraint that can fail silently**, so it appears in Global Constraints, in Task 1 Step 4 as an explicit check, and in Task 2 Step 3 as a size sanity-check.
- **No placeholders:** every move is a named method in a named file. The only judgement call left to the implementer is which `using` directives to copy, and the plan resolves that by saying "all of them, verbatim".
- **Stage B is not in this plan.** `SortPanel`, `HelpTab` and `FirstRunWindow` belong to pieces 2, 4 and 5 and are specified there.
