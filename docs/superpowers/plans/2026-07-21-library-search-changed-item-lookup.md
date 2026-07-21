# Library Search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only "Search" tab that lets the user find every installed mod (enabled or not)
whose changed items match a query, filtered by category and (for Gear) equipment slot — a reverse
lookup ("which mod affects this item?") built on the same enable-state-independent IPC data the Sort
tab's classifier already uses.

**Architecture:** A new `PenumbraOrganizer.Plugin/LibrarySearch/` namespace, sibling to `Organizer/`,
holding a mod-centric index (`ChangedItemIndex`/`IndexedMod`/`IndexedChangedItem`), a builder that
reuses the existing `ChangedItemKeyParser`/`ModTypeClassifier`/`ModEquipmentFileReader` primitives,
and a pure filter/match engine. `Plugin.BuildChangedItemIndex()` fetches the same two bulk IPC calls
`RunScan()` already makes; a new `MainWindow.DrawSearchTab()` renders a two-pane UI (mod list left,
selected mod's changed items right).

**Tech Stack:** C#/.NET (Dalamud.NET.Sdk), xUnit, Dalamud's ImGui bindings (`Dalamud.Bindings.ImGui`,
`Dalamud.Interface.Utility.Raii`).

## Global Constraints

- No new IPC calls beyond the two `RunScan()` already uses (`GetModListAdapter`,
  `GetChangedItemAdapterDictionary`). No write IPC calls anywhere in this feature.
- `ModTypeClassifier.Classify`'s existing behavior, and every existing `ModTypeClassifierTests` case,
  must remain byte-for-byte unchanged — achieved here by never modifying `Classify`'s body at all,
  only adding a new sibling method.
- The shared `ModCategory` enum (`PenumbraOrganizer.Core/Classification/ModCategory.cs`, linked from
  the standalone app) must not be modified. It has no `Unknown` member; "unrecognized" is represented
  as `null`, tracked via separate `HasUnknownFacetItems`/`IncludeUnknown` booleans, never a new enum
  value.
- No injected interfaces/dependency injection for disk I/O (`ModEquipmentFileReader` stays a static
  class, called directly) and no async/threading — this feature stays synchronous, matching every
  existing Scan/Sort/Apply/Folder Cleanup code path.
- All search-text comparisons use `StringComparison.OrdinalIgnoreCase`; queries are trimmed, and a
  whitespace-only query is treated as empty (no filtering).
- New production code lives under `PenumbraOrganizer.Plugin/LibrarySearch/`, not nested inside
  `Organizer/`. Tests mirror that path under `PenumbraOrganizer.Plugin.Tests/LibrarySearch/`.
- Disk-I/O tests use real temp directories with real fixture JSON, no mocked filesystem — matching
  `ModEquipmentFileReaderTests`'s existing convention.
- Test framework is xUnit (`[Fact]`/`[Theory]`, `Assert.*`), matching every existing test file in this
  project.
- Full spec: `docs/superpowers/specs/2026-07-21-library-search-changed-item-lookup-design.md`.

---

### Task 1: Relocate `GearSlotDiagnostic` out of `OrganizerModRow.cs`

`GearSlotDiagnostic` currently lives inside `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs` (a
plugin row-model file). This feature's `IndexedMod` needs the same enum, and referencing an enum
owned by a UI row model from the new, intentionally independent `LibrarySearch` namespace would be
backwards. Moving it to `Organizer/Classification/`, sibling to the `ModEquipmentFileReader` it
already describes, fixes this before anything in `LibrarySearch` depends on it.

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/GearSlotDiagnostic.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs:125,138-142`
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerExportFormatter.cs`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerExportFormatterTests.cs`

**Interfaces:**
- Produces: `PenumbraOrganizer.Plugin.Organizer.Classification.GearSlotDiagnostic` (enum: `NotApplicable`,
  `Single`, `Ambiguous`, `ZeroEvidence`, `DirectoryMissing`, `ReadFailure`) — used by Task 3's
  `IndexedMod` record.

- [ ] **Step 1: Run the full existing test suite and record the baseline pass count**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: all tests pass (e.g. "Passed! - Failed: 0, Passed: 364, Skipped: 0" — note the exact
number shown; Step 6 below must show the identical number).

- [ ] **Step 2: Create the new file with the enum moved verbatim**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

// Why a Gear mod did or didn't get a SubCategory from ModEquipmentFileReader/EnrichGearSubCategory.
// Recorded per-row at Scan time so the Export button can surface a breakdown without digging
// through the Dalamud log - added after a tester report where 0 of ~1500 Gear mods got a
// subcategory and there was no way to tell "every mod's config files failed to read" apart from
// "every mod is a legitimately ambiguous multi-piece outfit" without this.
public enum GearSlotDiagnostic
{
    NotApplicable,   // not a Gear mod (equipment-slot detection never runs for other categories)
    Single,          // resolved to exactly one slot - SubCategory was assigned
    Ambiguous,       // resolved to more than one slot - a real multi-piece outfit, not a bug
    ZeroEvidence,    // the mod's directory exists and every config file read fine, but none
                      // carried recognized equipment data - a real "nothing to find" case
    DirectoryMissing, // mod.ModPath.Exists was false - ReadEquipmentSlots can't distinguish this
                      // from ZeroEvidence on its own (by design, see its own tests), but it's a
                      // very different root cause worth separating for diagnostics: this means the
                      // path the IPC gave us for this mod couldn't be found at all, so no file was
                      // ever read - not "these files have no equipment info."
    ReadFailure,     // a config file could not be read or parsed - untrustworthy, treated as no evidence
}
```

Save it at `PenumbraOrganizer.Plugin/Organizer/Classification/GearSlotDiagnostic.cs`.

- [ ] **Step 3: Remove the enum from `OrganizerModRow.cs` and add a using directive**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`, delete lines 1-23 (the `using
PenumbraOrganizer.Core.Classification;` line stays; the `GearSlotDiagnostic` enum block, including its
doc comment, is deleted) and add the new namespace's using directive. The file's top should read:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

public sealed class OrganizerModRow
{
    public required string Identifier { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string CurrentPath { get; init; }
    public required string ProposedPath { get; set; }
    public bool Protected { get; set; }
    public bool HeliosphereManaged { get; init; }
    public ModCategory? Category { get; init; }
    public string? SubCategory { get; init; }
    public GearSlotDiagnostic GearSlotDiagnostic { get; init; } = GearSlotDiagnostic.NotApplicable;
}
```

- [ ] **Step 4: Update `Plugin.cs`'s references to drop the now-unnecessary `Organizer.` prefix**

`Plugin.cs` already has `using PenumbraOrganizer.Plugin.Organizer.Classification;` at the top (line
12), so once the enum lives in that namespace, the existing `Organizer.GearSlotDiagnostic.X`
references resolve unqualified. In `PenumbraOrganizer.Plugin/Plugin.cs`, change line 125 from:

```csharp
            var gearSlotDiagnostic = Organizer.GearSlotDiagnostic.NotApplicable;
```
to:
```csharp
            var gearSlotDiagnostic = GearSlotDiagnostic.NotApplicable;
```

And change lines 138-142 from:
```csharp
                gearSlotDiagnostic = equipmentSlots switch
                {
                    null => Organizer.GearSlotDiagnostic.ReadFailure,
                    { Count: 0 } when !mod.ModPath.Exists => Organizer.GearSlotDiagnostic.DirectoryMissing,
                    { Count: 0 } => Organizer.GearSlotDiagnostic.ZeroEvidence,
                    { Count: 1 } => Organizer.GearSlotDiagnostic.Single,
                    _ => Organizer.GearSlotDiagnostic.Ambiguous,
                };
```
to:
```csharp
                gearSlotDiagnostic = equipmentSlots switch
                {
                    null => GearSlotDiagnostic.ReadFailure,
                    { Count: 0 } when !mod.ModPath.Exists => GearSlotDiagnostic.DirectoryMissing,
                    { Count: 0 } => GearSlotDiagnostic.ZeroEvidence,
                    { Count: 1 } => GearSlotDiagnostic.Single,
                    _ => GearSlotDiagnostic.Ambiguous,
                };
```

