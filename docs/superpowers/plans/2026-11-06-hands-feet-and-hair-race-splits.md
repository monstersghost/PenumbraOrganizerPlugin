# Hands and Feet Sharing and Hair Race Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two Sort tab checkboxes: Gear "Hands and feet in a shared folder" and Hair "Split hair by race", both off by default so output equals 0.6.4.0.

**Architecture:** One small value type, `LayoutOptions(SharedHandsAndFeet, SplitHairByRace)`, flows from the Sort tab through `OrganizerState.Sort` into `SortFolderSelectors.Select` and `BuildGearLayout`. The hair rule lives in a new pure `HairRaceFolders` class. The template planner passes no options, so templates keep today's layout.

**Tech Stack:** C#/.NET 10, ImGui via `Dalamud.Bindings.ImGui`, xUnit. No `InternalsVisibleTo`: everything tests touch is `public`.

## Global Constraints

- The user asked for exactly two extra checkboxes: no presets, no Advanced section, no other options.
- Both options default to off and then reproduce 0.6.4.0 output exactly: the existing 1993 tests must pass unchanged.
- Hands and feet sharing applies only when the gear slot split is on. With the slot split off it changes nothing.
- A body that only has hands or feet mods takes no body number while sharing is on. The check "does this row get a body folder" must be ONE function used both when building the layout and when looking a body up (`GearFolderLayout.Folder` and `BodyFolderLayout.Tail` throw when they disagree).
- Hair race folder names are Penumbra's race names from `HairRaceCodes` (Midlander, Highlander, Elezen, Miqo'te, Roegadyn, Lalafell, Au Ra, Hrothgar, Viera). The multi-race folder is exactly `Multi-race`. Numbers are padded to three digits.
- Hair rules: one race (either gender) with exactly one hair number -> `Hair/<race>/<NNN>`; one race with several numbers -> `Hair/<race>`; several races or no race evidence -> `Hair/Multi-race` or `Hair/Multi-race/<NNN>` using the existing priority-race and Midlander rule. Priority race and Midlander never apply to single-race mods. With the hair number split off, only the race folder is used.
- The template planner is not given the options; a template applies today's layout for these two splits.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Nothing is pushed or released by this plan.
- Run tests from `C:\Repo\PenumbraOrganizer.Plugin` with `dotnet test --nologo -v q`.

---

### Task 1: LayoutOptions and shared hands and feet

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/LayoutOptions.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/SharedHandsAndFeetTests.cs`

**Interfaces:**
- Produces: `public readonly record struct LayoutOptions(bool SharedHandsAndFeet = false, bool SplitHairByRace = false)`; `SortFolderSelectors.Select(..., BodyFolderLayout? bodyLayout = null, LayoutOptions? options = null)`; `SortFolderSelectors.BuildGearLayout(rows, splitGear, splitGearBody, priority, LayoutOptions? options = null)`.

- [ ] **Step 1: Write the failing test**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class SharedHandsAndFeetTests
{
    private static OrganizerModRow Gear(string id, string slot, string body) => new()
    {
        Identifier = id, Name = id, Author = "A", CurrentPath = id, ProposedPath = id,
        Category = ModCategory.Gear, SubCategory = slot, BodyType = body,
    };

    private static string Folder(OrganizerModRow row, IEnumerable<OrganizerModRow> all,
        LayoutOptions options, bool splitGear = true, bool numbered = true)
    {
        var layout = numbered ? SortFolderSelectors.BuildGearLayout(all, splitGear, true, null, options) : null;
        return SortFolderSelectors.Select(SortStrategy.TypeOnly, splitGear, false, row,
            splitGearBody: true, gearLayout: layout, options: options).Primary!;
    }

    private static readonly OrganizerModRow[] Rows =
    [
        Gear("hands", "Hands", "Rue+"), Gear("top", "Top", "Rue+"), Gear("feet", "Feet", "YAB"),
    ];

    [Fact]
    public void Default_KeepsHandsAndFeetUnderTheirBody()
    {
        var o = new LayoutOptions();
        Assert.Equal("Gear/10. Rue+/03. Hands", Folder(Rows[0], Rows, o));
        Assert.Equal("Gear/11. YAB/05. Feet", Folder(Rows[2], Rows, o));
    }

    [Fact]
    public void Shared_PutsHandsAndFeetInThePlainSlotFolders()
    {
        var o = new LayoutOptions(SharedHandsAndFeet: true);
        Assert.Equal("Gear/03. Hands", Folder(Rows[0], Rows, o));
        Assert.Equal("Gear/05. Feet", Folder(Rows[2], Rows, o));
        Assert.Equal("Gear/10. Rue+/02. Top", Folder(Rows[1], Rows, o));
    }

    [Fact]
    public void Shared_ABodyWithOnlyHandsOrFeetTakesNoNumber()
    {
        var layout = SortFolderSelectors.BuildGearLayout(
            Rows, true, true, null, new LayoutOptions(SharedHandsAndFeet: true))!;
        Assert.True(layout.BodyNumbers.ContainsKey("Rue+"));
        Assert.False(layout.BodyNumbers.ContainsKey("YAB"));
    }

    [Fact]
    public void Shared_UnnumberedLayout_AlsoDropsTheBody()
    {
        var o = new LayoutOptions(SharedHandsAndFeet: true);
        Assert.Equal("Gear/Hands", Folder(Rows[0], Rows, o, numbered: false));
        Assert.Equal("Gear/Rue+/Top", Folder(Rows[1], Rows, o, numbered: false));
    }

    [Fact]
    public void Shared_DoesNothingWhileTheSlotSplitIsOff()
    {
        var o = new LayoutOptions(SharedHandsAndFeet: true);
        Assert.Equal("Gear/Rue+", Folder(Rows[0], Rows, o, splitGear: false, numbered: false));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --nologo -v q --filter SharedHandsAndFeetTests`
