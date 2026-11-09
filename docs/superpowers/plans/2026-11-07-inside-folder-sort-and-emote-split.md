# Sort Inside Chosen Folders and Emote Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two independent, off-by-default features: (1) let the user mark top-level folders as "sort inside" so their mods are re-sorted beneath the folder, and (2) split `Animation and VFX/Emotes` by the emote a mod replaces.

**Architecture:** (1) generalises the existing Heliosphere/Proteus "SortInside" mode: `ToolPolicy` carries a set of folder names, and `OrganizerState.ToolModeOf` reports `SortInside` for an ordinary row whose top folder is in that set, so the existing anchor and per-anchor numbering code does the rest. (2) adds an `EmoteNames` set on the mod row (filled at scan), a pure `EmoteFolders.Tail`, and a third `LayoutOptions` flag consumed in `SortFolderSelectors.Select`.

**Tech Stack:** C# / .NET 10, ImGui via `Dalamud.Bindings.ImGui`, xUnit. No `InternalsVisibleTo`: anything tests touch must be public.

Spec: `docs/superpowers/specs/2026-11-07-inside-folder-sort-and-emote-split-design.md`.
Follow how the 0.6.5.0 options were added: `git show ab3ff66 --stat` and `git show a4b69cf --stat` list every file a new Sort option or help topic touches.

## Global Constraints

- Both features are off by default; an unchanged install must produce exactly today's folders.
- Emote folder names are Penumbra's emote names exactly as reported, never shortened (only `PenumbraPathSemantics.FixName` for characters Penumbra cannot store).
- Facial expressions (set in `EmoteFolders.Expressions`) are secondary: ignored whenever the mod also replaces any other emote.
- With the emote split on, no emote mod stays flat in `Emotes`: no names at all goes to `Emotes/Undetermined`.
- Sort inside re-sorts everything under the folder; protection rules still apply (protected rows are never moved, as today).
- A sorter folder name (`Gear`, `Hair`, `Animation and VFX`, `Review`, ...) can never be a sort-inside folder (`ToolFolders.IsSorterFolder`).
- Templates do not follow the emote split. Applying a template keeps mods in a chosen sort-inside folder under it (as for Heliosphere and Proteus in Sort inside mode); the template preview does not show that folder prefix yet, and the workbook export ignores both options. `WorkbookWorkflowService` must not be edited.
- `TemplateFallbackConversionTests` counts `SortSelection` constructor parameters (10); do not add a parameter to `SortSelection` (the emote flag rides in `LayoutOptions`).
- Help `short` text must stay under 200 characters (`HelpContentTests`).
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Run `dotnet test` from the repo root before each commit.

## File Structure

- Create `PenumbraOrganizer.Plugin/Organizer/Classification/EmoteFolders.cs` - pure emote folder tail.
- Create `PenumbraOrganizer.Plugin/Organizer/SortInsideFolderChoices.cs` - pure candidate list for the Protect tab control.
- Modify `Organizer/OrganizerModRow.cs`, `LibraryWork/Pure/ScanProcessor.cs`, `Organizer/LayoutOptions.cs`, `Organizer/SortFolderSelectors.cs` - emote data and layout.
- Modify `Configuration.cs`, `Windows/SortLayout.cs`, `Windows/SortPanel.cs`, `Windows/HelpTopics.cs`, `Windows/HelpTopicUsage.cs`, `Windows/HelpTab.cs`, `Resources/help-content.json`, `LibraryWork/ScanJob.cs` - emote UI, help, diagnostic log.
- Modify `Organizer/ToolMode.cs`, `Organizer/OrganizerState.cs` - sort-inside folders in the policy.
- Modify `Windows/MainWindow.ProtectTab.cs` - the control.
- Tests mirror the production folders under `PenumbraOrganizer.Plugin.Tests/`.

---

### Task 1: Emote data and layout (core, no UI)

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/EmoteFolders.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs` (the `new OrganizerModRow { ... }` initializer near line 138)
- Modify: `PenumbraOrganizer.Plugin/Organizer/LayoutOptions.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs` (inside `Type()`, before the hair branch)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/EmoteFoldersTests.cs`

