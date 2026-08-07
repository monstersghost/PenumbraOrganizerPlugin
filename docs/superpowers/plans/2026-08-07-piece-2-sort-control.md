# Piece 2: Sort Control Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace seven sort buttons with a Group by dropdown, two split checkboxes and one Sort button, and expose the six combinations the buttons never offered.

**Architecture:** Sorting is reparameterised. `OrganizerState`'s seven `SortBy*` methods collapse into one entry point taking `(strategy, splitGear, splitNpc)`. The UI moves to `Windows/SortPanel.cs`, an instance owned by `MainWindow` so ImGui's `ref`-based widgets have backing storage that survives frames.

**Tech Stack:** C# / .NET 10, Dalamud plugin, Dear ImGui, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-07-sort-control-consolidation-design.md`. Read it before Task 1.

## Global Constraints

- **The regression boundary is the mapping table.** Every legacy combination must dispatch to exactly what its old button did. That is what Task 1's tests pin, and no later task may weaken them.
- **Six combinations are new, not two.** The selection space is 1 + (3 × 2 × 2) = 13; seven exist today. All six new ones are the NPC-split-off column.
- **This piece carries piece 3's schema and loader**, because its disabled-checkbox tooltip needs them and it is built first. Do not write an inline tooltip literal intending to replace it later; nothing would catch it being left behind.
- **The activity gate must not be lost.** The current sort block sits inside `ImGui.BeginDisabled(!gates.CanStageProposals)`. Dropping it makes Sort clickable during an in-flight Apply or Restore.
- **Do not reuse `OrganizationStrategy`** as the selection type: three of its seven members are meaningless here but representable, and it is the workbook dropdown's type.
- **`docs/USER_GUIDE.md` is updated by this piece.** It currently says "Five buttons compute a proposed folder path", which will be wrong on release day.
- Baseline: whatever the count is after pieces 0 and 1. Report the real number.

---

### Task 1: Reparameterise sorting in `OrganizerState`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortTests.cs` (create if absent)

**Interfaces:**
- Consumes: nothing.
- Produces: `public int Sort(SortStrategy strategy, bool splitGear, bool splitNpc, Func<string,string> canonicalizeCreator)` and `public enum SortStrategy { CreatorOnly, TypeOnly, TypeThenCreator, CreatorThenType }`. Task 3 dispatches to this.

- [ ] **Step 1: Write the legacy-equivalence tests first**

These are the regression boundary. Each asserts the new entry point produces exactly what the old button produced, using the existing seven methods as the oracle **before** they are removed.