- [ ] **Step 5: Add the using directive to the two files that referenced the enum unqualified via
  `Organizer`'s namespace**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerExportFormatter.cs`, change the top from:
```csharp
using System.Text;

namespace PenumbraOrganizer.Plugin.Organizer;
```
to:
```csharp
using System.Text;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;
```

In `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerExportFormatterTests.cs`, change the top from:
```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;
```
to:
```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;
```

- [ ] **Step 6: Build and run the full suite again, confirm the identical pass count from Step 1**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: the exact same "Passed: N" count recorded in Step 1 — this is a pure relocation, so the
count must not change.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/GearSlotDiagnostic.cs PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin/Organizer/OrganizerExportFormatter.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerExportFormatterTests.cs
git commit -m "refactor: move GearSlotDiagnostic to Organizer/Classification

Relocates the enum out of OrganizerModRow.cs (a plugin row-model file)
to sit alongside ModEquipmentFileReader, which it already describes.
Prep step for Library Search, which needs this enum from an
intentionally independent namespace and shouldn't depend on a UI row
model to get it."
```

---

### Task 2: Add `ModTypeClassifier.ClassifyKeyFacet` (new method, zero changes to `Classify`)

Library Search needs each individual changed-item key's own category, not just the mod's single
first-match-wins `Category`. `ModTypeClassifier.Classify`'s existing per-key checks already contain
this mapping inline; this task exposes it as a new, separate public method **without changing
`Classify`'s body at all** — the safest possible way to guarantee the existing ~40+
`ModTypeClassifierTests` stay passing unchanged, since nothing they exercise is touched.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs`

**Interfaces:**
- Consumes: `ChangedItemKey` (from `ChangedItemKeyParser.cs`) — `Shape`, `ItemName`, `Subtype`,
  `BodyPart`, `CategoryLiteral` fields, all already defined.
- Produces: `public static ModCategory? ModTypeClassifier.ClassifyKeyFacet(ChangedItemKey key)` — used
  by Task 3's `ChangedItemIndexBuilder`.

- [ ] **Step 1: Write the failing tests**

Add these test methods to the end of the `ModTypeClassifierTests` class in
`PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs` (add
`using PenumbraOrganizer.Core.Classification;` if not already present — it already is, per the file's
existing top):

```csharp
    [Fact] // Real named Gear item, no placeholder match — plain Gear facet
    public void ClassifyKeyFacet_RealGearItem_IsGear()
    {
        var key = ChangedItemKeyParser.Parse("Appointed Gloves");
        Assert.Equal(ModCategory.Gear, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact] // Placeholder override applies at the per-key level too, not just mod-level Classify
    public void ClassifyKeyFacet_SmallclothesPlaceholder_IsBody()
    {
        var key = ChangedItemKeyParser.Parse("Smallclothes");
        Assert.Equal(ModCategory.Body, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact]
    public void ClassifyKeyFacet_MountSuffix_IsMount()
    {
        var key = ChangedItemKeyParser.Parse("Archon Throne (Mount)");
        Assert.Equal(ModCategory.Mount, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact]
    public void ClassifyKeyFacet_MinionSuffix_IsMinion()
    {
        var key = ChangedItemKeyParser.Parse("Wind-up Bahamut (Companion)");
        Assert.Equal(ModCategory.Minion, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact]
    public void ClassifyKeyFacet_NpcSuffix_IsNpc()
    {
        var key = ChangedItemKeyParser.Parse("Smallclothes (NPC, 9903-1, Legs)");
        Assert.Equal(ModCategory.NPC, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact] // (Child) race-variant customization is an unconditional NPC signal
    public void ClassifyKeyFacet_ChildCustomization_IsNpc()
    {
        var key = ChangedItemKeyParser.Parse("Customization: Elezen Female (Child) Face 201");
        Assert.Equal(ModCategory.NPC, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Theory]
    [InlineData("Action: Sample Action", ModCategory.Animation)]
    [InlineData("Emote: Sample Emote", ModCategory.Animation)]
    [InlineData("Vfx", ModCategory.VFX)]
    [InlineData("Animation", ModCategory.Animation)]
    [InlineData("Housing", ModCategory.Furniture)]
    [InlineData("Sound", ModCategory.Sound)]
    public void ClassifyKeyFacet_LiteralAndPrefixedShapes_MapCorrectly(string rawKey, ModCategory expected)
    {
        var key = ChangedItemKeyParser.Parse(rawKey);
        Assert.Equal(expected, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Theory]
    [InlineData("Customization: Miqo'te Female Face 101", ModCategory.Face)]
    [InlineData("Customization: Midlander Female Hair 157", ModCategory.Hair)]
    [InlineData("Customization: Miqo'te Female Tail 3", ModCategory.Body)]
    [InlineData("Customization: Midlander Female Skin Textures", ModCategory.Skin)]
    public void ClassifyKeyFacet_CustomizationBodyPart_MapsCorrectly(string rawKey, ModCategory expected)
    {
        var key = ChangedItemKeyParser.Parse(rawKey);
        Assert.Equal(expected, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact] // Body part present but unrecognized ("Unknown") — never guess, return null
    public void ClassifyKeyFacet_UnrecognizedCustomizationBodyPart_IsNull()
    {
        var key = ChangedItemKeyParser.Parse("Customization: Unknown");
        Assert.Null(ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact] // Icon shape has no facet mapping at all — null, not a guess
    public void ClassifyKeyFacet_IconShape_IsNull()
    {
        var key = ChangedItemKeyParser.Parse("Icon: Something");
        Assert.Null(ModTypeClassifier.ClassifyKeyFacet(key));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ClassifyKeyFacet"`
Expected: FAIL — `ClassifyKeyFacet` does not exist on `ModTypeClassifier` (compile error).

- [ ] **Step 3: Add `ClassifyKeyFacet` to `ModTypeClassifier` — a new method, `Classify` untouched**

In `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`, add this new public
method anywhere inside the `ModTypeClassifier` class (e.g. immediately after the closing brace of
`EnrichGearSubCategory`, before `Classify`). It reuses the existing private
`KnownEquipmentPlaceholders` field and `MapBodyPart` method already defined in this same class — no
other file needs to change, and `Classify`'s own body is not touched by this step at all:

```csharp
    // Returns the single category one changed-item key alone implies, with no cross-key
    // aggregation and no first-match-wins ordering between different keys — that's what Classify
    // does. This exists for LibrarySearch, which needs a per-item facet, not one first-match answer
    // for the whole mod. Reuses the same KnownEquipmentPlaceholders/MapBodyPart Classify already
    // uses, so the two never define the placeholder table or body-part mapping in two places.
    public static ModCategory? ClassifyKeyFacet(ChangedItemKey key)
    {
        if (key.Shape == ChangedItemKeyShape.Gear)
        {
            return KnownEquipmentPlaceholders.TryGetValue(key.ItemName!, out var placeholderCategory)
                ? placeholderCategory
                : ModCategory.Gear;
        }

        if (key.Shape == ChangedItemKeyShape.Mount)
            return ModCategory.Mount;
        if (key.Shape == ChangedItemKeyShape.Minion)
            return ModCategory.Minion;
        if (key.Shape == ChangedItemKeyShape.Npc)
            return ModCategory.NPC;
        if (key.Shape == ChangedItemKeyShape.Customization && key.Subtype == "Child")
            return ModCategory.NPC;
        if (key.Shape is ChangedItemKeyShape.Action or ChangedItemKeyShape.Emote)
            return ModCategory.Animation;
        if (key.Shape == ChangedItemKeyShape.CategoryLiteral)
        {
            return key.CategoryLiteral switch
            {
                "Vfx" => ModCategory.VFX,
                "Animation" => ModCategory.Animation,
                "Housing" => ModCategory.Furniture,
                "Sound" => ModCategory.Sound,
                _ => null,
            };
        }
        if (key.Shape == ChangedItemKeyShape.Customization && key.BodyPart is not null)
            return MapBodyPart(key.BodyPart);

        return null;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ClassifyKeyFacet"`