**Interfaces:**
- Consumes: `ChangedItemKeyParser.Parse(string)` returns `ChangedItemKey` with `Shape == ChangedItemKeyShape.Emote` and `ItemName` = the emote name; `PenumbraPathSemantics.FixName(string)`; `ScanSeed.ChangedItemKeys` (strings) in `ScanProcessor.Process`.
- Produces: `EmoteFolders.Tail(IReadOnlySet<string>? names) -> string` (`"Undetermined"`, `"Multiple"`, `"Expressions"` or the single primary name); `EmoteFolders.Expressions` (public `IReadOnlySet<string>`); `OrganizerModRow.EmoteNames` (`IReadOnlySet<string>?`, init); `LayoutOptions(bool SharedHandsAndFeet = false, bool SplitHairByRace = false, bool SplitEmotes = false)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class EmoteFoldersTests
{
    private static IReadOnlySet<string> Set(params string[] names) => names.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void OnePrimaryEmote_UsesItsExactName() =>
        Assert.Equal("Dance 74", EmoteFolders.Tail(Set("Dance 74")));

    [Fact]
    public void ExpressionsAreIgnoredWhenARealEmoteIsPresent() =>
        Assert.Equal("Sit", EmoteFolders.Tail(Set("Sit", "Big Grin", "Ponder")));

    [Fact]
    public void SeveralPrimaryEmotes_GoToMultiple() =>
        Assert.Equal("Multiple", EmoteFolders.Tail(Set("Sit", "Groundsit", "Big Grin")));

    [Fact]
    public void OnlyExpressions_GoToExpressions() =>
        Assert.Equal("Expressions", EmoteFolders.Tail(Set("Big Grin", "Endure")));

    [Fact]
    public void NoNames_GoToUndetermined()
    {
        Assert.Equal("Undetermined", EmoteFolders.Tail(null));
        Assert.Equal("Undetermined", EmoteFolders.Tail(Set()));
    }

    [Fact]
    public void ExpressionMatchingIgnoresCase() =>
        Assert.Equal("Expressions", EmoteFolders.Tail(Set("big grin")));

    [Fact]
    public void NameWithSlashIsMadeStorable_NotShortened() =>
        Assert.Equal(PenumbraPathSemantics.FixName("A/B"), EmoteFolders.Tail(Set("A/B")));

    private static OrganizerModRow Emote(IReadOnlySet<string>? names) => new()
    {
        Identifier = "e", Name = "e", Author = "A", CurrentPath = "e", ProposedPath = "e",
        Category = ModCategory.Animation, SubCategory = "Emotes", EmoteNames = names,
    };

    [Fact]
    public void Select_SplitsEmotesOnlyWhenTheOptionIsOn()
    {
        var row = Emote(Set("Dance 74"));
        Assert.Equal("Animation and VFX/Emotes",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, row).Primary);
        Assert.Equal("Animation and VFX/Emotes/Dance 74",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, row,
                options: new LayoutOptions(SplitEmotes: true)).Primary);
    }

    [Fact]
    public void Select_NoEmoteNamesGoesToUndetermined() =>
        Assert.Equal("Animation and VFX/Emotes/Undetermined",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, Emote(null),
                options: new LayoutOptions(SplitEmotes: true)).Primary);

    [Fact]
    public void Select_DoesNotTouchOtherAnimationRows()
    {
        var battle = new OrganizerModRow
        {
            Identifier = "b", Name = "b", Author = "A", CurrentPath = "b", ProposedPath = "b",
            Category = ModCategory.Animation, SubCategory = "Battle Animation",
        };
        Assert.Equal("Animation and VFX/Battle Animation",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, battle,
                options: new LayoutOptions(SplitEmotes: true)).Primary);
    }

    [Fact]
    public void Select_TemplateRenameStillApplies() =>
        Assert.Equal("Moves/Sit",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, Emote(Set("Sit")),
                renameFolder: f => f == "Animation and VFX/Emotes" ? "Moves" : f,
                options: new LayoutOptions(SplitEmotes: true)).Primary);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~EmoteFoldersTests`