```csharp
public class OrganizerStateSortTests
{
    private static OrganizerState WithMods() { /* three mods: one Gear with a resolved slot,
        one NPC-classified, one Unknown. Follow the fixture style in the existing
        OrganizerState tests. */ }

    private static string Canon(string s) => s;

    [Theory]
    [InlineData(SortStrategy.CreatorOnly,     false, true)]
    [InlineData(SortStrategy.TypeOnly,        false, true)]
    [InlineData(SortStrategy.TypeOnly,        true,  true)]
    [InlineData(SortStrategy.TypeThenCreator, false, true)]
    [InlineData(SortStrategy.TypeThenCreator, true,  true)]
    [InlineData(SortStrategy.CreatorThenType, false, true)]
    [InlineData(SortStrategy.CreatorThenType, true,  true)]
    public void Sort_LegacyCombinations_MatchTheOldMethodExactly(
        SortStrategy strategy, bool splitGear, bool splitNpc)
    {
        var viaOld = WithMods();
        RunLegacyEquivalent(viaOld, strategy, splitGear);

        var viaNew = WithMods();
        viaNew.Sort(strategy, splitGear, splitNpc, Canon);

        Assert.Equal(
            viaOld.Mods.Select(m => m.ProposedPath),
            viaNew.Mods.Select(m => m.ProposedPath));
    }

    private static void RunLegacyEquivalent(OrganizerState s, SortStrategy strategy, bool splitGear) =>
        _ = (strategy, splitGear) switch
        {
            (SortStrategy.CreatorOnly,     _)     => s.SortByCreator(Canon),
            (SortStrategy.TypeOnly,        false) => s.SortByModType(),
            (SortStrategy.TypeOnly,        true)  => s.SortByModTypeDetailed(),
            (SortStrategy.TypeThenCreator, false) => s.SortByTypeThenCreatorFlat(Canon),
            (SortStrategy.TypeThenCreator, true)  => s.SortByTypeThenCreator(Canon),
            (SortStrategy.CreatorThenType, false) => s.SortByCreatorThenTypeFlat(Canon),
            (SortStrategy.CreatorThenType, true)  => s.SortByCreatorThenType(Canon),
        };

    [Theory]
    [InlineData(SortStrategy.TypeOnly,        false)]
    [InlineData(SortStrategy.TypeOnly,        true)]
    [InlineData(SortStrategy.TypeThenCreator, false)]
    [InlineData(SortStrategy.TypeThenCreator, true)]
    [InlineData(SortStrategy.CreatorThenType, false)]
    [InlineData(SortStrategy.CreatorThenType, true)]
    public void Sort_NpcSplitOff_ProducesNpcWithoutASubfolder(SortStrategy strategy, bool splitGear)
    {
        // The six new combinations. NPC-classified mods land in "NPC", never "NPC/Bosses".
        var state = WithMods();
        state.Sort(strategy, splitGear, splitNpc: false, Canon);

        var npcPaths = state.Mods
            .Where(m => m.Category == ModCategory.NPC)
            .Select(m => m.ProposedPath);

        Assert.All(npcPaths, p => Assert.DoesNotContain("NPC/NPCs", p));
        Assert.All(npcPaths, p => Assert.DoesNotContain("NPC/Bosses", p));
        Assert.All(npcPaths, p => Assert.DoesNotContain("NPC/Enemies", p));
    }

    [Fact]
    public void Sort_CreatorOnly_IgnoresBothSplits()
    {
        // By Creator never consults category, so neither split can change its output.
        var a = WithMods(); a.Sort(SortStrategy.CreatorOnly, false, false, Canon);
        var b = WithMods(); b.Sort(SortStrategy.CreatorOnly, true,  true,  Canon);

        Assert.Equal(a.Mods.Select(m => m.ProposedPath), b.Mods.Select(m => m.ProposedPath));
    }

    [Fact]
    public void Sort_LeavesProtectedModsAlone()
    {
        // Unchanged behaviour, asserted because the reparameterisation touches every path.
    }
}
```

- [ ] **Step 2: Run them**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "OrganizerStateSortTests"
```

Expected: compile failure — `SortStrategy` and `Sort` do not exist. That is the red state for this task.

- [ ] **Step 3: Implement**

Add the enum and the entry point. Keep the seven `SortBy*` methods for now: Step 1's oracle uses them, and deleting them in the same commit as adding their replacement makes the diff unreviewable.

The NPC flattener mirrors the existing gear one:

```csharp
private static string? FlattenNpcSubCategory(ModCategory? category, string? subCategory) =>
    category == ModCategory.NPC ? null : subCategory;
```

`Sort` composes the two flatteners over the existing derivation, then dispatches on `strategy` exactly as the seven methods did.

- [ ] **Step 4: Run, then remove the old methods in a separate commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

Then delete the seven `SortBy*` methods and rewrite Step 1's `RunLegacyEquivalent` to hold the **expected paths as literals** captured from the passing run. The oracle has done its job; keeping it would keep the old code alive.

- [ ] **Step 5: Commit both steps separately**

```bash
git commit -m "feat: add a parameterised OrganizerState.Sort entry point"
git commit -m "refactor: remove the seven SortBy methods now covered by Sort"
```

---

### Task 2: The help content loader, brought forward from piece 3

**Files:**
- Create: `PenumbraOrganizer.Plugin/Windows/Help.cs`
- Create: `PenumbraOrganizer.Plugin/Windows/HelpTopics.cs`
- Create: `PenumbraOrganizer.Plugin/Resources/help-content.json`
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/HelpContentTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Help.Short(HelpTopic)`, `Help.Tooltip(HelpTopic, string? disabledReason = null)`, and `HelpTopic` as a typed id. Pieces 3, 4 and 5 all build on these.

**Why this is here and not in piece 3:** piece 2's disabled checkboxes need a tooltip, and piece 2 is built first. The alternative — an inline literal to be swept up later — is what piece 3 exists to eliminate, with no test that would catch it being missed.