Expected: PASS (all new test methods green).

- [ ] **Step 5: Run the full suite to confirm `Classify`'s existing behavior is unaffected**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: the exact same "Passed: N" count as Task 1's Step 6, plus the new
`ClassifyKeyFacet_*` tests (N + 12).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs
git commit -m "feat: add ModTypeClassifier.ClassifyKeyFacet for per-item categorization

Classify itself is completely untouched -- this is a new sibling
method reusing the same placeholder table and body-part mapping, so
Library Search can get a per-key facet without duplicating that logic
or risking any drift in Classify's existing mod-level behavior."
```

---

### Task 3: `LibrarySearch/ChangedItemIndex.cs` data model + `ChangedItemIndexBuilder`

The core of the feature: given a plain mod list and a per-identifier changed-item-keys lookup, builds
a mod-centric index with per-item facets, per-mod category/slot evidence, and diagnostics.

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndex.cs`
- Create: `PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndexBuilder.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/LibrarySearch/ChangedItemIndexBuilderTests.cs`

**Interfaces:**
- Consumes: `ModTypeClassifier.ClassifyKeyFacet` (Task 2), `ChangedItemKeyParser.Parse` (existing),
  `ModEquipmentFileReader.ReadEquipmentSlots(DirectoryInfo)` (existing), `GearSlotDiagnostic` (Task 1's
  new location), `NpcNameMatcher.Match(string)` (existing).
- Produces: `LibraryModEntry(string Identifier, string Name, string Author, DirectoryInfo ModPath)`,
  `IndexedChangedItem(string Key, ModCategory? Facet)`, `IndexedMod(...)`, `ChangedItemIndex(...)`,
  and `ChangedItemIndexBuilder.Build(IReadOnlyList<LibraryModEntry> mods, IReadOnlySet<string>
  modIdentifiersWithChangedItems, Func<string, IEnumerable<string>> changedItemKeysByIdentifier,
  NpcNameMatcher npcNameMatcher)` — used by Task 6 (Plugin wiring).

- [ ] **Step 1: Create the data model file**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.LibrarySearch;

public sealed record IndexedChangedItem(string Key, ModCategory? Facet); // null = unrecognized shape

public sealed record IndexedMod(
    string Identifier,
    string Name,
    string Author,
    IReadOnlyList<IndexedChangedItem> ChangedItems,
    IReadOnlySet<ModCategory> Categories,      // union of non-null ChangedItems[].Facet — item evidence ONLY
    bool HasUnknownFacetItems,                 // true if any ChangedItems[].Facet is null
    bool MatchedByNpcNameHeuristic,            // separate provenance flag, never folded into Categories
    IReadOnlySet<EquipmentSlot> EquipmentSlots,
    GearSlotDiagnostic SlotDiagnostic);

public sealed record ChangedItemIndex(
    IReadOnlyList<IndexedMod> Mods,           // only mods with >= 1 changed item
    int TotalModsSeen,                        // every mod GetModListAdapter returned, including 0-item ones
    int OrphanedChangedItemEntryCount,        // dictionary entries whose identifier matched no mod
    DateTime BuiltAt);
```

Save at `PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndex.cs`.

- [ ] **Step 2: Write the failing tests for the builder**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibrarySearch;

public class ChangedItemIndexBuilderTests
{
    private static DirectoryInfo MakeTempModDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private static void WriteJson(DirectoryInfo modDirectory, string fileName, string json) =>
        File.WriteAllText(Path.Combine(modDirectory.FullName, fileName), json);

    private static LibraryModEntry MakeMod(string identifier, string name, string author, DirectoryInfo? modPath = null) =>
        new(identifier, name, author, modPath ?? new DirectoryInfo(Path.Combine(Path.GetTempPath(), "nonexistent-" + identifier)));

    [Fact]
    public void Build_ModWithNoChangedItems_ExcludedFromMods_ButCountedInTotal()
    {
        var mods = new List<LibraryModEntry> { MakeMod("a", "Empty Mod", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string>(), _ => Enumerable.Empty<string>(), NpcNameMatcher.Empty);

        Assert.Empty(result.Mods);
        Assert.Equal(1, result.TotalModsSeen);
    }

    [Fact]
    public void Build_SmallclothesPlusRealGear_CategoriesContainsBothBodyAndGear()
    {
        // Deliberately diverges from ModTypeClassifier.Classify, which would return Body alone
        // (Rule 0 wins) — Categories here is a per-item union, not a first-match-wins reduction.
        var mods = new List<LibraryModEntry> { MakeMod("a", "Compilation Mod", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" },
            _ => new[] { "Smallclothes", "Appointed Gloves" },
            NpcNameMatcher.Empty);

        var mod = Assert.Single(result.Mods);
        Assert.Equal(new HashSet<ModCategory> { ModCategory.Body, ModCategory.Gear }, mod.Categories);
    }

    [Fact]
    public void Build_NpcNameHeuristicMatch_SetsFlagIndependentlyOfCategories()
    {
        var npcMatcher = new NpcNameMatcher(["Zenos"], [], []);
        var mods = new List<LibraryModEntry> { MakeMod("a", "Zenos", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" },
            _ => new[] { "Customization: Midlander Male Skin Textures" },
            npcMatcher);

        var mod = Assert.Single(result.Mods);
        Assert.True(mod.MatchedByNpcNameHeuristic);
        Assert.DoesNotContain(ModCategory.NPC, mod.Categories); // no item is itself NPC-shaped
    }

    [Fact]
    public void Build_GearModWithSingleSlot_ReadsEquipmentSlotsAndSetsDiagnostic()
    {
        var modDir = MakeTempModDirectory();
        WriteJson(modDir, "default_mod.json", """
            {"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"files/sho.mdl"},"Manipulations":[]}
            """);
        var mods = new List<LibraryModEntry> { MakeMod("a", "Boots Mod", "Someone", modDir) };

        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" }, _ => new[] { "Calfskin Rider's Shoes" }, NpcNameMatcher.Empty);

        var mod = Assert.Single(result.Mods);
        Assert.Equal(new HashSet<EquipmentSlot> { EquipmentSlot.Feet }, mod.EquipmentSlots);
        Assert.Equal(GearSlotDiagnostic.Single, mod.SlotDiagnostic);
    }

    [Fact]
    public void Build_NonGearMod_NeverReadsDisk_EquipmentSlotsEmptyNotApplicable()
    {
        // A directory that doesn't exist would fail EquipmentSlot reads if ever touched -- proves
        // the builder never calls ModEquipmentFileReader for a mod whose Categories has no Gear.
        var mods = new List<LibraryModEntry> { MakeMod("a", "Vfx Mod", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" }, _ => new[] { "Vfx" }, NpcNameMatcher.Empty);

        var mod = Assert.Single(result.Mods);
        Assert.Empty(mod.EquipmentSlots);
        Assert.Equal(GearSlotDiagnostic.NotApplicable, mod.SlotDiagnostic);
    }

    [Fact]
    public void Build_UnrecognizedKeyAlongsideRecognizedOnes_SetsHasUnknownFacetItems()
    {
        var mods = new List<LibraryModEntry> { MakeMod("a", "Mixed Mod", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" }, _ => new[] { "Vfx", "Icon: Something" }, NpcNameMatcher.Empty);

        var mod = Assert.Single(result.Mods);
        Assert.True(mod.HasUnknownFacetItems);
        Assert.Contains(ModCategory.VFX, mod.Categories);
    }

    [Fact]
    public void Build_ChangedItemEntryWithNoMatchingMod_CountedAsOrphaned()
    {
        var mods = new List<LibraryModEntry> { MakeMod("a", "Real Mod", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods,
            new HashSet<string> { "a", "ghost-identifier" },
            id => id == "a" ? new[] { "Appointed Gloves" } : Enumerable.Empty<string>(),
            NpcNameMatcher.Empty);

        Assert.Equal(1, result.OrphanedChangedItemEntryCount);
    }

    [Fact]
    public void Build_TotalModsSeen_CountsEveryModRegardlessOfChangedItems()
    {
        var mods = new List<LibraryModEntry>
        {
            MakeMod("a", "Has Items", "Someone"),
            MakeMod("b", "No Items", "Someone"),
        };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" },
            id => id == "a" ? new[] { "Appointed Gloves" } : Enumerable.Empty<string>(),
            NpcNameMatcher.Empty);

        Assert.Equal(2, result.TotalModsSeen);
        Assert.Single(result.Mods);
    }
}
```