Expected: FAIL to compile (`LayoutOptions` and the `options` parameters do not exist).

- [ ] **Step 3: Implement**

Create `PenumbraOrganizer.Plugin/Organizer/LayoutOptions.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The two optional layout splits on the Sort tab. Both default to off, which is the 0.6.4.0 layout.
/// A null <c>LayoutOptions?</c> anywhere means both off.
/// </summary>
public readonly record struct LayoutOptions(bool SharedHandsAndFeet = false, bool SplitHairByRace = false);
```

In `SortFolderSelectors.cs`:
1. Add `LayoutOptions? options = null` as the last parameter of `Select`, and at its top `var opts = options ?? default;`.
2. In `Type()`, numbered gear block, replace `BodyFor(row, splitGearBody, splitBodyMods, bodyPriority)` with `GearBodyFor(row, splitGear, splitGearBody, splitBodyMods, bodyPriority, opts)`.
3. In the legacy path replace `var body = BodyFor(row, splitGearBody, splitBodyMods, bodyPriority);` with `var body = IsSharedSlot(row, splitGear, opts) ? null : BodyFor(row, splitGearBody, splitBodyMods, bodyPriority);`.
4. Add the helpers and extend `BuildGearLayout`:

```csharp
    // The ONE answer to "does this gear row go under a body folder", used by the layout build and
    // by the lookup so the two can never disagree (Folder throws when they do).
    private static bool IsSharedSlot(OrganizerModRow row, bool splitGear, LayoutOptions options) =>
        options.SharedHandsAndFeet && splitGear && row.Category == ModCategory.Gear
        && row.SubCategory is "Hands" or "Feet";

    private static string? GearBodyFor(
        OrganizerModRow row, bool splitGear, bool splitGearBody, bool splitBodyMods,
        BodyPriority? priority, LayoutOptions options) =>
        IsSharedSlot(row, splitGear, options) ? null : BodyFor(row, splitGearBody, splitBodyMods, priority);
```

and

```csharp
    public static GearFolderLayout? BuildGearLayout(
        IEnumerable<OrganizerModRow> rows, bool splitGear, bool splitGearBody, BodyPriority? priority,
        LayoutOptions? options = null)
    {
        var opts = options ?? default;
        return splitGear && splitGearBody
            ? GearFolderLayout.Build(
                rows.Where(r => r.Category == ModCategory.Gear && !IsSharedSlot(r, splitGear, opts))
                    .Select(r => ResolveBody(r, priority)), priority)
            : null;
    }
```

