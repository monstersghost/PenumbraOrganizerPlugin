# Split Hair By Hair Number Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a third Sort-tab split checkbox so a hair mod that unambiguously targets one vanilla hair slot is proposed into `Hair/115/` instead of flat `Hair/`.

**Architecture:** The existing "Split gear by equipment slot" feature is the template, and this is deliberately its mirror image. A new `ModHairFileReader` reads hair numbers out of each mod's Penumbra config JSON with the same return contract the equipment reader uses; the shared config-file discovery and recursion is first extracted into a `ModConfigFileWalker` both readers consume. A new `EnrichHairSubCategory`, a new `FlattenHairSubCategory`, and a `splitHair` axis on `OrganizerState.Sort` carry it to the proposed path.

**Tech Stack:** C# / .NET 10, Dalamud.NET.Sdk 15.0.0, `System.Text.Json`, ImGui via `Dalamud.Bindings.ImGui`, xUnit for tests.

**Spec:** `docs/superpowers/specs/2026-11-01-hair-number-sorting-design.md`

## Global Constraints

- **Folder name is the bare number, not padded, not prefixed**: the proposed path is `Hair/115/`. Not `Hair/0115/`, not `Hair/Hair 115/`. The checkbox label carries the "hair number" meaning so the folder does not repeat it.
- **Exactly one** resolved hair number gets a subfolder. Two or more, zero, or a failed read all leave the mod directly in `Hair/`. There is no `Mixed` bucket and no lowest-number-wins rule. This mirrors `EnrichGearSubCategory`, which assigns only on `Count == 1`.
- **Race is ignored.** `c0101` and `c0801` paths naming `h0115` contribute the single number 115, not two entries. Without this the common multi-clan hair mod would read as ambiguous and get no subfolder at all.
- The checkbox **defaults off**, like `SplitGear`, and is **session state, not persisted config**, like the rest of `SortPanel`'s selection.
- **No `HairNumberDiagnostic`.** Explicitly out of scope (maintainer decision, 2026-11-01). Do not touch `ChangedItemIndex`, `ChangedItemIndexSummary`, `LibrarySearchFilter`, `IndexProcessor` or `OrganizerExportFormatter`. Do not add a hair-number search facet.
- `ModEquipmentFileReader`'s **public contract must not change**, and `ModEquipmentFileReaderTests` must keep passing **without edits**. That is the only check that Task 1's refactor preserved behaviour.
- Disk I/O for hair must be **gated on `Category == Hair`**, matching how the gear read is gated, so non-hair mods never touch disk for this.
- All strings in this feature are ASCII. If a non-ASCII character is ever needed, write it as a `\uXXXX` escape rather than pasting the glyph - this repo has been bitten three times by a pasted codepoint reaching code as the wrong value while the test still passed.
- Build and test commands, run from the repository root:
  - `dotnet build PenumbraOrganizer.Plugin.sln`
  - `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`

## File Structure

| File | Responsibility |
|---|---|
| `PenumbraOrganizer.Plugin/Organizer/Classification/ModConfigFileWalker.cs` | **Create.** Knows Penumbra's on-disk config layout and nothing else: which files to read, how to recurse them, and how to signal a read failure. |
| `PenumbraOrganizer.Plugin/Organizer/Classification/ModHairFileReader.cs` | **Create.** Knows how a hair number is spelled in a game path and in an `Est` manipulation. |
| `PenumbraOrganizer.Plugin/Organizer/Classification/ModEquipmentFileReader.cs` | **Modify.** Keeps its public contract; its traversal is replaced by walker calls. |
| `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs` | **Modify.** `ModTypeFolders.GetFolder` gains a hair arm plus `IsHairNumber`; `ModTypeClassifier` gains `EnrichHairSubCategory`. |
| `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs` | **Modify.** One new gated block beside the gear one. |
| `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` | **Modify.** `splitHair` parameter, third flattener. |
| `PenumbraOrganizer.Plugin/Windows/SortPanel.cs` | **Modify.** Fourth field on `SortSelection`, third checkbox. |
| `PenumbraOrganizer.Plugin/Windows/HelpTopics.cs`, `HelpTab.cs`, `HelpTopicUsage.cs` | **Modify.** Register `sort.split-hair`. |
| Help resource file | **Modify.** The topic's title, short and body. Task 7 locates it. |
| `docs/HOW_SORTING_WORKS.md`, `docs/USER_GUIDE.md` | **Modify.** Document the third split. |

Tasks are ordered so every commit builds and tests green on its own. Tasks 1-4 are pure logic with no UI; 5-7 carry it to the screen; 8 is documentation and the in-game check.

---

### Task 1: Extract `ModConfigFileWalker`

A pure refactor, no behaviour change, done first so Task 2 has something to build on. The knowledge being moved is expensive: the file-discovery method encodes Penumbra 1.7.0's layout change (2026-07-20, `meta.json` replacing `default_mod.json` + `group_*.json`) and the deliberate choice to support both so a build spanning that update does not regress for testers. Carry the comments across; do not paraphrase them.

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/ModConfigFileWalker.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModEquipmentFileReader.cs`
- Test: no new tests. The gate is `ModEquipmentFileReaderTests` passing unedited.

**Interfaces:**
- Consumes: nothing.
- Produces, both used by Task 2 and by the rewritten equipment reader:
  - `ModConfigFileWalker.TryWalk(DirectoryInfo modDirectory, Action<string> onFileKey, Action<JsonElement> onManipulation)` returns `bool` - `false` means a file could not be enumerated, read or parsed. `onFileKey` receives each `Files` key **already normalised** so separators are `/`. `onManipulation` receives each element of each `Manipulations` array. A non-existent directory returns `true` with no callbacks.
  - `ModConfigFileWalker.CountConfigFiles(DirectoryInfo modDirectory)` returns `int`, `0` on a missing directory or an enumeration failure.

- [ ] **Step 1: Create the walker**

```csharp
using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// Walks a mod's Penumbra config JSON, handing each declared game path and each meta manipulation
/// to a callback.
/// </summary>
/// <remarks>
/// Extracted from <see cref="ModEquipmentFileReader"/> once <see cref="ModHairFileReader"/> became
/// a second consumer. Everything here is about Penumbra's on-disk LAYOUT; what a given path or
/// manipulation MEANS belongs to the readers.
/// </remarks>
public static class ModConfigFileWalker
{
    /// <summary>
    /// Returns false if any config file could not be enumerated, read, or parsed - an
    /// untrustworthy partial walk, which every caller must treat as "no evidence" rather than as a
    /// confident empty answer. A non-existent directory returns true with no callbacks: that is
    /// not a read failure, just nothing to find.
    /// </summary>
    public static bool TryWalk(
        DirectoryInfo modDirectory,
        Action<string> onFileKey,
        Action<JsonElement> onManipulation)
    {
        if (!modDirectory.Exists)
            return true;

        IReadOnlyList<FileInfo> configFiles;
        try
        {
            configFiles = DiscoverConfigFiles(modDirectory);
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            return false;
        }

        foreach (var file in configFiles)
        {
            try
            {
                using var stream = File.OpenRead(file.FullName);
                using var document = JsonDocument.Parse(stream);
                WalkElement(document.RootElement, onFileKey, onManipulation);
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex) || ex is JsonException)
            {
                return false;
            }
        }