- [ ] **Step 1: Write the tests**

```csharp
public class HelpContentTests
{
    [Fact]
    public void EveryTopicConstant_ResolvesInTheResource()
    {
        var missing = HelpTopics.All.Where(t => !Help.TryGet(t, out _)).Select(t => t.Id);
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryTopicHasATitle()
        => Assert.All(HelpTopics.All, t => Assert.False(string.IsNullOrWhiteSpace(Help.Title(t))));

    [Fact]
    public void EveryTopicHasAtLeastOneContentField()
    {
        // short/body/step are each optional, but a topic with none of them is dead weight.
        Assert.All(HelpTopics.All, t =>
            Assert.True(Help.Short(t) is not null || Help.Body(t) is not null || Help.Step(t) is not null));
    }

    [Fact]
    public void NoShortContainsANewlineOrExceedsTheLengthCap()
    {
        foreach (var t in HelpTopics.All)
        {
            var s = Help.Short(t);
            if (s is null) continue;
            Assert.DoesNotContain('\n', s);
            Assert.True(s.Length <= 200, $"{t.Id} short is {s.Length} chars");
        }
    }

    [Fact]
    public void MissingResource_ThrowsWithAMessageNamingIt()
    {
        // A packaging bug, not a runtime condition - same treatment NpcNameListStore gives its seed.
    }
}
```

- [ ] **Step 2: Implement**

`HelpTopic` is a `readonly record struct HelpTopic(string Id)`. `HelpTopics` is a static class of constants plus `public static IReadOnlyList<HelpTopic> All` built by reflection over its own fields, so the test set is exhaustive rather than hand-maintained.

`Help` is a static holder over a lazily-initialised loader parsing the embedded `help-content.json`. **`Tooltip` pushes a fixed wrap position** — `ImGui.SetTooltip` does not wrap, so a long `short` produces a tooltip wider than the viewport — and passes `ImGuiHoveredFlags.AllowWhenDisabled` internally.

For this task the resource needs only the topics piece 2 uses: `sort.grouping`, `sort.split-gear`, `sort.split-npc`, `sort.button`, `sort.scraped-npc-list`, `sort.import-workbook`.

- [ ] **Step 3: Run and commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "HelpContentTests"
```

```bash
git add PenumbraOrganizer.Plugin/Windows/Help.cs PenumbraOrganizer.Plugin/Windows/HelpTopics.cs PenumbraOrganizer.Plugin/Resources/help-content.json PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj PenumbraOrganizer.Plugin.Tests/Windows/HelpContentTests.cs
git commit -m "feat: add the shared help content resource and typed topic ids"
```

---

### Task 3: `SortPanel` and the dispatch function

**Files:**
- Create: `PenumbraOrganizer.Plugin/Windows/SortPanel.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.SortTab.cs` (created by piece 0)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` (one field)
- Modify: `PenumbraOrganizer.Plugin.Tests/Windows/WorkbookStrategyOptionsTests.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/SortSelectionTests.cs`

**Interfaces:**
- Consumes: `OrganizerState.Sort(...)` and `SortStrategy` (Task 1); `Help.Tooltip` (Task 2).
- Produces: `SortPanel`, an instance held by `MainWindow`.

- [ ] **Step 1: Write the dispatch tests**

```csharp
public class SortSelectionTests
{
    [Fact]
    public void SplitsApply_IsFalseOnlyForCreatorOnly()
    {
        Assert.False(new SortSelection(SortStrategy.CreatorOnly, false, false).SplitsApply);
        Assert.True(new SortSelection(SortStrategy.TypeOnly, false, false).SplitsApply);
    }

    [Fact]
    public void ItemOrder_IsDerivedFromTheEnum_NotDuplicated()
    {
        // MainWindow.cs:81-83 already carries a warning about index-based strategy selection.
        // The combo's labels come from the enum so index and meaning cannot drift apart.
        Assert.Equal(Enum.GetValues<SortStrategy>().Length, SortPanel.GroupingLabels.Count);
    }
}
```

- [ ] **Step 2: Implement `SortPanel`**