- [ ] **Step 3b: Run the new tests, then the whole suite**

Run: `dotnet test --nologo -v q --filter SharedHandsAndFeetTests` then `dotnet test --nologo -v q`
Expected: new tests PASS; full suite 1998 passed, 0 failed.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/LayoutOptions.cs PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs PenumbraOrganizer.Plugin.Tests/Organizer/SharedHandsAndFeetTests.cs
git commit -m "feat: option to keep hands and feet gear out of the body folders"
```

---

### Task 2: Hair race folders

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/HairRaceFolders.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/HairRaceFoldersTests.cs`

**Interfaces:**
- Consumes: `HairPriority`, `HairPriorityResolver.Resolve`, `HairRaceCodes.TryGetByCode`, `LayoutOptions` (Task 1).
- Produces: `HairRaceFolders.Tail(IReadOnlySet<int>? flat, IReadOnlyDictionary<string, IReadOnlySet<int>>? byRace, HairPriority? priority, string? storedNumber, bool splitByNumber) -> string` (the part below `Hair/`, never empty); `HairRaceFolders.MultiRace`.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class HairRaceFoldersTests
{
    private static IReadOnlyDictionary<string, IReadOnlySet<int>> By(params (string Code, int[] Numbers)[] e) =>
        e.ToDictionary(x => x.Code, x => (IReadOnlySet<int>)x.Numbers.ToHashSet());

    private static string Tail(IReadOnlyDictionary<string, IReadOnlySet<int>>? by, HairPriority? p = null,
        string? stored = null, bool byNumber = true)
    {
        var flat = by?.Values.SelectMany(v => v).ToHashSet();
        return HairRaceFolders.Tail(flat, by, p, stored, byNumber);
    }

    [Fact]
    public void OneRaceOneNumber_FilesUnderRaceAndNumber() =>
        Assert.Equal("Viera/115", Tail(By(("1801", [115]))));

    [Fact]
    public void BothGendersOfOneRace_CountAsOneRace() =>
        Assert.Equal("Viera/115", Tail(By(("1701", [115]), ("1801", [115]))));

    [Fact]
    public void OneRaceSeveralNumbers_FilesUnderTheRaceAlone() =>
        Assert.Equal("Viera", Tail(By(("1801", [115, 120]))));

    [Fact]
    public void SeveralRaces_GoToMultiRace_NumberedByTheStoredNumber() =>
        Assert.Equal("Multi-race/115", Tail(By(("1801", [115]), ("0801", [115])), stored: "115"));

    [Fact]
    public void SeveralRaces_UseThePriorityRace()
    {
        var by = By(("1801", [115]), ("0801", [105]));
        Assert.Equal("Multi-race/105", Tail(by, new HairPriority("0801", false)));
    }

    [Fact]
    public void SeveralRaces_UseTheMidlanderFallback() =>
        Assert.Equal("Multi-race/004", Tail(By(("0101", [4]), ("0801", [9])), new HairPriority(null, true)));

    [Fact]
    public void PriorityRaceNeverMovesASingleRaceMod() =>
        Assert.Equal("Viera/115", Tail(By(("1801", [115])), new HairPriority("0801", true)));

    [Fact]
    public void NoRaceEvidence_IsMultiRace()
    {
        Assert.Equal("Multi-race", Tail(null));
        Assert.Equal("Multi-race/007", Tail(null, stored: "7"));
    }

    [Fact]
    public void UnknownRaceCodesOnly_IsMultiRace() =>
        Assert.Equal("Multi-race", Tail(By(("9999", [115]))));

    [Fact]
    public void NumberSplitOff_UsesOnlyTheRaceFolder()
    {
        Assert.Equal("Viera", Tail(By(("1801", [115])), byNumber: false));
        Assert.Equal("Multi-race", Tail(By(("1801", [115]), ("0801", [115])), stored: "115", byNumber: false));
    }

    [Fact]
    public void Select_AppliesTheTemplateRenameToHairOnly()
    {
        var row = new OrganizerModRow
        {
            Identifier = "h", Name = "h", Author = "A", CurrentPath = "h", ProposedPath = "h",
            Category = ModCategory.Hair, SubCategory = "115",
            HairFlat = new HashSet<int> { 115 }, HairByRaceCode = By(("1801", [115])),
        };
        var folder = SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, row,
            renameFolder: f => f == "Hair" ? "Hairstyles" : f, splitHair: true,
            options: new LayoutOptions(SplitHairByRace: true)).Primary;
        Assert.Equal("Hairstyles/Viera/115", folder);
    }

    [Fact]
    public void Select_DefaultOptionsKeepTheFlatNumberFolder()
    {
        var row = new OrganizerModRow
        {
            Identifier = "h", Name = "h", Author = "A", CurrentPath = "h", ProposedPath = "h",
            Category = ModCategory.Hair, SubCategory = "115",
        };
        Assert.Equal("Hair/115",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, row, splitHair: true).Primary);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --nologo -v q --filter HairRaceFoldersTests`
Expected: FAIL to compile (`HairRaceFolders` missing).

- [ ] **Step 3: Implement**

Create `PenumbraOrganizer.Plugin/Organizer/Classification/HairRaceFolders.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The folder below <c>Hair/</c> when hair is split by race: the race for a mod made for one race,
/// <c>Multi-race</c> for a mod made for several or one with no race evidence.
/// </summary>
public static class HairRaceFolders
{
    public const string MultiRace = "Multi-race";

