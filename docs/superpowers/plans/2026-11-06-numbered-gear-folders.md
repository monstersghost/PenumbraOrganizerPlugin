# Numbered Gear Folders and Padded Hair Numbers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** With both gear splits on, lay `Gear/` out as `00. Sets`, `01.`-`09.` slot folders, then `10.`+ body folders (by Body priority) each holding the same numbered slot folders; pad hair folders to three digits.

**Architecture:** A new pure `GearFolderLayout` owns the numbering and the `Gear/...` paths. `SortFolderSelectors.Select` takes an optional layout and uses it for Gear rows (applying the template `renameFolder` after the numbered path is built). `OrganizerState.Sort` and `TemplatePlanner` build the layout from the rows that will actually be placed by the gear rule. Hair padding is one change in `ModTypeFolders.GetFolder`.

**Tech Stack:** C# / .NET 10, xUnit. Spec: `docs/superpowers/specs/2026-11-05-numbered-gear-folders-design.md`.

## Global Constraints

- Number format is `NN. Name` (two digits, dot, space): `01. Head`, `10. Rue+`, `00. Sets`.
- Slot order and numbers: Head 01, Top 02, Hands 03, Legs 04, Feet 05, Ears 06, Neck 07, Wrists 08, Rings 09. Unresolved or multi-slot gear: `00. Sets`.
- Bodies are numbered from 10, by `BodyPriority.Rank` then ordinal label, only for bodies that at least one placed gear mod resolves to; no gaps. With no `BodyPriority`, ordinal label order.
- Applies ONLY when Category is Gear and BOTH the slot split and the gear body split are on. One flag alone, or any other category, is unchanged.
- Hair: `Hair/{n}` padded to three digits below 1000 (`002`, `115`); 1000 and above unpadded (accepted limitation). `SubCategory` stays a plain integer string.
- The template `renameFolder` is applied to the whole numbered path after it is built.
- Template numbering uses fallback-eligible rows only (unprotected rows no template entry matched).
- No `InternalsVisibleTo`: anything tests touch must be `public`.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Run from `C:\Repo\PenumbraOrganizer.Plugin`. Test command: `dotnet test PenumbraOrganizer.Plugin.Tests -v q`.

---

### Task 1: Pad hair folder numbers to three digits

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs` (the Hair arm of `GetFolder`, and the `IsHairNumber` comment)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs`, `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortHairTests.cs`

**Interfaces:**
- Consumes: `ModTypeFolders.GetFolder(ModCategory, string?)`.
- Produces: `GetFolder(Hair, "2")` returns `"Hair/002"`.

- [ ] **Step 1: Write the failing tests.** In `ModTypeClassifierTests.cs`, replace the body of `GetFolder_HairWithANumber_NestsUnderHair` (rename it) and the assertion near line 618, and add a limitation test:

```csharp
    [Fact]
    public void GetFolder_HairWithANumber_NestsUnderHairPaddedToThreeDigits()
    {
        Assert.Equal("Hair/115", ModTypeFolders.GetFolder(ModCategory.Hair, "115"));
        Assert.Equal("Hair/009", ModTypeFolders.GetFolder(ModCategory.Hair, "9"));
        Assert.Equal("Hair/002", ModTypeFolders.GetFolder(ModCategory.Hair, "2"));
        Assert.Equal("Hair/053", ModTypeFolders.GetFolder(ModCategory.Hair, "53"));
        Assert.Equal("Hair/999", ModTypeFolders.GetFolder(ModCategory.Hair, "999"));
    }

    [Fact]
    public void GetFolder_HairNumberOfAThousandOrMore_IsWrittenUnpadded_AcceptedLimitation()
    {
        // The hair reader accepts 1..9999. Three digits is the approved format, so 1000 and above
        // are written as is and would sort before 185 as text. Pinned so the limit is visible; the
        // fix, if a real number that large turns up, is padding to four digits.
        Assert.Equal("Hair/1000", ModTypeFolders.GetFolder(ModCategory.Hair, "1000"));
        Assert.Equal("Hair/9999", ModTypeFolders.GetFolder(ModCategory.Hair, "9999"));
    }
```
Change the assertion near line 618 from `"Hair/9"` to `"Hair/009"`. In `OrganizerStateSortHairTests.cs` change `"Hair/3/Hair Mod"` to `"Hair/003/Hair Mod"` (line ~166). Leave the `Hair/115` assertions as they are.