Expected: build error, `EmoteFolders`, `EmoteNames` and `SplitEmotes` do not exist.

- [ ] **Step 3: Implement**

`EmoteFolders.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The folder below <c>Animation and VFX/Emotes</c> when emotes are split: the emote a mod replaces,
/// exactly as Penumbra names it. Facial expressions are usually bundled with a real emote, so they
/// only count when a mod has nothing else.
/// </summary>
public static class EmoteFolders
{
    public const string Multiple = "Multiple";
    public const string ExpressionsFolder = "Expressions";
    public const string Undetermined = "Undetermined";

    /// <summary>The game's Expressions emote category. An emote not listed here counts as a real emote.</summary>
    public static readonly IReadOnlySet<string> Expressions = new HashSet<string>(
        ["Endure", "Ponder", "Reflect", "Big Grin"], StringComparer.OrdinalIgnoreCase);

    public static string Tail(IReadOnlySet<string>? names)
    {
        if (names is null || names.Count == 0)
            return Undetermined;

        var primary = names.Where(n => !Expressions.Contains(n)).ToList();
        if (primary.Count == 0)
            return ExpressionsFolder;
        if (primary.Count > 1)
            return Multiple;
        return PenumbraPathSemantics.FixName(primary[0]);
    }
}
```

`OrganizerModRow.cs`, after `HairByRaceCode`:

```csharp
    /// <summary>The emote names ("Emote: Dance 74" keys) the mod replaces. Null unless an Animation/Emotes row.</summary>
    public IReadOnlySet<string>? EmoteNames { get; init; }
```

`ScanProcessor.Process`, add to the initializer after `HairByRaceCode = ...,`:

```csharp
            EmoteNames = classification is { Category: ModCategory.Animation, SubCategory: "Emotes" }
                ? item.ChangedItemKeys.Select(ChangedItemKeyParser.Parse)
                    .Where(k => k.Shape == ChangedItemKeyShape.Emote && !string.IsNullOrWhiteSpace(k.ItemName))
                    .Select(k => k.ItemName!).ToHashSet(StringComparer.Ordinal)
                : null,
```

`LayoutOptions.cs`:

```csharp
/// <summary>
/// The optional layout splits on the Sort tab. All default to off, which is the 0.6.4.0 layout.
/// A null <c>LayoutOptions?</c> anywhere means all off.
/// </summary>
public readonly record struct LayoutOptions(
    bool SharedHandsAndFeet = false, bool SplitHairByRace = false, bool SplitEmotes = false);
```

`SortFolderSelectors.Type()`, immediately before the `// Hair split by race` comment:

```csharp
            // Emotes split by the emote they replace; the template rename applies to the Emotes folder only.
            if (opts.SplitEmotes && row.Category == ModCategory.Animation && row.SubCategory == "Emotes")
            {
                var emoteRoot = TypeFolder(row.Category, row.SubCategory, renameFolder);
                return emoteRoot is null ? null : $"{emoteRoot}/{EmoteFolders.Tail(row.EmoteNames)}";
            }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~EmoteFoldersTests` then the whole suite.
Expected: PASS, no other test changes.

- [ ] **Step 5: Scan test**