        return true;
    }

    // Counts the config file(s) TryWalk would read, without reading or parsing them. A caller that
    // gets an empty walk cannot otherwise tell "no config files were found here at all" from
    // "config files were read and had nothing recognized" - very different root causes.
    public static int CountConfigFiles(DirectoryInfo modDirectory)
    {
        if (!modDirectory.Exists)
            return 0;

        try
        {
            return DiscoverConfigFiles(modDirectory).Count;
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            return 0;
        }
    }

    // Penumbra 1.7.0 (2026-07-20) replaced the older default_mod.json + group_*.json layout with
    // a single meta.json per mod ("This version gets rid of the group- and default option JSON
    // files and instead stores all this information in the meta.json again" - Penumbra's own
    // changelog). The two layouts are mutually exclusive after Penumbra's own migration, but both
    // are checked here so a plugin build spanning the update doesn't regress for a tester still on
    // the older Penumbra version.
    private static IReadOnlyList<FileInfo> DiscoverConfigFiles(DirectoryInfo modDirectory)
    {
        // Materialized eagerly: DirectoryInfo.EnumerateFiles is lazy, so a permission error during
        // enumeration would otherwise surface outside the caller's catch.
        var metaJson = new FileInfo(Path.Combine(modDirectory.FullName, "meta.json"));
        if (metaJson.Exists)
            return [metaJson];

        var files = new List<FileInfo>();
        var defaultMod = new FileInfo(Path.Combine(modDirectory.FullName, "default_mod.json"));
        if (defaultMod.Exists)
            files.Add(defaultMod);
        files.AddRange(modDirectory.EnumerateFiles("group_*.json"));
        return files;
    }

    private static bool IsExpectedFileSystemException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException; // UnauthorizedAccessException does NOT
                                                         // derive from IOException in .NET

    private static void WalkElement(
        JsonElement element,
        Action<string> onFileKey,
        Action<JsonElement> onManipulation)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return; // guards every TryGetProperty call below against a non-object element

        if (element.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            // Normalised here rather than in each reader: every consumer wants '/' separators, and
            // Penumbra writes some keys with '\'.
            foreach (var entry in files.EnumerateObject())
                onFileKey(entry.Name.Replace('\\', '/'));
        }

        if (element.TryGetProperty("Manipulations", out var manipulations)
            && manipulations.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in manipulations.EnumerateArray())
                onManipulation(item);
        }

        WalkChildArray(element, "Options", onFileKey, onManipulation);
        WalkChildArray(element, "Containers", onFileKey, onManipulation);

        // Penumbra 1.7.0's meta.json shape: the mod's default option's Files/Manipulations live
        // nested one level under "DefaultData" (an object, not an array like Options/Containers),
        // and every group that used to be its own group_*.json file is now one entry in a
        // top-level "Groups" array - each group entry has the same Options/Containers shape
        // already handled above, so recursing into it needs no further special-casing.
        if (element.TryGetProperty("DefaultData", out var defaultData)
            && defaultData.ValueKind == JsonValueKind.Object)
            WalkElement(defaultData, onFileKey, onManipulation);
        WalkChildArray(element, "Groups", onFileKey, onManipulation);
    }

    private static void WalkChildArray(
        JsonElement element,
        string propertyName,
        Action<string> onFileKey,
        Action<JsonElement> onManipulation)
    {
        if (!element.TryGetProperty(propertyName, out var children) || children.ValueKind != JsonValueKind.Array)
            return;

        // Genuinely recursive (calls WalkElement, not just the Files/Manipulations step) - real
        // mods checked across ~2,280 combined mods never nest beyond one level, but this doesn't
        // silently stop early if a future/unusual mod does.
        foreach (var child in children.EnumerateArray())
            WalkElement(child, onFileKey, onManipulation);
    }
}
```

- [ ] **Step 2: Rewrite `ModEquipmentFileReader` on top of the walker**

Replace the entire body of `ModEquipmentFileReader.cs` with this. Both public methods keep their exact signatures and contracts; the `Est` comment is updated to point at the new reader, per the spec.

```csharp
using System.Text.Json;
using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public static class ModEquipmentFileReader
{
    // Returns every resolved equipment/accessory slot found across the mod's files.
    // - null: some file could not be read, parsed, or enumerated — an untrustworthy partial
    //   result, treated as "no subcategory," never as a confident (possibly wrong) answer.
    // - non-null (possibly empty): every file was read successfully; empty means none of them
    //   carried recognized equipment/accessory path or manipulation data.
    public static IReadOnlySet<EquipmentSlot>? ReadEquipmentSlots(DirectoryInfo modDirectory)
    {
        var slots = new HashSet<EquipmentSlot>();

        var ok = ModConfigFileWalker.TryWalk(
            modDirectory,
            path => CollectFromFileKey(path, slots),
            item => CollectFromManipulation(item, slots));

        return ok ? slots : null;
    }

    // Counts the config file(s) the walker would read, without reading or parsing them.
    // ReadEquipmentSlots collapses "no config files were found here at all" and "config files
    // were found and read but had no recognized equipment data" into the same empty result - this
    // lets a caller (diagnostics) tell the two apart, since they point at very different root
    // causes (a naming/location mismatch for this mod's config files vs. genuinely no equipment
    // data in files that were actually read).
    public static int CountConfigFiles(DirectoryInfo modDirectory) =>
        ModConfigFileWalker.CountConfigFiles(modDirectory);

    private static void CollectFromFileKey(string path, HashSet<EquipmentSlot> slots)
    {
        // Already '/'-normalised by the walker.
        if (!path.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("chara/accessory/", StringComparison.OrdinalIgnoreCase))
            return;

        var fileName = Path.GetFileName(path);
        if (EquipmentSlotMapper.ExtractSlotFromFileName(fileName) is { } slot)
            slots.Add(slot);
    }

    private static void CollectFromManipulation(JsonElement item, HashSet<EquipmentSlot> slots)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;
        if (!item.TryGetProperty("Type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
            return;
        if (!item.TryGetProperty("Manipulation", out var manipulation) || manipulation.ValueKind != JsonValueKind.Object)
            return;

        // Real shape, confirmed against two independent real mod libraries: the slot
        // lives nested under "Manipulation", not as a direct property of the array
        // element, and the field name depends on "Type" — Eqp/Eqdp use "Slot", Imc uses
        // "EquipSlot". "Est" also has a "Slot", but it names a customization slot
        // (Hair/Face), not equipment — deliberately excluded here by only recognizing the
        // three equipment-relevant types. Est is NOT useless, though: ModHairFileReader
        // reads exactly those entries to resolve a hair number.
        var slotFieldName = typeProp.GetString() switch
        {
            "Eqp" or "Eqdp" => "Slot",
            "Imc" => "EquipSlot",
            _ => null,
        };
        if (slotFieldName is null)
            return;

        if (manipulation.TryGetProperty(slotFieldName, out var slotProp)
            && slotProp.ValueKind == JsonValueKind.String
            && EquipmentSlotMapper.MapManipulationSlot(slotProp.GetString()!) is { } slot)
            slots.Add(slot);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`

Expected: build succeeded, 0 errors.

- [ ] **Step 4: Run the equipment reader's tests, unedited**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModEquipmentFileReaderTests"`

Expected: PASS, every test, with **no edits to the test file**. If a test fails, the refactor changed behaviour - fix the walker, never the test. The likely culprits are the `!modDirectory.Exists` early return (must yield an empty set, not `null`) and the `'\\'` normalisation.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`

Expected: all tests pass. `IndexProcessor` also calls `ReadEquipmentSlots`, so `IndexProcessorTests` and `LibraryWorkPurityTests` are part of this gate.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModConfigFileWalker.cs PenumbraOrganizer.Plugin/Organizer/Classification/ModEquipmentFileReader.cs
git commit -m "refactor: extract ModConfigFileWalker from the equipment reader

The config-file discovery and recursion encode Penumbra's 1.7.0 layout
change and the decision to support both layouts. The hair reader needs
the same walk, and this knowledge should not exist twice.

ModEquipmentFileReaderTests passes unedited, which is the check.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: `ModHairFileReader`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/ModHairFileReader.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModHairFileReaderTests.cs`

**Interfaces:**
- Consumes: `ModConfigFileWalker.TryWalk` from Task 1.
- Produces: `ModHairFileReader.ReadHairNumbers(DirectoryInfo modDirectory)` returning `IReadOnlySet<int>?` - `null` on read failure, otherwise the distinct hair numbers found. Task 4 and Task 5 call this.

**On the `Est` manipulation:** its shape is corroborated by a fixture already in this repo, in `ModEquipmentFileReaderTests.ReadEquipmentSlots_EstManipulation_ContributesNothing`:

```
{"Type":"Est","Manipulation":{"Entry":161,"Gender":"Female","Race":"Miqote","SetId":157,"Slot":"Hair"}}
```

`SetId` (157) is the hair number. `Entry` (161) is the EST skeleton row it maps to, which is why the two differ. **Read `SetId`, never `Entry`.** Task 8 verifies this against a real library.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModHairFileReaderTests.cs`. The temp-directory and `WriteJson` helpers are copied from `ModEquipmentFileReaderTests` on purpose - that file already establishes the pattern for this kind of fixture.

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

using PenumbraOrganizer.Plugin.Organizer.Classification;

public class ModHairFileReaderTests
{
    private static DirectoryInfo MakeTempModDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private static void WriteJson(DirectoryInfo modDirectory, string fileName, string json) =>
        File.WriteAllText(Path.Combine(modDirectory.FullName, fileName), json);

    [Fact]
    public void ReadHairNumbers_SingleNumberFromFiles_Resolves()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/hair.mdl"},"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_SameHairAcrossManyRaceCodes_CollapsesToOneNumber()
    {
        // The common case by far: one hair shipped for every clan. Without discarding the c####
        // race code this would read as ambiguous and the mod would get no subfolder at all.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{
              "chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl",
              "chara/human/c0801/obj/hair/h0115/model/c0801h0115_hir.mdl":"files/b.mdl",
              "chara/human/c1401/obj/hair/h0115/texture/c1401h0115_hir_n.tex":"files/c.tex"
            },"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_TwoDistinctNumbers_ReturnsBoth()
    {
        // The reader reports what it found; deciding that two numbers means "no subfolder" is
        // EnrichHairSubCategory's job, not this one's.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{
              "chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl",
              "chara/human/c0101/obj/hair/h0009/model/c0101h0009_hir.mdl":"files/b.mdl"
            },"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([9, 115], result!.Order());
    }

    [Fact]
    public void ReadHairNumbers_BackslashSeparators_StillResolve()
    {
        // Penumbra writes some Files keys with '\'. The walker normalises them.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara\\human\\c0101\\obj\\hair\\h0115\\model\\c0101h0115_hir.mdl":"files/hair.mdl"},"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_EstManipulationWithHairSlot_ResolvesFromSetId()
    {
        // SetId is the hair number; Entry is the EST skeleton row it maps to. Reading Entry would
        // silently produce a plausible-looking wrong number, which is why they differ here.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Type":"Est","Manipulation":{"Entry":161,"Gender":"Female","Race":"Miqote","SetId":157,"Slot":"Hair"}}]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([157], result);
    }

    [Fact]
    public void ReadHairNumbers_EstManipulationWithFaceSlot_ContributesNothing()
    {
        // Est covers Face too. Only Hair is ours.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Type":"Est","Manipulation":{"Entry":4,"Gender":"Male","Race":"Hyur","SetId":201,"Slot":"Face"}}]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadHairNumbers_EquipmentPaths_ContributeNothing()
    {
        // A gear mod must not acquire a hair number.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"files/sho.mdl"},"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadHairNumbers_MetaJsonLayout_ReachesNestedGroupsAndDefaultData()
    {
        // Penumbra 1.7.0's single-file layout. A number reachable only through Groups/Options or
        // only through DefaultData must still be found.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """
            {
              "DefaultData":{"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl"},"Manipulations":[]},
              "Groups":[{"Options":[{"Files":{"chara/human/c0101/obj/hair/h0009/model/c0101h0009_hir.mdl":"files/b.mdl"},"Manipulations":[]}]}]
            }
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([9, 115], result!.Order());
    }

    [Fact]
    public void ReadHairNumbers_LegacyGroupFiles_AreRead()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[]}
            """);
        WriteJson(mod, "group_001_style.json", """
            {"Options":[{"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl"},"Manipulations":[]}]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_MalformedJson_ReturnsNull()
    {
        // An untrustworthy partial read must never look like a confident answer.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", "{\"Files\":{");

        Assert.Null(ModHairFileReader.ReadHairNumbers(mod));
    }

    [Fact]
    public void ReadHairNumbers_MissingDirectory_ReturnsEmptyNotNull()
    {
        // No directory is "nothing to find", not a read failure - the same distinction the
        // equipment reader draws, and the one the scan's diagnostics rely on.
        var mod = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N")));

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadHairNumbers_NonHairHumanPaths_ContributeNothing()
    {
        // chara/human also carries face, body, tail and ear paths. Only obj/hair is ours.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0101/obj/face/f0002/model/c0101f0002_fac.mdl":"files/f.mdl"},"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModHairFileReaderTests"`

Expected: FAIL at compile time with `CS0103 The name 'ModHairFileReader' does not exist`. A compile failure is the correct red here.

- [ ] **Step 3: Write the reader**

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// Reads the vanilla hair numbers a mod targets, for the Sort tab's "Split hair by hair number".
/// </summary>
/// <remarks>
/// The hair mirror of <see cref="ModEquipmentFileReader"/>, sharing its walker and its return
/// contract. What "one number means a subfolder, two means none" is lives in
/// <c>ModTypeClassifier.EnrichHairSubCategory</c>, not here: this reports evidence, it does not
/// make the filing decision.
/// </remarks>
public static class ModHairFileReader
{
    // Anchored at the start: Files keys are full game paths, and an unanchored match would accept
    // a path that merely CONTAINS this shape. The c#### race code is matched so the path shape is
    // actually verified, then discarded - one hair shipped for eight clans is one number, not eight.
    private static readonly Regex HairPath = new(
        @"^chara/human/c\d{4}/obj/hair/h(\d{4})/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Returns every hair number found across the mod's files.
    /// <list type="bullet">
    /// <item>null - some file could not be read, parsed, or enumerated. An untrustworthy partial
    /// result, treated as "no subcategory", never as a confident (possibly wrong) answer.</item>
    /// <item>non-null, possibly empty - every file read successfully; empty means none carried
    /// recognized hair path or Est manipulation data.</item>
    /// </list>
    /// </summary>
    public static IReadOnlySet<int>? ReadHairNumbers(DirectoryInfo modDirectory)
    {
        var numbers = new HashSet<int>();

        var ok = ModConfigFileWalker.TryWalk(
            modDirectory,
            path => CollectFromFileKey(path, numbers),
            item => CollectFromManipulation(item, numbers));

        return ok ? numbers : null;
    }

    private static void CollectFromFileKey(string path, HashSet<int> numbers)
    {
        // Already '/'-normalised by the walker.
        var match = HairPath.Match(path);
        if (!match.Success)
            return;

        // Four ASCII digits by construction of the pattern, so this cannot overflow or fail.
        numbers.Add(int.Parse(match.Groups[1].ValueSpan, provider: System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void CollectFromManipulation(JsonElement item, HashSet<int> numbers)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;
        if (!item.TryGetProperty("Type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
            return;
        if (typeProp.GetString() != "Est")
            return;
        if (!item.TryGetProperty("Manipulation", out var manipulation) || manipulation.ValueKind != JsonValueKind.Object)
            return;

        // Est covers customization slots generally - Hair and Face. Only Hair is ours.
        if (!manipulation.TryGetProperty("Slot", out var slotProp)
            || slotProp.ValueKind != JsonValueKind.String
            || slotProp.GetString() != "Hair")
            return;

        // SetId is the hair number. "Entry" on the same object is the EST skeleton row the hair
        // maps to - a different, equally plausible-looking integer, so reading it would be a
        // silent wrong answer rather than a visible failure.
        if (manipulation.TryGetProperty("SetId", out var setId)
            && setId.ValueKind == JsonValueKind.Number
            && setId.TryGetInt32(out var number)
            && number > 0)
            numbers.Add(number);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModHairFileReaderTests"`

Expected: PASS, 12 tests.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`

Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModHairFileReader.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModHairFileReaderTests.cs
git commit -m "feat: read hair numbers from a mod's config files

Hair paths and Est manipulations, sharing the equipment reader's walker
and its null-on-read-failure contract. Race codes are discarded so one
hair shipped for every clan reads as one number rather than eight.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Teach `ModTypeFolders.GetFolder` about hair

Without this the feature **crashes inside the sort**. `GetFolder` enumerates valid `(category, subCategory)` pairs explicitly and its final arm throws `ArgumentOutOfRangeException`, with a comment saying that is on purpose: an open-ended `$"{category}/{subCategory}"` "would silently accept a nonsense combination a classifier bug could produce (e.g. Gear + 'Bosses') instead of failing fast during development."

Hair numbers are an open set of several hundred values, so they cannot be enumerated like the nine gear slots. Validate the **shape** instead, which keeps the fail-fast property for a genuinely nonsense pairing.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs:20-29` (the `GetFolder` switch)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ModTypeFolders.GetFolder(ModCategory.Hair, "115")` returns `"Hair/115"`. Task 6's sorting relies on this.

- [ ] **Step 1: Write the failing tests**

Append to the existing `ModTypeClassifierTests` class.

```csharp
    [Fact]
    public void GetFolder_HairWithANumber_NestsUnderHair()
    {
        Assert.Equal("Hair/115", ModTypeFolders.GetFolder(ModCategory.Hair, "115"));
        Assert.Equal("Hair/9", ModTypeFolders.GetFolder(ModCategory.Hair, "9"));
        Assert.Equal("Hair/9999", ModTypeFolders.GetFolder(ModCategory.Hair, "9999"));
    }

    [Fact]
    public void GetFolder_HairWithNoSubCategory_IsStillFlatHair()
    {
        // The split-off case, and every hair mod whose number could not be resolved.
        Assert.Equal("Hair", ModTypeFolders.GetFolder(ModCategory.Hair, null));
    }

    [Theory]
    [InlineData("Bosses")]    // a classifier bug crossing NPC vocabulary into Hair
    [InlineData("0115")]      // zero-padded: EnrichHairSubCategory never produces this, so it is a bug
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("115a")]
    [InlineData("12345")]     // five digits is not a hair number
    [InlineData("-1")]
    public void GetFolder_HairWithANonNumberSubCategory_Throws(string subCategory)
    {
        // The fail-fast property the switch's own comment is protecting. An open-ended arm here
        // would silently create a nonsense folder instead.
        Assert.Throws<ArgumentOutOfRangeException>(() => ModTypeFolders.GetFolder(ModCategory.Hair, subCategory));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModTypeClassifierTests"`

Expected: the three new `GetFolder_HairWithANumber_*` assertions FAIL with `ArgumentOutOfRangeException: Unsupported subcategory '115' for Hair`. `GetFolder_HairWithNoSubCategory_IsStillFlatHair` and the throwing theory should already PASS - they pin behaviour that must survive this change.

- [ ] **Step 3: Add the arm and the guard**

In `ModTypeFolders`, add the hair arm to the switch immediately after the Gear arm, and the helper below it:

```csharp
        (ModCategory.Gear, "Head" or "Top" or "Hands" or "Legs" or "Feet" or "Ears" or "Neck" or "Wrists" or "Rings")
            => $"{ModCategory.Gear}/{subCategory}",
        // Hair cannot be enumerated the way the nine gear slots and three NPC kinds are - there are
        // several hundred vanilla hair numbers. The shape is validated instead, which keeps the
        // fail-fast intent above: Hair + "Bosses" still throws.
        (ModCategory.Hair, _) when IsHairNumber(subCategory) => $"{ModCategory.Hair}/{subCategory}",
        _ => throw new ArgumentOutOfRangeException(
            nameof(subCategory), subCategory, $"Unsupported subcategory '{subCategory}' for {category}."),
    };

    // One to four ASCII digits, no leading zero. EnrichHairSubCategory is the only producer and
    // formats an int invariantly, so the two agree by construction - this guard is what makes a
    // future second producer fail loudly rather than quietly creating a "Hair/0115" folder beside
    // an existing "Hair/115" one.
    private static bool IsHairNumber(string? subCategory) =>
        subCategory is { Length: >= 1 and <= 4 }
        && subCategory[0] is >= '1' and <= '9'
        && subCategory.All(char.IsAsciiDigit);
```

Note `char.IsAsciiDigit`, not `char.IsDigit`: the latter accepts non-ASCII decimal digits, which would land a folder name this repo's path handling has been bitten by before.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModTypeClassifierTests"`

Expected: PASS, all of them.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs
git commit -m "feat: accept a hair number as a Hair subcategory folder

GetFolder throws on unenumerated pairs by design, so Hair + a number
would have crashed inside the sort. Several hundred hair numbers cannot
be enumerated, so the shape is validated and Hair + 'Bosses' still throws.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: `EnrichHairSubCategory`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs` (add after `EnrichGearSubCategory`, which ends around line 66)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs`

**Interfaces:**
- Consumes: `ModHairFileReader.ReadHairNumbers`'s return type from Task 2; `IsHairNumber`'s contract from Task 3.
- Produces: `ModTypeClassifier.EnrichHairSubCategory(ClassificationResult baseResult, IReadOnlySet<int>? hairNumbers)` returning `ClassificationResult`. Task 5 calls this.

- [ ] **Step 1: Write the failing tests**

Append to `ModTypeClassifierTests`.

```csharp
    [Fact]
    public void EnrichHairSubCategory_ExactlyOneNumber_AssignsIt()
    {
        var baseResult = new ClassificationResult(ModCategory.Hair, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichHairSubCategory(baseResult, new HashSet<int> { 115 });

        Assert.Equal("115", result.SubCategory);
        Assert.Equal(ModCategory.Hair, result.Category);
        Assert.Equal(ClassificationSource.Structural, result.Source);
    }

    [Fact]
    public void EnrichHairSubCategory_ProducesAFolderNameGetFolderAccepts()
    {
        // The two halves of this feature agreeing is the property that matters: a format change on
        // one side without the other would throw inside the sort rather than fail here.
        var baseResult = new ClassificationResult(ModCategory.Hair, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichHairSubCategory(baseResult, new HashSet<int> { 9 });

        Assert.Equal("Hair/9", ModTypeFolders.GetFolder(ModCategory.Hair, result.SubCategory));
    }

    [Theory]
    [InlineData(ModCategory.Gear)]
    [InlineData(ModCategory.Body)]
    [InlineData(ModCategory.NPC)]
    public void EnrichHairSubCategory_NonHairCategory_IsUntouched(ModCategory category)
    {
        var baseResult = new ClassificationResult(category, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichHairSubCategory(baseResult, new HashSet<int> { 115 });

        Assert.Same(baseResult, result);
    }

    [Fact]
    public void EnrichHairSubCategory_ReadFailure_IsUntouched()
    {
        // null means the read could not be trusted. Filing on it would be a confident wrong answer.
        var baseResult = new ClassificationResult(ModCategory.Hair, null, ClassificationSource.Structural);

        Assert.Same(baseResult, ModTypeClassifier.EnrichHairSubCategory(baseResult, null));
    }

    [Fact]
    public void EnrichHairSubCategory_NoEvidence_IsUntouched()
    {
        var baseResult = new ClassificationResult(ModCategory.Hair, null, ClassificationSource.Structural);

        Assert.Same(baseResult, ModTypeClassifier.EnrichHairSubCategory(baseResult, new HashSet<int>()));
    }

    [Fact]
    public void EnrichHairSubCategory_TwoOrMoreNumbers_IsUntouched()
    {
        // The maintainer's decision (2026-11-01): ambiguous mods stay directly in Hair/. No Mixed
        // bucket, and no lowest-number-wins.
        var baseResult = new ClassificationResult(ModCategory.Hair, null, ClassificationSource.Structural);

        Assert.Same(baseResult, ModTypeClassifier.EnrichHairSubCategory(baseResult, new HashSet<int> { 9, 115 }));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModTypeClassifierTests"`

Expected: FAIL at compile time, `CS0117 'ModTypeClassifier' does not contain a definition for 'EnrichHairSubCategory'`.

- [ ] **Step 3: Write the method**

Add directly after `EnrichGearSubCategory` in `ModTypeClassifier`, so the two sit together:

```csharp
    // The hair mirror of EnrichGearSubCategory, with the same guard shape and the same caller
    // contract: a second pass the caller runs only when Classify already returned Category: Hair,
    // never a new top-level classification path. Disk I/O happens only where the caller chooses to
    // call this, which ScanProcessor gates on Category == Hair.
    public static ClassificationResult EnrichHairSubCategory(
        ClassificationResult baseResult, IReadOnlySet<int>? hairNumbers)
    {
        if (baseResult.Category != ModCategory.Hair || hairNumbers is null || hairNumbers.Count != 1)
            return baseResult; // not Hair, read failed, no evidence, or ambiguous (>1 number) — untouched

        // Invariant, and unpadded: ModTypeFolders.IsHairNumber rejects a leading zero, so a
        // culture-dependent or padded format here would throw inside the sort rather than quietly
        // creating a second folder for the same hair.
        return baseResult with
        {
            SubCategory = hairNumbers.Single().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModTypeClassifierTests"`

Expected: PASS, all of them.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs
git commit -m "feat: assign a hair-number subcategory on exactly one number

The mirror of EnrichGearSubCategory, including its handling of
ambiguity: two numbers, no evidence and a failed read all leave the
classification untouched so the mod stays flat in Hair/.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Read hair numbers during the scan

**Files:**
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs:54-68` (the gear block and just after it)
- Test: `PenumbraOrganizer.Plugin.Tests/LibraryWork/ScanProcessorTests.cs`

**Interfaces:**
- Consumes: `ModHairFileReader.ReadHairNumbers` (Task 2) and `ModTypeClassifier.EnrichHairSubCategory` (Task 4).
- Produces: an `OrganizerModRow` whose `SubCategory` holds the hair number for a single-number hair mod. Task 6 sorts on it.

- [ ] **Step 1: Write the failing test**

Append to the existing `ScanProcessorTests` class. This uses that file's own helpers - `NewProcessor()` and `Seed(modDirectoryPath, name, params changedItemKeys)` - and its temp-directory-with-`try`/`finally` idiom, which is what the surrounding tests do.

The changed-item key matters: classification is driven by it, not by the mod's name or files. `"Customization: Midlander Female Hair 157"` is a real key form that classifies as `ModCategory.Hair`, pinned by `ModTypeClassifierTests` at line 522. The hair number in the *key* (157) is deliberately different from the one in the *files* (115), so the test proves the subcategory came from the file read rather than from the key.

```csharp
    [Fact]
    public void HairModWithOneHairNumber_GetsThatNumberAsItsSubCategory()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"),
                """{"DefaultData":{"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"hair.mdl"}}}""");

            var row = NewProcessor().Process(
                Seed(dir.FullName, "Some Hair", "Customization: Midlander Female Hair 157"),
                CancellationToken.None);

            Assert.NotNull(row);
            Assert.Equal(ModCategory.Hair, row!.Category);
            // 115 from the files, not 157 from the changed-item key - the subcategory must come
            // from the file read.
            Assert.Equal("115", row.SubCategory);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void HairModWithTwoHairNumbers_StaysFlat()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"),
                """{"DefaultData":{"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"a.mdl","chara/human/c0101/obj/hair/h0009/model/c0101h0009_hir.mdl":"b.mdl"}}}""");

            var row = NewProcessor().Process(
                Seed(dir.FullName, "Some Hair Pack", "Customization: Midlander Female Hair 157"),
                CancellationToken.None);

            Assert.NotNull(row);
            Assert.Equal(ModCategory.Hair, row!.Category);
            Assert.Null(row.SubCategory);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void HairPathsInAGearMod_ContributeNoSubCategory()
    {
        // Guards the gate itself: a Gear mod must not pick up a hair number, which would throw in
        // GetFolder the moment it reached the sort.
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"),
                """{"DefaultData":{"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"hair.mdl"}}}""");

            var row = NewProcessor().Process(
                Seed(dir.FullName, changedItemKeys: "Ala Mhigan Gown"),
                CancellationToken.None);

            Assert.NotNull(row);
            Assert.Equal(ModCategory.Gear, row!.Category);
            Assert.Null(row.SubCategory);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ScanProcessorTests"`

Expected: `HairModWithOneHairNumber_GetsThatNumberAsItsSubCategory` FAILS, asserting `"115"` against an actual of `null`. The other two should already PASS - they pin behaviour that must survive this change.

- [ ] **Step 3: Add the gated block**

In `ScanProcessor.Process`, immediately after the closing brace of the existing `if (classification.Category == ModCategory.Gear) { ... }` block, add:

```csharp
        // Disk I/O only for mods the changed-items rule already confirmed are Hair, matching the
        // gear gate above - every other category never touches disk for this. No diagnostic
        // counterpart to gearSlotDiagnostic on purpose: both readers share one walker and read the
        // same files, so a library-wide read failure is already visible through the gear one.
        if (classification.Category == ModCategory.Hair)
        {
            var hairNumbers = ModHairFileReader.ReadHairNumbers(modPath);
            classification = ModTypeClassifier.EnrichHairSubCategory(classification, hairNumbers);
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ScanProcessorTests"`

Expected: PASS.

- [ ] **Step 5: Run the full suite, with attention to purity**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`

Expected: all tests pass. `LibraryWorkPurityTests` exists specifically to police what this file may touch; if it fails, the new block reached something it must not - re-read that test's message rather than working around it.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs PenumbraOrganizer.Plugin.Tests/LibraryWork/ScanProcessorTests.cs
git commit -m "feat: resolve hair numbers during the scan

Gated on Category == Hair, exactly like the gear-slot read beside it, so
no other category touches disk for this.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: The `splitHair` axis on `OrganizerState.Sort`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs:265-291` (the `Sort` overload and `Sub`), plus a third flattener after `FlattenNpcSubCategory` at line 302
- Modify: `PenumbraOrganizer.Plugin/Windows/SortPanel.cs:121` (the one production call site)
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortTests.cs` - seven `Sort(` call sites, at lines 68, 86, 109, 119, 120, 133 and 144
- Create: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortHairTests.cs`

**Interfaces:**
- Consumes: `ModTypeFolders.GetFolder(Hair, "115")` from Task 3; rows carrying a hair subcategory from Task 5.
- Produces: `OrganizerState.Sort(SortStrategy strategy, bool splitGear, bool splitNpc, bool splitHair, Func<string, string> canonicalizeCreator)` returning `int`. Task 7's panel calls it.

**Why hair coverage gets its own fixture.** `OrganizerStateSortTests` is a 16-row `[Theory]` over `(strategy, splitGear, splitNpc)` plus three expected paths, and its comment records that those expectations were *captured* from the seven original `SortBy*` methods rather than hand-written, "rather than getting the order wrong and then 'fixing' the expectations until green, which destroys the guarantee." Adding a hair row to its shared fixture changes all 16 rows' expected output; adding a `splitHair` axis doubles it to 32 hand-written rows. Both are the exact failure that comment warns about. So: pass `splitHair: false` at its call sites, leaving every captured expectation byte-identical, and put hair coverage in a new file.

- [ ] **Step 1: Write the failing hair sort tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortHairTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateSortHairTests
{
    // Its own fixture, deliberately not OrganizerStateSortTests'. That file's 16-row theory holds
    // expectations CAPTURED from the original SortBy* methods; adding a hair row would change all
    // 16 of them and a splitHair axis would double it to 32 hand-written rows.
    //
    // Two rows: one hair mod whose number resolved, one whose number did not. The second is
    // load-bearing - without it, "split off" assertions would pass against a flattener that only
    // ever sees null anyway.
    private static OrganizerState WithHairMods()
    {
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "numbered", Name = "Numbered Hair", Author = "Ann",
                CurrentPath = "Numbered Hair", ProposedPath = "Numbered Hair",
                Category = ModCategory.Hair, SubCategory = "115" },

            new OrganizerModRow { Identifier = "ambiguous", Name = "Ambiguous Hair", Author = "Bob",
                CurrentPath = "Ambiguous Hair", ProposedPath = "Ambiguous Hair",
                Category = ModCategory.Hair, SubCategory = null },
        ], new HashSet<string>());
        return state;
    }

    private static string Canon(string s) => s;

    private static string PathOf(OrganizerState state, string identifier) =>
        state.Mods.Single(m => m.Identifier == identifier).ProposedPath;

    [Theory]
    [InlineData(SortStrategy.TypeOnly, "Hair/115/Numbered Hair", "Hair/Ambiguous Hair")]
    [InlineData(SortStrategy.TypeThenCreator, "Hair/115/Ann/Numbered Hair", "Hair/Bob/Ambiguous Hair")]
    [InlineData(SortStrategy.CreatorThenType, "Ann/Hair/115/Numbered Hair", "Bob/Hair/Ambiguous Hair")]
    public void Sort_HairSplitOn_NestsTheNumberedModOnly(
        SortStrategy strategy, string expectedNumbered, string expectedAmbiguous)
    {
        var state = WithHairMods();

        state.Sort(strategy, splitGear: false, splitNpc: false, splitHair: true, Canon);

        Assert.Equal(expectedNumbered, PathOf(state, "numbered"));
        Assert.Equal(expectedAmbiguous, PathOf(state, "ambiguous"));
    }

    [Theory]
    [InlineData(SortStrategy.TypeOnly, "Hair/Numbered Hair", "Hair/Ambiguous Hair")]
    [InlineData(SortStrategy.TypeThenCreator, "Hair/Ann/Numbered Hair", "Hair/Bob/Ambiguous Hair")]
    [InlineData(SortStrategy.CreatorThenType, "Ann/Hair/Numbered Hair", "Bob/Hair/Ambiguous Hair")]
    public void Sort_HairSplitOff_LeavesEveryHairModFlat(
        SortStrategy strategy, string expectedNumbered, string expectedAmbiguous)
    {
        var state = WithHairMods();

        state.Sort(strategy, splitGear: false, splitNpc: false, splitHair: false, Canon);

        Assert.Equal(expectedNumbered, PathOf(state, "numbered"));
        Assert.Equal(expectedAmbiguous, PathOf(state, "ambiguous"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sort_CreatorOnly_IgnoresTheHairSplitEntirely(bool splitHair)
    {
        // CreatorOnly never consults the category, which is why SplitsApply disables all three
        // checkboxes for it.
        var state = WithHairMods();

        state.Sort(SortStrategy.CreatorOnly, splitGear: false, splitNpc: false, splitHair, Canon);

        Assert.Equal("Ann/Numbered Hair", PathOf(state, "numbered"));
        Assert.Equal("Bob/Ambiguous Hair", PathOf(state, "ambiguous"));
    }

    [Fact]
    public void Sort_HairSplitDoesNotDisturbGearOrNpc()
    {
        // The three flatteners are independent; each must touch only its own category.
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "hair", Name = "Hair Mod", Author = "Ann",
                CurrentPath = "Hair Mod", ProposedPath = "Hair Mod",
                Category = ModCategory.Hair, SubCategory = "115" },
            new OrganizerModRow { Identifier = "gear", Name = "Gear Mod", Author = "Ann",
                CurrentPath = "Gear Mod", ProposedPath = "Gear Mod",
                Category = ModCategory.Gear, SubCategory = "Feet" },
            new OrganizerModRow { Identifier = "npc", Name = "Npc Mod", Author = "Bob",
                CurrentPath = "Npc Mod", ProposedPath = "Npc Mod",
                Category = ModCategory.NPC, SubCategory = "Bosses" },
        ], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: true, splitHair: true, Canon);

        Assert.Equal("Hair/115/Hair Mod", PathOf(state, "hair"));
        Assert.Equal("Gear/Gear Mod", PathOf(state, "gear"));
        Assert.Equal("NPC/Bosses/Npc Mod", PathOf(state, "npc"));
    }
}
```

`OrganizerState.Mods` is an `IReadOnlyList<OrganizerModRow>` (`OrganizerState.cs:30`), ordered by name rather than insertion order - which is why `PathOf` looks rows up by identifier instead of indexing, unlike the existing theory which asserts against the whole ordered sequence.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OrganizerStateSortHairTests"`

Expected: FAIL at compile time - `Sort` takes four arguments today, not five, so `CS1501 No overload for method 'Sort' takes 5 arguments`.

- [ ] **Step 3: Add the parameter and the flattener**

In `OrganizerState.cs`, change the `Sort` signature and `Sub`:

```csharp
    public int Sort(SortStrategy strategy, bool splitGear, bool splitNpc, bool splitHair,
                    Func<string, string> canonicalizeCreator)
    {
        // Composed, not switched over: the three flatteners are independent decisions about the same
        // subcategory, so applying them in sequence is what makes every combination reachable
        // without writing eight variants of each strategy.
        string? Sub(OrganizerModRow row)
        {
            var sub = row.SubCategory;
            if (!splitGear) sub = FlattenGearSubCategory(row.Category, sub);
            if (!splitNpc) sub = FlattenNpcSubCategory(row.Category, sub);
            if (!splitHair) sub = FlattenHairSubCategory(row.Category, sub);
            return sub;
        }
```

Add the third flattener directly after `FlattenNpcSubCategory`:

```csharp
    // The hair mirror of the two above. Like gear and unlike NPC, this has a checkbox and defaults
    // off, so flat "Hair" is what an unchanged install produces.
    private static string? FlattenHairSubCategory(ModCategory? category, string? subCategory) =>
        category == ModCategory.Hair ? null : subCategory;
```

Then update the `<remarks>` arithmetic on the `Sort` doc comment, which documents why the old seven buttons were replaced:

```
    /// The three splits are independent of each other and all are ignored by
    /// <see cref="SortStrategy.CreatorOnly"/>, which never consults the category at all. That gives
    /// 1 + (3 x 2 x 2 x 2) = 25 selections, of which the seven original buttons offered seven.
```

- [ ] **Step 4: Update the eight existing call sites**

One in production, at `SortPanel.cs:121`:

```csharp
            state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, selection.SplitHair, canonicalizeCreator);
```

`selection.SplitHair` does not exist until Task 7. To keep this task's commit compiling, use `splitHair: false` here now and change it to `selection.SplitHair` in Task 7:

```csharp
            state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, splitHair: false, canonicalizeCreator);
```

Seven in `OrganizerStateSortTests.cs`. Add `splitHair: false` to each, changing nothing else - its expectations must stay byte-identical:

| Line | Becomes |
|---|---|
| 68 | `state.Sort(strategy, splitGear, splitNpc, splitHair: false, Canon);` |
| 86 | `state.Sort(strategy, splitGear, splitNpc: false, splitHair: false, Canon);` |
| 109 | `state.Sort(strategy, splitGear, splitNpc: true, splitHair: false, Canon);` |
| 119 | `var a = WithMods(); a.Sort(SortStrategy.CreatorOnly, false, false, splitHair: false, Canon);` |
| 120 | `var b = WithMods(); b.Sort(SortStrategy.CreatorOnly, true, true, splitHair: false, Canon);` |
| 133 | `state.Sort(SortStrategy.TypeThenCreator, splitGear: true, splitNpc: true, splitHair: false, Canon);` |
| 144 | `Assert.Equal(2, state.Sort(SortStrategy.TypeOnly, false, true, splitHair: false, Canon));` |

Then confirm none was missed:

Run: `grep -rn "\.Sort(" PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests --include=*.cs | grep -v "splitHair"`

Expected: no output. Any line printed is a call site still on the old signature.

- [ ] **Step 5: Run the hair tests, then the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OrganizerStateSortHairTests"`

Expected: PASS, 9 test cases (3 + 3 theory rows, 2 for Creator-only, 1 for the independence check).

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`

Expected: all tests pass, including all 16 rows of the original theory with unchanged expectations. If one of those 16 now fails, `FlattenHairSubCategory` is touching a category that is not Hair.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin/Windows/SortPanel.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortTests.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortHairTests.cs
git commit -m "feat: add a splitHair axis to OrganizerState.Sort

A third independent flattener, composed with the other two, taking the
selection space from 13 to 25. Hair coverage lives in its own fixture so
the existing theory's captured expectations stay untouched.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 7: The checkbox

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/SortPanel.cs` - `SortSelection` at line 17, `_splitHair` field near line 63, the checkbox block after the gear one at lines 88-91, the `selection` construction at line 115, and the `Sort` call at line 121
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTopics.cs` (near line 47), `PenumbraOrganizer.Plugin/Windows/HelpTab.cs` (the Sort section, near line 84), `PenumbraOrganizer.Plugin/Windows/HelpTopicUsage.cs` (near line 29)
- Modify: the help resource file holding each topic's title/short/body. Find it with `grep -rln "sort.split-gear" PenumbraOrganizer.Plugin` - `Help.TryGet` reads from it, and `HelpTabTests` enforces that every section topic has a body.
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/SortSelectionTests.cs`

**Interfaces:**
- Consumes: `OrganizerState.Sort`'s five-parameter signature from Task 6.
- Produces: `SortSelection(SortStrategy Strategy, bool SplitGear, bool SplitNpc, bool SplitHair)`. Nothing later depends on it.

- [ ] **Step 1: Write the failing tests**

`SortSelectionTests` constructs `SortSelection` positionally in five places, so adding the fourth parameter breaks them at compile time - that is wanted, since it makes every construction site visible. Update each existing `new SortSelection(x, y, z)` to `new SortSelection(x, y, z, false)`, then append:

```csharp
    [Fact]
    public void SplitHair_DefaultsOffInTheRecord()
    {
        // Off by default, like SplitGear: an unchanged install keeps producing flat Hair/.
        Assert.False(new SortSelection(SortStrategy.TypeOnly, false, false, false).SplitHair);
    }

    [Fact]
    public void SplitsApply_StillIgnoresEveryFlagAndOnlyReadsStrategy()
    {
        // The third checkbox must be disabled under Creator-only for the same reason as the other
        // two: that strategy never consults the category.
        Assert.False(new SortSelection(SortStrategy.CreatorOnly, true, true, true).SplitsApply);
        Assert.True(new SortSelection(SortStrategy.TypeOnly, false, false, true).SplitsApply);
    }

    [Fact]
    public void SortSelection_ValueEquality_CoversSplitHair()
    {
        // The staleness line compares the current selection against the one last sorted with. A
        // SplitHair left out of equality would make toggling it look like no change at all.
        Assert.NotEqual(
            new SortSelection(SortStrategy.TypeOnly, false, false, false),
            new SortSelection(SortStrategy.TypeOnly, false, false, true));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~SortSelectionTests"`

Expected: FAIL at compile time with `CS1729 'SortSelection' does not contain a constructor that takes 4 arguments`.

- [ ] **Step 3: Extend `SortSelection` and the panel**

The record declaration at `SortPanel.cs:17`:

```csharp
public readonly record struct SortSelection(SortStrategy Strategy, bool SplitGear, bool SplitNpc, bool SplitHair)
```

The backing field, beside `_splitGear` near line 63:

```csharp
    private bool _splitHair;
```

Update the `splitsApply` line near line 84 and the `selection` construction near line 115 to pass `_splitHair` as the fourth argument:

```csharp
        var splitsApply = new SortSelection(strategy, _splitGear, _splitNpc, _splitHair).SplitsApply;
```

```csharp
        var selection = new SortSelection(strategy, _splitGear, _splitNpc, _splitHair);
```

Add the checkbox immediately after the NPC one, following that file's exact pattern - each checkbox closes its own disabled scope *before* its tooltip, because ImGui is positional and `EndDisabled` submits no widget:

```csharp
        ImGui.BeginDisabled(!splitsApply);
        ImGui.Checkbox("Split hair by hair number", ref _splitHair);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortSplitHair, splitsApply ? null : SplitsInapplicableReason);
```

And the `Sort` call near line 121 becomes what Task 6 deferred:

```csharp
            state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, selection.SplitHair, canonicalizeCreator);
```

- [ ] **Step 4: Register the help topic**

`HelpTopics.cs`, beside `SortSplitGear` near line 47:

```csharp
    public static readonly HelpTopic SortSplitHair = new("sort.split-hair");
```

`HelpTab.cs`, in the Sort section's control list, directly after `HelpTopics.SortSplitNpc`:

```csharp
            HelpTopics.SortSplitHair,
```

`HelpTopicUsage.cs`, in `ReferencedByControls` near line 29, after `HelpTopics.SortSplitGear`:

```csharp
        HelpTopics.SortSplitHair,
```

Then add the content to the help resource file, matching the surrounding entries' shape exactly (find it with the grep in this task's Files list, and copy the `sort.split-gear` entry's structure):

- **Title:** `Split hair by hair number`
- **Short:** `File each hair mod under the vanilla hair number it replaces.`
- **Body:** `A hair mod that clearly replaces one vanilla hair goes into a numbered subfolder, for example Hair/115. A mod that covers several hair numbers, or whose files could not be read, stays directly in Hair. Penumbra sorts folders as text, so Hair/9 appears after Hair/115.`

That last sentence is there so nobody files it as a bug. It is the accepted consequence of using the bare number rather than a zero-padded one.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`

Expected: all tests pass. Three `HelpTabTests` cases are the real gate here and they check **both** directions: `EverySectionTopicResolves_AndHasABody` fails if the resource content is missing, `EveryTopicWithABody_AppearsInSomeSection` fails if it is in the resource but not listed in `HelpTab.Sections`, and `EveryControlTopicAppearsInExactlyOneSection` fails if it is in `HelpTopicUsage` but not in a section, or listed twice.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/ PenumbraOrganizer.Plugin.Tests/Windows/SortSelectionTests.cs
git commit -m "feat: add the Split hair by hair number checkbox

Fourth field on SortSelection and a third checkbox, defaulting off and
disabled under Creator-only like the other two. Help topic registered in
all three places the coverage tests require.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 8: Documentation and the in-game verification

Two things only a real library can settle: whether `Est`'s `SetId` really is the hair number, and whether the numbers look plausible at all. Both are written down here rather than carried in someone's head.

**Files:**
- Modify: `docs/HOW_SORTING_WORKS.md`, `docs/USER_GUIDE.md`
- Create: `docs/testing/2026-11-01-hair-number-sorting-checklist.md`

**Interfaces:**
- Consumes: the finished feature from Tasks 1-7.
- Produces: nothing in code.

- [ ] **Step 1: Document the third split**

In `docs/HOW_SORTING_WORKS.md`, find the section describing the gear and NPC splits and add hair alongside them in the same voice. Cover: one resolved number gives `Hair/115`; two or more, or an unreadable mod, stays flat in `Hair/`; race codes are ignored so a multi-clan hair is one number; the numbers come from the mod's own config files, not from its name.

In `docs/USER_GUIDE.md`, find the Sort tab's checkbox descriptions and add a matching entry, including the text-ordering note (`Hair/9` after `Hair/115`).

- [ ] **Step 2: Write the checklist**

```markdown
# Manual check: split hair by hair number

Build, load in game, and run a scan over a real library before sorting anything.

## The Est question

This is the one correctness question no unit test can answer.

- [ ] Pick three hair mods whose `Files` paths name a hair number AND whose config contains an
      `Est` manipulation with `"Slot":"Hair"`.
- [ ] For each, confirm the `SetId` on that manipulation equals the `h####` number in the paths.
- [ ] If they disagree on real mods, `SetId` is not the hair number: delete
      `CollectFromManipulation` from `ModHairFileReader`, drop its two Est tests, and ship path
      matching alone. Say so in the report - it changes the spec.

## Plausibility

- [ ] Scan a library with a decent number of hair mods. Export the diagnostic dump.
- [ ] Spot-check five hair mods against what they claim to replace. The numbers should match the
      hair they say they are, not be off by a constant.
- [ ] Count how many hair mods got a number versus stayed flat. A library where essentially none
      resolved is the signal that would justify the cut-down `HairNumberDiagnostic` the spec left
      out - report the counts either way.

## The checkbox

- [ ] "Split hair by hair number" appears under "Split NPC mods by kind" on the Sort tab.
- [ ] It is unchecked on a fresh plugin load.
- [ ] Selecting "Creator" under Group by disables it, with the tooltip explaining that grouping by
      creator never uses the mod's type.
- [ ] Hovering it shows its own help text.
- [ ] It appears in the Help tab's Sort section with a body.

## Sorting

- [ ] With it ON, review the proposal: numbered hair mods are under `Hair/<number>/`, and
      multi-number ones are directly in `Hair/`.
- [ ] With it OFF, every hair mod is directly in `Hair/`.
- [ ] Toggling it clears the "already sorted" staleness state, so the Sort button acts on the new
      selection rather than reporting no change.
- [ ] Gear and NPC splitting still behave exactly as before, in every combination.
- [ ] Apply the proposal on a small protected-down test selection, then roll it back from the
      History tab. Both complete without error.

## No crash in GetFolder

- [ ] Nothing in the scan or sort throws `ArgumentOutOfRangeException` with a message like
      "Unsupported subcategory". That exception means a hair subcategory reached `GetFolder` in a
      shape `IsHairNumber` rejects, which is a bug in `EnrichHairSubCategory`, not a bad library.
```

- [ ] **Step 3: Commit the docs**

```bash
git add docs/HOW_SORTING_WORKS.md docs/USER_GUIDE.md docs/testing/2026-11-01-hair-number-sorting-checklist.md
git commit -m "docs: document splitting hair by hair number

Including the manual checklist, whose first section settles whether Est's
SetId really is the hair number - the one correctness question no unit
test can reach.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

- [ ] **Step 4: Run the checklist in game**

Walk every box above. Report which passed, which did not, and the resolved-versus-flat counts from the Plausibility section. Do not report the feature complete on an unrun checklist.