- [ ] **Step 2: Run to verify they fail.** `dotnet test PenumbraOrganizer.Plugin.Tests --filter "ModTypeClassifierTests|OrganizerStateSortHairTests" -v q` - Expected: FAIL (`Hair/9` vs `Hair/009`, `Hair/3` vs `Hair/003`).

- [ ] **Step 3: Implement.** In `ModTypeFolders.GetFolder`, change the hair arm to:

```csharp
        (ModCategory.Hair, _) when IsHairNumber(subCategory) => $"{ModCategory.Hair}/{subCategory!.PadLeft(3, '0')}",
```
Add to the comment above `IsHairNumber`: `// The input is the plain number; GetFolder pads it to three digits when it builds the folder.`

- [ ] **Step 4: Run the full suite.** `dotnet test PenumbraOrganizer.Plugin.Tests -v q` - Expected: PASS. If another test asserts an unpadded hair folder, update it to the padded form.

- [ ] **Step 5: Commit.** `git add <only the files this task touched> && git commit -m "feat: hair folders are padded to three digits" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"`

---

### Task 2: GearFolderLayout (pure numbering and paths)

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/GearFolderLayout.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/GearFolderLayoutTests.cs`

**Interfaces:**
- Consumes: `BodyPriority.Rank(string)` and `BodyPriority.Normalize(IEnumerable<string>?, IEnumerable<string>)` in `PenumbraOrganizer.Plugin.Organizer.BodyTypes`; `ModTypeFolders.GetFolder(ModCategory, string?, string?)` for validation.
- Produces: `GearFolderLayout.Build(IEnumerable<string?> resolvedBodies, BodyPriority? priority)`, `.BodyNumbers`, `GearFolderLayout.SlotFolder(string? slot)` (static), `.Folder(string? slot, string? body)`.

- [ ] **Step 1: Write the failing tests.**

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class GearFolderLayoutTests
{
    private static readonly string[] Bodies = ["Rue+", "Bibo+", "YAB", "TBSE"];

    private static BodyPriority Order(params string[] first) => BodyPriority.Normalize(first, Bodies);

    [Fact]
    public void Bodies_AreNumberedFromTenByPriority_OnlyThoseWithGear()
    {
        var layout = GearFolderLayout.Build(["Bibo+", "Rue+", "Bibo+", null], Order("Rue+", "Bibo+"));

        Assert.Equal(10, layout.BodyNumbers["Rue+"]);
        Assert.Equal(11, layout.BodyNumbers["Bibo+"]);
        Assert.False(layout.BodyNumbers.ContainsKey("YAB")); // no gear, no number, no gap
        Assert.Equal(2, layout.BodyNumbers.Count);
    }

    [Fact]
    public void AbsentBodiesDoNotLeaveGaps()
    {
        var layout = GearFolderLayout.Build(["TBSE", "Rue+"], Order("Rue+", "Bibo+", "YAB", "TBSE"));

        Assert.Equal(10, layout.BodyNumbers["Rue+"]);
        Assert.Equal(11, layout.BodyNumbers["TBSE"]);
    }

    [Fact]
    public void NoPriority_NumbersInOrdinalLabelOrder()
    {
        var layout = GearFolderLayout.Build(["Rue+", "Bibo+"], priority: null);

        Assert.Equal(10, layout.BodyNumbers["Bibo+"]);
        Assert.Equal(11, layout.BodyNumbers["Rue+"]);
    }

    [Theory]
    [InlineData("Head", "Gear/01. Head")]
    [InlineData("Top", "Gear/02. Top")]
    [InlineData("Hands", "Gear/03. Hands")]
    [InlineData("Legs", "Gear/04. Legs")]
    [InlineData("Feet", "Gear/05. Feet")]
    [InlineData("Ears", "Gear/06. Ears")]
    [InlineData("Neck", "Gear/07. Neck")]
    [InlineData("Wrists", "Gear/08. Wrists")]
    [InlineData("Rings", "Gear/09. Rings")]
    [InlineData(null, "Gear/00. Sets")]
    public void NoBody_SlotFoldersSitDirectlyUnderGear(string? slot, string expected)
    {
        var layout = GearFolderLayout.Build([], null);

        Assert.Equal(expected, layout.Folder(slot, body: null));
    }

    [Fact]
    public void WithBody_TheSameSlotNumbersSitInsideTheBodyFolder()
    {
        var layout = GearFolderLayout.Build(["Rue+"], Order("Rue+"));

        Assert.Equal("Gear/10. Rue+/02. Top", layout.Folder("Top", "Rue+"));
        Assert.Equal("Gear/10. Rue+/00. Sets", layout.Folder(null, "Rue+"));
    }

    [Fact]
    public void ABodyTheLayoutWasNotBuiltFrom_IsABugAndThrows()
    {
        var layout = GearFolderLayout.Build(["Rue+"], Order("Rue+"));

        Assert.Throws<InvalidOperationException>(() => layout.Folder("Top", "Bibo+"));
    }

    [Fact]
    public void ANonsenseSlot_StillFailsFast()
    {
        var layout = GearFolderLayout.Build([], null);

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Folder("Bosses", null));
    }
}
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test PenumbraOrganizer.Plugin.Tests --filter GearFolderLayoutTests -v q` - Expected: build error (type missing).