Save at `PenumbraOrganizer.Plugin.Tests/LibrarySearch/ChangedItemIndexBuilderTests.cs`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ChangedItemIndexBuilderTests"`
Expected: FAIL — `ChangedItemIndexBuilder` and `LibraryModEntry` do not exist (compile error).

- [ ] **Step 4: Implement `ChangedItemIndexBuilder`**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.LibrarySearch;

public sealed record LibraryModEntry(string Identifier, string Name, string Author, DirectoryInfo ModPath);

public static class ChangedItemIndexBuilder
{
    public static ChangedItemIndex Build(
        IReadOnlyList<LibraryModEntry> mods,
        IReadOnlySet<string> modIdentifiersWithChangedItems,
        Func<string, IEnumerable<string>> changedItemKeysByIdentifier,
        NpcNameMatcher npcNameMatcher)
    {
        var indexedMods = new List<IndexedMod>();

        foreach (var mod in mods)
        {
            var changedItemKeys = changedItemKeysByIdentifier(mod.Identifier).ToList();
            if (changedItemKeys.Count == 0)
                continue; // zero-changed-item mods are excluded from the browsable index

            var changedItems = changedItemKeys
                .Select(key => new IndexedChangedItem(
                    key, ModTypeClassifier.ClassifyKeyFacet(ChangedItemKeyParser.Parse(key))))
                .ToList();

            var categories = changedItems
                .Where(item => item.Facet is not null)
                .Select(item => item.Facet!.Value)
                .ToHashSet();
            var hasUnknownFacetItems = changedItems.Any(item => item.Facet is null);
            var matchedByNpcNameHeuristic = npcNameMatcher.Match(mod.Name) is not null;

            IReadOnlySet<EquipmentSlot> equipmentSlots = new HashSet<EquipmentSlot>();
            var slotDiagnostic = GearSlotDiagnostic.NotApplicable;
            if (categories.Contains(ModCategory.Gear))
            {
                var resolvedSlots = ModEquipmentFileReader.ReadEquipmentSlots(mod.ModPath);
                slotDiagnostic = resolvedSlots switch
                {
                    null => GearSlotDiagnostic.ReadFailure,
                    { Count: 0 } when !mod.ModPath.Exists => GearSlotDiagnostic.DirectoryMissing,
                    { Count: 0 } => GearSlotDiagnostic.ZeroEvidence,
                    { Count: 1 } => GearSlotDiagnostic.Single,
                    _ => GearSlotDiagnostic.Ambiguous,
                };
                equipmentSlots = resolvedSlots ?? new HashSet<EquipmentSlot>();
            }

            indexedMods.Add(new IndexedMod(
                mod.Identifier, mod.Name, mod.Author, changedItems, categories,
                hasUnknownFacetItems, matchedByNpcNameHeuristic, equipmentSlots, slotDiagnostic));
        }

        var orphanedCount = modIdentifiersWithChangedItems
            .Except(mods.Select(m => m.Identifier), StringComparer.Ordinal)
            .Count();

        return new ChangedItemIndex(indexedMods, mods.Count, orphanedCount, DateTime.Now);
    }
}
```

Save at `PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndexBuilder.cs`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ChangedItemIndexBuilderTests"`
Expected: PASS (all 8 tests green).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: previous count + 8 new tests, all passing, 0 failures.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndex.cs PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndexBuilder.cs PenumbraOrganizer.Plugin.Tests/LibrarySearch/ChangedItemIndexBuilderTests.cs
git commit -m "feat: add ChangedItemIndexBuilder for Library Search

Mod-centric index: per-item facets via the new ClassifyKeyFacet,
per-mod category union (deliberately diverging from Classify's
single-answer reduction), Gear-gated equipment-slot reads reusing
ModEquipmentFileReader as-is, and orphan/total-mod diagnostics."
```

---

### Task 4: `LibrarySearch/ChangedItemIndexSummary.cs`

Derives the human-readable refresh summary from a built index — one source of truth, no parallel
count fields to keep in sync.

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndexSummary.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/LibrarySearch/ChangedItemIndexSummaryTests.cs`

**Interfaces:**
- Consumes: `ChangedItemIndex`, `IndexedMod` (Task 3).
- Produces: `public static string ChangedItemIndexSummary.Describe(ChangedItemIndex index)` — used by
  Task 7 (UI).

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibrarySearch;

public class ChangedItemIndexSummaryTests
{
    private static IndexedMod MakeGearMod(string id, GearSlotDiagnostic diagnostic, int itemCount = 1) =>
        new(id, id, "Author",
            Enumerable.Range(0, itemCount).Select(i => new IndexedChangedItem($"Item {i}", ModCategory.Gear)).ToList(),
            new HashSet<ModCategory> { ModCategory.Gear }, false, false,
            new HashSet<EquipmentSlot>(), diagnostic);

    [Fact]
    public void Describe_ReportsIndexedAndTotalModCounts()
    {
        var index = new ChangedItemIndex(
            [MakeGearMod("a", GearSlotDiagnostic.Single)], TotalModsSeen: 5, OrphanedChangedItemEntryCount: 0, BuiltAt: DateTime.Now);

        var summary = ChangedItemIndexSummary.Describe(index);

        Assert.Contains("Indexed 1 of 5 mods", summary);
    }

    [Fact]
    public void Describe_ReportsTotalChangedItemCount()
    {
        var index = new ChangedItemIndex(
            [MakeGearMod("a", GearSlotDiagnostic.Single, itemCount: 3)], 1, 0, DateTime.Now);

        Assert.Contains("3 changed items", ChangedItemIndexSummary.Describe(index));
    }

    [Fact]
    public void Describe_BreaksDownGearModsBySlotDiagnostic()
    {
        var index = new ChangedItemIndex(
            [
                MakeGearMod("a", GearSlotDiagnostic.Single),
                MakeGearMod("b", GearSlotDiagnostic.Ambiguous),
                MakeGearMod("c", GearSlotDiagnostic.ZeroEvidence),
                MakeGearMod("d", GearSlotDiagnostic.DirectoryMissing),
                MakeGearMod("e", GearSlotDiagnostic.ReadFailure),
            ],
            TotalModsSeen: 5, OrphanedChangedItemEntryCount: 0, BuiltAt: DateTime.Now);

        var summary = ChangedItemIndexSummary.Describe(index);

        Assert.Contains("5 gear mods scanned", summary);
        Assert.Contains("1 single-slot", summary);
        Assert.Contains("1 multi-slot", summary);
        Assert.Contains("1 unresolved", summary);
        Assert.Contains("1 missing directories", summary);
        Assert.Contains("1 read failures", summary);
    }

    [Fact] // Ambiguous (multi-slot) is a SUCCESS for this feature, never described as a failure
    public void Describe_NeverUsesSorterFlavoredFailureLanguageForAmbiguous()
    {
        var index = new ChangedItemIndex([MakeGearMod("a", GearSlotDiagnostic.Ambiguous)], 1, 0, DateTime.Now);

        Assert.DoesNotContain("could not be assigned", ChangedItemIndexSummary.Describe(index));
    }

    [Fact]
    public void Describe_ZeroOrphans_OmitsOrphanClause()
    {
        var index = new ChangedItemIndex([], TotalModsSeen: 1, OrphanedChangedItemEntryCount: 0, BuiltAt: DateTime.Now);

        Assert.DoesNotContain("orphaned", ChangedItemIndexSummary.Describe(index));
    }