    /// <param name="flat">Every hair number the mod uses; unused here beyond the resolver.</param>
    /// <param name="byRace">Hair numbers per race and gender folder code, or null when none were read.</param>
    /// <param name="priority">Null means no priority; the stored scan-time number is used for Multi-race.</param>
    /// <param name="storedNumber">The number the scan stored on the row, used for Multi-race with no priority.</param>
    /// <param name="splitByNumber">Whether the hair number split is also on.</param>
    public static string Tail(
        IReadOnlySet<int>? flat,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? byRace,
        HairPriority? priority,
        string? storedNumber,
        bool splitByNumber)
    {
        var raceNumbers = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        if (byRace is not null)
        {
            foreach (var (code, numbers) in byRace)
            {
                if (!HairRaceCodes.TryGetByCode(code, out var race))
                    continue;
                if (!raceNumbers.TryGetValue(race.Race, out var set))
                    raceNumbers[race.Race] = set = [];
                set.UnionWith(numbers);
            }
        }

        // One race: its own folder. The priority race and the Midlander rule never apply here.
        if (raceNumbers.Count == 1)
        {
            var (raceName, numbers) = raceNumbers.Single();
            return Join(raceName, splitByNumber && numbers.Count == 1 ? numbers.Single() : null);
        }

        // Several races, or none: Multi-race, numbered by the existing priority rule.
        int? number = null;
        if (splitByNumber)
            number = priority is { } p ? HairPriorityResolver.Resolve(flat, byRace, p) : ParseStored(storedNumber);
        return Join(MultiRace, number);
    }

    private static string Join(string folder, int? number) =>
        number is { } n ? $"{folder}/{n.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(3, '0')}" : folder;