```csharp
internal sealed class SortPanel
{
    // Instance, not static: ImGui.Combo(ref int) and Checkbox(ref bool) need backing storage that
    // survives between frames. A per-frame instance would reset the selection every frame.
    private int _strategyIndex = (int)SortStrategy.TypeThenCreator;
    private bool _splitGear;
    private bool _splitNpc = true;        // matches the always-on NPC subdivision it replaces
    private bool _useScrapedNpcList;      // piece 1's checkbox
    private SortSelection? _lastSorted;

    public static IReadOnlyList<string> GroupingLabels { get; } = [...];

    public void Draw(
        OrganizerState state,
        ActivityGates gates,
        Func<string, string> canonicalizeCreator,
        FileDialogManager fileDialogs);
}
```

Inside `Draw`, in order:

1. `ImGui.BeginDisabled(!gates.CanStageProposals)` around the whole block — **this is the gate that must not be lost**.
2. The Group by combo, selecting by value through `GroupingLabels`, never by bare index.
3. Both checkboxes, each `ImGui.BeginDisabled(!selection.SplitsApply)` with `Help.Tooltip(HelpTopics.SortSplitGear)` after the widget and outside the disabled scope.
4. The Sort button, labelled `Sort##sort-mods` — **an explicit id suffix**, because a label varying with mod count makes the widget id vary with it, and a background scan publishing mid-click would silently drop the click. If a count is displayed it goes in adjacent text and counts **unprotected** mods only, since `Sort()` touches nothing else.
5. When `_lastSorted` differs from the current selection, a muted line: `Selection changed since the last sort.`
6. `ImGui.EndDisabled()`, then the existing gate tooltip.

`MainWindow.SortTab.cs` keeps Import Workbook, the NPC refresh button and manual assignment, and calls `_sortPanel.Draw(...)` where the button row used to be. **Import Workbook must be re-established explicitly**: it was the eighth element of the same `DrawWrappingButtonRow`, sharing that row's `BeginDisabled` scope and the trailing `IsItemHovered` tooltip at `MainWindow.cs:803`. Its behaviour — including the re-check inside the dialog callback — is unchanged.

- [ ] **Step 3: Fix the reflective test piece 0 warned about**

`WorkbookStrategyOptionsTests` reads `MainWindow`'s private static `WorkbookStrategyOptions` by name. If this task moves that field, the test fails at **runtime** with an `InvalidOperationException` from the reflection lookup, not a compile error. Either leave the field on `MainWindow` or update the test to point at its new home. Say which you did in the report.

- [ ] **Step 4: Run the full suite, then commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

```bash
git add PenumbraOrganizer.Plugin/Windows/ PenumbraOrganizer.Plugin.Tests/Windows/
git commit -m "feat: replace the seven sort buttons with a dropdown and two split checkboxes"
```

---

### Task 4: Update the user guide

**Files:**
- Modify: `docs/USER_GUIDE.md`

**Interfaces:** none.

- [ ] **Step 1: Rewrite the Sort section**

Replace "Five buttons compute a proposed folder path for every unprotected mod" and the bullet list of strategies with the dropdown, the two checkboxes and the Sort button. State plainly that turning off Split NPC puts NPC mods in `NPC` rather than `NPC/Bosses`, since that combination did not previously exist and nobody will expect it.

- [ ] **Step 2: Verify no other section contradicts it**

```bash
grep -n "Five buttons\|By Mod Type Detailed\|seven" docs/USER_GUIDE.md
```

- [ ] **Step 3: Commit**

```bash
git add docs/USER_GUIDE.md
git commit -m "docs: describe the sort dropdown and split checkboxes in the user guide"
```

---

## Self-review notes

- **Spec coverage:** the reparameterised sort with all six new combinations (Task 1), the brought-forward help loader (Task 2), `SortPanel` with the gate, the stable button id, `AllowWhenDisabled` and the staleness indicator (Task 3), the guide update (Task 4).
- **The legacy mapping table is pinned by a `[Theory]` using the old methods as an oracle before they are deleted**, which is the only way to prove equivalence rather than assert it.
- **Type consistency:** `SortStrategy` is defined in Task 1 and used in Tasks 1 and 3; `SortSelection` and `SortPanel.GroupingLabels` are defined in Task 3 and used only there; `HelpTopic`/`Help` are defined in Task 2 and used in Task 3.
- **Known risk carried from piece 0:** Task 3 Step 3 is the reflective-test breakage piece 0 predicted. It is called out rather than left to be discovered.
