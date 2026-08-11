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
    // The fixture is load-bearing. Each row below exists to make a specific pair of legacy
    // combinations produce DIFFERENT output; drop one and the theory passes while proving nothing.
    private static OrganizerState WithMods()
    {
        var state = new OrganizerState();
        state.LoadScan(
        [
            // Separates gear-split on from off. The slot must be one GetFolder accepts:
            // Head/Top/Hands/Legs/Feet/Ears/Neck/Wrists/Rings.
            new OrganizerModRow { Identifier = "gear", Name = "Gear Mod", Author = "Ann",
                CurrentPath = "Gear Mod", ProposedPath = "Gear Mod",
                Category = ModCategory.Gear, SubCategory = "Feet" },

            // Separates NPC-split on from off. SubCategory MUST be set: it is nullable, and
            // GetFolder(NPC, null) already returns "NPC", so a null here makes every
            // Sort_NpcSplitOff assertion pass against a completely no-op flattener.
            new OrganizerModRow { Identifier = "npc", Name = "Npc Mod", Author = "Bob",
                CurrentPath = "Npc Mod", ProposedPath = "Npc Mod",
                Category = ModCategory.NPC, SubCategory = "Bosses" },

            // Separates the three strategies through the creator segment, and covers the
            // no-category fallback.
            new OrganizerModRow { Identifier = "unknown", Name = "Unknown Mod", Author = "Cy",
                CurrentPath = "Unknown Mod", ProposedPath = "Unknown Mod" },
        ], new HashSet<string>());
        return state;
    }

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

**There is already a `private int Sort(Func<OrganizerModRow, (string?, string?)>)` at `OrganizerState.cs:226`.** The new public method is a legal overload and should *call* it, exactly as the seven `SortBy*` methods do. Do not rename or replace the private one.

- [ ] **Step 4: Run, then retire the oracle in a separate commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

Once green, the oracle has done its job and the seven `SortBy*` methods can go. **Do not hand-write the expected literals** — an earlier draft said to "rewrite `RunLegacyEquivalent` to hold expected paths as literals", which is not mechanically possible as described (`RunLegacyEquivalent` is `void` and works by mutating `viaOld`), and hand-writing them invites getting the order wrong and then "fixing" the expectations until green, which destroys the guarantee.

Two things make hand-writing error-prone: `OrganizerState.Mods` is ordered by `Name` (`OrganizerState.cs:16-17`), not insertion order, and `FinishProposals`/`CollisionDisambiguator` can rewrite paths after the sort (`OrganizerState.cs:272-281`).

Instead, **capture them mechanically**. Add a temporary throwaway test that prints the actual output for all seven rows:

```csharp
[Fact(Skip = "one-shot: capture expectations, then delete")]
public void CaptureLegacyExpectations()
{
    foreach (var (strategy, splitGear) in AllLegacyRows())
    {
        var s = WithMods();
        s.Sort(strategy, splitGear, splitNpc: true, Canon);
        Console.WriteLine($"[InlineData(SortStrategy.{strategy}, {splitGear.ToString().ToLower()}, " +
            string.Join(", ", s.Mods.Select(m => $"\"{m.ProposedPath}\"")) + ")]");
    }
}
```

Run it once with `Skip` removed, paste the emitted `[InlineData]` lines into the theory, delete the capture test and the seven `SortBy*` methods. The expectations are then observed output, not invention.

- [ ] **Step 4a: Add the missing switch arm**

`RunLegacyEquivalent`'s switch is non-exhaustive over four strategies × two bools and emits **CS8509**. No project sets `TreatWarningsAsErrors`, so it builds — but piece 0 established a zero-new-warnings baseline that this piece inherits. Add:

```csharp
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), (strategy, splitGear), null),
```

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
- Produces: `public sealed class SortPanel` and `public readonly record struct SortSelection`, an instance of the former held by `MainWindow`.

**Both types are `public`.** There is no `InternalsVisibleTo` anywhere in this repo — verified — so
`internal` types are unreachable from `PenumbraOrganizer.Plugin.Tests` and Step 1's test would fail
with CS0122. `WorkbookStrategyOptionsTests` uses reflection precisely because of this. Do not add
`InternalsVisibleTo` as a side effect of this task; make the two types public.