    [Fact]
    public void Describe_NonZeroOrphans_IncludesOrphanClause()
    {
        var index = new ChangedItemIndex([], TotalModsSeen: 1, OrphanedChangedItemEntryCount: 2, BuiltAt: DateTime.Now);

        Assert.Contains("2 orphaned changed-item entries", ChangedItemIndexSummary.Describe(index));
    }
}
```

Save at `PenumbraOrganizer.Plugin.Tests/LibrarySearch/ChangedItemIndexSummaryTests.cs`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ChangedItemIndexSummaryTests"`
Expected: FAIL — `ChangedItemIndexSummary` does not exist (compile error).

- [ ] **Step 3: Implement `ChangedItemIndexSummary`**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.LibrarySearch;

public static class ChangedItemIndexSummary
{
    public static string Describe(ChangedItemIndex index)
    {
        var totalChangedItems = index.Mods.Sum(m => m.ChangedItems.Count);
        var gearMods = index.Mods.Where(m => m.Categories.Contains(ModCategory.Gear)).ToList();
        var singleSlot = gearMods.Count(m => m.SlotDiagnostic == GearSlotDiagnostic.Single);
        var multiSlot = gearMods.Count(m => m.SlotDiagnostic == GearSlotDiagnostic.Ambiguous);
        var unresolved = gearMods.Count(m => m.SlotDiagnostic == GearSlotDiagnostic.ZeroEvidence);
        var missingDirectory = gearMods.Count(m => m.SlotDiagnostic == GearSlotDiagnostic.DirectoryMissing);
        var readFailure = gearMods.Count(m => m.SlotDiagnostic == GearSlotDiagnostic.ReadFailure);

        var summary =
            $"Indexed {index.Mods.Count} of {index.TotalModsSeen} mods · " +
            $"{totalChangedItems} changed items · " +
            $"{gearMods.Count} gear mods scanned " +
            $"({singleSlot} single-slot, {multiSlot} multi-slot, {unresolved} unresolved) · " +
            $"{missingDirectory} missing directories · {readFailure} read failures";

        return index.OrphanedChangedItemEntryCount > 0
            ? summary + $" · {index.OrphanedChangedItemEntryCount} orphaned changed-item entries"
            : summary;
    }
}
```

Save at `PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndexSummary.cs`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ChangedItemIndexSummaryTests"`
Expected: PASS (all 6 tests green).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: previous count + 6 new tests, all passing.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndexSummary.cs PenumbraOrganizer.Plugin.Tests/LibrarySearch/ChangedItemIndexSummaryTests.cs
git commit -m "feat: add ChangedItemIndexSummary for Library Search refresh reporting

Derives every count from the built index itself rather than storing
parallel fields, so the rendered summary text and the underlying data
can never drift apart. Ambiguous (multi-slot) is described as a
success, never sorter-flavored failure language."
```

---

### Task 5: `LibrarySearch/LibrarySearchFilter.cs`

The filter/match engine: fixes the mod-level matching bug caught in design review (a Gear-unrelated
category match must never be gated by slot state), and computes exactly which changed items to
display per mod.

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibrarySearch/LibrarySearchFilter.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/LibrarySearch/LibrarySearchFilterTests.cs`

**Interfaces:**
- Consumes: `IndexedMod`, `IndexedChangedItem` (Task 3).
- Produces: `LibrarySearchFilter(IReadOnlySet<ModCategory> Categories, bool IncludeUnknown,
  IReadOnlySet<EquipmentSlot> Slots, bool IncludeUnresolved, string NameQuery, string ItemQuery)`,
  `LibrarySearchEngine.Matches(IndexedMod, LibrarySearchFilter) : bool`,
  `LibrarySearchEngine.DisplayedItems(IndexedMod, LibrarySearchFilter) : (IReadOnlyList<IndexedChangedItem>
  Items, bool MatchedByNameOnly)` — both used by Task 7 (UI).

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibrarySearch;

public class LibrarySearchFilterTests
{
    private static IndexedMod MakeMod(
        string name = "Test Mod",
        IEnumerable<ModCategory>? categories = null,
        bool hasUnknownFacetItems = false,
        bool matchedByNpcNameHeuristic = false,
        IEnumerable<EquipmentSlot>? equipmentSlots = null,
        IEnumerable<IndexedChangedItem>? changedItems = null) =>
        new("id", name, "Author",
            (changedItems ?? [new IndexedChangedItem("Some Item", ModCategory.Gear)]).ToList(),
            (categories ?? [ModCategory.Gear]).ToHashSet(),
            hasUnknownFacetItems, matchedByNpcNameHeuristic,
            (equipmentSlots ?? []).ToHashSet(),
            GearSlotDiagnostic.NotApplicable);

    private static LibrarySearchFilter MakeFilter(
        IEnumerable<ModCategory>? categories = null,
        bool includeUnknown = true,
        IEnumerable<EquipmentSlot>? slots = null,
        bool includeUnresolved = true,
        string nameQuery = "",
        string itemQuery = "") =>
        new(
            (categories ?? Enum.GetValues<ModCategory>()).ToHashSet(),
            includeUnknown,
            (slots ?? Enum.GetValues<EquipmentSlot>()).ToHashSet(),
            includeUnresolved,
            nameQuery,
            itemQuery);

    [Fact] // The design-review bug fix: an NPC+Gear mod must match on NPC regardless of slot state
    public void Matches_MixedGearAndNpcMod_MatchesOnNpcWithNoSlotsSelected()
    {
        var mod = MakeMod(categories: [ModCategory.Gear, ModCategory.NPC]);
        var filter = MakeFilter(categories: [ModCategory.NPC], slots: [], includeUnresolved: false);

        Assert.True(LibrarySearchEngine.Matches(mod, filter));
    }

    [Fact]
    public void Matches_GearOnlyMod_ExcludedWhenNoSlotsAndUnresolvedBothOff()
    {
        var mod = MakeMod(categories: [ModCategory.Gear], equipmentSlots: []);
        var filter = MakeFilter(categories: [ModCategory.Gear], slots: [], includeUnresolved: false);

        Assert.False(LibrarySearchEngine.Matches(mod, filter));
    }

    [Fact]
    public void Matches_NonGearMod_UnaffectedByAnySlotOrUnresolvedToggleState()
    {
        var mod = MakeMod(categories: [ModCategory.VFX]);
        var filter = MakeFilter(categories: [ModCategory.VFX], slots: [], includeUnresolved: false);

        Assert.True(LibrarySearchEngine.Matches(mod, filter));
    }

    [Fact]
    public void Matches_ZeroCategoriesSelected_NeverMatches()
    {
        var mod = MakeMod(categories: [ModCategory.Gear, ModCategory.NPC]);
        var filter = MakeFilter(categories: []);

        Assert.False(LibrarySearchEngine.Matches(mod, filter));
    }

    [Fact]
    public void Matches_MultiSlotMod_MatchesEverySlotToggleItOverlaps()
    {
        var mod = MakeMod(categories: [ModCategory.Gear], equipmentSlots: [EquipmentSlot.Top, EquipmentSlot.Feet]);

        Assert.True(LibrarySearchEngine.Matches(mod, MakeFilter(categories: [ModCategory.Gear], slots: [EquipmentSlot.Top])));
        Assert.True(LibrarySearchEngine.Matches(mod, MakeFilter(categories: [ModCategory.Gear], slots: [EquipmentSlot.Feet])));
        Assert.False(LibrarySearchEngine.Matches(mod, MakeFilter(categories: [ModCategory.Gear], slots: [EquipmentSlot.Head], includeUnresolved: false)));
    }

    [Fact]
    public void Matches_UnresolvedGearMod_HiddenWhenUnresolvedToggleOff()
    {
        var mod = MakeMod(categories: [ModCategory.Gear], equipmentSlots: []);
        var shown = MakeFilter(categories: [ModCategory.Gear], slots: [], includeUnresolved: true);
        var hidden = MakeFilter(categories: [ModCategory.Gear], slots: [], includeUnresolved: false);

        Assert.True(LibrarySearchEngine.Matches(mod, shown));
        Assert.False(LibrarySearchEngine.Matches(mod, hidden));
    }