Find the existing `ScanProcessor` tests (`grep -rn "HairFlat" PenumbraOrganizer.Plugin.Tests | grep -i scan`) and add one beside the hair-evidence test: a seed whose `ChangedItemKeys` are `["Emote: Dance 74", "Emote: Big Grin"]` produces a row with `SubCategory == "Emotes"` and `EmoteNames` equal to both names; a gear seed produces `EmoteNames == null`. Run it, expect PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: emote names on the row and an option-gated Emotes/<emote> layout

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Emote UI, help and the measuring log

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Configuration.cs` (after `SortSplitHairByRace`)
- Modify: `PenumbraOrganizer.Plugin/Windows/SortLayout.cs`, `Windows/SortPanel.cs`
- Modify: `Windows/HelpTopics.cs`, `Windows/HelpTopicUsage.cs`, `Windows/HelpTab.cs`, `Resources/help-content.json` (mirror `sort.split-hair-by-race`)
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/ScanJob.cs` (`Publish`)
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/SortLayoutOptionsTests.cs`

**Interfaces:**
- Consumes: `LayoutOptions(..., SplitEmotes)` and `EmoteFolders.Tail` from Task 1.
- Produces: `Configuration.SortSplitEmotes` (bool, default false); `SortControlStates.SplitEmotes`; help topic id `sort.split-emotes`; `EmoteDiagnostic.Summarize(IEnumerable<OrganizerModRow>) -> IReadOnlyList<string>` lines for the log.

- [ ] **Step 1: Write the failing tests**

In `SortLayoutOptionsTests.cs` add:

```csharp
    [Fact]
    public void SplitEmotes_IsAlwaysVisible_AndGreyedUnderCreatorGrouping()
    {
        var typeStates = SortLayout.Compute(SortStrategy.TypeOnly, false, false, true);
        Assert.Equal(new ControlState(true, true), typeStates.SplitEmotes);

        var creatorStates = SortLayout.Compute(SortStrategy.CreatorOnly, false, false, true);
        Assert.Equal(new ControlState(true, false), creatorStates.SplitEmotes);
    }