**`SortSelection` is defined here, in full.** An earlier draft used it in four places and defined it
nowhere:

```csharp
// A record struct, not a class: the staleness check compares the current selection against the one
// last sorted with, and that needs value equality.
public readonly record struct SortSelection(SortStrategy Strategy, bool SplitGear, bool SplitNpc)
{
    // By Creator never consults category, so neither split can change its output. Both checkboxes
    // are disabled when this is false.
    public bool SplitsApply => Strategy != SortStrategy.CreatorOnly;
}
```

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
    public void Groupings_CoverEveryStrategyExactlyOnce()
    {
        // An earlier draft compared only the COUNT of a labels array against the enum's length,
        // which passes with the labels in the wrong order. A single tuple array makes the
        // relationship structural, and this asserts coverage rather than arithmetic.
        Assert.Equal(
            Enum.GetValues<SortStrategy>().Order(),
            SortPanel.Groupings.Select(g => g.Strategy).Order());
    }

    [Fact]
    public void Groupings_DefaultIndexSelectsTypeThenCreator()
    {
        // The default must survive someone reordering the array.
        Assert.Equal(SortStrategy.TypeThenCreator, SortPanel.Groupings[2].Strategy);
    }
}
```

- [ ] **Step 2: Implement `SortPanel`**

```csharp
public sealed class SortPanel
{
    // Instance, not static: ImGui.Combo(ref int) and Checkbox(ref bool) need backing storage that
    // survives between frames. A per-frame instance would reset the selection every frame.
    //
    // These four are SESSION state. A sort strategy is a choice about the action you are about to
    // take, not a preference worth persisting.
    private int _groupingIndex = 2;       // index into Groupings below: "Type then creator"
    private bool _splitGear;
    private bool _splitNpc = true;        // matches the always-on NPC subdivision it replaces
    private SortSelection? _lastSorted;

    // ONE mapping, not two parallel arrays. An earlier draft had a labels array whose only test
    // compared Count against the enum's length, which passes with the labels in the wrong order.
    // This also removes the assumption that enum numeric values double as UI indices.
    public static readonly (SortStrategy Strategy, string Label)[] Groupings =
    [
        (SortStrategy.CreatorOnly,     "Creator"),
        (SortStrategy.TypeOnly,        "Mod type"),
        (SortStrategy.TypeThenCreator, "Type then creator"),
        (SortStrategy.CreatorThenType, "Creator then type"),
    ];

    public void Draw(
        OrganizerState state,
        ActivityGates gates,
        Configuration config,             // the scraped-list opt-in is persistent, not session
        Action saveConfig,
        Func<string, string> canonicalizeCreator);
}
```

**`public`, not `internal`.** There is no `InternalsVisibleTo` anywhere in this repo — verified — so
an internal type is unreachable from `PenumbraOrganizer.Plugin.Tests` and Step 1's test fails with
CS0122. `WorkbookStrategyOptionsTests` uses reflection precisely because of that. Do not add
`InternalsVisibleTo` as a side effect of this task.

**No `FileDialogManager` parameter.** An earlier draft passed one while also stating Import Workbook
stays in `MainWindow.SortTab.cs`. It stays there; the panel has no use for it.

**The scraped-list opt-in lives in `Configuration`, not in a private field.** An earlier draft
declared `_useScrapedNpcList` here beside piece 1's `Configuration.UseScrapedNpcNameList` — two
sources of truth for one setting, and the private one was never rendered, never bound and never
saved. Dead state on the seam between two plans is exactly where a feature survives every individual
task review while not existing.

```
SortPanel
  _groupingIndex, _splitGear, _splitNpc, _lastSorted   session
  config.UseScrapedNpcNameList                          persistent, read and written through config