- [ ] **Step 3: Implement.** Create `PenumbraOrganizer.Plugin/Organizer/GearFolderLayout.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The numbered layout of <c>Gear/</c> when both the slot split and the body split are on:
/// <c>00. Sets</c>, <c>01.</c>-<c>09.</c> slot folders, then <c>10.</c> and up for the bodies that
/// have gear, ordered by the user's Body priority, each holding the same numbered slot folders.
/// </summary>
/// <remarks>
/// Pure and built once per sort from the rows that will be placed by the gear rule, so the numbers
/// are contiguous for exactly those rows. Public because the test project has no
/// <c>InternalsVisibleTo</c>.
/// </remarks>
public sealed class GearFolderLayout
{
    public const int FirstBodyNumber = 10;

    // Order and numbers match ModTypeFolders' enumerated gear slots.
    private static readonly string[] SlotOrder =
        ["Head", "Top", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "Rings"];

    private const string SetsFolder = "00. Sets";

    private GearFolderLayout(IReadOnlyDictionary<string, int> bodyNumbers) => BodyNumbers = bodyNumbers;

    /// <summary>Body label to its folder number, for the bodies the layout was built from.</summary>
    public IReadOnlyDictionary<string, int> BodyNumbers { get; }

    /// <param name="resolvedBodies">The body each placed gear mod resolves to; null for none.</param>
    /// <param name="priority">Null orders bodies by label alone.</param>
    public static GearFolderLayout Build(IEnumerable<string?> resolvedBodies, BodyPriority? priority)
    {
        var ordered = resolvedBodies
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(label => priority?.Rank(label) ?? int.MaxValue)
            .ThenBy(label => label, StringComparer.Ordinal)
            .ToList();

        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
            numbers[ordered[i]] = FirstBodyNumber + i;

        return new GearFolderLayout(numbers);
    }

    /// <summary>The slot's folder name: <c>02. Top</c>, or <c>00. Sets</c> when no single slot resolved.</summary>
    public static string SlotFolder(string? slot)
    {
        if (slot is null)
            return SetsFolder;

        var index = Array.IndexOf(SlotOrder, slot);
        return index >= 0
            ? $"{index + 1:D2}. {slot}"
            : throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Unsupported gear slot '{slot}'.");
    }

    /// <summary>The full <c>Gear/...</c> folder for a gear mod's slot and (optional) body.</summary>
    public string Folder(string? slot, string? body)
    {
        // Reuses GetFolder's validation of the slot and the body label, so a malformed value fails
        // here exactly as it would have without numbering.
        _ = ModTypeFolders.GetFolder(ModCategory.Gear, slot, body);

        var slotFolder = SlotFolder(slot);
        if (body is null)
            return $"Gear/{slotFolder}";

        if (!BodyNumbers.TryGetValue(body, out var number))
            throw new InvalidOperationException(
                $"Body '{body}' was not part of the rows this layout was built from.");

        return $"Gear/{number:D2}. {body}/{slotFolder}";
    }
}
```
Note: `GetFolder(Gear, null, body)` with a null slot is valid (`(_, null) => category`).

- [ ] **Step 4: Run.** `dotnet test PenumbraOrganizer.Plugin.Tests --filter GearFolderLayoutTests -v q` - Expected: PASS.

- [ ] **Step 5: Commit.** `git add <only the files this task touched> && git commit -m "feat: GearFolderLayout numbers the gear slot and body folders" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"`

---