    [Fact]
    public void Matches_UnknownFacetMod_GatedByIncludeUnknownToggle()
    {
        var mod = MakeMod(categories: [], hasUnknownFacetItems: true);

        Assert.True(LibrarySearchEngine.Matches(mod, MakeFilter(categories: [], includeUnknown: true)));
        Assert.False(LibrarySearchEngine.Matches(mod, MakeFilter(categories: [], includeUnknown: false)));
    }

    [Fact]
    public void Matches_NameQuery_IsOrdinalCaseInsensitiveAndTrimmed()
    {
        var mod = MakeMod(name: "Carlotta's Outfit");

        Assert.True(LibrarySearchEngine.Matches(mod, MakeFilter(nameQuery: "  CARLOTTA  ")));
        Assert.False(LibrarySearchEngine.Matches(mod, MakeFilter(nameQuery: "Nonexistent")));
    }

    [Fact]
    public void Matches_ItemQuery_RequiresAtLeastOneMatchingKey()
    {
        var mod = MakeMod(changedItems: [new IndexedChangedItem("Calfskin Rider's Shoes", ModCategory.Gear)]);

        Assert.True(LibrarySearchEngine.Matches(mod, MakeFilter(itemQuery: "shoes")));
        Assert.False(LibrarySearchEngine.Matches(mod, MakeFilter(itemQuery: "boots")));
    }

    [Fact]
    public void DisplayedItems_CategoryFilter_NarrowsToMatchingFacetItemsOnly()
    {
        var mod = MakeMod(
            categories: [ModCategory.Gear, ModCategory.VFX],
            changedItems:
            [
                new IndexedChangedItem("Calfskin Rider's Shoes", ModCategory.Gear),
                new IndexedChangedItem("Vfx", ModCategory.VFX),
            ]);
        var filter = MakeFilter(categories: [ModCategory.Gear]);

        var (items, matchedByNameOnly) = LibrarySearchEngine.DisplayedItems(mod, filter);

        Assert.Equal(["Calfskin Rider's Shoes"], items.Select(i => i.Key));
        Assert.False(matchedByNameOnly);
    }

    [Fact]
    public void DisplayedItems_ItemTextFilter_NarrowsToMatchingKeyItemsOnly()
    {
        var mod = MakeMod(changedItems:
        [
            new IndexedChangedItem("Calfskin Rider's Shoes", ModCategory.Gear),
            new IndexedChangedItem("Faerie Tale Prince's Vest", ModCategory.Gear),
        ]);
        var filter = MakeFilter(itemQuery: "shoes");

        var (items, _) = LibrarySearchEngine.DisplayedItems(mod, filter);

        Assert.Equal(["Calfskin Rider's Shoes"], items.Select(i => i.Key));
    }

    [Fact]
    public void DisplayedItems_NpcNameHeuristicOnlyMatch_ShowsAllItemsFlaggedAsNameMatch()
    {
        var mod = MakeMod(
            categories: [ModCategory.Face], // no item's own Facet is NPC
            matchedByNpcNameHeuristic: true,
            changedItems: [new IndexedChangedItem("Customization: Miqo'te Female Face 101", ModCategory.Face)]);
        var filter = MakeFilter(categories: [ModCategory.NPC]);

        var (items, matchedByNameOnly) = LibrarySearchEngine.DisplayedItems(mod, filter);

        Assert.Single(items);
        Assert.True(matchedByNameOnly);
    }

    [Fact]
    public void DisplayedItems_UnknownFacetItem_ShownOnlyWhenIncludeUnknownSelected()
    {
        var mod = MakeMod(
            categories: [ModCategory.Gear, ModCategory.VFX],
            hasUnknownFacetItems: true,
            changedItems:
            [
                new IndexedChangedItem("Calfskin Rider's Shoes", ModCategory.Gear),
                new IndexedChangedItem("Vfx", ModCategory.VFX),
                new IndexedChangedItem("Icon: Something", null),
            ]);

        var (withUnknown, _) = LibrarySearchEngine.DisplayedItems(mod, MakeFilter(includeUnknown: true));
        var (withoutUnknown, _) = LibrarySearchEngine.DisplayedItems(mod, MakeFilter(includeUnknown: false));

        Assert.Contains(withUnknown, i => i.Key == "Icon: Something");
        Assert.DoesNotContain(withoutUnknown, i => i.Key == "Icon: Something");
    }

    [Fact] // Slot filtering never narrows which items display -- only whether the mod appears at all
    public void DisplayedItems_GearMatchedViaSlot_ShowsAllCategoryMatchedItemsRegardlessOfSlot()
    {
        var mod = MakeMod(
            categories: [ModCategory.Gear],
            equipmentSlots: [EquipmentSlot.Feet],
            changedItems:
            [
                new IndexedChangedItem("Calfskin Rider's Shoes", ModCategory.Gear),
                new IndexedChangedItem("Faerie Tale Prince's Vest", ModCategory.Gear),
            ]);
        var filter = MakeFilter(categories: [ModCategory.Gear], slots: [EquipmentSlot.Feet]);

        var (items, _) = LibrarySearchEngine.DisplayedItems(mod, filter);

        Assert.Equal(2, items.Count);
    }
}
```

Save at `PenumbraOrganizer.Plugin.Tests/LibrarySearch/LibrarySearchFilterTests.cs`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~LibrarySearchFilterTests"`
Expected: FAIL — `LibrarySearchFilter`/`LibrarySearchEngine` do not exist (compile error).

- [ ] **Step 3: Implement `LibrarySearchFilter` and `LibrarySearchEngine`**

```csharp
using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.LibrarySearch;

public sealed record LibrarySearchFilter(
    IReadOnlySet<ModCategory> Categories,
    bool IncludeUnknown,
    IReadOnlySet<EquipmentSlot> Slots,
    bool IncludeUnresolved,
    string NameQuery,
    string ItemQuery);

public static class LibrarySearchEngine
{
    public static bool Matches(IndexedMod mod, LibrarySearchFilter filter) =>
        MatchesCategoryFilter(mod, filter) && MatchesTextFilters(mod, filter);

    public static bool MatchesCategoryFilter(IndexedMod mod, LibrarySearchFilter filter)
    {
        var matchesNonGear =
            mod.Categories.Where(c => c != ModCategory.Gear).Any(filter.Categories.Contains)
            || (filter.Categories.Contains(ModCategory.NPC) && mod.MatchedByNpcNameHeuristic)
            || (filter.IncludeUnknown && mod.HasUnknownFacetItems);

        var matchesGear =
            filter.Categories.Contains(ModCategory.Gear)
            && mod.Categories.Contains(ModCategory.Gear)
            && MatchesGearSlotFilter(mod, filter);

        return matchesNonGear || matchesGear;
    }

    public static bool MatchesGearSlotFilter(IndexedMod mod, LibrarySearchFilter filter) =>
        (filter.IncludeUnresolved && mod.EquipmentSlots.Count == 0)
        || mod.EquipmentSlots.Overlaps(filter.Slots);

    public static bool MatchesTextFilters(IndexedMod mod, LibrarySearchFilter filter)
    {
        var nameQuery = Normalize(filter.NameQuery);
        if (nameQuery.Length > 0 && !mod.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
            return false;

        var itemQuery = Normalize(filter.ItemQuery);
        if (itemQuery.Length > 0 && !mod.ChangedItems.Any(item => item.Key.Contains(itemQuery, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    public static (IReadOnlyList<IndexedChangedItem> Items, bool MatchedByNameOnly) DisplayedItems(
        IndexedMod mod, LibrarySearchFilter filter)
    {
        var itemQuery = Normalize(filter.ItemQuery);
        IReadOnlyList<IndexedChangedItem> afterItemText = itemQuery.Length > 0
            ? mod.ChangedItems.Where(item => item.Key.Contains(itemQuery, StringComparison.OrdinalIgnoreCase)).ToList()
            : mod.ChangedItems;

        var afterCategory = afterItemText
            .Where(item => (item.Facet is { } facet && filter.Categories.Contains(facet))
                            || (item.Facet is null && filter.IncludeUnknown))
            .ToList();

        var matchedByNameOnly = afterCategory.Count == 0
            && filter.Categories.Contains(ModCategory.NPC)
            && mod.MatchedByNpcNameHeuristic
            && !mod.Categories.Contains(ModCategory.NPC);

        return matchedByNameOnly ? (afterItemText, true) : (afterCategory, false);
    }

    private static string Normalize(string query) => query.Trim();
}
```

