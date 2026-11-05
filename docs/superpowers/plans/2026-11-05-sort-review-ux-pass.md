# Sort and Review Tab UX Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the body checkbox into two per-category checkboxes, restructure the Sort tab into three sections with hidden-until-ticked dependents, a purple Sort button and a First-choice body quick pick, move workbook import next to export on Review Changes, and land the small History, Scan and Protect fixes from the UX critique.

**Architecture:** The per-category body flags and the First-choice move are pure, tested code (`SortSelection`, `SortFolderSelectors`, `OrganizerState.Sort`, `BodyPriority.MoveToTop`). Which controls show or are enabled lives in one pure helper (`SortLayout`) so the "hidden until ticked" rules are unit-tested; the ImGui drawing code only follows that helper. The Review tab is regrouped into Workbook, Apply and a collapsed Diagnostics section.

**Tech Stack:** C# / .NET 10 (`net10.0-windows7.0`), Dalamud + `Dalamud.Bindings.ImGui`, xUnit.

Spec: `docs/superpowers/specs/2026-11-05-sort-tab-ux-pass-design.md` (read it first).

## Global Constraints

- Work in the main checkout `C:\Repo\PenumbraOrganizer.Plugin` on a NEW branch `feat/sort-review-ux` cut from `main` (no worktree, so the sibling-checkout junction problem does not arise). Do not touch the stale worktree under `.claude\worktrees\body-type`. Run commands from the repo root.
- Test command: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~<Name>"`. Build command: `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj -c Debug`. Baseline before this work: 1757 tests passing, build 0 errors.
- No `InternalsVisibleTo`: anything a test must reach is `public`.
- ImGui calls are positional: `Help.Tooltip` binds to the last submitted item, so a tooltip goes right after its widget, and `EndDisabled` submits no item. Every `Begin*` needs its `End*` on every path; `EndChild` is called regardless of `BeginChild`'s result; a collapsed `CollapsingHeader` returns early before any disabled scope is opened.
- `help-content.json`: every `short` is at most 200 characters (enforced by `HelpContentTests`); every `HelpTopics` constant needs a json entry and an entry in `HelpTopicUsage`.
- A header label that changes (a count) must carry a stable id: `"Live events (3)###scan-events"`.
- Hidden means not rendered at all; disabled means rendered greyed. Dependents of an UNTICKED box are hidden. Under Group by Creator the split checkboxes and any dependents whose box is ticked stay visible but greyed, values preserved.
- Purple primary button: `using (PluginTheme.PrimaryButton()) { ... }`, with any `BeginDisabled` INSIDE the using, as the Scan tab does.
- Nothing is released; no migration code, no version bump. Docs, PDF, tutorial and handoffs are out of scope except help tooltips/entries and the in-game checklist file.
- Plain ASCII in docs and test strings (this repo has repeatedly had non-ASCII corruption). Commit trailer on every commit: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## File Structure

| File | Change |
|---|---|
| `PenumbraOrganizer.Plugin/Organizer/BodyTypes/BodyPriority.cs` | add `MoveToTop` |
| `PenumbraOrganizer.Plugin/Windows/SortPanel.cs` | `SortSelection` flags; section drawing; `FirstChoice` helper |
| `PenumbraOrganizer.Plugin/Windows/SortLayout.cs` | create: pure visibility/enabled helper |
| `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs`, `OrganizerState.cs` | per-category body flags |
| `PenumbraOrganizer.Plugin/Windows/MainWindow.SortTab.cs` | Manual assignment section; drop Import and wiki refresh |
| `PenumbraOrganizer.Plugin/Windows/MainWindow.ReviewTab.cs` | Workbook group, primary Apply, Diagnostics group |
| `PenumbraOrganizer.Plugin/Windows/MainWindow.HistoryTab.cs`, `MainWindow.cs` | Delete confirmation |
| `PenumbraOrganizer.Plugin/Windows/MainWindow.ScanTab.cs` | collapsible Live events |
| `PenumbraOrganizer.Plugin/Windows/MainWindow.ProtectTab.cs`, `ProtectToggleLabels.cs` | direction-aware labels |
| `HelpTopics.cs`, `HelpTopicUsage.cs`, `HelpTab.cs`, `Resources/help-content.json` | new and renamed topics |
| `docs/testing/2026-11-05-sort-review-ux-checklist.md` | create: in-game checklist |

---

### Task 1: `BodyPriority.MoveToTop`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/BodyTypes/BodyPriority.cs` (after `MoveDown`)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/BodyTypes/BodyPriorityTests.cs`

**Interfaces:**
- Produces: `public BodyPriority MoveToTop(string label)` - a new snapshot with `label` (matched ordinal-ignore-case) first and every other label in its existing order; a label that is not in the list, or already first, returns `this`.

- [ ] **Step 0: Create the branch.** `git checkout main && git checkout -b feat/sort-review-ux`. Confirm `git status` is clean and `git log --oneline -1` shows the main tip.