    // Same range the hair reader accepts: 1..9999.
    private static int? ParseStored(string? stored) =>
        int.TryParse(stored, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
        && n is >= 1 and <= 9999 ? n : null;
}
```

In `SortFolderSelectors.Type()`, immediately before the line `var sub = row.SubCategory;`, add:

```csharp
            // Hair split by race: the template rename applies to "Hair" only; the race tail is appended.
            if (opts.SplitHairByRace && row.Category == ModCategory.Hair)
            {
                var hairRoot = TypeFolder(row.Category, null, renameFolder);
                return hairRoot is null
                    ? null
                    : $"{hairRoot}/{HairRaceFolders.Tail(row.HairFlat, row.HairByRaceCode, hairPriority, row.SubCategory, splitHair)}";
            }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --nologo -v q --filter HairRaceFoldersTests` then `dotnet test --nologo -v q`
Expected: new tests PASS; full suite green.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/HairRaceFolders.cs PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/HairRaceFoldersTests.cs
git commit -m "feat: option to split hair by race with a Multi-race folder"
```

---

### Task 3: Wire the options through the sort and the Sort tab

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (the long `Sort` overload and its layout build), `PenumbraOrganizer.Plugin/Configuration.cs`, `PenumbraOrganizer.Plugin/Windows/SortPanel.cs`, `PenumbraOrganizer.Plugin/Windows/SortLayout.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateLayoutOptionsTests.cs`, `PenumbraOrganizer.Plugin.Tests/Windows/SortLayoutOptionsTests.cs`

**Interfaces:**
- Consumes: `LayoutOptions`, `SortFolderSelectors.Select/BuildGearLayout` (Tasks 1-2).
- Produces: `OrganizerState.Sort(..., BodyPriority? bodyPriority = null, LayoutOptions? options = null)`; `Configuration.SortSharedHandsFeet`, `Configuration.SortSplitHairByRace` (both default false); `SortSelection(..., BodyPriority? BodyPriority = null, LayoutOptions Layout = default)`; `SortLayout.Compute(..., bool activityGateOpen, bool splitGear = false)` with new `SortControlStates.SharedHandsFeet` (visible only when `splitGear`) and `SortControlStates.SplitHairByRace` (always visible).

- [ ] **Step 1: Write the failing tests**

`OrganizerStateLayoutOptionsTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateLayoutOptionsTests
{
    private static OrganizerState WithMods()
    {
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "gloves", Name = "Gloves", Author = "A", CurrentPath = "Gloves",
                ProposedPath = "Gloves", Category = ModCategory.Gear, SubCategory = "Hands", BodyType = "Rue+" },
            new OrganizerModRow { Identifier = "top", Name = "Top", Author = "A", CurrentPath = "Top",
                ProposedPath = "Top", Category = ModCategory.Gear, SubCategory = "Top", BodyType = "Rue+" },
            new OrganizerModRow { Identifier = "hair", Name = "Hair", Author = "A", CurrentPath = "Hair",
                ProposedPath = "Hair", Category = ModCategory.Hair, SubCategory = "115",
                HairFlat = new HashSet<int> { 115 },
                HairByRaceCode = new Dictionary<string, IReadOnlySet<int>> { ["1801"] = new HashSet<int> { 115 } } },
        ], new HashSet<string>());
        return state;
    }

    private static string PathOf(OrganizerState s, string id) => s.Mods.Single(m => m.Identifier == id).ProposedPath;

    [Fact]
    public void BothOn_SortsHandsToTheSharedFolderAndHairByRace()
    {
        var state = WithMods();
        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: true, n => n,
            splitGearBody: true, options: new LayoutOptions(true, true));

        Assert.Equal("Gear/03. Hands/Gloves", PathOf(state, "gloves"));
        Assert.Equal("Gear/10. Rue+/02. Top/Top", PathOf(state, "top"));
        Assert.Equal("Hair/Viera/115/Hair", PathOf(state, "hair"));
    }

    [Fact]
    public void BothOff_IsTheExistingLayout()
    {
        var state = WithMods();
        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: true, n => n,
            splitGearBody: true);

        Assert.Equal("Gear/10. Rue+/03. Hands/Gloves", PathOf(state, "gloves"));
        Assert.Equal("Hair/115/Hair", PathOf(state, "hair"));
    }
}
```

`SortLayoutOptionsTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SortLayoutOptionsTests
{
    [Fact]
    public void SharedHandsFeet_IsHiddenUntilTheSlotSplitIsOn()
    {
        Assert.False(SortLayout.Compute(SortStrategy.TypeOnly, false, false, true, splitGear: false).SharedHandsFeet.Visible);
        Assert.True(SortLayout.Compute(SortStrategy.TypeOnly, false, false, true, splitGear: true).SharedHandsFeet.Visible);
    }

    [Fact]
    public void BothOptions_AreGreyedUnderCreatorGrouping()
    {
        var s = SortLayout.Compute(SortStrategy.CreatorOnly, false, false, true, splitGear: true);
        Assert.False(s.SharedHandsFeet.Enabled);
        Assert.False(s.SplitHairByRace.Enabled);
        Assert.True(s.SplitHairByRace.Visible);
    }

    [Fact]
    public void Configuration_DefaultsBothOptionsOff()
    {
        var config = new Configuration();
        Assert.False(config.SortSharedHandsFeet);
        Assert.False(config.SortSplitHairByRace);
    }

    [Fact]
    public void SortSelection_ComparesTheOptions()
    {
        var a = new SortSelection(SortStrategy.TypeOnly, true, false, false);
        var b = a with { Layout = new LayoutOptions(SharedHandsAndFeet: true) };
        Assert.NotEqual(a, b);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --nologo -v q --filter "OrganizerStateLayoutOptionsTests|SortLayoutOptionsTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`OrganizerState.cs`, the long `Sort` overload: add `LayoutOptions? options = null` as the last parameter; in the `layouts` build pass it to `BuildGearLayout(g, splitGear, splitGearBody, bodyPriority, options)`; in the `Select(...)` call add `options: options`.

`Configuration.cs`, after `SortSplitBodyMods`:

```csharp
    /// <summary>Gear: hands and feet in the plain slot folders, not under a body. Off keeps 0.6.4.0 behavior.</summary>
    public bool SortSharedHandsFeet { get; set; }