### Task 3: Use the layout in the sort

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs` (add `BuildGearLayout`, a `gearLayout` parameter on `Select`)
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (the `Sort` overload that calls `SortFolderSelectors.Select`, around line 600)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortBodyTests.cs`, `PenumbraOrganizer.Plugin.Tests/Organizer/SortFolderSelectorsGearLayoutTests.cs` (create)

**Interfaces:**
- Consumes: `GearFolderLayout` (Task 2); the private `ResolveBody`/`BodyFor` in `SortFolderSelectors`.
- Produces: `SortFolderSelectors.BuildGearLayout(IEnumerable<OrganizerModRow> rows, bool splitGear, bool splitGearBody, BodyPriority? priority)` returning `GearFolderLayout?` (null unless both flags); `Select(..., GearFolderLayout? gearLayout = null)` as the LAST parameter.

- [ ] **Step 1: Write the failing tests.** Create `SortFolderSelectorsGearLayoutTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Templates;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class SortFolderSelectorsGearLayoutTests
{
    private static OrganizerModRow Gear(string id, string? slot, string? body) => new()
    {
        Identifier = id, Name = id, Author = "A", CurrentPath = id, ProposedPath = id,
        Category = ModCategory.Gear, SubCategory = slot, BodyType = body,
    };

    [Fact]
    public void BuildGearLayout_IsNullUnlessBothFlagsAreOn()
    {
        var rows = new[] { Gear("g", "Top", "Rue+") };

        Assert.Null(SortFolderSelectors.BuildGearLayout(rows, splitGear: true, splitGearBody: false, null));
        Assert.Null(SortFolderSelectors.BuildGearLayout(rows, splitGear: false, splitGearBody: true, null));
        Assert.NotNull(SortFolderSelectors.BuildGearLayout(rows, splitGear: true, splitGearBody: true, null));
    }

    [Fact]
    public void Select_WithALayout_UsesTheNumberedFolder()
    {
        var row = Gear("Dress", "Top", "Rue+");
        var layout = SortFolderSelectors.BuildGearLayout([row], true, true, null)!;

        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, true, true, row, gearLayout: layout, splitGearBody: true);

        Assert.Equal("Gear/10. Rue+/02. Top", primary);
    }

    [Fact]
    public void Select_TemplateRename_AppliesToTheNumberedPath()
    {
        var row = Gear("Dress", "Top", "Rue+");
        var layout = SortFolderSelectors.BuildGearLayout([row], true, true, null)!;
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Gear"] = "Equipment" });

        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, true, true, row, renameFolder: rename, gearLayout: layout, splitGearBody: true);

        Assert.Equal("Equipment/10. Rue+/02. Top", primary);
    }

    [Fact]
    public void Select_NonGearRows_IgnoreTheLayout()
    {
        var npc = new OrganizerModRow
        {
            Identifier = "n", Name = "n", Author = "A", CurrentPath = "n", ProposedPath = "n",
            Category = ModCategory.NPC, SubCategory = "Bosses",
        };
        var layout = GearFolderLayout.Build([], null);

        var (primary, _) = SortFolderSelectors.Select(SortStrategy.TypeOnly, true, true, npc, gearLayout: layout);

        Assert.Equal("NPC/Bosses", primary);
    }
}
```
Append to `OrganizerStateSortBodyTests.cs` (it already has `Canon`, `PathOf`, `Bodies`):