- [ ] **Step 1: Write the failing tests** (append inside `BodyPriorityTests`; the fixture's `All` is `["Bibo+", "Rue+", "TBSE", "YAB"]`)

```csharp
    [Fact]
    public void MoveToTop_PutsTheLabelFirst_AndKeepsTheRestInOrder()
    {
        var p = BodyPriority.Default(All);

        Assert.Equal(["TBSE", "Bibo+", "Rue+", "YAB"], p.MoveToTop("TBSE").Labels);
        Assert.Equal(["YAB", "Bibo+", "Rue+", "TBSE"], p.MoveToTop("YAB").Labels);
    }

    [Fact]
    public void MoveToTop_MatchesIgnoringCase_AndUsesTheCanonicalSpelling()
    {
        var p = BodyPriority.Default(All);

        Assert.Equal(["Rue+", "Bibo+", "TBSE", "YAB"], p.MoveToTop("rue+").Labels);
    }

    [Fact]
    public void MoveToTop_UnknownOrAlreadyFirst_ReturnsTheSameInstance()
    {
        var p = BodyPriority.Default(All);

        Assert.Same(p, p.MoveToTop("Nope"));
        Assert.Same(p, p.MoveToTop("Bibo+"));
        Assert.Same(p, p.MoveToTop(""));
    }

    [Fact]
    public void MoveToTop_DoesNotMutateTheOriginal_AndTheResultIsComparable()
    {
        var p = BodyPriority.Default(All);
        var moved = p.MoveToTop("YAB");

        Assert.Equal(All, p.Labels);
        Assert.True(moved != p);
        Assert.True(moved == BodyPriority.Normalize(["YAB"], All));
    }
```

- [ ] **Step 2: Run and see them fail.** `dotnet test ... --filter "FullyQualifiedName~BodyPriorityTests.MoveToTop"`. Expected: build FAIL, `BodyPriority` has no `MoveToTop`.

- [ ] **Step 3: Implement** (insert after `MoveDown`)

```csharp
    /// <summary>
    /// A snapshot with <paramref name="label"/> first and the rest in their existing order. The
    /// First-choice body dropdown uses it: picking a body promotes it without disturbing how the
    /// others rank. A label not in the list, or one that is already first, returns this instance.
    /// </summary>
    public BodyPriority MoveToTop(string label)
    {
        var index = Array.FindIndex(_labels, l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase));
        if (index <= 0)
            return this;

        var copy = new string[_labels.Length];
        copy[0] = _labels[index];
        Array.Copy(_labels, 0, copy, 1, index);
        Array.Copy(_labels, index + 1, copy, index + 1, _labels.Length - index - 1);
        return new BodyPriority(copy);
    }
```

- [ ] **Step 4: Run.** `--filter "FullyQualifiedName~BodyPriorityTests"`. Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/BodyTypes/BodyPriority.cs PenumbraOrganizer.Plugin.Tests/Organizer/BodyTypes/BodyPriorityTests.cs
git commit -m "feat: BodyPriority.MoveToTop promotes one body without reordering the rest" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Per-category body split (core, with a transitional UI shim)

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/SortPanel.cs` (`SortSelection`, and the shim in `Draw`)
- Modify: `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs` (`Select`, lines ~33-66)
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (the `Sort` overload with `splitBody`, ~lines 535-555)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortBodyTests.cs`, `OrganizerExportFormatterTests.cs`, `PenumbraOrganizer.Plugin.Tests/Windows/SortSelectionTests.cs`, `TemplateFallbackConversionTests.cs`

**Interfaces:**
- Produces:
  - `SortSelection(SortStrategy Strategy, bool SplitGear, bool SplitNpc, bool SplitHair, string? HairRaceCode = null, bool MidlanderFallback = false, bool SplitGearBody = false, bool SplitBodyMods = false, BodyPriority? BodyPriority = null)` - nine primary-constructor parameters; `SplitBody` is gone.
  - `SortFolderSelectors.Select(..., bool splitHair = false, HairPriority? hairPriority = null, bool splitGearBody = false, bool splitBodyMods = false, BodyPriority? bodyPriority = null)`.
  - `OrganizerState.Sort(strategy, splitGear, splitNpc, splitHair, canonicalizeCreator, HairPriority? hairPriority = null, bool splitGearBody = false, bool splitBodyMods = false, BodyPriority? bodyPriority = null)`.
- Rules: a row's body segment is used only when ITS category's flag is on (`Gear` -> `splitGearBody`, `Body` -> `splitBodyMods`, every other category never). `StagedBodyResolution` is recorded only for rows whose flag is on, and `StagedBodyPriority` is non-null while EITHER flag is on.

- [ ] **Step 1: Write the failing tests.** Append to `OrganizerStateSortBodyTests` (the fixture `WithMods()` has `dress` Gear/Top/Bibo+, `unslotted` Gear/TBSE, `bodiless`, and `bodymod` Body/Rue+; `Canon` and `PathOf` exist):

```csharp
    [Fact]
    public void Sort_GearBodyFlagOnly_FilesGearUnderItsBody_AndLeavesBodyModsFlat()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon,
            splitGearBody: true, splitBodyMods: false);

        Assert.Equal("Gear/Bibo+/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Body/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Fact]
    public void Sort_BodyModsFlagOnly_FilesBodyModsUnderTheirBody_AndLeavesGearWithoutOne()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon,
            splitGearBody: false, splitBodyMods: true);

        Assert.Equal("Gear/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Body/Rue+/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Fact]
    public void Sort_BothFlags_FilesBothCategories()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon,
            splitGearBody: true, splitBodyMods: true);

        Assert.Equal("Gear/Bibo+/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Body/Rue+/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Fact]
    public void Sort_GearBodyFlagWithoutTheSlotSplit_FilesGearAtGearBodyMod()
    {
        // The two splits are independent: the body flag does not switch the slot split on.
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: false, splitHair: false, Canon,
            splitGearBody: true, splitBodyMods: false);

        Assert.Equal("Gear/Bibo+/Bibo+ Medieval Dress", PathOf(state, "dress"));
    }

    [Fact]
    public void Sort_NeitherFlag_UsesNoBodySegmentAnywhere()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon);

        Assert.Equal("Gear/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Body/Rue+ Body", PathOf(state, "bodymod"));
    }

    private static OrganizerState WithGearAndBodyCandidates()
    {
        var state = new OrganizerState();
        var both = new BodyCandidates
        {
            Content = [new BodyCandidate("YAB", BodyEvidenceSource.Option),
                       new BodyCandidate("Rue+", BodyEvidenceSource.Option)],
        };
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "g", Name = "Gear Multi", Author = "Ann",
                CurrentPath = "Gear Multi", ProposedPath = "Gear Multi",
                Category = ModCategory.Gear, SubCategory = "Top", BodyType = "YAB", BodyCandidates = both },
            new OrganizerModRow { Identifier = "b", Name = "Body Multi", Author = "Ann",
                CurrentPath = "Body Multi", ProposedPath = "Body Multi",
                Category = ModCategory.Body, BodyType = "YAB", BodyCandidates = both },
        ], new HashSet<string>());
        return state;
    }

    [Fact]
    public void Sort_StagedResolution_IsRecordedOnlyForRowsWhoseCategoryFlagIsOn()
    {
        var state = WithGearAndBodyCandidates();
        var rueFirst = BodyPriority.Normalize(["Rue+"], ["YAB", "Rue+"]);

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon,
            splitGearBody: true, splitBodyMods: false, bodyPriority: rueFirst);

        Assert.Equal("Rue+", state.Mods.Single(m => m.Identifier == "g").StagedBodyResolution!.Label);
        Assert.Null(state.Mods.Single(m => m.Identifier == "b").StagedBodyResolution);
        Assert.Equal(rueFirst, state.StagedBodyPriority);
        Assert.Equal("Body/Body Multi", PathOf(state, "b"));
    }

    [Fact]
    public void Sort_StagedPriority_IsNullOnlyWhenBothFlagsAreOff()
    {
        var state = WithGearAndBodyCandidates();
        var p = BodyPriority.Default(["YAB", "Rue+"]);

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: false, splitBodyMods: true, bodyPriority: p);
        Assert.Equal(p, state.StagedBodyPriority);

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: false, splitBodyMods: false, bodyPriority: p);
        Assert.Null(state.StagedBodyPriority);
    }
```

(The test file already imports `BodyPriority` and the classification namespace from the earlier feature; add `using PenumbraOrganizer.Plugin.Organizer.BodyTypes;` and `...Classification;` if missing.)

Then update the EXISTING tests mechanically so they keep their meaning:
- In `OrganizerStateSortBodyTests.cs` and `OrganizerExportFormatterTests.cs` replace `splitBody: true` with `splitGearBody: true, splitBodyMods: true`, and `splitBody: false` with `splitGearBody: false, splitBodyMods: false`. Confirm with `grep -rn "splitBody" PenumbraOrganizer.Plugin.Tests` returning nothing.
- `SortSelectionTests.cs`: replace `SortSelection_ValueEquality_CoversSplitBody` with two tests and update the default test and the helper:

```csharp
    [Fact]
    public void SortSelection_ValueEquality_CoversBothBodySplits()
    {
        // Spelled positionally on purpose: a member appended in the wrong place would still compare
        // unequal under a named argument and this test would not notice.
        var off = new SortSelection(SortStrategy.TypeOnly, false, false, false, null, false, false, false);

        Assert.NotEqual(off, new SortSelection(SortStrategy.TypeOnly, false, false, false, null, false, true, false));
        Assert.NotEqual(off, new SortSelection(SortStrategy.TypeOnly, false, false, false, null, false, false, true));
    }

    [Fact]
    public void SortSelection_BodySplits_DefaultOff()
    {
        var selection = new SortSelection(SortStrategy.TypeOnly, false, false, false);

        Assert.False(selection.SplitGearBody);
        Assert.False(selection.SplitBodyMods);
    }
```
  and in the `WithPriority` helper change `SplitBody: true` to `SplitGearBody: true`.
- `TemplateFallbackConversionTests.cs`: change `Assert.Equal(8, ...SortSelection...)` to `9` and replace the trailing "The eighth is the body priority..." comment paragraph with: `// The seventh and eighth are the two body-split flags (gear, and body mods), which replaced the single body split, and the ninth is the body priority. None of the three is stored by templates, for the same reason the hair split is not.` Keep it plain ASCII; `TemplateFallback` stays 3.

- [ ] **Step 2: Run and see failures.** `--filter "FullyQualifiedName~OrganizerStateSortBodyTests|FullyQualifiedName~SortSelectionTests|FullyQualifiedName~TemplateFallbackConversionTests|FullyQualifiedName~OrganizerExportFormatterTests"`. Expected: build FAIL (`splitGearBody` is not a parameter, `SplitGearBody` is not a member).

- [ ] **Step 3: Implement.**

`SortPanel.cs` - replace the `SortSelection` declaration:

```csharp
public readonly record struct SortSelection(
    SortStrategy Strategy, bool SplitGear, bool SplitNpc, bool SplitHair,
    string? HairRaceCode = null, bool MidlanderFallback = false,
    bool SplitGearBody = false, bool SplitBodyMods = false,
    BodyPriority? BodyPriority = null)
```

and keep the panel compiling with a TRANSITIONAL shim until Task 5 replaces it: in `Draw`, the existing single checkbox and `_splitBody` field stay, and the two uses become `..., HairRaceCodeAt(_hairRaceIndex), _midlanderFallback, _splitBody, _splitBody, _bodyPriority)` for the `SortSelection` and `selection.HairPriority, selection.SplitGearBody, selection.SplitBodyMods, selection.BodyPriority` for `state.Sort`. (Task 5 deletes this shim; say so in the commit message.)

`SortFolderSelectors.cs` - change the `Select` parameters `bool splitBody = false, BodyPriority? bodyPriority = null` to:

```csharp
        bool splitGearBody = false,
        bool splitBodyMods = false,
        BodyPriority? bodyPriority = null)
```

and the last line of `Type()` to:

```csharp
            return TypeFolder(row.Category, sub, renameFolder,
                BodyFor(row, splitGearBody, splitBodyMods, bodyPriority));
```

and add beside `ResolveBody`:

```csharp
    // A row's body segment is used only when ITS category's flag is on. Gear and Body are the only
    // categories that ever carry one (ModTypeClassifier.BodyTypeApplies), so no other category can
    // reach ResolveBody.
    private static string? BodyFor(
        OrganizerModRow row, bool splitGearBody, bool splitBodyMods, BodyPriority? priority) =>
        (row.Category == ModCategory.Gear && splitGearBody) || (row.Category == ModCategory.Body && splitBodyMods)
            ? ResolveBody(row, priority)
            : null;
```

`OrganizerState.cs` - replace the `Sort` overload that takes `splitBody`:

```csharp
    public int Sort(SortStrategy strategy, bool splitGear, bool splitNpc, bool splitHair,
                    Func<string, string> canonicalizeCreator, HairPriority? hairPriority = null,
                    bool splitGearBody = false, bool splitBodyMods = false, BodyPriority? bodyPriority = null)
    {
        // Recorded BEFORE the paths are computed, from the same inputs, so the explanation and the
        // proposal cannot disagree. A resolution is kept only for rows whose own category's flag is
        // on, and cleared otherwise so a stale one never lingers.
        StagedBodyPriority = splitGearBody || splitBodyMods ? bodyPriority : null;
        foreach (var row in Mods)
        {
            var applies = row.Category == ModCategory.Gear ? splitGearBody
                : row.Category == ModCategory.Body && splitBodyMods;
            row.StagedBodyResolution = applies && bodyPriority is not null
                ? BodyPriorityResolver.Resolve(row.BodyCandidates, bodyPriority)
                : null;
        }

        return Sort(row => SortFolderSelectors.Select(strategy, splitGear, splitNpc, row, canonicalizeCreator,
                                                      splitHair: splitHair, hairPriority: hairPriority,
                                                      splitGearBody: splitGearBody, splitBodyMods: splitBodyMods,
                                                      bodyPriority: bodyPriority));
    }
```

(Keep the existing `<summary>` comment above it, adjusting "hair and body splits" to "hair and two body splits". `ModCategory` is already imported there.)

- [ ] **Step 4: Run.** The Step 2 filter, then the whole project once: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`. Expected: all PASS (no existing test may be loosened; the mechanical `splitBody:` replacements keep their old meaning because both flags are set together).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: split the body flag into gear and body-mod flags (UI shim until the panel rewrite)" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `SortLayout` - which controls show and which are enabled

**Files:**
- Create: `PenumbraOrganizer.Plugin/Windows/SortLayout.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/SortLayoutTests.cs`

**Interfaces:**
- Produces: `public readonly record struct ControlState(bool Visible, bool Enabled)`; `public readonly record struct SortControlStates(ControlState GroupBy, ControlState SplitGear, ControlState SplitNpc, ControlState SplitHair, ControlState PriorityRace, ControlState MidlanderFallback, ControlState SplitGearBody, ControlState SplitBodyMods, ControlState FirstChoiceBody, ControlState FullPriorityList, ControlState ScrapedNpcCheckbox, ControlState WikiRefresh, ControlState SortButton)`; `public static class SortLayout { public static SortControlStates Compute(SortStrategy strategy, bool splitHair, bool scrapedFeatureEnabled, bool activityGateOpen); }`.
- Rules (spec table): the four split checkboxes and the two body checkboxes are always visible and enabled when the strategy is not CreatorOnly AND the activity gate is open; Priority race and Midlander fallback are visible only while `splitHair` is true, with the same enabled rule; First-choice body, the full list, Group by and Sort are enabled when the gate is open; the scraped checkbox and the wiki refresh are visible only when `scrapedFeatureEnabled`.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SortLayoutTests
{
    private static SortControlStates Compute(
        SortStrategy strategy = SortStrategy.TypeThenCreator, bool splitHair = false,
        bool scrapedFeature = false, bool gateOpen = true) =>
        SortLayout.Compute(strategy, splitHair, scrapedFeature, gateOpen);

    [Fact]
    public void HairDependents_AreHiddenUntilTheHairBoxIsTicked()
    {
        var off = Compute(splitHair: false);
        var on = Compute(splitHair: true);

        Assert.False(off.PriorityRace.Visible);
        Assert.False(off.MidlanderFallback.Visible);
        Assert.True(on.PriorityRace.Visible);
        Assert.True(on.MidlanderFallback.Visible);
    }

    [Fact]
    public void TheCheckboxesThemselves_AreAlwaysVisible()
    {
        var s = Compute(strategy: SortStrategy.CreatorOnly, splitHair: false);

        Assert.True(s.SplitGear.Visible);
        Assert.True(s.SplitNpc.Visible);
        Assert.True(s.SplitHair.Visible);
        Assert.True(s.SplitGearBody.Visible);
        Assert.True(s.SplitBodyMods.Visible);
    }

    [Fact]
    public void GroupByCreator_GreysEverySplit_AndKeepsATickedHairBoxsDependentsVisibleButGreyed()
    {
        var s = Compute(strategy: SortStrategy.CreatorOnly, splitHair: true);

        Assert.False(s.SplitGear.Enabled);
        Assert.False(s.SplitNpc.Enabled);
        Assert.False(s.SplitHair.Enabled);
        Assert.False(s.SplitGearBody.Enabled);
        Assert.False(s.SplitBodyMods.Enabled);
        Assert.True(s.PriorityRace.Visible);
        Assert.False(s.PriorityRace.Enabled);
        Assert.True(s.MidlanderFallback.Visible);
        Assert.False(s.MidlanderFallback.Enabled);
    }

    [Theory]
    [InlineData(SortStrategy.TypeOnly)]
    [InlineData(SortStrategy.TypeThenCreator)]
    [InlineData(SortStrategy.CreatorThenType)]
    public void EveryTypeAwareStrategy_EnablesTheSplits(SortStrategy strategy)
    {
        var s = Compute(strategy: strategy, splitHair: true);

        Assert.True(s.SplitGear.Enabled);
        Assert.True(s.SplitNpc.Enabled);
        Assert.True(s.SplitHair.Enabled);
        Assert.True(s.PriorityRace.Enabled);
        Assert.True(s.MidlanderFallback.Enabled);
        Assert.True(s.SplitGearBody.Enabled);
        Assert.True(s.SplitBodyMods.Enabled);
    }

    [Fact]
    public void ClosedActivityGate_DisablesEverythingActionable_ButHidesNothingExtra()
    {
        var open = Compute(splitHair: true, scrapedFeature: true, gateOpen: true);
        var closed = Compute(splitHair: true, scrapedFeature: true, gateOpen: false);

        Assert.False(closed.GroupBy.Enabled);
        Assert.False(closed.SplitGear.Enabled);
        Assert.False(closed.PriorityRace.Enabled);
        Assert.False(closed.SplitBodyMods.Enabled);
        Assert.False(closed.FirstChoiceBody.Enabled);
        Assert.False(closed.FullPriorityList.Enabled);
        Assert.False(closed.ScrapedNpcCheckbox.Enabled);
        Assert.False(closed.WikiRefresh.Enabled);
        Assert.False(closed.SortButton.Enabled);

        Assert.Equal(open.PriorityRace.Visible, closed.PriorityRace.Visible);
        Assert.Equal(open.ScrapedNpcCheckbox.Visible, closed.ScrapedNpcCheckbox.Visible);
    }

    [Fact]
    public void TheWikiControls_AreHiddenWhileTheFeatureIsOff_AndShownWhenOn()
    {
        var off = Compute(scrapedFeature: false);
        var on = Compute(scrapedFeature: true);

        Assert.False(off.ScrapedNpcCheckbox.Visible);
        Assert.False(off.WikiRefresh.Visible);
        Assert.True(on.ScrapedNpcCheckbox.Visible);
        Assert.True(on.WikiRefresh.Visible);
    }

    [Fact]
    public void BodyPriorityControls_StayUsableUnderCreatorGrouping_WhenTheGateIsOpen()
    {
        // The priority is a saved preference, not a per-sort split, so it is editable even while the
        // splits are greyed; only a closed activity gate stops it.
        var s = Compute(strategy: SortStrategy.CreatorOnly);

        Assert.True(s.FirstChoiceBody.Visible);
        Assert.True(s.FirstChoiceBody.Enabled);
        Assert.True(s.FullPriorityList.Enabled);
        Assert.True(s.SortButton.Enabled);
    }
}
```

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~SortLayoutTests"`. Expected: build FAIL, `SortLayout` not found.

- [ ] **Step 3: Implement** `SortLayout.cs`

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>Whether a control is drawn at all, and whether it can be used when it is.</summary>
public readonly record struct ControlState(bool Visible, bool Enabled);

/// <summary>The state of every control on the Sort tab's first section and Body priority section.</summary>
public readonly record struct SortControlStates(
    ControlState GroupBy,
    ControlState SplitGear,
    ControlState SplitNpc,
    ControlState SplitHair,
    ControlState PriorityRace,
    ControlState MidlanderFallback,
    ControlState SplitGearBody,
    ControlState SplitBodyMods,
    ControlState FirstChoiceBody,
    ControlState FullPriorityList,
    ControlState ScrapedNpcCheckbox,
    ControlState WikiRefresh,
    ControlState SortButton);

/// <summary>
/// Which Sort tab controls show, and which are enabled, as one pure function so the "hidden until
/// ticked" and "greyed under Creator grouping" rules are tested rather than read off ImGui calls.
/// </summary>
/// <remarks>
/// public, not internal: this repo has no InternalsVisibleTo.
/// <para>
/// HIDDEN means not drawn: a dependent of an unticked box, and a feature that is switched off.
/// GREYED means drawn but disabled: the splits under Creator grouping (they never change a
/// by-creator path), and any dependent whose own box is ticked, so its value is preserved and it
/// comes back usable when the strategy changes back. A closed activity gate disables everything
/// that acts, and hides nothing.
/// </para>
/// </remarks>
public static class SortLayout
{
    public static SortControlStates Compute(
        SortStrategy strategy, bool splitHair, bool scrapedFeatureEnabled, bool activityGateOpen)
    {
        var splitsApply = strategy != SortStrategy.CreatorOnly;
        var splitEnabled = splitsApply && activityGateOpen;

        return new SortControlStates(
            GroupBy: new(true, activityGateOpen),
            SplitGear: new(true, splitEnabled),
            SplitNpc: new(true, splitEnabled),
            SplitHair: new(true, splitEnabled),
            PriorityRace: new(splitHair, splitEnabled),
            MidlanderFallback: new(splitHair, splitEnabled),
            SplitGearBody: new(true, splitEnabled),
            SplitBodyMods: new(true, splitEnabled),
            FirstChoiceBody: new(true, activityGateOpen),
            FullPriorityList: new(true, activityGateOpen),
            ScrapedNpcCheckbox: new(scrapedFeatureEnabled, activityGateOpen),
            WikiRefresh: new(scrapedFeatureEnabled, activityGateOpen),
            SortButton: new(true, activityGateOpen));
    }
}
```

- [ ] **Step 4: Run.** `--filter "FullyQualifiedName~SortLayoutTests"`. Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/SortLayout.cs PenumbraOrganizer.Plugin.Tests/Windows/SortLayoutTests.cs
git commit -m "feat: SortLayout decides which Sort controls show and which are enabled" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Help topics for the new and renamed controls (before the panel uses them)

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTopics.cs`, `HelpTopicUsage.cs`, `HelpTab.cs`, `PenumbraOrganizer.Plugin/Resources/help-content.json`
- Modify: `PenumbraOrganizer.Plugin/Windows/SortPanel.cs` (rename the one reference to `SortSplitBody`)
- Test: the existing `HelpContentTests` and `HelpTabTests` enforce completeness; no new test file.

**Interfaces:**
- Produces: `HelpTopics.SortSplitGearBody` (`"sort.split-gear-body"`, replaces `SortSplitBody` / `"sort.split-body"`), `HelpTopics.SortSplitBodyMods` (`"sort.split-body-mods"`), `HelpTopics.SortFirstChoiceBody` (`"sort.first-choice-body"`), `HelpTopics.ReviewDiagnostics` (`"review.diagnostics"`). `Help.Short(topic)` is already public.

- [ ] **Step 1: Add and rename the topics.** In `HelpTopics.cs` replace `public static readonly HelpTopic SortSplitBody = new("sort.split-body");` with:

```csharp
    public static readonly HelpTopic SortSplitGearBody = new("sort.split-gear-body");
    public static readonly HelpTopic SortSplitBodyMods = new("sort.split-body-mods");
    public static readonly HelpTopic SortFirstChoiceBody = new("sort.first-choice-body");
```

and add `public static readonly HelpTopic ReviewDiagnostics = new("review.diagnostics");` beside the other `Review*` constants. In `HelpTopicUsage.cs` replace `HelpTopics.SortSplitBody,` with the three Sort lines and add `HelpTopics.ReviewDiagnostics,` in the Review block. In `HelpTab.cs`: in the Sort section replace `HelpTopics.SortSplitBody,` with `HelpTopics.SortSplitGearBody, HelpTopics.SortSplitBodyMods, HelpTopics.SortFirstChoiceBody,` (one per line). Keep `SortRefreshNpcList` and `SortScrapedNpcList` in the Sort section: the topics still exist and only the controls are hidden while the feature is off. MOVE `HelpTopics.SortImportWorkbook,` from the Sort section into the Review section (after `ReviewExportWorkbook`), and add `HelpTopics.ReviewDiagnostics,` after `ReviewDiagnosticDump`. The topic id `sort.import-workbook` stays unchanged.

- [ ] **Step 2: Update `help-content.json`.** Replace the `sort.split-body` entry with these three and add `review.diagnostics`; every `short` must be at most 200 characters (count them; trim if a test says otherwise):

```json
    {
      "id": "sort.split-gear-body",
      "title": "Split gear by body type",
      "short": "Files gear under the body it was made for, as Gear/Bibo+/... Read from the mod's options and files, then its name, then its folder. If none names a body, no body subfolder is added."
    },
    {
      "id": "sort.split-body-mods",
      "title": "Split body mods by body type",
      "short": "Body mods are body replacers (Rue+, YAB...) and SFW variants filed under Body. This groups them under Body/<body>/. Leave it off if you keep one mod per body."
    },
    {
      "id": "sort.first-choice-body",
      "title": "First-choice body",
      "short": "Your main body. A mod that supports several bodies is filed under this one when it offers it. The others follow the order in the full list below."
    },
```

and, near the other review entries:

```json
    {
      "id": "review.diagnostics",
      "title": "Diagnostics",
      "short": "Troubleshooting tools: export the review as text, open the config file, or create a diagnostic dump to share when something looks wrong."
    },
```

Also update two existing entries' wording only (read them first, keep their meaning and length cap): `review.workbook-destinations` becomes titled "Group workbook by" and its `short` should say the choice is how the workbook groups mods into folders (it is the same control, relabelled); `sort.import-workbook`'s `short` stays but must not say it is on the Sort tab (grep the file for "Sort tab" near it).

- [ ] **Step 3: Keep the build green.** In `SortPanel.cs` the one reference `HelpTopics.SortSplitBody` becomes `HelpTopics.SortSplitGearBody` (the transitional single checkbox; Task 5 replaces it).

- [ ] **Step 4: Run.** `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj -c Debug` (0 errors) then `--filter "FullyQualifiedName~Help"`. Expected: PASS. A failure naming one of the new ids means one of the registration places (HelpTopics, HelpTopicUsage, HelpTab, json) is missing it; a length failure means a `short` is over 200 characters.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin
git commit -m "feat: help topics for the two body splits, First-choice body and Diagnostics" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The Sort tab: three sections, hidden dependents, purple Sort, First-choice body

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/SortPanel.cs` (`Draw`, `DrawBodyPriority`, fields; delete the Task 2 shim)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.SortTab.cs` (remove Import and the wiki block; collapse Manual assignment)
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/SortPanelPreferencesTests.cs`

**Interfaces:**
- Consumes: `SortLayout.Compute` / `SortControlStates` / `ControlState` (Task 3), `BodyPriority.MoveToTop` (Task 1), `HelpTopics.SortSplitGearBody` / `SortSplitBodyMods` / `SortFirstChoiceBody` (Task 4), `PluginTheme.PrimaryButton()`.
- Produces: `public static string? SortPanel.FirstChoice(BodyPriority priority)` (the label at rank 1, or null for an empty list) - the one value the First-choice dropdown displays, derived each frame so it can never disagree with the list.

- [ ] **Step 1: Write the failing tests** (append to `SortPanelPreferencesTests`; it already imports `BodyPriority`, `SortPanel`)

```csharp
    // ---- First-choice body is always the list's rank 1 ----

    private static readonly string[] FirstChoiceLabels = ["Bibo+", "Rue+", "TBSE"];

    [Fact]
    public void FirstChoice_IsTheRankOneLabel()
    {
        Assert.Equal("Bibo+", SortPanel.FirstChoice(BodyPriority.Default(FirstChoiceLabels)));
        Assert.Null(SortPanel.FirstChoice(BodyPriority.Default([])));
    }

    [Fact]
    public void FirstChoice_FollowsArrowMoves_Reset_AndAPickFromTheDropdown()
    {
        var p = BodyPriority.Default(FirstChoiceLabels);

        Assert.Equal("Rue+", SortPanel.FirstChoice(p.MoveDown(0)));          // arrow down on rank 1
        Assert.Equal("Bibo+", SortPanel.FirstChoice(p.MoveDown(0).MoveUp(1))); // and back up again
        Assert.Equal("TBSE", SortPanel.FirstChoice(p.MoveToTop("TBSE")));     // the dropdown pick
        Assert.Equal("Bibo+", SortPanel.FirstChoice(                            // Reset to default
            BodyPriority.Default(FirstChoiceLabels)));
    }

    [Fact]
    public void FirstChoice_FollowsABodyListRefresh()
    {
        var saved = new[] { "TBSE", "Rue+", "Bibo+" };
        var refreshed = new[] { "Bibo+", "Rue+" }; // TBSE removed from the body list

        var repaired = SortPanel.RefreshBodyPriority(refreshed, FirstChoiceLabels, saved, out _);

        Assert.Equal("Rue+", SortPanel.FirstChoice(repaired!));
    }
```

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~SortPanelPreferencesTests.FirstChoice"`. Expected: build FAIL (`FirstChoice` not found).

- [ ] **Step 3: Implement `FirstChoice`** (next to `HairRaceIndexFor` in `SortPanel.cs`):

```csharp
    /// <summary>
    /// The body at rank 1, which is what the First-choice dropdown shows. Derived from the priority
    /// every frame and never stored separately, so it cannot disagree with the full list after an
    /// arrow move, a Reset or a body-list refresh. Null only for an empty list.
    /// </summary>
    public static string? FirstChoice(BodyPriority priority) =>
        priority.Labels.Count > 0 ? priority.Labels[0] : null;
```

- [ ] **Step 4: Run.** Expected: PASS.

- [ ] **Step 5: Rewrite `SortPanel.Draw` and `DrawBodyPriority`.** This is ImGui drawing and cannot be unit-tested; it follows `SortLayout`, which is. Replace the fields `_splitBody` with:

```csharp
    private bool _splitGearBody;         // off: no body folders until asked, like gear and hair
    private bool _splitBodyMods;
    private const string BodyModsHelpPopup = "sort-body-mods-help";
```

Replace the body of `Draw` from `ImGui.BeginDisabled(!gates.CanStageProposals);` through the end of the method with the code below (keep the existing preferences-load block and the `RefreshBodyPriority` block at the top of `Draw` exactly as they are):

```csharp
        // Group by is its own tiny section so the tab reads top to bottom with the same headings.
        ImGui.TextColored(PluginTheme.ChangedGood, "Group by");
        ImGui.BeginDisabled(!gates.CanStageProposals);
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("##group-by", ref _groupingIndex, GroupingLabels, GroupingLabels.Length);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortGrouping, gates.CanStageProposals ? null : ActivityGateReason);

        // The strategy is settled the moment the combo has been submitted. Every control below takes
        // its visible and enabled state from SortLayout, which is what the tests pin.
        var strategy = Groupings[_groupingIndex].Strategy;
        var states = SortLayout.Compute(
            strategy, _splitHair, Configuration.ScrapedNpcListFeatureEnabled, gates.CanStageProposals);
        var splitReason = !gates.CanStageProposals ? ActivityGateReason
            : strategy == SortStrategy.CreatorOnly ? SplitsInapplicableReason
            : null;

        GroupHeading("Gear");
        DrawCheckbox("Split gear by equipment slot", ref _splitGear, states.SplitGear, HelpTopics.SortSplitGear, splitReason);

        GroupHeading("NPC");
        DrawCheckbox("Split NPC mods by kind", ref _splitNpc, states.SplitNpc, HelpTopics.SortSplitNpc, splitReason);

        // The scraped-list opt-in and its refresh button are hidden while the feature is switched
        // off at compile time (Configuration.ScrapedNpcListFeatureEnabled): they did nothing but
        // explain, at length, why they were disabled. MainWindow draws no refresh controls either.
        if (states.ScrapedNpcCheckbox.Visible)
        {
            ImGui.BeginDisabled(!states.ScrapedNpcCheckbox.Enabled);
            var useScraped = config.UseScrapedNpcNameList;
            if (ImGui.Checkbox("Also use the NPC list scraped from the wiki", ref useScraped))
            {
                config.UseScrapedNpcNameList = useScraped;
                saveConfig();
            }
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.SortScrapedNpcList, states.ScrapedNpcCheckbox.Enabled ? null : ActivityGateReason);
        }

        GroupHeading("Hair");
        DrawCheckbox("Split hair by hair number", ref _splitHair, states.SplitHair, HelpTopics.SortSplitHair, splitReason);
        if (states.PriorityRace.Visible)
        {
            ImGui.BeginDisabled(!states.PriorityRace.Enabled);
            ImGui.SetNextItemWidth(220);
            ImGui.Combo("Priority race", ref _hairRaceIndex, HairRaceLabels, HairRaceLabels.Length);
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.SortPriorityRace, splitReason);
        }
        if (states.MidlanderFallback.Visible)
            DrawCheckbox("Midlander fallback", ref _midlanderFallback, states.MidlanderFallback,
                HelpTopics.SortMidlanderFallback, splitReason);

        var raceCode = HairRaceCodeAt(_hairRaceIndex);
        if (config.HairRaceCode != raceCode || config.MidlanderFallback != _midlanderFallback)
        {
            config.HairRaceCode = raceCode;
            config.MidlanderFallback = _midlanderFallback;
            saveConfig();
        }

        GroupHeading("Body type");
        DrawCheckbox("Split gear by body type", ref _splitGearBody, states.SplitGearBody,
            HelpTopics.SortSplitGearBody, splitReason);
        DrawCheckbox("Split body mods by body type", ref _splitBodyMods, states.SplitBodyMods,
            HelpTopics.SortSplitBodyMods, splitReason);
        DrawBodyModsHelpMarker();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Every control above has now been submitted, so this is the selection as the user has it
        // THIS frame. The body priority section below may change the priority later in the frame; the
        // staleness hint then picks that up on the next frame, which is not visible.
        var selection = new SortSelection(strategy, _splitGear, _splitNpc, _splitHair,
            HairRaceCodeAt(_hairRaceIndex), _midlanderFallback, _splitGearBody, _splitBodyMods, _bodyPriority);

        // The primary action: the plugin's purple accent, like Scan's Refresh and Search's Build.
        // Stable id: a label varying with mod count would vary the widget id, so a background scan
        // publishing mid-click would drop the click.
        bool sortClicked;
        using (PluginTheme.PrimaryButton())
        {
            ImGui.BeginDisabled(!states.SortButton.Enabled);
            sortClicked = ImGui.Button("Sort##sort-mods");
            ImGui.EndDisabled();
        }
        Help.Tooltip(HelpTopics.SortButton, gates.CanStageProposals ? null : ActivityGateReason);
        if (sortClicked)
        {
            state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, selection.SplitHair,
                canonicalizeCreator, selection.HairPriority, selection.SplitGearBody, selection.SplitBodyMods,
                selection.BodyPriority);
            _lastSorted = selection;
        }

        // Outside the activity gate on purpose: it only navigates to Help, so it works while a scan runs.
        ImGui.SameLine();
        if (ImGui.Button("?##sort-help"))
            onShowSortHelp?.Invoke();
        Help.Tooltip(HelpTopics.SortHelpButton);

        if (_lastSorted is { } last && last != selection)
            ImGui.TextDisabled("Selection changed since the last sort.");

        ImGui.Spacing();
        DrawBodyPriority(config, saveConfig, bodyLabels, states, anyBodySplit: _splitGearBody || _splitBodyMods);
    }

    // One checkbox: not drawn when hidden, greyed when disabled, and its tooltip always carries the
    // reason when it is greyed. Each closes its own disabled scope BEFORE the tooltip because
    // Help.Tooltip binds to the last submitted item and EndDisabled submits none.
    private static void DrawCheckbox(
        string label, ref bool value, ControlState state, HelpTopic topic, string? disabledReason)
    {
        if (!state.Visible)
            return;

        ImGui.BeginDisabled(!state.Enabled);
        ImGui.Checkbox(label, ref value);
        ImGui.EndDisabled();
        Help.Tooltip(topic, state.Enabled ? null : disabledReason);
    }

    // The "(?)" beside "Split body mods by body type": a small CLICKABLE button that opens the
    // explanation in a popup (a visible question mark is not itself an explanation), with the same
    // text as a hover tooltip as an extra. SmallButton is a normal ImGui item, so it takes
    // keyboard focus and Enter/Space like any button where ImGui keyboard navigation is on.
    private static void DrawBodyModsHelpMarker()
    {
        ImGui.SameLine();
        if (ImGui.SmallButton("(?)##body-mods-help"))
            ImGui.OpenPopup(BodyModsHelpPopup);
        Help.Tooltip(HelpTopics.SortSplitBodyMods);

        if (ImGui.BeginPopup(BodyModsHelpPopup))
        {
            ImGui.PushTextWrapPos(400f);
            ImGui.TextUnformatted(Help.Short(HelpTopics.SortSplitBodyMods) ?? string.Empty);
            ImGui.PopTextWrapPos();
            ImGui.EndPopup(); // BeginPopup closes on click-away and on Escape by itself
        }
    }
```

Replace `DrawBodyPriority` with the version below. It is a collapsed header, always present; the full ranked list sits in a nested collapsed header; the First-choice combo shows `FirstChoice(_bodyPriority)`:

```csharp
    // Always present, collapsed by default. Set once and forget: most people use one body, so the
    // First-choice dropdown is the common path and the full ranked list is nested beneath it.
    private void DrawBodyPriority(
        Configuration config, Action saveConfig, IReadOnlyList<string> bodyLabels,
        SortControlStates states, bool anyBodySplit)
    {
        if (!ImGui.CollapsingHeader("Body priority##body-priority"))
            return;

        ImGui.BeginDisabled(!states.FirstChoiceBody.Enabled);
        ImGui.TextDisabled(anyBodySplit
            ? "Highest first. Used when a mod supports more than one body."
            : "Highest first. Used when a mod supports more than one body. It applies when a body split above is ticked.");

        var order = _bodyPriority.Labels; // immutable: safe to iterate while a click replaces the field
        if (FirstChoice(_bodyPriority) is { } first)
        {
            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo("First-choice body##first-choice-body", first))
            {
                for (var i = 0; i < order.Count; i++)
                {
                    if (ImGui.Selectable($"{order[i]}##first-choice-{i}", i == 0))
                        SetBodyPriority(config, saveConfig, _bodyPriority.MoveToTop(order[i]));
                }
                ImGui.EndCombo();
            }
            Help.Tooltip(HelpTopics.SortFirstChoiceBody);
        }

        if (ImGui.CollapsingHeader("Full priority order##body-priority-full"))
        {
            if (ImGui.BeginChild("##body-priority-list", new System.Numerics.Vector2(0, 220)))
            {
                for (var i = 0; i < order.Count; i++)
                {
                    ImGui.PushID(i);

                    ImGui.BeginDisabled(i == 0);
                    if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                        SetBodyPriority(config, saveConfig, _bodyPriority.MoveUp(i));
                    ImGui.EndDisabled();

                    ImGui.SameLine();
                    ImGui.BeginDisabled(i == order.Count - 1);
                    if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                        SetBodyPriority(config, saveConfig, _bodyPriority.MoveDown(i));
                    ImGui.EndDisabled();

                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{i + 1}. {order[i]}");

                    ImGui.PopID();
                }
            }
            ImGui.EndChild();

            ImGui.Spacing();
            if (ImGui.Button("Reset to default##body-priority-reset"))
            {
                // An EMPTY saved list is what "default" means, so a reset keeps following the shipped
                // order across releases instead of freezing today's order into the file.
                _bodyPriority = BodyPriority.Default(bodyLabels);
                config.BodyPriority = [];
                saveConfig();
            }
            ImGui.Spacing();
        }

        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortBodyPriority);
    }
```

(Keep `SetBodyPriority` as it is. Delete the old `DrawBodyPriority`, the old Group-by/checkbox code and the Task 2 shim.) If the compiler rejects a Dalamud binding overload shape (`BeginCombo`, `Selectable`, `SmallButton`, `BeginPopup`), look at how the neighbouring code calls ImGui and adapt the call, not the structure.

In `MainWindow.SortTab.cs`: delete the Import Workbook button block (lines ~29-69, including `_lastWorkbookImportResult` rendering; Task 6 re-creates them on Review), and wrap everything from `ImGui.TextUnformatted("Start Manually: ...")` to the end of the method in a collapsed section:

```csharp
        ImGui.Spacing();
        // Collapsed by default: most sorts never touch it. Same controls as before, unchanged.
        if (!ImGui.CollapsingHeader("Manual assignment##manual-assign"))
            return;

        ImGui.TextUnformatted("Start Manually: check mods below, type a folder, click Assign.");
        ...existing search box, destination, Assign button, summary, reconcile and list, unchanged...
```

and delete the whole "Refresh NPC list from wiki" block (the disabled button, its long yellow `TextColoredWrapped`, and the `_npcRefreshResult` rendering) since the feature is off. Keep `_npcRefreshResult` and the refresh method if other code references them (grep first); if they become unreferenced fields, leave them (YAGNI: do not widen this task), but wrap the block's removal in a comment `// The wiki refresh controls return, gated on Configuration.ScrapedNpcListFeatureEnabled, when that feature ships.` Remove now-unused `using` lines only if the compiler warns.

- [ ] **Step 6: Build and run.** `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj -c Debug` (0 errors, 0 warnings), then `--filter "FullyQualifiedName~SortPanelPreferencesTests|FullyQualifiedName~SortLayoutTests|FullyQualifiedName~SortSelectionTests|FullyQualifiedName~Help"`, then the full project. Expected: all PASS. This task's drawing is verified in game by the author.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: three-section Sort tab with two body checkboxes, First-choice body and a primary Sort button" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Review Changes: Workbook group, primary Apply, collapsed Diagnostics

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.ReviewTab.cs`

**Interfaces:**
- Consumes: `HelpTopics.ReviewDiagnostics`, `HelpTopics.SortImportWorkbook` (id unchanged), `PluginTheme.PrimaryButton()`, the existing fields `_lastExportPath`, `_lastDiagnosticDumpPath`, `_lastWorkbookExportPath`, `_lastWorkbookImportResult`, `_workbookStrategyIndex`, `WorkbookStrategyOptions`, `_fileDialogManager`, and the methods `ExportWorkbook`, `ImportWorkbook`, `CurrentGates`, `OpenConfigFile`, `OpenContainingFolder`, `OpenFileWithDefaultApp`, `CreateDiagnosticDump`.
- Behavior to preserve exactly: Import's activity gate AND the re-check of `CurrentGates().CanStageProposals` inside the file-dialog callback; the Apply confirmation popup and the Rediscover reminder popup; `Protect & Skip All Blocking Mods`; the orphaned-folders section.

- [ ] **Step 1: Restructure `DrawReviewTab`.** Keep lines 15-33 (`TabItem`, `Validate`, the issue lines, the tree) as they are. Delete the block from `ImGui.Spacing(); if (ImGui.Button("Export"))` through the `Open Workbook` button's closing brace (lines ~35-81) and replace it with:

```csharp
        ImGui.Spacing();
        DrawWorkbookGroup(CurrentGates());
```

Leave the `if (result.HasIssues) { ...Protect & Skip... }` block as it is. Replace the Apply button (from `ImGui.BeginDisabled(result.HasIssues || !gates.CanStartApply);` through `if (applyClicked) ImGui.OpenPopup("Apply changes?");`) with the same logic wrapped in the primary style and set apart by a separator:

```csharp
        ImGui.Separator();
        ImGui.Spacing();
        bool applyClicked;
        using (PluginTheme.PrimaryButton())
        {
            ImGui.BeginDisabled(result.HasIssues || !gates.CanStartApply);
            applyClicked = ImGui.Button("Apply");
            ImGui.EndDisabled();
        }
        // One of two disable reasons, resolved at the call site: errors the user can fix are named
        // before the activity gate, exactly as before.
        Help.Tooltip(
            HelpTopics.ReviewApply,
            result.HasIssues ? "Fix the errors listed above first."
            : !gates.CanStartApply ? ActivityGateReason
            : null);
        if (applyClicked)
            ImGui.OpenPopup("Apply changes?");
```

(Everything between the old Apply and the progress block - the two modals - stays untouched.) Immediately before the final `ImGui.Spacing(); ImGui.Separator(); DrawOrphanedFoldersSection();` add `ImGui.Spacing(); DrawDiagnosticsGroup();`.

- [ ] **Step 2: Add the two group methods** to `MainWindow.ReviewTab.cs`:

```csharp
    // Export and import side by side: the workflow is sort, export a pre-sorted workbook, tune it
    // in Excel, import it, then review. Import is available whenever the activity gate allows it,
    // including with a scan loaded and nothing staged, so a workbook can be imported without
    // sorting first. Results stay inside this group: the export path with Open Workbook, and the
    // import summary, errors and warnings, each until its own next run.
    private void DrawWorkbookGroup(ActivityGates gates)
    {
        ImGui.TextColored(PluginTheme.ChangedGood, "Workbook");

        var strategyLabels = WorkbookStrategyOptions.Select(o => o.Label).ToArray();
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Group workbook by", ref _workbookStrategyIndex, strategyLabels, strategyLabels.Length);
        Help.Tooltip(HelpTopics.ReviewWorkbookDestinations);

        if (ImGui.Button("Export Workbook"))
            ExportWorkbook(WorkbookStrategyOptions[_workbookStrategyIndex].Strategy);
        Help.Tooltip(HelpTopics.ReviewExportWorkbook);

        ImGui.SameLine();
        // Re-established here from the Sort tab, where it used to live. The dialog callback fires on
        // a later frame, after this frame's BeginDisabled has lapsed, so the gate is re-checked at
        // the moment the import would actually run: a scan can have started in between, and
        // ReplaceScanAtomically would silently wipe whatever ImportWorkbook is about to stage.
        ImGui.BeginDisabled(!gates.CanStageProposals);
        if (ImGui.Button("Import Workbook"))
        {
            _fileDialogManager.OpenFileDialog(
                "Import Workbook",
                ".xlsx",
                (success, paths) =>
                {
                    if (!success || paths.Count == 0)
                        return;
                    if (!CurrentGates().CanStageProposals)
                    {
                        _lastError = "Import Workbook was cancelled because library work started before the file was chosen.";
                        return;
                    }
                    ImportWorkbook(paths[0]);
                },
                selectionCountMax: 1);
        }
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortImportWorkbook, gates.CanStageProposals ? null : ActivityGateReason);

        if (_lastWorkbookExportPath is not null)
        {
            // Own line, wrapped, with the button below it rather than SameLine'd: a path is unbounded.
            ImGui.TextWrapped($"Workbook exported to: {_lastWorkbookExportPath}");
            if (ImGui.Button("Open Workbook"))
                OpenFileWithDefaultApp(_lastWorkbookExportPath);
        }

        if (_lastWorkbookImportResult is not null)
        {
            ImGui.TextWrapped(_lastWorkbookImportResult.Summary);
            foreach (var error in _lastWorkbookImportResult.Errors)
                TextColoredWrapped(PluginTheme.CollisionBad, $"  {error}");
            foreach (var warning in _lastWorkbookImportResult.Warnings)
                TextColoredWrapped(ImGuiColors.DalamudYellow, $"  {warning}");
        }
    }

    // The troubleshooting tools, collapsed by default: most users never open them. Each keeps its
    // own result line, so a diagnostic export can never replace the workbook path above.
    private void DrawDiagnosticsGroup()
    {
        var open = ImGui.CollapsingHeader("Diagnostics##review-diagnostics");
        Help.Tooltip(HelpTopics.ReviewDiagnostics);
        if (!open)
            return;

        if (ImGui.Button("Export"))
            _lastExportPath = _plugin.ExportReview();
        Help.Tooltip(HelpTopics.ReviewExport);

        ImGui.SameLine();
        if (ImGui.Button("Show Config File"))
            OpenConfigFile();
        Help.Tooltip(HelpTopics.ReviewShowConfigFile);

        ImGui.SameLine();
        if (ImGui.Button("Create Diagnostic Dump"))
            _lastDiagnosticDumpPath = CreateDiagnosticDump();
        Help.Tooltip(HelpTopics.ReviewDiagnosticDump);

        if (_lastExportPath is not null)
            ImGui.TextWrapped($"Exported to: {_lastExportPath}");

        if (_lastDiagnosticDumpPath is not null)
        {
            ImGui.TextWrapped($"Diagnostic dump written to: {_lastDiagnosticDumpPath}");
            if (ImGui.Button("Show Dump File"))
                OpenContainingFolder(_lastDiagnosticDumpPath);
        }
    }