```

Create `PenumbraOrganizer.Plugin.Tests/Organizer/EmoteDiagnosticTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class EmoteDiagnosticTests
{
    private static OrganizerModRow Row(string id, params string[] names) => new()
    {
        Identifier = id, Name = id, Author = "A", CurrentPath = id, ProposedPath = id,
        Category = ModCategory.Animation, SubCategory = "Emotes",
        EmoteNames = names.Length == 0 ? null : names.ToHashSet(),
    };

    [Fact]
    public void Summary_CountsOutcomesAndEachDistinctName()
    {
        var lines = EmoteDiagnostic.Summarize([
            Row("a", "Sit"), Row("b", "Sit", "Big Grin"), Row("c", "Sit", "Groundsit"), Row("d", "Big Grin"), Row("e")]);

        Assert.Contains(lines, l => l.Contains("Multiple=1") && l.Contains("Expressions=1") && l.Contains("Undetermined=1") && l.Contains("Single=2"));
        Assert.Contains(lines, l => l.Contains("Sit") && l.Contains("3"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~SortLayoutOptionsTests|FullyQualifiedName~EmoteDiagnosticTests"`
Expected: build error (`SplitEmotes`, `EmoteDiagnostic` missing).

- [ ] **Step 3: Implement**

`Configuration.cs`:

```csharp
    /// <summary>Animation: split Emotes by the emote a mod replaces. Off keeps 0.6.5.0 behavior.</summary>
    public bool SortSplitEmotes { get; set; }
```

`SortLayout.cs`: add `ControlState SplitEmotes,` to `SortControlStates` after `SplitHairByRace`, and `SplitEmotes: new(true, splitEnabled),` after `SplitHairByRace: new(true, splitEnabled),` in `Compute`.

`SortPanel.cs`: add `private bool _splitEmotes;           // off: emotes stay in one Emotes folder`; load it with the others (`_splitEmotes = config.SortSplitEmotes;`); add `&& config.SortSplitEmotes == _splitEmotes` to the `PersistSortChoices` equality and `config.SortSplitEmotes = _splitEmotes;` to its writes; add after the Body type checkboxes (before the `ImGui.Spacing(); ImGui.Separator();` that precedes `PersistSortChoices`):

```csharp
        GroupHeading("Animation");
        DrawCheckbox("Split emotes by emote", ref _splitEmotes, states.SplitEmotes,
            HelpTopics.SortSplitEmotes, splitReason);
```

and change the selection to `new LayoutOptions(_sharedHandsFeet, _splitHairByRace, _splitEmotes)`.

Help: mirror `sort.split-hair-by-race` in all four files. Topic `sort.split-emotes`, short text (under 200 chars): `Emote mods go in Animation and VFX/Emotes/<emote name>. Mods with several emotes go in Multiple, face-only ones in Expressions, and ones with no emote name in Undetermined.` Add a longer `body` in the same style as the hair topic's.

`EmoteDiagnostic` (new, `Organizer/EmoteDiagnostic.cs`, public static):

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>Temporary measuring aid: how emote mods would split, written to dalamud.log after a scan.</summary>
public static class EmoteDiagnostic
{
    public static IReadOnlyList<string> Summarize(IEnumerable<OrganizerModRow> rows)
    {
        var emoteRows = rows.Where(r => r.Category == ModCategory.Animation && r.SubCategory == "Emotes").ToList();
        if (emoteRows.Count == 0)
            return [];

        int single = 0, multiple = 0, expressions = 0, undetermined = 0;
        var perName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in emoteRows)
        {
            switch (EmoteFolders.Tail(row.EmoteNames))
            {
                case EmoteFolders.Multiple: multiple++; break;
                case EmoteFolders.ExpressionsFolder: expressions++; break;
                case EmoteFolders.Undetermined: undetermined++; break;
                default: single++; break;
            }
            foreach (var name in row.EmoteNames ?? (IReadOnlySet<string>)new HashSet<string>())
                perName[name] = perName.GetValueOrDefault(name) + 1;
        }

        var lines = new List<string>
        {
            $"[emote-diag] {emoteRows.Count} emote mods: Single={single} Multiple={multiple} " +
            $"Expressions={expressions} Undetermined={undetermined}",
        };
        lines.AddRange(perName.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"[emote-diag] {p.Key} x{p.Value}"));
        return lines;
    }
}
```

`ScanJob.Publish`, after `ReplaceScanAtomically(...)` and before `RunPostScanSideEffects`, inside a try/catch that only warns:

```csharp
        try
        {
            foreach (var line in EmoteDiagnostic.Summarize(results))
                Plugin.Log.Information(line);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "emote diagnostic failed");
        }
```

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test` from the repo root. Expected: PASS (including `HelpContentTests` for the new topic).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: Sort tab checkbox for splitting emotes, help topic, and an emote measuring log

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Sort-inside folders in the policy (core, no UI)

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/ToolMode.cs` (`ToolPolicy`)
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (`ToolModeOf`, near line 656)
- Modify: `PenumbraOrganizer.Plugin/Configuration.cs` (new list, `ToolPolicy` property)
- Create: `PenumbraOrganizer.Plugin/Organizer/SortInsideFolderChoices.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/SortInsideFoldersTests.cs`

**Interfaces:**
- Consumes: `ToolFolders.AnchorOf(string)`, `ToolFolders.IsSorterFolder(string)`, `OrganizerState.SetToolPolicy(ToolPolicy)`, `OrganizerState.Sort(...)` (long overload, see `OrganizerStateToolSortTests` for how the existing SortInside tests build rows and call it).
- Produces: `ToolPolicy(ToolMode Heliosphere, ToolMode Proteus, bool ProteusManagedAtRoot, IReadOnlySet<string>? SortInsideFolders = null)`; `ToolPolicy.From(int, int, bool, IEnumerable<string>? sortInsideFolders = null)` (drops sorter-folder names and blanks; case-insensitive set); `Configuration.SortInsideFolders` (`List<string>`, default empty); `SortInsideFolderChoices.Candidates(IEnumerable<OrganizerModRow> rows, IEnumerable<string> chosen) -> IReadOnlyList<string>`.

- [ ] **Step 1: Write the failing tests**

Model them on `OrganizerStateToolSortTests` (read it first for the row builder and the exact `Sort` call it uses). Create `SortInsideFoldersTests.cs` with:

```csharp
// 1. A plain (non-tool) gear mod at ".Chars/Old Folder/My Dress" with policy SortInsideFolders = {".chars"}
//    and a type sort proposes ".Chars/Gear/My Dress" (anchor kept, case-insensitive match).
// 2. The same mod with an empty policy proposes "Gear/My Dress" (today's behaviour, unchanged).
// 3. A mod already under "Gear/..." is unaffected even if "Gear" is in the set
//    (ToolPolicy.From drops sorter-folder names, so the set is empty).
// 4. A protected row in the folder is not moved.
// 5. Two anchors with a numbered gear layout: mods in ".Chars" and "halloween+" get layouts built
//    per anchor, so a body that exists only in ".Chars" does not take a number in "halloween+"
//    (mirror OrganizerStateToolPolicyTests.SortInside_NumbersATools_BodiesSeparatelyFromThePlainFolders).
// 6. ToolPolicy.From("  ", "Review", ".Chars", ".chars") yields a set containing only ".Chars" (one entry).
// 7. SortInsideFolderChoices.Candidates: given rows at "A/x", "A/y", "B/z", "Gear/q", "root" (no slash) and
//    chosen ["B"], returns ["A"] (distinct top folders, no sorter folders, none already chosen, no root mods),
//    ordered case-insensitively.
```

Write each as a real `[Fact]` with the concrete row construction used by the neighbouring tests; do not leave the comments in.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter FullyQualifiedName~SortInsideFoldersTests`
Expected: build errors (new `ToolPolicy` member, `SortInsideFolderChoices`).

- [ ] **Step 3: Implement**

`ToolMode.cs`, replace the `ToolPolicy` record:

```csharp
/// <summary>The saved tool settings the protection rule and the sort consult.</summary>
/// <param name="SortInsideFolders">
/// Top-level folders the user keeps as the top level while their mods are sorted beneath them
/// (case-insensitive). Never contains one of the sorter's own folders.
/// </param>
public readonly record struct ToolPolicy(
    ToolMode Heliosphere, ToolMode Proteus, bool ProteusManagedAtRoot,
    IReadOnlySet<string>? SortInsideFolders = null)
{
    public static ToolPolicy Default => new(ToolMode.Protected, ToolMode.Protected, false);

    /// <summary>A stored value this build does not know reads as Protected, the safe default.</summary>
    public static ToolMode Normalize(ToolMode mode) =>
        Enum.IsDefined(mode) ? mode : ToolMode.Protected;

    public static ToolPolicy From(
        int heliosphere, int proteus, bool managedAtRoot, IEnumerable<string>? sortInsideFolders = null)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in sortInsideFolders ?? [])
        {
            if (!string.IsNullOrWhiteSpace(folder) && !ToolFolders.IsSorterFolder(folder))
                folders.Add(folder.Trim());
        }
        return new(Normalize((ToolMode)heliosphere), Normalize((ToolMode)proteus), managedAtRoot, folders);
    }
}
```

`OrganizerState.ToolModeOf`:

```csharp
    private ToolMode ToolModeOf(OrganizerModRow row) =>
        row.ProteusRole == ProteusRole.Made ? _toolPolicy.Proteus
        : row.HeliosphereManaged ? _toolPolicy.Heliosphere
        : InSortInsideFolder(row) ? ToolMode.SortInside
        : ToolMode.Normal;

    // An ordinary mod whose top folder the user marked "sort inside": it is kept under that folder
    // exactly like a Heliosphere or Proteus mod in SortInside mode.
    private bool InSortInsideFolder(OrganizerModRow row) =>
        _toolPolicy.SortInsideFolders is { Count: > 0 } folders
        && ToolFolders.AnchorOf(row.CurrentPath) is { } anchor
        && folders.Contains(anchor);
```

`Configuration.cs`:

```csharp
    /// <summary>Top-level folders kept as the top level while their mods are sorted beneath them.</summary>
    public List<string> SortInsideFolders { get; set; } = [];
```

and change `ToolPolicy` to `Organizer.ToolPolicy.From((int)HeliosphereMode, (int)ProteusMode, ProteusManagedAtRoot, SortInsideFolders)`.

`SortInsideFolderChoices.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>The top-level folders the Protect tab offers for "sort inside".</summary>
public static class SortInsideFolderChoices
{
    public static IReadOnlyList<string> Candidates(IEnumerable<OrganizerModRow> rows, IEnumerable<string> chosen)
    {
        var taken = new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase);
        return rows.Select(r => ToolFolders.AnchorOf(r.CurrentPath))
            .Where(a => a is not null && !taken.Contains(a))
            .Select(a => a!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
```

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test`. Expected: PASS; existing `ToolPolicy` call sites compile because the new parameter is optional.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: sort-inside folders in the tool policy, using the existing anchor machinery

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Protect tab control and help

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.ProtectTab.cs` (call from the same place as `DrawToolModes(allMods)`, line ~35; add `DrawSortInsideFolders`)
- Modify: `Windows/HelpTopics.cs`, `Windows/HelpTopicUsage.cs`, `Windows/HelpTab.cs`, `Resources/help-content.json`

**Interfaces:**
- Consumes: `SortInsideFolderChoices.Candidates`, `Configuration.SortInsideFolders`, `Plugin.ApplyToolPolicy()` (calls `ChangeToolPolicy(Config.ToolPolicy)` and saves), `CurrentGates().CanStageProposals`, `ActivityGateReason`, `Help.Tooltip(topic, disabledReason)`.
- Produces: help topic `protect.sort-inside-folders`.

- [ ] **Step 1: Add the control**

After `DrawToolModes(allMods);` add `DrawSortInsideFolders(allMods);` and the method (it follows the same gate and tooltip pattern as `DrawToolModes`):

```csharp
    private string _sortInsidePick = "";

    // One list of folders the user keeps as the top level: pick a folder, Add, and its mods are sorted
    // beneath it. A saved setting pushed into the state through ApplyToolPolicy, like the tool modes.
    private void DrawSortInsideFolders(IReadOnlyList<OrganizerModRow> mods)
    {
        var gateOpen = CurrentGates().CanStageProposals;
        var config = _plugin.Config;
        var candidates = SortInsideFolderChoices.Candidates(mods, config.SortInsideFolders);

        if (!ImGui.CollapsingHeader("Sort inside folders##sort-inside"))
            return;
        Help.Tooltip(HelpTopics.ProtectSortInsideFolders, gateOpen ? null : ActivityGateReason);

        ImGui.BeginDisabled(!gateOpen);
        if (candidates.Count > 0)
        {
            if (!candidates.Contains(_sortInsidePick, StringComparer.OrdinalIgnoreCase))
                _sortInsidePick = candidates[0];
            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo("##sort-inside-pick", _sortInsidePick))
            {
                foreach (var candidate in candidates)
                    if (ImGui.Selectable(candidate, string.Equals(candidate, _sortInsidePick, StringComparison.OrdinalIgnoreCase)))
                        _sortInsidePick = candidate;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Button("Add##sort-inside-add"))
            {
                config.SortInsideFolders.Add(_sortInsidePick);
                _plugin.ApplyToolPolicy();
            }
        }
        else
        {
            ImGui.TextDisabled("No other top-level folders to add.");
        }

        string? remove = null;
        foreach (var folder in config.SortInsideFolders)
        {
            ImGui.TextUnformatted(folder);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##sort-inside-{folder}"))
                remove = folder;
        }
        if (remove is not null)
        {
            config.SortInsideFolders.RemoveAll(f => string.Equals(f, remove, StringComparison.OrdinalIgnoreCase));
            _plugin.ApplyToolPolicy();
        }
        ImGui.EndDisabled();
    }
```

(Adjust only if a named member differs in this file: `CurrentGates`, `ActivityGateReason`, `Help.Tooltip` are used exactly this way by `DrawToolModes` above it.)

- [ ] **Step 2: Help topic**

Mirror `sort.split-hair-by-race` in the four help files. Topic `protect.sort-inside-folders`, short (under 200 chars): `Keeps a folder as the top level and sorts its mods beneath it, so "Characters" becomes "Characters/Gear/...". Everything inside is re-sorted; protected mods stay put.` Longer body: say what happens when the box is removed (the next sort moves the mods back to the normal folders; History undoes applied sorts) and that sorter folders such as Gear cannot be chosen.

- [ ] **Step 3: Build and run the whole suite**

Run: `dotnet build PenumbraOrganizer.Plugin -c Debug --nologo -v q` then `dotnet test`. Expected: 0 errors, all tests pass. ImGui drawing is not unit-testable here; the in-game check is in Task 5's checklist.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat: Protect tab list for folders to sort inside, with help

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Docs and checklist

**Files:**
- Modify: `docs/HOW_SORTING_WORKS.md`, `docs/USER_GUIDE.md`, `docs/ROADMAP.md`
- Modify: `docs/testing/2026-11-06-body-parts-checklist.md` (new rows)
- Create: `docs/RELEASE_NOTES_0.6.6.0.md` (draft; do not bump the version or publish)

- [ ] **Step 1: Write the docs**

- `HOW_SORTING_WORKS.md`: a section for the emote split (the four outcomes and the expression rule) and one for sort-inside folders (anchor kept, re-sorts everything, protection still applies, numbering per folder).
- `USER_GUIDE.md`: in the Sort tab section describe "Split emotes by emote" next to the other split options, and in the Protect tab section describe "Sort inside folders". Note both are off by default; templates do not follow the emote split, applying a template keeps mods inside a chosen sort-inside folder (its preview does not show the prefix yet), and the workbook ignores both. Mark the screenshots for the Sort and Protect tabs as needing a retake (do not edit images).
- Checklist rows: emote split on shows `Emotes/<name>`, `Multiple`, `Expressions`, `Undetermined` and nothing flat; unticked returns today's layout; adding `.Character specific mods` (or any folder) under Sort inside keeps it on top and sorts beneath it; a protected mod inside stays; Remove returns mods to the normal folders on the next sort; the `[emote-diag]` lines appear in `dalamud.log` after a scan.
- `ROADMAP.md`: under "Where we are", add "Built on a branch, awaiting in-game check: Sort inside folders and the emote split", and note the `[emote-diag]` log is temporary and to be removed before release.
- `RELEASE_NOTES_0.6.6.0.md`: short user-facing draft of both options.

- [ ] **Step 2: Run the whole suite and commit**

Run: `dotnet test`. Expected: PASS.

```bash
git add -A && git commit -m "docs: guide, sorting notes, checklist and draft notes for sort-inside folders and the emote split

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage.** Emote split: one/several/expressions-only/none outcomes (Task 1), exact names with `FixName` only (Task 1), checkbox in an "Animation" section saved with Sort settings (Task 2), LayoutOptions flag (Task 1), measuring log before shipping (Task 2). Sort inside: saved folder set (Task 3), anchor generalisation reusing `AnchorRootOf`/`ToolModeOf` and per-anchor numbering (Task 3), sorter-folder names refused (Task 3 `From`), protection unchanged (Task 3 test 4), control on the Protect tab (Task 4). Out of scope items stay out. One deliberate change from the spec text: the Protect tab control is a small list (Task 4) rather than a per-row control on the folder tree, which avoids touching the tree's drawing code; protection still wins because protected rows are never re-pathed.

**Placeholder scan.** Task 3's test list is seven concrete cases to be written against the neighbouring tests' row builder rather than pasted, because that builder lives in `OrganizerStateToolSortTests` and must be read first; each case states exact inputs and the expected path.

**Type consistency.** `LayoutOptions.SplitEmotes`, `OrganizerModRow.EmoteNames`, `EmoteFolders.Tail/Expressions/Multiple/ExpressionsFolder/Undetermined`, `ToolPolicy.SortInsideFolders`, `ToolPolicy.From(..., IEnumerable<string>?)`, `Configuration.SortInsideFolders`/`SortSplitEmotes`, `SortInsideFolderChoices.Candidates`, `EmoteDiagnostic.Summarize` are named identically wherever used.