```csharp
    private static OrganizerState WithGearLibrary()
    {
        OrganizerModRow G(string id, string? slot, string? body) => new()
        {
            Identifier = id, Name = id, Author = "A", CurrentPath = id, ProposedPath = id,
            Category = ModCategory.Gear, SubCategory = slot, BodyType = body,
        };
        var state = new OrganizerState();
        state.LoadScan(
        [
            G("rue-top", "Top", "Rue+"), G("bibo-set", null, "Bibo+"), G("head", "Head", null),
            G("set", null, null), G("rue-set", null, "Rue+"),
        ], new HashSet<string>());
        return state;
    }

    [Fact]
    public void Sort_BothGearSplitsOn_LaysGearOutNumbered()
    {
        var state = WithGearLibrary();

        state.Sort(SortStrategy.TypeOnly, true, true, false, Canon, splitGearBody: true, splitBodyMods: false,
            bodyPriority: BodyPriority.Normalize(["Rue+"], Bodies));

        Assert.Equal("Gear/10. Rue+/02. Top/rue-top", PathOf(state, "rue-top"));
        Assert.Equal("Gear/10. Rue+/00. Sets/rue-set", PathOf(state, "rue-set"));
        Assert.Equal("Gear/11. Bibo+/00. Sets/bibo-set", PathOf(state, "bibo-set"));
        Assert.Equal("Gear/01. Head/head", PathOf(state, "head"));
        Assert.Equal("Gear/00. Sets/set", PathOf(state, "set"));
    }

    [Fact]
    public void Sort_BothGearSplitsOn_NullPriority_IsStillNumbered()
    {
        var state = WithGearLibrary();

        state.Sort(SortStrategy.TypeOnly, true, true, false, Canon, splitGearBody: true, splitBodyMods: false);

        Assert.Equal("Gear/10. Bibo+/00. Sets/bibo-set", PathOf(state, "bibo-set"));
        Assert.Equal("Gear/11. Rue+/02. Top/rue-top", PathOf(state, "rue-top"));
    }

    [Fact]
    public void Sort_OnlyTheSlotSplit_IsUnchanged()
    {
        var state = WithGearLibrary();

        state.Sort(SortStrategy.TypeOnly, true, true, false, Canon, splitGearBody: false, splitBodyMods: false);

        Assert.Equal("Gear/Top/rue-top", PathOf(state, "rue-top"));
        Assert.Equal("Gear/Head/head", PathOf(state, "head"));
        Assert.Equal("Gear/set", PathOf(state, "set"));
    }

    [Fact]
    public void Sort_OnlyTheBodySplit_IsUnchanged()
    {
        var state = WithGearLibrary();

        state.Sort(SortStrategy.TypeOnly, false, true, false, Canon, splitGearBody: true, splitBodyMods: false,
            bodyPriority: BodyPriority.Normalize(["Rue+"], Bodies));

        Assert.Equal("Gear/Rue+/rue-top", PathOf(state, "rue-top"));
        Assert.Equal("Gear/head", PathOf(state, "head"));
    }
```
(`Sort`'s parameter order is `(strategy, splitGear, splitNpc, splitHair, canonicalizeCreator, ..., splitGearBody, splitBodyMods, bodyPriority)`; the existing tests in that file show the exact call shape.)

- [ ] **Step 2: Run to verify they fail.** `dotnet test PenumbraOrganizer.Plugin.Tests --filter "SortFolderSelectorsGearLayoutTests|OrganizerStateSortBodyTests" -v q` - Expected: build error (`BuildGearLayout`, `gearLayout` missing).

- [ ] **Step 3: Implement `SortFolderSelectors`.** Change the `Select` signature to end with `BodyPriority? bodyPriority = null, GearFolderLayout? gearLayout = null)`. In the local `Type()`, as its FIRST statements, add:

```csharp
            // The numbered gear layout owns Gear rows entirely. The template rename still applies,
            // to the whole numbered path, exactly where TypeFolder applies it.
            if (gearLayout is not null && row.Category == ModCategory.Gear)
            {
                var gearFolder = gearLayout.Folder(
                    row.SubCategory, BodyFor(row, splitGearBody, splitBodyMods, bodyPriority));
                return renameFolder is null ? gearFolder : renameFolder(gearFolder);
            }
```
Add the builder next to `BodyFor`:

```csharp
    /// <summary>
    /// The numbered Gear layout for the rows that are about to be placed by the gear rule, or null
    /// unless BOTH the slot split and the gear body split are on (any other combination keeps
    /// today's layout exactly).
    /// </summary>
    public static GearFolderLayout? BuildGearLayout(
        IEnumerable<OrganizerModRow> rows, bool splitGear, bool splitGearBody, BodyPriority? priority) =>
        splitGear && splitGearBody
            ? GearFolderLayout.Build(
                rows.Where(r => r.Category == ModCategory.Gear).Select(r => ResolveBody(r, priority)), priority)
            : null;
```

- [ ] **Step 4: Implement `OrganizerState.Sort`.** In the overload that ends with `return Sort(row => SortFolderSelectors.Select(strategy, splitGear, splitNpc, row, canonicalizeCreator, ...))`, build the layout just before that `return` and pass it:

```csharp
        var gearLayout = SortFolderSelectors.BuildGearLayout(
            _mods.Values.Where(m => !m.Protected), splitGear, splitGearBody, bodyPriority);
```
and add `gearLayout: gearLayout` to the `Select` call.

- [ ] **Step 5: Run the full suite.** `dotnet test PenumbraOrganizer.Plugin.Tests -v q` - Expected: PASS. Fix any existing test that asserted the old layout with BOTH gear flags on (update it to the numbered form).

- [ ] **Step 6: Commit.** `git add <only the files this task touched> && git commit -m "feat: sorting uses the numbered gear layout when both gear splits are on" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"`

---

### Task 4: Template fallback uses the layout, from fallback rows only

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplatePlanner.cs` (`Plan`)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplatePlannerTests.cs`

**Interfaces:**
- Consumes: `SortFolderSelectors.BuildGearLayout` and `Select(..., gearLayout:)` (Task 3); `ModNameNormalizer.Normalize`; `template.EntriesByNormalizedName`; the existing `bodyPriority` parameter of `Plan`.

- [ ] **Step 1: Write the failing tests** (append to `TemplatePlannerTests`, adding `using PenumbraOrganizer.Plugin.Organizer.BodyTypes;` at the top; its `Row(...)` helper already takes `bodyType`, and `Template(...)` takes no splits, so build the template directly):

```csharp
    private static ValidatedOrganizationTemplate GearBothSplits(Dictionary<string, string>? entries = null,
        Dictionary<string, string>? labels = null) => new(
            "T", "A", null,
            new TemplateFallback(SortStrategy.TypeOnly, SplitGear: true, SplitNpc: true, SplitGearBody: true),
            labels ?? new Dictionary<string, string>(), [], entries ?? new Dictionary<string, string>());

    [Fact]
    public void Plan_FallbackBothGearSplits_UsesTheNumberedLayout()
    {
        var gear = Row("g1", "Cool Dress", ModCategory.Gear, "Top", bodyType: "Rue+");

        var plan = TemplatePlanner.Plan(GearBothSplits(), [gear], Same);

        Assert.Equal("Gear/10. Rue+/02. Top", plan.DestinationFolders["g1"]);
    }

    [Fact]
    public void Plan_FallbackNumbers_IgnoreBodiesThatOnlyExplicitEntriesPlace()
    {
        // The Rue+ mod is placed by an explicit entry, so it never reaches the fallback. With Rue+
        // ranked FIRST, counting its row wrongly would hand Rue+ the number 10 and push the
        // fallback's Bibo+ to 11 (a gap). Without a priority Bibo+ would sort first anyway and the
        // test could not fail, so the priority is supplied on purpose.
        var explicitRow = Row("e1", "Explicit Dress", ModCategory.Gear, "Top", bodyType: "Rue+");
        var fallbackRow = Row("f1", "Fallback Dress", ModCategory.Gear, "Top", bodyType: "Bibo+");
        var template = GearBothSplits(entries: new() { ["explicit dress"] = "Chosen" });
        var rueFirst = BodyPriority.Normalize(["Rue+"], ["Rue+", "Bibo+"]);

        var plan = TemplatePlanner.Plan(template, [explicitRow, fallbackRow], Same, bodyPriority: rueFirst);

        Assert.Equal("Chosen", plan.DestinationFolders["e1"]);
        Assert.Equal("Gear/10. Bibo+/02. Top", plan.DestinationFolders["f1"]);
    }

    [Fact]
    public void Plan_FallbackNumbers_ApplyTheTemplateRenameToTheNumberedPath()
    {
        var gear = Row("g1", "Cool Dress", ModCategory.Gear, "Top", bodyType: "Rue+");

        var plan = TemplatePlanner.Plan(
            GearBothSplits(labels: new() { ["Gear"] = "Equipment" }), [gear], Same);

        Assert.Equal("Equipment/10. Rue+/02. Top", plan.DestinationFolders["g1"]);
    }
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test PenumbraOrganizer.Plugin.Tests --filter TemplatePlannerTests -v q` - Expected: FAIL (old layout `Gear/Rue+/Top`).

- [ ] **Step 3: Implement.** In `TemplatePlanner.Plan`, before the `foreach (var row in rows)` loop add:

```csharp
        // Numbering for the numbered gear layout comes from the rows the FALLBACK will place: an
        // explicit template entry bypasses the fallback, so counting its rows would let a body that
        // only explicit entries use take a number and leave gaps in the fallback's.
        var fallbackEligibleRows = rows.Where(r =>
        {
            if (r.Protected)
                return false;
            var key = ModNameNormalizer.Normalize(r.Name);
            return !(key.Length > 0 && template.EntriesByNormalizedName.ContainsKey(key));
        });
        var gearLayout = SortFolderSelectors.BuildGearLayout(
            fallbackEligibleRows, template.Fallback.SplitGear, template.Fallback.SplitGearBody, bodyPriority);
```
and pass `gearLayout: gearLayout` in the `SortFolderSelectors.Select(...)` call inside the loop (with the existing named arguments).

- [ ] **Step 4: Run the full suite.** `dotnet test PenumbraOrganizer.Plugin.Tests -v q` - Expected: PASS.

- [ ] **Step 5: Commit.** `git add <only the files this task touched> && git commit -m "feat: template fallback uses the numbered gear layout, numbered from fallback rows only" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"`

---

### Task 5: Help text, docs and the in-game checklist

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Resources/help-content.json` (the topics for the gear body split and the hair split; find them with `grep -n "split" PenumbraOrganizer.Plugin/Resources/help-content.json`)
- Modify: `docs/HOW_SORTING_WORKS.md`, `docs/USER_GUIDE.md` (the Sort section's splits), `docs/ROADMAP.md`
- Create: `docs/testing/2026-11-06-numbered-gear-checklist.md`

- [ ] **Step 1: Help text.** Update the `short` of the gear-body-split topic to say that with the slot split also on, gear is laid out as `00. Sets`, `01.`-`09.` slots, then numbered bodies; update the hair topic to say folders are three digits (`Hair/002`). Keep every `short` at most 200 characters with no newline (a test enforces it).

- [ ] **Step 2: Docs and migration notes.** The user-facing documentation MUST carry the migration effects, not only the layout. Create `docs/RELEASE_NOTES_UNRELEASED.md` (renamed to the version at release time) with a "What moves and what stops matching" section stating: (a) the first sort after updating moves gear and hair folders once, and History restores the old layout; (b) template `folderLabels` keyed to the old subfolders (`Gear/Legs`, `Gear/Bibo+`, `Hair/2`) stop matching, while a key on `Gear` itself still renames the root; (c) a hair mod protected at `Hair/2` stays there beside the new `Hair/002`; (d) folder protection rules naming the old gear folders no longer match the renamed ones; (e) workbooks exported earlier use the old names; (f) reordering Body priority, or gaining or losing gear for a body, renumbers the body folders and moves those mods once; (g) hair numbers of 1000 or more are not padded (accepted limitation). Repeat (a)-(d) in the user guide's Sort section. In `HOW_SORTING_WORKS.md` add a section "Numbered gear folders" with the layout diagram from the spec and the rule (both gear splits on; numbers by Body priority; `00. Sets`). In `USER_GUIDE.md` update the splits text: the layout, that reordering Body priority renumbers body folders, hair three-digit padding, and the one-time move with History restore. In `ROADMAP.md` add the change to the unreleased list.

- [ ] **Step 3: Checklist.** Create `docs/testing/2026-11-06-numbered-gear-checklist.md`:

```markdown
# Manual check: numbered gear folders and padded hair

Load the build, scan a real library, then check each item.

- [ ] Both gear splits on, Group by Mod type: Sort. Review Changes shows `Gear/00. Sets`, `01.`-`09.`
      slot folders and `10.`+ body folders; nothing loose directly in `Gear/`.
- [ ] Your first-choice body (Rue+) is `10.`; a body with no gear has no folder and no gap.
- [ ] Inside a body folder the slot numbers match the top level (`02. Top`).
- [ ] Reorder Body priority, Sort again: body numbers follow the new order.
- [ ] Only the slot split, or only the body split: layout is as before, no numbers.
- [ ] Hair split on: `Hair/002`, `Hair/053`, `Hair/185` list in numeric order in Penumbra.
- [ ] Apply works (no `PathRenameFailed`); Penumbra shows `01. Head`-style names exactly as written.
- [ ] A template with both gear splits ticked previews and applies with numbered gear; a template
      with `Gear` renamed to `Equipment` renames the root.
- [ ] History: Restore puts the old layout back.
- [ ] Verify: no vanilla hair number is 1000 or more.
```

- [ ] **Step 4: Run the suite and rebuild the PDF.** `dotnet test PenumbraOrganizer.Plugin.Tests -v q` (PASS), then `python docs/build-pdf.py`.

- [ ] **Step 5: Commit.** `git add <only the files this task touched> && git commit -m "docs: numbered gear folders in the help text, guide, sorting reference, roadmap and checklist" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"`