```

- [ ] **Step 2b: Check what else referenced the moved code.** `grep -n "_lastWorkbookImportResult\|SortImportWorkbook" PenumbraOrganizer.Plugin/Windows/*.cs` should now show them only in `MainWindow.ReviewTab.cs`, `MainWindow.cs` (field and `ImportWorkbook`) and `HelpTopics*`/`HelpTab`. Any other reference to the removed Sort-tab Import code is a bug to fix here.

- [ ] **Step 3: Build and run.** Build (0 errors, 0 warnings), then the full test project. Expected: all PASS (`HelpTabTests` and `HelpContentTests` confirm the Import topic is now listed under Review).

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin
git commit -m "feat: Review tab gets a Workbook group with Import, a primary Apply and collapsed Diagnostics" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Smaller fixes: History Delete confirm, Scan Live events, Protect labels

**Files:**
- Create: `PenumbraOrganizer.Plugin/Windows/ProtectToggleLabels.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.ProtectTab.cs` (lines 24-41), `MainWindow.ScanTab.cs` (lines 45-55), `MainWindow.HistoryTab.cs` (lines ~76-86), `MainWindow.cs` (one new field next to `_pendingRestoreSnapshotId`)
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/ProtectToggleLabelsTests.cs`

**Interfaces:**
- Produces: `public static class ProtectToggleLabels { public static string All(bool allProtected); public static string Heliosphere(bool allProtected); }`.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class ProtectToggleLabelsTests
{
    [Fact]
    public void All_NamesTheActionAClickWillTake()
    {
        Assert.Equal("Protect all", ProtectToggleLabels.All(allProtected: false));
        Assert.Equal("Unprotect all", ProtectToggleLabels.All(allProtected: true));
    }

    [Fact]
    public void Heliosphere_NamesTheActionAClickWillTake()
    {
        Assert.Equal("Protect Heliosphere mods", ProtectToggleLabels.Heliosphere(allProtected: false));
        Assert.Equal("Unprotect Heliosphere mods", ProtectToggleLabels.Heliosphere(allProtected: true));
    }
}
```

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~ProtectToggleLabelsTests"`. Expected: build FAIL.

- [ ] **Step 3: Implement.** `ProtectToggleLabels.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The Protect tab's two bulk buttons used to read "Toggle ...", which does not say which way. They
/// now name the action a click performs, chosen from the same state the click already uses.
/// </summary>
/// <remarks>public, not internal: this repo has no InternalsVisibleTo.</remarks>
public static class ProtectToggleLabels
{
    public static string All(bool allProtected) => allProtected ? "Unprotect all" : "Protect all";

    public static string Heliosphere(bool allProtected) =>
        allProtected ? "Unprotect Heliosphere mods" : "Protect Heliosphere mods";
}
```

In `MainWindow.ProtectTab.cs` replace the two toggle buttons so the state is computed BEFORE the label and the ImGui id stays stable with a `##` suffix (a changing label would otherwise change the widget id):

```csharp
        var allProtected = _plugin.OrganizerState.Mods.All(m => m.Protected);
        if (ImGui.Button($"{ProtectToggleLabels.All(allProtected)}##toggle-protect-all"))
        {
            _plugin.OrganizerState.SetAllProtection(!allProtected);
            SaveProtectionStateSafely();
        }
        Help.Tooltip(HelpTopics.ProtectToggleAll);

        ImGui.SameLine();
        var allHeliosphereProtected = _plugin.OrganizerState.Mods
            .Where(m => m.HeliosphereManaged)
            .All(m => m.Protected);
        if (ImGui.Button($"{ProtectToggleLabels.Heliosphere(allHeliosphereProtected)}##toggle-protect-heliosphere"))
        {
            _plugin.OrganizerState.SetHeliosphereProtection(!allHeliosphereProtected);
            SaveProtectionStateSafely();
        }
        Help.Tooltip(HelpTopics.ProtectToggleHeliosphere);
```

(Same `All(...)` semantics as the old in-click computation, including an empty list, so behavior is unchanged; only the label changed.) Then read the `protect.toggle-all` and `protect.toggle-heliosphere` entries in `help-content.json` and, only if they still say "toggle", reword them to match the new labels (<= 200 chars).

In `MainWindow.ScanTab.cs` replace the heading, tooltip and fixed-height child (lines 45-55) with a collapsed header whose id is stable while its count changes:

```csharp
        ImGui.Spacing();
        // Collapsed by default: the log is empty most of the time, and an always-open 150 px box
        // took about a third of the window. The count is in the label; the "###" id keeps the open
        // state when the count changes.
        var eventCount = _eventLog.Lines.Count();
        var eventsOpen = ImGui.CollapsingHeader($"Live events ({eventCount})###scan-events");
        Help.Tooltip(HelpTopics.ScanEventLog);
        if (eventsOpen)
        {
            using var child = ImRaii.Child("EventLog", new Vector2(0, 150), border: true);
            if (child)
                foreach (var line in _eventLog.Lines)
                    ImGui.TextUnformatted(line);
        }
```

(If `_eventLog.Lines` already has a `Count` property, use it instead of `Count()`.)

In `MainWindow.cs` add beside `_pendingRestoreSnapshotId`: `private Guid? _pendingDeleteSnapshotId;`. In `MainWindow.HistoryTab.cs` replace the Delete handling (the `if (deleteButtonClicked) { DeleteHistorySnapshot(snapshot.Id); }` block) with a confirmation, and add the modal next to the Restore modal inside the same per-row loop so it can read `snapshot.Id` and `title`:

```csharp
            if (deleteButtonClicked)
            {
                _pendingDeleteSnapshotId = snapshot.Id;
                ImGui.OpenPopup("Delete snapshot?");
            }

            if (_pendingDeleteSnapshotId == snapshot.Id)
                ImGui.SetNextWindowSize(new Vector2(StandardPopupWidth, 0), ImGuiCond.Appearing);
            if (_pendingDeleteSnapshotId == snapshot.Id && ImGui.BeginPopupModal("Delete snapshot?"))
            {
                ImGui.TextWrapped($"Delete this backup? {title}");
                ImGui.TextWrapped("Your mods are not touched. The saved snapshot is removed for good and cannot be recovered.");
                // The destructive choice is coloured and is not the first button, so a reflexive
                // Enter or click on the left cannot confirm it.
                if (ImGui.Button("Cancel"))
                {
                    _pendingDeleteSnapshotId = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, PluginTheme.CollisionBad);
                var confirmDelete = ImGui.Button("Yes, delete");
                ImGui.PopStyleColor();
                if (confirmDelete)
                {
                    DeleteHistorySnapshot(snapshot.Id);
                    _pendingDeleteSnapshotId = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
```

(`StandardPopupWidth` and `DeleteHistorySnapshot` already exist in the partial class; if the compiler cannot find `ImGuiCol.Text` overload with a `Vector4`, mirror the `PushStyleColor` calls already used in `PluginTheme.cs`.) Update `history.delete-snapshot`'s `short` only if it describes the click as immediate (it says "Deletes this backup permanently"; add "after you confirm" if it fits in 200 characters).

- [ ] **Step 4: Build and run.** Build (0 errors, 0 warnings), `--filter "FullyQualifiedName~ProtectToggleLabelsTests|FullyQualifiedName~Help"`, then the full project. Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: confirm History Delete, collapse Scan's Live events, name the Protect toggles' direction" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: In-game checklist and the full run

**Files:**
- Create: `docs/testing/2026-11-05-sort-review-ux-checklist.md`

- [ ] **Step 1: Write the checklist** from the spec's Testing section. Plain ASCII. One checkbox per check:

```markdown
# Manual check: Sort and Review tab UX pass

Build, load in game (dev plugin path: the MAIN checkout's bin\Debug, not an old worktree), scan a
real library, then check each item. Nothing here is covered by the unit suite except the layout rules.

## Sort tab
- [ ] Group by has a small heading; Gear, NPC, Hair and Body type sections follow; Sort is purple.
- [ ] Untick "Split hair by hair number": Priority race and Midlander fallback disappear. Tick it: they return.
- [ ] Pick Group by "Creator": every split checkbox greys with the tooltip; a TICKED hair box keeps its
      dependents visible but greyed, and switching Group by back restores their values untouched.
- [ ] The wiki-refresh button, its yellow note and the scraped-NPC checkbox are gone.
- [ ] "Split gear by body type" and "Split body mods by body type" are separate. The "(?)" opens a popup
      by click, closes on click-away and on Escape, shows the same text on hover, and takes keyboard
      focus (Tab) and activates with Enter or Space. If it cannot be focused, note that ImGui keyboard
      navigation is off in Dalamud and the click still works.
- [ ] Gear flag only: gear files under a body, Body mods stay `Body/<mod>`. Body flag only: the reverse.
      Gear flag with the slot split off gives `Gear/<body>/<mod>`; with it on, `Gear/<body>/<slot>/<mod>`.
- [ ] Body priority is always there, collapsed. Expanded: "First-choice body" shows rank 1. Pick another
      body: it moves to rank 1 and the others keep their order. Reorder with the arrows in the nested
      "Full priority order": the dropdown follows. Reset to default: the dropdown follows. Restart the game:
      the order persists.
- [ ] "Manual assignment" is collapsed by default and works when opened.
- [ ] "Selection changed since the last sort." still appears after changing a checkbox or the priority.

## Review Changes
- [ ] A Workbook group holds "Group workbook by", Export Workbook and Import Workbook.
- [ ] Import Workbook works with a scan loaded and nothing staged, and stays greyed while a scan runs.
- [ ] Create a diagnostic dump after exporting a workbook: the workbook path and Open Workbook are unchanged.
- [ ] Apply is purple and set apart. Diagnostics is collapsed and still produces the dump.
- [ ] Export, import, review and Apply work end to end.

## Other tabs
- [ ] History: Delete asks first, Cancel keeps the backup, "Yes, delete" removes it.
- [ ] Scan: "Live events (N)" is collapsed and keeps its open state as N changes.
- [ ] Protect: the buttons read "Protect all"/"Unprotect all" and "Protect Heliosphere mods"/"Unprotect
      Heliosphere mods" and each does what it says.
```

- [ ] **Step 2: Full verification.** `dotnet build PenumbraOrganizer.Plugin.sln -c Debug` (0 errors) and the full test project; the count must be higher than the 1757 baseline and nothing skipped. Confirm `git grep -n "SplitBody\b\|splitBody:" -- '*.cs'` finds nothing outside `.claude/worktrees`.

- [ ] **Step 3: Commit**

```bash
git add docs/testing/2026-11-05-sort-review-ux-checklist.md
git commit -m "docs: in-game checklist for the Sort and Review UX pass" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## Self-Review (against the spec)

- Three sections, Sort actions never collapsible, Body priority and Manual assignment collapsed by default, Body priority always present: Task 5.
- Hair stays in Sort actions; dependents hidden until ticked; Creator greys ticked dependents and keeps values; activity gate in the tested rules: Task 3 (rules) and Task 5 (drawing).
- Two independent body checkboxes, category-aware sorting, staged record per category, gear paths with and without the slot split: Task 2.
- `(?)` as a clickable popup with a hover tooltip as an extra, keyboard-focusable where ImGui allows: Task 5, with the keyboard caveat recorded in the checklist.
- First-choice body derived from rank 1, `MoveToTop`, nested full list, spacing above Reset: Tasks 1 and 5.
- Purple Sort and purple Apply, wiki controls hidden, Import moved to Review with its result lines and its gate re-check, workbook and diagnostic results kept apart, Diagnostics collapsed: Tasks 5 and 6.
- Help entries for controls this pass touches, Import listed under Review, ids unchanged for Import: Task 4.
- Smaller fixes (History confirm, Scan collapse, Protect labels): Task 7. The Search tab's fate and the tutorial, guide, PDF and handoff updates stay out of scope as the spec says.
- Type and name consistency: `SortSelection` has nine parameters from Task 2 onward and Task 5 builds it with `_splitGearBody, _splitBodyMods, _bodyPriority` in that order; `SortLayout.Compute(strategy, splitHair, scrapedFeatureEnabled, activityGateOpen)` is called with exactly those four arguments; `FirstChoice`, `MoveToTop`, `DrawCheckbox` and `ControlState` names match across tasks.
- Known limits: the ImGui drawing in Tasks 5, 6 and 7 is verified by compile and in game, not by unit tests; the rules it follows are tested in Task 3, and `FirstChoice` in Task 5.