Save at `PenumbraOrganizer.Plugin/LibrarySearch/LibrarySearchFilter.cs`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~LibrarySearchFilterTests"`
Expected: PASS (all 14 tests green).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: previous count + 14 new tests, all passing.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibrarySearch/LibrarySearchFilter.cs PenumbraOrganizer.Plugin.Tests/LibrarySearch/LibrarySearchFilterTests.cs
git commit -m "feat: add LibrarySearchFilter/LibrarySearchEngine for Library Search

Fixes the design-review-caught bug where slot toggles wrongly gated
whole-mod matches instead of only the Gear path (matchesNonGear OR
matchesGear, evaluated independently). Displayed-item selection is a
strict function of (mod, filter): category/item-text filtering narrow
precisely per item; slot state only ever affects whether a mod
appears at all, never which of its items render."
```

---

### Task 6: Wire `Plugin.BuildChangedItemIndex()`

Connects the pure builder to the plugin's real IPC calls, with atomic replace-on-success semantics so
a failed refresh never discards a previously good index.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: `ChangedItemIndexBuilder.Build` (Task 3), `LibraryModEntry` (Task 3), the existing
  `GetModListAdapterIpc` field, `NpcNameListStore.Load`/`BuildMatcher` (existing, already used by
  `RunScan()`).
- Produces: `Plugin.LibraryIndex` (`ChangedItemIndex?`), `Plugin.LibraryIndexError` (`string?`),
  `Plugin.BuildChangedItemIndex()` — used by Task 7 (UI).

- [ ] **Step 1: Add the new fields and method**

In `PenumbraOrganizer.Plugin/Plugin.cs`, add `using PenumbraOrganizer.Plugin.LibrarySearch;` to the
top of the file (alongside the existing `using PenumbraOrganizer.Plugin.Organizer.Classification;` on
line 12). Add two new public properties near the existing `public readonly Organizer.OrganizerState
OrganizerState = new();` field (around line 32):

```csharp
    public LibrarySearch.ChangedItemIndex? LibraryIndex { get; private set; }
    public string? LibraryIndexError { get; private set; }
```

Then add this new public method, placed after `RunScan()` (after line 162's closing brace):

```csharp
    public void BuildChangedItemIndex()
    {
        try
        {
            var allChangedItems = new Penumbra.Api.IpcSubscribers.GetChangedItemAdapterDictionary(PluginInterface).Invoke();
            using var modList = GetModListAdapterIpc.Invoke();

            var mods = modList
                .Select(mod => new LibrarySearch.LibraryModEntry(mod.Identifier, mod.Name, mod.Author, mod.ModPath))
                .ToList();

            var npcNameListResult = NpcNameListStore.Load(NpcNameListPath, ReadEmbeddedNpcNameSeed());
            if (npcNameListResult.Warning is not null)
                Log.Warning(npcNameListResult.Warning);
            var npcNameMatcher = NpcNameListStore.BuildMatcher(npcNameListResult.Document);

            var changedItemIdentifiers = allChangedItems.Keys.ToHashSet(StringComparer.Ordinal);

            LibraryIndex = LibrarySearch.ChangedItemIndexBuilder.Build(
                mods,
                changedItemIdentifiers,
                identifier => allChangedItems.TryGetValue(identifier, out var changedItems)
                    ? changedItems.Keys
                    : Enumerable.Empty<string>(),
                npcNameMatcher);
            LibraryIndexError = null;
        }
        catch (Exception ex)
        {
            // Atomic replacement: LibraryIndex is only ever reassigned above, after every step
            // succeeds. A thrown exception here (e.g. Penumbra unavailable) leaves the previous
            // index (and its BuiltAt timestamp) exactly as it was -- a failed refresh must not
            // discard a previously good result.
            LibraryIndexError = $"Refresh failed: {ex.Message}";
            Log.Warning(ex, "Library Search index refresh failed.");
        }
    }
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: same count as Task 5's Step 5 (this task adds no new unit tests — `BuildChangedItemIndex()`
is a plugin-glue method exercised in-game, matching this codebase's existing convention that
`RunScan()`/`ExportReview()` etc. aren't unit-tested in isolation either).

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: wire Plugin.BuildChangedItemIndex

Same two bulk IPC calls RunScan() already makes. LibraryIndex is only
reassigned after a fully successful build, so a thrown exception (e.g.
Penumbra unavailable) leaves the previous index and its BuiltAt
timestamp untouched, with LibraryIndexError set for the UI to show
alongside the stale results."
```

---

### Task 7: `MainWindow.DrawSearchTab()` — the Search tab UI

The two-pane UI: category/slot toggle buttons and text inputs filter a left-hand mod list; selecting
a mod shows its changed items (narrowed per the displayed-item algorithm) on the right.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `Plugin.LibraryIndex`, `Plugin.LibraryIndexError`, `Plugin.BuildChangedItemIndex()` (Task
  6); `ChangedItemIndexSummary.Describe` (Task 4); `LibrarySearchFilter`, `LibrarySearchEngine.Matches`,
  `LibrarySearchEngine.DisplayedItems` (Task 5).

- [ ] **Step 1: Add the required usings and new private fields**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, add two new usings after the existing ones (after
line 7's `using PenumbraOrganizer.Core.Services;`):

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;
```

Add these new private fields after the existing `_npcRefreshResult` field (after line 43):

```csharp
    private string _librarySearchNameQuery = string.Empty;
    private string _librarySearchItemQuery = string.Empty;
    private readonly HashSet<ModCategory> _librarySearchCategories = new(SearchableCategories);
    private bool _librarySearchIncludeUnknown = true;
    private readonly HashSet<EquipmentSlot> _librarySearchSlots = new(Enum.GetValues<EquipmentSlot>());
    private bool _librarySearchIncludeUnresolved = true;
    private string? _librarySearchSelectedModIdentifier;

    private static readonly ModCategory[] SearchableCategories =
    [
        ModCategory.Gear, ModCategory.NPC, ModCategory.Mount, ModCategory.Minion,
        ModCategory.Animation, ModCategory.VFX, ModCategory.Furniture, ModCategory.Sound,
        ModCategory.Face, ModCategory.Hair, ModCategory.Body, ModCategory.Skin,
    ];
```

- [ ] **Step 2: Register the new tab in `Draw()`**

In `MainWindow.cs`'s `Draw()` method, change the tab list (currently lines 91-95) from:

```csharp
                DrawScanTab();
                DrawProtectTab();
                DrawSortTab();
                DrawReviewTab();
                DrawHistoryTab();
```

to:

```csharp
                DrawScanTab();
                DrawProtectTab();
                DrawSortTab();
                DrawReviewTab();
                DrawHistoryTab();
                DrawSearchTab();
```

- [ ] **Step 3: Add `DrawSearchTab()` and its helper methods**

Add this new method anywhere among the other `Draw*Tab()` private methods (e.g. after
`DrawHistoryTab()`):