```

`Draw`'s body is given as code, not as an ordered list. **ImGui is positional** — `IsItemHovered`
binds to whichever widget was submitted last — so prose cannot express which item a tooltip attaches
to, and an earlier draft's ordered list hid a real bug in exactly that spot:

```csharp
public void Draw(OrganizerState state, ActivityGates gates, Configuration config,
                 Action saveConfig, Func<string, string> canonicalizeCreator)
{
    var selection = new SortSelection(
        Groupings[_groupingIndex].Strategy, _splitGear, _splitNpc);

    ImGui.BeginDisabled(!gates.CanStageProposals);

    ImGui.SetNextItemWidth(220);
    var labels = Groupings.Select(g => g.Label).ToArray();
    ImGui.Combo("Group by", ref _groupingIndex, labels, labels.Length);
    Help.Tooltip(HelpTopics.SortGrouping);

    ImGui.BeginDisabled(!selection.SplitsApply);
    ImGui.Checkbox("Split gear by equipment slot", ref _splitGear);
    ImGui.EndDisabled();
    Help.Tooltip(HelpTopics.SortSplitGear,
        selection.SplitsApply ? null : "Grouping by creator alone never uses the mod's type.");

    ImGui.BeginDisabled(!selection.SplitsApply);
    ImGui.Checkbox("Split NPC mods by kind", ref _splitNpc);
    ImGui.EndDisabled();
    Help.Tooltip(HelpTopics.SortSplitNpc,
        selection.SplitsApply ? null : "Grouping by creator alone never uses the mod's type.");

    // Piece 1's opt-in. Bound directly to config, saved on change, and force-disabled for 0.6.0
    // through the SAME gate the backend consults - so a config value left true cannot quietly
    // enable a path the UI says is off.
    ImGui.BeginDisabled(!Configuration.ScrapedNpcListFeatureEnabled);
    var useScraped = config.UseScrapedNpcNameList;
    if (ImGui.Checkbox("Also use the NPC list scraped from the wiki", ref useScraped))
    {
        config.UseScrapedNpcNameList = useScraped;
        saveConfig();
    }
    ImGui.EndDisabled();
    Help.Tooltip(HelpTopics.SortScrapedNpcList,
        Configuration.ScrapedNpcListFeatureEnabled ? null : "Not available in this version.");

    // Stable id: a label varying with mod count makes the widget id vary with it, so a background
    // scan publishing mid-click would change the active id and the click would be dropped.
    if (ImGui.Button("Sort##sort-mods"))
    {
        state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, canonicalizeCreator);
        _lastSorted = selection;
    }
    Help.Tooltip(HelpTopics.SortButton);

    // THE GATE TOOLTIP GOES HERE, immediately after the last widget inside the disabled scope.
    // An earlier draft put it after EndDisabled and after the staleness line, where IsItemHovered
    // would have bound to a text label - or to nothing consistent, since the staleness line only
    // renders sometimes.
    if (!gates.CanStageProposals && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        ImGui.SetTooltip("Another operation is in progress or requires recovery.");

    ImGui.EndDisabled();

    if (_lastSorted is { } last && last != selection)
        ImGui.TextDisabled("Selection changed since the last sort.");
}
```

`MainWindow.SortTab.cs` keeps Import Workbook, the NPC refresh button and manual assignment, and calls `_sortPanel.Draw(...)` where the button row used to be. **Import Workbook must be re-established explicitly**: it was the eighth element of the same `DrawWrappingButtonRow`, sharing that row's `BeginDisabled` scope and the trailing `IsItemHovered` tooltip at `MainWindow.cs:803`. Its behaviour — including the re-check inside the dialog callback — is unchanged.

- [ ] **Step 3: Confirm `WorkbookStrategyOptions` is untouched**

Piece 0 predicted a reflective-test breakage here, and on inspection it cannot occur: `WorkbookStrategyOptions` backs the **workbook export** dropdown, not the sort buttons, and piece 0 forbids moving any field. Nothing in this task should go near it.

Confirm with a grep rather than assuming, and report the result:

```bash
grep -n "WorkbookStrategyOptions" PenumbraOrganizer.Plugin/Windows/*.cs
```

Expected: still declared in `MainWindow.cs`, still used by `DrawReviewTab`. If this task has moved it, stop — that is scope creep, and `WorkbookStrategyOptionsTests` will fail at runtime with an `InvalidOperationException` rather than a compile error.

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