    /// <summary>Hair: split by race, with a Multi-race folder. Off keeps 0.6.4.0 behavior.</summary>
    public bool SortSplitHairByRace { get; set; }
```

`SortLayout.cs`: add `ControlState SharedHandsFeet, ControlState SplitHairByRace` to `SortControlStates` (place them after `SplitHair`); add parameter `bool splitGear = false` as the LAST parameter of `Compute`; and in the returned record `SharedHandsFeet: new(splitGear, splitEnabled)`, `SplitHairByRace: new(true, splitEnabled)`. Update every positional construction of `SortControlStates` found by `grep -rn "new SortControlStates" PenumbraOrganizer.Plugin*` (only `Compute` today).

`SortPanel.cs`:
1. `SortSelection`: append `LayoutOptions Layout = default` after `BodyPriority`.
2. Fields: `private bool _sharedHandsFeet; private bool _splitHairByRace;`
3. In `_prefsLoaded` block: `_sharedHandsFeet = config.SortSharedHandsFeet; _splitHairByRace = config.SortSplitHairByRace;`
4. In `PersistSortChoices`: add `&& config.SortSharedHandsFeet == _sharedHandsFeet && config.SortSplitHairByRace == _splitHairByRace` to the early-return condition and assign both config fields before `saveConfig()`.
5. `SortLayout.Compute(strategy, _splitHair, Configuration.ScrapedNpcListFeatureEnabled, gates.CanStageProposals, _splitGear)`.
6. After the gear-slot checkbox: `if (states.SharedHandsFeet.Visible) DrawCheckbox("Hands and feet in a shared folder", ref _sharedHandsFeet, states.SharedHandsFeet, HelpTopics.SortSharedHandsFeet, splitReason);`
7. After the "Split hair by hair number" checkbox: `DrawCheckbox("Split hair by race", ref _splitHairByRace, states.SplitHairByRace, HelpTopics.SortSplitHairByRace, splitReason);`
8. Selection: pass `new LayoutOptions(_sharedHandsFeet, _splitHairByRace)` as the last argument of `new SortSelection(...)`.
9. Sort call: append `selection.Layout` as the last argument of `state.Sort(...)`.

Add the two help topics (Task 4 supplies their text; add the `HelpTopics` entries now so this compiles):
`public static readonly HelpTopic SortSharedHandsFeet = new("sort.shared-hands-feet");` and `public static readonly HelpTopic SortSplitHairByRace = new("sort.split-hair-by-race");` in `Windows/HelpTopics.cs`, next to `SortSplitHair`.

- [ ] **Step 4: Run tests and build**

Run: `dotnet build PenumbraOrganizer.Plugin -c Debug --nologo -v q` then `dotnet test --nologo -v q`
Expected: build succeeds; new tests PASS. A help-coverage test may fail until Task 4 registers and writes the two topics; if so, continue to Task 4 before committing.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: Sort tab checkboxes for shared hands and feet and hair by race"
```

---

### Task 4: Help text, docs and the in-game checklist

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTopicUsage.cs`, `PenumbraOrganizer.Plugin/Windows/HelpTab.cs`, `PenumbraOrganizer.Plugin/Resources/help-content.json`, `docs/HOW_SORTING_WORKS.md`, `docs/USER_GUIDE.md`, `docs/ROADMAP.md`, `docs/RELEASE_NOTES_0.6.4.0.md` (add a short section only), `docs/testing/2026-11-06-body-parts-checklist.md`

**Interfaces:**
- Consumes: the two `HelpTopics` entries from Task 3.

- [ ] **Step 1: Register the topics**

In both `HelpTopicUsage.cs` and `HelpTab.cs`, add `HelpTopics.SortSharedHandsFeet,` right after `HelpTopics.SortSplitGear,` and `HelpTopics.SortSplitHairByRace,` right after `HelpTopics.SortSplitHair,` (same list positions as the existing Sort topics).

- [ ] **Step 2: Add the help content**

In `help-content.json`, after the `sort.split-hair` entry, add:

```json
    {
      "id": "sort.split-hair-by-race",
      "title": "Split hair by race",
      "short": "Hair made for one race files under that race: Hair/Viera/115. Hair made for several races, or with no race found, files under Hair/Multi-race. With the hair number split off you get just the race folder."
    },
```

and after `sort.split-gear` (find its entry and add after it):

```json
    {
      "id": "sort.shared-hands-feet",
      "title": "Hands and feet in a shared folder",
      "short": "Gloves, gauntlets, boots and nails fit most bodies, so they go in Gear/03. Hands and Gear/05. Feet instead of under each body. Tops, legs and sets still go under their body. Needs the slot split."
    },
```

- [ ] **Step 3: Update docs**

- `docs/HOW_SORTING_WORKS.md`: in the Gear section add the shared hands and feet rule and its slot-split dependency; in the Hair section add the race rules from the Global Constraints, verbatim in meaning.
- `docs/USER_GUIDE.md`: one short paragraph per option next to the existing split-checkbox descriptions.
- `docs/ROADMAP.md`: mark both options shipped (0.6.4.0 testing); list under "later": per-category settings records and presets, the Sets split by slot combination, race-specific gear.
- `docs/RELEASE_NOTES_0.6.4.0.md`: append a section "Two more split options" with one line each and the note that both are off by default.
- `docs/testing/2026-11-06-body-parts-checklist.md`: add rows: tick Hands and feet shared and sort, confirm Gear/03. Hands and Gear/05. Feet gain the hands and feet mods and no body folder holds a hands or feet slot; confirm a body that only had hands or feet mods vanishes; tick Split hair by race, confirm one-race hair files under its race and a known multi-race hair under Multi-race; untick both and confirm the old layout returns after a sort.

- [ ] **Step 4: Verify**

Run: `dotnet test --nologo -v q`
Expected: all tests pass (including the help-coverage tests that require every topic to be registered and every registered topic to have content).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "docs: help text, guide and checklist for the two new split options"
```

---

## Self-Review

- **Spec coverage:** shared hands and feet (Task 1, UI Task 3), hair race split with Multi-race and the judge's single-race-several-numbers rule (Task 2), exactly two checkboxes and no presets (Task 3), defaults reproduce 0.6.4.0 (Tasks 1-3 default tests, full suite), docs and checklist (Task 4), template planner untouched (Global Constraints).
- **Placeholders:** none; the docs bullets name the content to write. The `sort.split-gear` help entry is located by id before inserting.
- **Type consistency:** `LayoutOptions` (Task 1) is used by name in Tasks 2-3; `HairRaceFolders.Tail` signature is identical in Task 2's tests, implementation and the `Select` wiring; `SortLayout.Compute` gains only a trailing optional parameter, so existing callers compile.
- **Known risk:** `row.SubCategory is "Hands" or "Feet"` in `IsSharedSlot` binds as `is ("Hands" or "Feet")` at the end of a `&&` chain, which is the intended reading; Task 1's tests pin it.