```csharp
    private void DrawSearchTab()
    {
        using var tab = ImRaii.TabItem("Search");
        if (!tab)
            return;

        using (PluginTheme.PrimaryButton())
        {
            if (ImGui.Button("Build/Refresh Index"))
                _plugin.BuildChangedItemIndex();
        }

        if (_plugin.LibraryIndexError is { } error)
            ImGui.TextColored(PluginTheme.CollisionBad, error);

        if (_plugin.LibraryIndex is not { } index)
        {
            ImGui.TextUnformatted("Click Build/Refresh Index to search your mod library.");
            return;
        }

        ImGui.TextWrapped(ChangedItemIndexSummary.Describe(index));
        ImGui.Text($"Index built at {index.BuiltAt:HH:mm:ss}");
        ImGui.Spacing();

        ImGui.InputText("Mod name contains", ref _librarySearchNameQuery, 256);
        ImGui.InputText("Item contains", ref _librarySearchItemQuery, 256);
        ImGui.Spacing();

        ImGui.TextUnformatted("Categories:");
        foreach (var category in SearchableCategories)
            DrawCategoryToggle(category);
        DrawUnknownToggle();

        if (_librarySearchCategories.Contains(ModCategory.Gear))
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Slots:");
            foreach (var slot in Enum.GetValues<EquipmentSlot>())
                DrawSlotToggle(slot);
            var includeUnresolved = _librarySearchIncludeUnresolved;
            if (ImGui.Checkbox("Unresolved##slot-unresolved", ref includeUnresolved))
                _librarySearchIncludeUnresolved = includeUnresolved;
        }

        ImGui.Spacing();

        var filter = new LibrarySearchFilter(
            _librarySearchCategories, _librarySearchIncludeUnknown,
            _librarySearchSlots, _librarySearchIncludeUnresolved,
            _librarySearchNameQuery, _librarySearchItemQuery);

        var matches = index.Mods.Where(mod => LibrarySearchEngine.Matches(mod, filter)).ToList();

        // Same flag combination as PathTreeView.cs (the only other table in this codebase) --
        // Resizable | SizingStretchProp, no per-column width flags, for proportional stretch.
        using var columns = ImRaii.Table("SearchColumns", 2,
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 420));
        if (!columns)
            return;

        ImGui.TableSetupColumn("Mods");
        ImGui.TableSetupColumn("Changed items");
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        using (var left = ImRaii.Child("SearchModList", new Vector2(0, 400), border: true))
        {
            if (left)
            {
                if (matches.Count == 0)
                {
                    ImGui.TextUnformatted("No mods found.");
                }
                else
                {
                    foreach (var mod in matches)
                    {
                        var isSelected = mod.Identifier == _librarySearchSelectedModIdentifier;
                        if (ImGui.Selectable($"{mod.Name} ({mod.Author})##search-{mod.Identifier}", isSelected))
                            _librarySearchSelectedModIdentifier = mod.Identifier;
                    }
                }
            }
        }

        ImGui.TableNextColumn();
        using (var right = ImRaii.Child("SearchItemList", new Vector2(0, 400), border: true))
        {
            if (right)
            {
                var selectedMod = matches.FirstOrDefault(m => m.Identifier == _librarySearchSelectedModIdentifier);
                if (selectedMod is null)
                {
                    ImGui.TextUnformatted("Select a mod to see its changed items.");
                }
                else
                {
                    var (items, matchedByNameOnly) = LibrarySearchEngine.DisplayedItems(selectedMod, filter);
                    if (matchedByNameOnly)
                        ImGui.TextColored(PluginTheme.CollisionBad, "Matched by mod name, not by item.");
                    foreach (var item in items)
                        ImGui.TextUnformatted(item.Key);
                }
            }
        }
    }

    private void DrawCategoryToggle(ModCategory category)
    {
        var isChecked = _librarySearchCategories.Contains(category);
        if (ImGui.Checkbox($"{category}##search-category-{category}", ref isChecked))
        {
            if (isChecked)
                _librarySearchCategories.Add(category);
            else
                _librarySearchCategories.Remove(category);
        }
        ImGui.SameLine();
    }

    private void DrawUnknownToggle()
    {
        var isChecked = _librarySearchIncludeUnknown;
        if (ImGui.Checkbox("Unknown##search-category-unknown", ref isChecked))
            _librarySearchIncludeUnknown = isChecked;
    }

    private void DrawSlotToggle(EquipmentSlot slot)
    {
        var isChecked = _librarySearchSlots.Contains(slot);
        if (ImGui.Checkbox($"{SlotLabel(slot)}##search-slot-{slot}", ref isChecked))
        {
            if (isChecked)
                _librarySearchSlots.Add(slot);
            else
                _librarySearchSlots.Remove(slot);
        }
        ImGui.SameLine();
    }

    private static string SlotLabel(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Head => "Hats",
        EquipmentSlot.Top => "Tops",
        EquipmentSlot.Hands => "Hands",
        EquipmentSlot.Legs => "Bottoms",
        EquipmentSlot.Feet => "Feet",
        EquipmentSlot.Ears => "Earrings",
        EquipmentSlot.Neck => "Necklaces",
        EquipmentSlot.Wrists => "Bracelets",
        EquipmentSlot.Rings => "Rings",
        _ => slot.ToString(),
    };
```

- [ ] **Step 4: Build to confirm it compiles**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: same count as Task 6's Step 3 (this task adds no new unit tests — ImGui rendering code in
this codebase is verified in-game, matching the existing convention for every other `Draw*Tab()`
method).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add Search tab UI for Library Search

Two-pane layout: category/slot toggles and text inputs filter a flat,
directly-rendered mod list on the left (same pattern PathTreeView
already uses for the Review Changes table -- no ImGuiListClipper
exists anywhere in this codebase, so this doesn't invent it); the
right pane shows the selected mod's changed items via the displayed-
item algorithm, always bounded to one mod's own item count."
```

---

### Task 8: Final whole-branch review

Matches this project's established process (see `[[plugin-mvp-scope-and-status]]` memory) of a final
review across the entire branch's diff after all tasks land, not just per-task review — this is what
has caught every cross-task issue in prior features (wrong icon paths, dead code, a missed
`PenumbraPathSemantics` comparison, etc.).

- [ ] **Step 1: Confirm a clean full build and full test run from the worktree root**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: 0 failures; total count = Task 1's baseline + 12 (`ClassifyKeyFacet`) + 8
(`ChangedItemIndexBuilder`) + 6 (`ChangedItemIndexSummary`) + 14 (`LibrarySearchFilter`) = baseline + 40.

- [ ] **Step 2: Review the whole-branch diff against the spec**

Run: `git diff main --stat` (or the equivalent against whatever base branch this worktree branched
from) and read the full diff. Confirm against
`docs/superpowers/specs/2026-07-21-library-search-changed-item-lookup-design.md`:
- `ModTypeClassifier.Classify`'s body has zero diff lines (only a new sibling method was added).
- No new IPC calls were introduced beyond the two `RunScan()` already used.
- `GearSlotDiagnostic`'s relocation didn't leave any stale `Organizer.GearSlotDiagnostic`-qualified
  reference anywhere (`git grep "Organizer.GearSlotDiagnostic"` should return nothing).
- No injected interfaces, no async/await, no `Task.Run`/threading anywhere in the new
  `LibrarySearch/` code (per the spec's "Explicitly declined" section).

- [ ] **Step 3: In-game verification checklist (manual, requires FFXIV + Penumbra running)**

- Open the plugin, click the new Search tab's "Build/Refresh Index" button against a real mod
  library; confirm the summary line renders with plausible, non-negative counts.
- Type a known item name (e.g. a boots/shoes item from an installed Gear mod) into "Item contains";
  confirm the matching mod appears in the left pane and the specific item text appears on the right
  once selected.
- Toggle off all categories except one (e.g. only NPC); confirm only NPC-matching mods remain in the
  left pane.
- With Gear selected, toggle off every slot and Unresolved; confirm the left pane's Gear-only mods
  disappear, while any mixed Gear+NPC/VFX/etc. mods remain visible (this is the design-review bug fix
  — the concrete manual check for it).
- Click "Build/Refresh Index" again after temporarily renaming/breaking Penumbra's connection (or
  simply confirm by code inspection per Step 2 if this isn't reproducible live) to confirm a failed
  refresh leaves the previous index and summary visible rather than blanking the tab.

- [ ] **Step 4: Update project status documentation**

Add a new entry to `docs/ROADMAP.md` (or wherever this project's shipped-feature log lives — check the
existing file for the right section) noting: "Library Search (reverse changed-item lookup), design
`docs/superpowers/specs/2026-07-21-library-search-changed-item-lookup-design.md`, plan
`docs/superpowers/plans/2026-07-21-library-search-changed-item-lookup.md` — shipped [date], N tests
added, not yet in-game verified" (or "in-game verified [date]" if Step 3 was completed live before
this task closes).

- [ ] **Step 5: Commit the roadmap update**

```bash
git add docs/ROADMAP.md
git commit -m "docs: record Library Search feature in ROADMAP"
```
