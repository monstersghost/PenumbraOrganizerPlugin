# Detailed Gear-Slot Classification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** For mods already classified as `Category: Gear`, resolve `SubCategory` to one of 9 equipment slots (Head/Top/Hands/Legs/Feet/Ears/Neck/Wrists/Rings) by reading the mod's own files directly from Penumbra's mod library on disk — the data `GetChangedItems` never exposes.

**Architecture:** A small pure `EquipmentSlotMapper` (extracted into the standalone app's Core project, linked into the plugin like `ModCategory.cs`) turns a raw file-path suffix or `Manipulations` entry into a typed `EquipmentSlot`. A new plugin-native `ModEquipmentFileReader` reads a mod's `default_mod.json`/`group_*.json` files and resolves every slot they reference, failing closed (returning `null`, never a partial answer) on any read/parse error. `ModTypeClassifier` gets a new, separate `EnrichGearSubCategory` step — not a new `Classify` parameter — so disk I/O only ever happens for mods the existing `GetChangedItems`-based rule already confirmed are Gear.

**Tech Stack:** C#/.NET (net10.0-windows7.0), `System.Text.Json` (`JsonDocument`, streaming, no deserialization), `System.Text.RegularExpressions`, xUnit (plugin repo) / xUnit + FluentAssertions (standalone app repo).

**Depends on / must not contradict:**
`docs/superpowers/specs/2026-07-18-plugin-organizer-gear-slot-classification-design.md` (design, revised twice after external review and two rounds of real-library validation against 247 and 2,035 real mods).

**Cross-repo note:** Tasks 1-2 modify the standalone app repo (`C:\Repo\PenumbraOrganizer`) directly on its own `main` branch — no worktree for that repo, matching the precedent set by the workbook feature's `ScanIdentity` extraction. Tasks 3-6 modify this plugin repo, inside whatever worktree this plan is executed under. Always verify which repo's working directory you're actually in before running `git`/`dotnet` commands — a past session mistakenly wrote to the wrong repo's checkout mid-plan.

## Global Constraints

- `EquipmentSlot` enum values, exact names: `Head, Top, Hands, Legs, Feet, Ears, Neck, Wrists, Rings`.
- Path-suffix → slot table: `met→Head, top→Top, glv→Hands, dwn→Legs, sho→Feet, ear→Ears, nek→Neck, wrs→Wrists, ril/rir→Rings`.
- Manipulation-slot → slot table: `Head→Head, Body→Top, Hands→Hands, Legs→Legs, Feet→Feet, Ears→Ears, Neck→Neck, Wrists→Wrists, RFinger/LFinger→Rings`. Penumbra's own slot literally named `"Body"` maps to `Top` here — deliberately not reused as this plugin's unrelated `ModCategory.Body` (Smallclothes/skin bucket).
- Suffix-token regex, exact pattern: `(?:^|_)(met|top|glv|dwn|sho|ear|nek|wrs|ril|rir)(?:_|\.|$)` with `RegexOptions.IgnoreCase | RegexOptions.Compiled` — a delimited-token search, not "text after the last underscore" (confirmed broken against real texture/material filenames in two independent real mod libraries).
- `Manipulations` entries: only `Type: "Eqp"` and `"Eqdp"` carry the slot in a field called `Slot`; `Type: "Imc"` carries it in a field called `EquipSlot`; every other `Type` (including `"Est"`, which has its own unrelated `Slot` meaning a customization slot like `Hair`/`Face`) is excluded by checking `Type` first, not by relying on vocabulary non-overlap.
- Fail-closed, not partial-collect: if any config file in a mod can't be read, parsed, or enumerated, the reader returns `null` for the whole mod — never a confident slot built from only the files that happened to succeed.
- `EnrichGearSubCategory` only assigns a `SubCategory` when the read is non-null AND resolves to exactly one distinct slot. Zero slots, more than one slot, or a failed (`null`) read all leave `SubCategory` as it already was (`null`, since this only ever runs on results that were already `Category: Gear, SubCategory: null`).
- `ModTypeClassifier.Classify`'s existing three-argument signature (`string modName, IEnumerable<string> changedItemKeys, NpcNameMatcher npcNameMatcher`) does not change. No existing `ModTypeClassifierTests` call site needs migration.
- `ModPathClassifier`'s existing public contract is unchanged: `Subcategory` stays a raw lowercase suffix string (`"sho"`, `"top"`), pinned by two real, already-passing standalone-app tests (`ModPathClassifierTests.Resolve_GearTarget_CarriesRawSlotSuffixAsSubcategory`, `PenumbraScanServiceTests`' two Gear-mod fixtures) — only *how* that string gets computed changes (bug fix), not its shape.
- Disk I/O for gear-slot detection only ever happens for mods `ModTypeClassifier.Classify` already resolved to `Category: Gear` — never for every mod.
- No async/threading/caching machinery for the file reads — matches every other synchronous, locally-verified feature in this plugin (Folder Cleanup, NPC classification, the workbook feature).
- Only `chara/equipment/` and `chara/accessory/` paths are read for slot detection — no VFX/Sound/Animation/customization path handling.
- Never cite the specific NSFW mod titles from the original research corpus in code, comments, or test names (unrelated to this feature's content, but a standing project rule) — not a concern here since this feature has zero contact with that research area.

---

## Task 1: `EquipmentSlot` enum + `EquipmentSlotMapper` (standalone app repo)

**Files:**
- Create: `PenumbraOrganizer.Core/Classification/EquipmentSlot.cs`
- Create: `PenumbraOrganizer.Core/Classification/EquipmentSlotMapper.cs`
- Test: `PenumbraOrganizer.Tests/Classification/EquipmentSlotMapperTests.cs`

**Repo:** `C:\Repo\PenumbraOrganizer` (standalone app), directly on `main` — no worktree for this repo.

**Interfaces:**
- Produces: `enum EquipmentSlot { Head, Top, Hands, Legs, Feet, Ears, Neck, Wrists, Rings }`; `static class EquipmentSlotMapper` with `ExtractRawSuffixToken(string fileName) -> string?`, `MapPathSuffix(string suffix) -> EquipmentSlot?`, `ExtractSlotFromFileName(string fileName) -> EquipmentSlot?`, `MapManipulationSlot(string slotName) -> EquipmentSlot?`, `FolderName(EquipmentSlot slot) -> string`. Task 2 (`ModPathClassifier`) consumes `ExtractRawSuffixToken`. Task 3 (`ModEquipmentFileReader`, plugin repo) consumes `ExtractSlotFromFileName`, `MapManipulationSlot`, `FolderName`.

This task is fully self-contained — no dependency on any other task, and nothing in the plugin repo yet references it (that starts in Task 3).

- [ ] **Step 1: Write the failing tests**

```csharp
namespace PenumbraOrganizer.Tests.Classification;

using FluentAssertions;
using PenumbraOrganizer.Core.Classification;

public sealed class EquipmentSlotMapperTests
{
    [Theory]
    [InlineData("met", EquipmentSlot.Head)]
    [InlineData("top", EquipmentSlot.Top)]
    [InlineData("glv", EquipmentSlot.Hands)]
    [InlineData("dwn", EquipmentSlot.Legs)]
    [InlineData("sho", EquipmentSlot.Feet)]
    [InlineData("ear", EquipmentSlot.Ears)]
    [InlineData("nek", EquipmentSlot.Neck)]
    [InlineData("wrs", EquipmentSlot.Wrists)]
    [InlineData("ril", EquipmentSlot.Rings)]
    [InlineData("rir", EquipmentSlot.Rings)]
    [InlineData("MET", EquipmentSlot.Head)] // case-insensitive
    public void MapPathSuffix_RecognizedSuffix_ReturnsExpectedSlot(string suffix, EquipmentSlot expected)
    {
        EquipmentSlotMapper.MapPathSuffix(suffix).Should().Be(expected);
    }

    [Fact]
    public void MapPathSuffix_UnrecognizedSuffix_ReturnsNull()
    {
        EquipmentSlotMapper.MapPathSuffix("xyz").Should().BeNull();
    }

    [Theory]
    [InlineData("Head", EquipmentSlot.Head)]
    [InlineData("Body", EquipmentSlot.Top)] // Penumbra's "Body" manipulation slot means torso equipment
    [InlineData("Hands", EquipmentSlot.Hands)]
    [InlineData("Legs", EquipmentSlot.Legs)]
    [InlineData("Feet", EquipmentSlot.Feet)]
    [InlineData("Ears", EquipmentSlot.Ears)]
    [InlineData("Neck", EquipmentSlot.Neck)]
    [InlineData("Wrists", EquipmentSlot.Wrists)]
    [InlineData("RFinger", EquipmentSlot.Rings)]
    [InlineData("LFinger", EquipmentSlot.Rings)]
    public void MapManipulationSlot_RecognizedSlot_ReturnsExpectedSlot(string slotName, EquipmentSlot expected)
    {
        EquipmentSlotMapper.MapManipulationSlot(slotName).Should().Be(expected);
    }

    [Fact]
    public void MapManipulationSlot_CustomizationSlot_ReturnsNull()
    {
        // "Hair"/"Face" are real Manipulations[].Slot values too, but for customization (Est
        // manipulations), not equipment — must not be mistaken for an equipment slot.
        EquipmentSlotMapper.MapManipulationSlot("Hair").Should().BeNull();
        EquipmentSlotMapper.MapManipulationSlot("Face").Should().BeNull();
    }

    // Real filename shapes confirmed against two independent real mod libraries (~2,280 mods
    // combined) via a validation script — "last underscore" extraction fails on all but the
    // first of these; the token-search regex must match "sho" in every one.
    [Theory]
    [InlineData("c0101e6116_sho.mdl")]
    [InlineData("v01_c0101e6116_sho_m.tex")]
    [InlineData("mt_c0101e6116_sho_a.mtrl")]
    [InlineData("c0101e6116_sho_b_d.tex")] // two trailing tokens
    [InlineData("mt_c0101e6116_sho_b.mtrl")]
    public void ExtractRawSuffixToken_RealFilenameShapes_ExtractsSho(string fileName)
    {
        EquipmentSlotMapper.ExtractRawSuffixToken(fileName).Should().Be("sho");
    }

    [Fact]
    public void ExtractRawSuffixToken_NoKnownToken_ReturnsNull()
    {
        EquipmentSlotMapper.ExtractRawSuffixToken("c0101e6116_xyz.mdl").Should().BeNull();
    }

    [Fact]
    public void ExtractRawSuffixToken_NoUnderscoreAtAll_ReturnsNull()
    {
        EquipmentSlotMapper.ExtractRawSuffixToken("w0101b0117.mdl").Should().BeNull();
    }

    [Fact]
    public void ExtractSlotFromFileName_RealFilenameWithTrailingTokens_ResolvesFeet()
    {
        EquipmentSlotMapper.ExtractSlotFromFileName("c0101e6116_sho_b_d.tex").Should().Be(EquipmentSlot.Feet);
    }

    [Fact]
    public void ExtractSlotFromFileName_UnrecognizedToken_ReturnsNull()
    {
        EquipmentSlotMapper.ExtractSlotFromFileName("c0101e6116_xyz.mdl").Should().BeNull();
    }

    [Theory]
    [InlineData(EquipmentSlot.Head, "Head")]
    [InlineData(EquipmentSlot.Top, "Top")]
    [InlineData(EquipmentSlot.Rings, "Rings")]
    public void FolderName_ReturnsExpectedFriendlyName(EquipmentSlot slot, string expected)
    {
        EquipmentSlotMapper.FolderName(slot).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run (from `C:\Repo\PenumbraOrganizer`): `dotnet test PenumbraOrganizer.Tests --filter EquipmentSlotMapperTests`
Expected: FAIL (compile error — `EquipmentSlot`/`EquipmentSlotMapper` don't exist yet)

- [ ] **Step 3: Implement `EquipmentSlot`**

```csharp
namespace PenumbraOrganizer.Core.Classification;

public enum EquipmentSlot { Head, Top, Hands, Legs, Feet, Ears, Neck, Wrists, Rings }
```

- [ ] **Step 4: Implement `EquipmentSlotMapper`**

```csharp
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Core.Classification;

public static class EquipmentSlotMapper
{
    // Matches a known slot code as a delimited token anywhere in a filename, not just as the
    // final segment — "text after the last underscore" fails on real texture/material
    // filenames with a trailing token (e.g. "..._sho_b_d.tex"), confirmed against two
    // independent real mod libraries (~2,280 mods combined) before this was written.
    private static readonly Regex SlotTokenPattern = new(
        @"(?:^|_)(met|top|glv|dwn|sho|ear|nek|wrs|ril|rir)(?:_|\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Extracts the raw, lowercase slot token from a filename (e.g. "sho"). Returns null if no
    // known slot token appears anywhere. This is the raw-string shape ModPathClassifier's own
    // Subcategory field already exposes and has real, passing tests pinning it to — kept
    // separate from the enum-returning methods below so both consumers get the shape they
    // actually need.
    public static string? ExtractRawSuffixToken(string fileName)
    {
        var match = SlotTokenPattern.Match(fileName);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    // Maps a raw file-path suffix from a chara/equipment or chara/accessory path
    // (e.g. "top" from ".../c0101e0755_top.mdl") to its equipment slot.
    // Returns null for anything not a recognized equipment/accessory suffix.
    public static EquipmentSlot? MapPathSuffix(string suffix) => suffix.ToLowerInvariant() switch
    {
        "met" => EquipmentSlot.Head,
        "top" => EquipmentSlot.Top,
        "glv" => EquipmentSlot.Hands,
        "dwn" => EquipmentSlot.Legs,
        "sho" => EquipmentSlot.Feet,
        "ear" => EquipmentSlot.Ears,
        "nek" => EquipmentSlot.Neck,
        "wrs" => EquipmentSlot.Wrists,
        "ril" or "rir" => EquipmentSlot.Rings,
        _ => null,
    };

    // Composes the two methods above: "what slot does this file belong to," as a typed
    // EquipmentSlot rather than a raw string.
    public static EquipmentSlot? ExtractSlotFromFileName(string fileName) =>
        ExtractRawSuffixToken(fileName) is { } suffix ? MapPathSuffix(suffix) : null;

    // Maps a raw Manipulations[].Slot value (Penumbra's own internal slot names) to the same
    // slot. Note: Penumbra's own slot literally named "Body" means torso equipment here —
    // deliberately mapped to Top, not this plugin's unrelated Category.Body (Smallclothes/skin
    // bucket), to avoid conflating the two.
    public static EquipmentSlot? MapManipulationSlot(string slotName) => slotName switch
    {
        "Head" => EquipmentSlot.Head,
        "Body" => EquipmentSlot.Top,
        "Hands" => EquipmentSlot.Hands,
        "Legs" => EquipmentSlot.Legs,
        "Feet" => EquipmentSlot.Feet,
        "Ears" => EquipmentSlot.Ears,
        "Neck" => EquipmentSlot.Neck,
        "Wrists" => EquipmentSlot.Wrists,
        "RFinger" or "LFinger" => EquipmentSlot.Rings,
        _ => null,
    };

    public static string FolderName(EquipmentSlot slot) => slot.ToString();
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Tests --filter EquipmentSlotMapperTests`
Expected: PASS (36 test cases: 11 `MapPathSuffix` InlineData rows + 1 unrecognized-suffix fact + 10 `MapManipulationSlot` InlineData rows + 1 customization-slot fact + 5 `ExtractRawSuffixToken` real-filename InlineData rows + 1 no-known-token fact + 1 no-underscore fact + 1 `ExtractSlotFromFileName` real-filename fact + 1 `ExtractSlotFromFileName` unrecognized fact + 3 `FolderName` InlineData rows)

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Core/Classification/EquipmentSlot.cs PenumbraOrganizer.Core/Classification/EquipmentSlotMapper.cs PenumbraOrganizer.Tests/Classification/EquipmentSlotMapperTests.cs
git commit -m "feat: add EquipmentSlot enum and EquipmentSlotMapper"
```

---

## Task 2: `ModPathClassifier` delegates to `EquipmentSlotMapper` (standalone app repo)

**Files:**
- Modify: `PenumbraOrganizer.Core/Classification/ModPathClassifier.cs`

**Repo:** `C:\Repo\PenumbraOrganizer` (standalone app), directly on `main`.

**Interfaces:**
- Consumes: `EquipmentSlotMapper.ExtractRawSuffixToken(string fileName) -> string?` (Task 1).
- Produces: nothing new — `ModPathClassifier`'s existing public surface (`Classify`, `Resolve`) is completely unchanged; this task only changes internals.

No new tests — this task's job is deleting duplicated, buggy logic and confirming the *existing* test suite still passes, proving the fix is behavior-preserving for every case those tests actually cover.

- [ ] **Step 1: Locate and remove the duplicated suffix-extraction logic**

In `PenumbraOrganizer.Core/Classification/ModPathClassifier.cs`, find `BuildCanonicalTarget`:

```csharp
private static CanonicalGameTarget BuildCanonicalTarget(string normalizedPath)
{
    var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var root = segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : segments.ElementAtOrDefault(0) ?? string.Empty;
    var primaryId = segments.Length >= 3 ? segments[2] : null;
    var fileName = segments.Length > 0 ? segments[^1] : normalizedPath;
    var suffix = ExtractFileSuffix(fileName);
    var secondaryId = segments.FirstOrDefault(IsSecondaryIdSegment);
    return new CanonicalGameTarget(normalizedPath, root, suffix, primaryId, secondaryId);
}
```

Replace the `ExtractFileSuffix(fileName)` call with the shared mapper:

```csharp
private static CanonicalGameTarget BuildCanonicalTarget(string normalizedPath)
{
    var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var root = segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : segments.ElementAtOrDefault(0) ?? string.Empty;
    var primaryId = segments.Length >= 3 ? segments[2] : null;
    var fileName = segments.Length > 0 ? segments[^1] : normalizedPath;
    var suffix = EquipmentSlotMapper.ExtractRawSuffixToken(fileName);
    var secondaryId = segments.FirstOrDefault(IsSecondaryIdSegment);
    return new CanonicalGameTarget(normalizedPath, root, suffix, primaryId, secondaryId);
}
```

`EquipmentSlotMapper` is in the same namespace (`PenumbraOrganizer.Core.Classification`) as `ModPathClassifier` — no new `using` needed.

- [ ] **Step 2: Delete the now-unused private `ExtractFileSuffix` method**

Find and delete this method entirely (it has no other callers after Step 1):

```csharp
private static string? ExtractFileSuffix(string fileName)
{
    var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
    var lastUnderscore = withoutExtension.LastIndexOf('_');
    return lastUnderscore >= 0 && lastUnderscore < withoutExtension.Length - 1
        ? withoutExtension[(lastUnderscore + 1)..]
        : null;
}
```

- [ ] **Step 3: Build to confirm no compile errors**

Run: `dotnet build PenumbraOrganizer.Core/PenumbraOrganizer.Core.csproj`
Expected: Build succeeds, 0 errors.

- [ ] **Step 4: Run the full standalone-app test suite to confirm no regressions**

Run: `dotnet test PenumbraOrganizer.Tests`
Expected: PASS, full suite green — specifically confirm these two pass (they're the ones pinning `ModPathClassifier`'s raw-string `Subcategory` contract this change must not break):
- `ModPathClassifierTests.Resolve_GearTarget_CarriesRawSlotSuffixAsSubcategory` (expects `"sho"`)
- `PenumbraScanServiceTests`' two Gear-mod scan fixtures (expect `"top"` and `"sho"`)

If either fails, STOP and report — it means the token-search regex disagrees with the old "last underscore" logic for one of these exact fixtures, which was verified NOT to happen by hand before this plan was written; a failure here means that hand-verification was wrong and needs to be re-examined before proceeding, not worked around.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Core/Classification/ModPathClassifier.cs
git commit -m "fix: delegate ModPathClassifier's suffix extraction to EquipmentSlotMapper"
```

---

## Task 3: Link `EquipmentSlot`/`EquipmentSlotMapper` into the plugin + `ModEquipmentFileReader`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/ModEquipmentFileReader.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModEquipmentFileReaderTests.cs`

**Repo:** `C:\Repo\PenumbraOrganizer.Plugin` (this plugin), inside the worktree this plan is executed under.

**Interfaces:**
- Consumes: `EquipmentSlot`, `EquipmentSlotMapper.ExtractSlotFromFileName`, `EquipmentSlotMapper.MapManipulationSlot` (Task 1, now linked into this repo).
- Produces: `static class ModEquipmentFileReader` with `ReadEquipmentSlots(DirectoryInfo modDirectory) -> IReadOnlySet<EquipmentSlot>?`. Task 4 consumes this signature.

- [ ] **Step 1: Link the two new standalone-app files into the plugin csproj**

In `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`, find the last `<ItemGroup>` (the one with `<Compile Include>` entries) and add two new lines, matching the existing pattern exactly:

```xml
  <ItemGroup>
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\ModCategory.cs" Link="Linked\ModCategory.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\ModClassificationModels.cs" Link="Linked\ModClassificationModels.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Interfaces\ICreatorCanonicalizer.cs" Link="Linked\ICreatorCanonicalizer.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Services\CreatorCanonicalizer.cs" Link="Linked\CreatorCanonicalizer.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Models\WorkbookWorkflowModels.cs" Link="Linked\WorkbookWorkflowModels.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Models\DomainModels.cs" Link="Linked\DomainModels.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Models\OrganizerModels.cs" Link="Linked\OrganizerModels.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Identity\ScanIdentity.cs" Link="Linked\ScanIdentity.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\EquipmentSlot.cs" Link="Linked\EquipmentSlot.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\EquipmentSlotMapper.cs" Link="Linked\EquipmentSlotMapper.cs" />
    <Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Infrastructure\Exports\WorkbookWorkflowService.cs" Link="Linked\WorkbookWorkflowService.cs" />
  </ItemGroup>
```

(Only the two new lines are additions — everything else in that block stays exactly as it is; shown in full here so the exact insertion point is unambiguous.)

- [ ] **Step 2: Build to confirm the linked files compile into the plugin**

Run: `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
Expected: Build succeeds. This requires Tasks 1-2 to already be committed in the standalone app repo, since the link is a relative filesystem path (`..\..\PenumbraOrganizer\...`) — if this fails with a missing-file error, confirm Tasks 1-2 actually landed in `C:\Repo\PenumbraOrganizer` first.

- [ ] **Step 3: Write the failing tests for `ModEquipmentFileReader`**

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

using System.Text.Json;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

public class ModEquipmentFileReaderTests
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
    public void ReadEquipmentSlots_SingleSlotFromFiles_ResolvesFeet()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"files/sho.mdl"},"Manipulations":[]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Feet], result);
    }

    [Fact]
    public void ReadEquipmentSlots_SingleSlotFromEqpManipulation_ResolvesTop()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Type":"Eqp","Manipulation":{"Entry":16129,"SetId":6040,"Slot":"Body"}}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Top], result);
    }

    [Fact]
    public void ReadEquipmentSlots_SingleSlotFromImcManipulation_ResolvesFeet()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Type":"Imc","Manipulation":{"PrimaryId":227,"Variant":0,"EquipSlot":"Feet","BodySlot":"Unknown"}}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Feet], result);
    }

    [Fact]
    public void ReadEquipmentSlots_EstManipulation_ContributesNothing()
    {
        // Est manipulations have a "Slot" too, but it means a customization slot (Hair/Face),
        // not equipment — must be excluded by the Type filter, not vocabulary non-overlap.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Type":"Est","Manipulation":{"Entry":161,"Gender":"Female","Race":"Miqote","SetId":157,"Slot":"Hair"}}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadEquipmentSlots_MultipleGroupsDifferentSlots_ResolvesBothDistinctSlots()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", "{}");
        WriteJson(mod, "group_001_top.json", """
            {"Options":[{"Files":{"chara/equipment/e0686/model/c0201e0686_top.mdl":"x"},"Manipulations":[]}]}
            """);
        WriteJson(mod, "group_002_legs.json", """
            {"Options":[{"Files":{"chara/equipment/e0686/model/c0201e0686_dwn.mdl":"x"},"Manipulations":[]}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains(EquipmentSlot.Top, result);
        Assert.Contains(EquipmentSlot.Legs, result);
    }

    [Fact]
    public void ReadEquipmentSlots_MissingDirectory_ReturnsEmptySetNotNull()
    {
        var missingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", "does-not-exist-" + Guid.NewGuid().ToString("N")));

        var result = ModEquipmentFileReader.ReadEquipmentSlots(missingDir);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadEquipmentSlots_NoConfigFiles_ReturnsEmptySetNotNull()
    {
        var mod = MakeTempModDirectory(); // directory exists, but no default_mod.json/group_*.json

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadEquipmentSlots_OneMalformedFileAmongValidOnes_ReturnsNullNotPartialResult()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"x"},"Manipulations":[]}
            """);
        WriteJson(mod, "group_001_broken.json", "{ not valid json");

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.Null(result); // the fail-closed fix: NOT a set containing only Feet
    }

    [Fact]
    public void ReadEquipmentSlots_NonEquipmentPath_ContributesNothing()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0101/obj/face/f0001/model/c0101f0001_fac.mdl":"x"},"Manipulations":[]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadEquipmentSlots_ManipulationMissingTypeOrManipulationField_IgnoredNotCrash()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Slot":"Body"},{"Type":"Eqp"}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadEquipmentSlots_NestedOptionsWithinContainers_TraversalIsGenuinelyRecursive()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Containers":[{"Options":[{"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"x"},"Manipulations":[]}]}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Feet], result);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter ModEquipmentFileReaderTests`
Expected: FAIL (compile error — `ModEquipmentFileReader` doesn't exist yet)

- [ ] **Step 5: Implement `ModEquipmentFileReader`**

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

        if (!modDirectory.Exists)
            return slots; // no directory, no evidence — not a read failure, just nothing to find

        IReadOnlyList<FileInfo> configFiles;
        try
        {
            // Materialized eagerly, inside the try: DirectoryInfo.EnumerateFiles is lazy, so a
            // permission error during enumeration would otherwise surface outside any catch.
            var files = new List<FileInfo>();
            var defaultMod = new FileInfo(Path.Combine(modDirectory.FullName, "default_mod.json"));
            if (defaultMod.Exists)
                files.Add(defaultMod);
            files.AddRange(modDirectory.EnumerateFiles("group_*.json"));
            configFiles = files;
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            return null;
        }

        foreach (var file in configFiles)
        {
            if (!TryCollectSlots(file, slots))
                return null;
        }

        return slots;
    }

    private static bool TryCollectSlots(FileInfo file, HashSet<EquipmentSlot> slots)
    {
        try
        {
            using var stream = File.OpenRead(file.FullName);
            using var document = JsonDocument.Parse(stream);
            CollectSlotsFromElement(document.RootElement, slots);
            return true;
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex) || ex is JsonException)
        {
            return false;
        }
    }

    private static bool IsExpectedFileSystemException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException; // UnauthorizedAccessException does NOT
                                                            // derive from IOException in .NET

    private static void CollectSlotsFromElement(JsonElement element, HashSet<EquipmentSlot> slots)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return; // guards every TryGetProperty call below against a non-object element

        CollectFromFilesAndManipulations(element, slots);
        CollectFromChildArray(element, "Options", slots);
        CollectFromChildArray(element, "Containers", slots);
    }

    private static void CollectFromChildArray(JsonElement element, string propertyName, HashSet<EquipmentSlot> slots)
    {
        if (!element.TryGetProperty(propertyName, out var children) || children.ValueKind != JsonValueKind.Array)
            return;

        // Genuinely recursive (calls CollectSlotsFromElement, not just the Files/Manipulations
        // step) — real mods checked across ~2,280 combined mods never nest beyond one level,
        // but this doesn't silently stop early if a future/unusual mod does.
        foreach (var child in children.EnumerateArray())
            CollectSlotsFromElement(child, slots);
    }

    private static void CollectFromFilesAndManipulations(JsonElement element, HashSet<EquipmentSlot> slots)
    {
        if (element.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in files.EnumerateObject())
            {
                var path = entry.Name.Replace('\\', '/');
                if (!path.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith("chara/accessory/", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileName = Path.GetFileName(path);
                if (EquipmentSlotMapper.ExtractSlotFromFileName(fileName) is { } slot)
                    slots.Add(slot);
            }
        }

        if (element.TryGetProperty("Manipulations", out var manipulations) && manipulations.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in manipulations.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!item.TryGetProperty("Type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                    continue;
                if (!item.TryGetProperty("Manipulation", out var manipulation) || manipulation.ValueKind != JsonValueKind.Object)
                    continue;

                // Real shape, confirmed against two independent real mod libraries: the slot
                // lives nested under "Manipulation", not as a direct property of the array
                // element, and the field name depends on "Type" — Eqp/Eqdp use "Slot", Imc uses
                // "EquipSlot". "Est" also has a "Slot", but it names a customization slot
                // (Hair/Face), not equipment — deliberately excluded by only recognizing the
                // three equipment-relevant types.
                var slotFieldName = typeProp.GetString() switch
                {
                    "Eqp" or "Eqdp" => "Slot",
                    "Imc" => "EquipSlot",
                    _ => null,
                };
                if (slotFieldName is null)
                    continue;

                if (manipulation.TryGetProperty(slotFieldName, out var slotProp)
                    && slotProp.ValueKind == JsonValueKind.String
                    && EquipmentSlotMapper.MapManipulationSlot(slotProp.GetString()!) is { } slot)
                    slots.Add(slot);
            }
        }
    }
}
```

Note the `using PenumbraOrganizer.Core.Classification;` at the top: `ModEquipmentFileReader` lives in `PenumbraOrganizer.Plugin.Organizer.Classification`, a sibling namespace to `PenumbraOrganizer.Core.Classification` (where the linked `EquipmentSlotMapper`/`EquipmentSlot` live), not a parent/child relationship, so C#'s enclosing-namespace search won't resolve `EquipmentSlotMapper` unqualified without it. No ambiguity risk — nothing of the same name exists in `PenumbraOrganizer.Plugin.Organizer.Classification` (`NpcNameKind`/`NpcNameMatcher` etc. are differently named).

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter ModEquipmentFileReaderTests`
Expected: PASS (11 tests)

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, no regressions.

- [ ] **Step 8: Commit**

```bash
git add PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj PenumbraOrganizer.Plugin/Organizer/Classification/ModEquipmentFileReader.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModEquipmentFileReaderTests.cs
git commit -m "feat: add ModEquipmentFileReader with fail-closed equipment slot resolution"
```

---

## Task 4: `ModTypeClassifier.EnrichGearSubCategory` + `ModTypeFolders.GetFolder` Gear cases

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs`

**Repo:** `C:\Repo\PenumbraOrganizer.Plugin`, in the worktree.

**Interfaces:**
- Consumes: `EquipmentSlot` (Task 1, linked), `ModEquipmentFileReader.ReadEquipmentSlots` return shape (Task 3 — not called here, just the type it returns).
- Produces: `ModTypeClassifier.EnrichGearSubCategory(ClassificationResult baseResult, IReadOnlySet<EquipmentSlot>? equipmentSlots) -> ClassificationResult`. Task 5 (`Plugin.cs`) consumes this exact signature. `ModTypeFolders.GetFolder` gains 9 new valid `(Gear, slotName)` pairs.

`ModTypeClassifier.Classify` itself is NOT modified in this task — its three-argument signature stays exactly as it is.

- [ ] **Step 1: Add the failing tests for `EnrichGearSubCategory`**

Add to `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs` (add a `using PenumbraOrganizer.Core.Classification;` at the top if not already present, for `EquipmentSlot`):

```csharp
    // --- EnrichGearSubCategory ---

    [Fact]
    public void EnrichGearSubCategory_GearResultWithOneSlot_AssignsSubCategory()
    {
        var baseResult = new ClassificationResult(ModCategory.Gear, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichGearSubCategory(baseResult, new HashSet<EquipmentSlot> { EquipmentSlot.Feet });

        Assert.Equal(ModCategory.Gear, result.Category);
        Assert.Equal("Feet", result.SubCategory);
    }

    [Fact]
    public void EnrichGearSubCategory_GearResultWithNullRead_LeavesSubCategoryNull()
    {
        var baseResult = new ClassificationResult(ModCategory.Gear, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichGearSubCategory(baseResult, null);

        Assert.Equal(ModCategory.Gear, result.Category);
        Assert.Null(result.SubCategory);
    }

    [Fact]
    public void EnrichGearSubCategory_GearResultWithEmptySet_LeavesSubCategoryNull()
    {
        var baseResult = new ClassificationResult(ModCategory.Gear, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichGearSubCategory(baseResult, new HashSet<EquipmentSlot>());

        Assert.Null(result.SubCategory);
    }

    [Fact]
    public void EnrichGearSubCategory_GearResultWithMultipleSlots_LeavesSubCategoryNull()
    {
        var baseResult = new ClassificationResult(ModCategory.Gear, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichGearSubCategory(
            baseResult, new HashSet<EquipmentSlot> { EquipmentSlot.Top, EquipmentSlot.Legs });

        Assert.Null(result.SubCategory);
    }

    [Fact]
    public void EnrichGearSubCategory_NonGearResult_ReturnedUnchangedRegardlessOfSlots()
    {
        var baseResult = new ClassificationResult(ModCategory.Face, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichGearSubCategory(
            baseResult, new HashSet<EquipmentSlot> { EquipmentSlot.Feet });

        Assert.Equal(baseResult, result); // completely untouched — proves the gating is real
    }
```

- [ ] **Step 2: Add the failing tests for `GetFolder`'s new Gear cases**

Find the existing `GetFolder_MapsCategoryAndSubCategory` theory in the same test file and add the 9 new rows:

```csharp
    [Theory]
    [InlineData(ModCategory.Gear, null, "Gear")]
    [InlineData(ModCategory.NPC, null, "NPC")]
    [InlineData(ModCategory.Animation, "Battle Animation", "Animation and VFX/Battle Animation")]
    [InlineData(ModCategory.VFX, "VFX", "Animation and VFX/VFX")]
    [InlineData(ModCategory.NPC, "NPCs", "NPC/NPCs")]
    [InlineData(ModCategory.NPC, "Enemies", "NPC/Enemies")]
    [InlineData(ModCategory.NPC, "Bosses", "NPC/Bosses")]
    [InlineData(ModCategory.Gear, "Head", "Gear/Head")]
    [InlineData(ModCategory.Gear, "Top", "Gear/Top")]
    [InlineData(ModCategory.Gear, "Hands", "Gear/Hands")]
    [InlineData(ModCategory.Gear, "Legs", "Gear/Legs")]
    [InlineData(ModCategory.Gear, "Feet", "Gear/Feet")]
    [InlineData(ModCategory.Gear, "Ears", "Gear/Ears")]
    [InlineData(ModCategory.Gear, "Neck", "Gear/Neck")]
    [InlineData(ModCategory.Gear, "Wrists", "Gear/Wrists")]
    [InlineData(ModCategory.Gear, "Rings", "Gear/Rings")]
    public void GetFolder_MapsCategoryAndSubCategory(ModCategory category, string? sub, string expected)
    {
        Assert.Equal(expected, ModTypeFolders.GetFolder(category, sub));
    }
```

(Replace the existing theory's `InlineData` list with this expanded version — same method name, more rows.)

Also add a dedicated throw test right after it, if one doesn't already exist for the `Gear` category specifically:

```csharp
    [Fact]
    public void GetFolder_GearWithUnsupportedSubCategory_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModTypeFolders.GetFolder(ModCategory.Gear, "NotARealSlot"));
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "EnrichGearSubCategory|GetFolder"`
Expected: FAIL (compile error — `EnrichGearSubCategory` doesn't exist yet; the new `GetFolder` rows fail because `Gear` isn't a validated case yet)

- [ ] **Step 4: Implement `EnrichGearSubCategory`**

In `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`, add a `using PenumbraOrganizer.Core.Classification;` if not already present (it already is, at the top of the file, for `ModCategory`) — `EquipmentSlot` is in the same namespace, no new using needed. Add this new public method to the `ModTypeClassifier` class, near `Classify`:

```csharp
    // Second pass, run by the caller only when Classify already returned Category: Gear — never
    // a new top-level classification path, and Classify itself is never modified to call this.
    // Disk I/O for equipment-slot detection only happens where the caller chooses to call this,
    // which Plugin.RunScan() gates on Category == Gear (see Plugin.cs).
    public static ClassificationResult EnrichGearSubCategory(
        ClassificationResult baseResult, IReadOnlySet<EquipmentSlot>? equipmentSlots)
    {
        if (baseResult.Category != ModCategory.Gear || equipmentSlots is null || equipmentSlots.Count != 1)
            return baseResult; // not Gear, read failed, no evidence, or ambiguous (>1 slot) — untouched
        return baseResult with { SubCategory = EquipmentSlotMapper.FolderName(equipmentSlots.Single()) };
    }
```

- [ ] **Step 5: Extend `ModTypeFolders.GetFolder` with the Gear cases**

Find the existing switch expression:

```csharp
    public static string GetFolder(ModCategory category, string? subCategory) => (category, subCategory) switch
    {
        (_, null) => category.ToString(),
        (ModCategory.Animation or ModCategory.VFX, _) => $"{AnimationVfxParent}/{subCategory}",
        (ModCategory.NPC, "NPCs" or "Enemies" or "Bosses") => $"{ModCategory.NPC}/{subCategory}",
        _ => throw new ArgumentOutOfRangeException(
            nameof(subCategory), subCategory, $"Unsupported subcategory '{subCategory}' for {category}."),
    };
```

Add one new case above the `_ =>` fallback:

```csharp
    public static string GetFolder(ModCategory category, string? subCategory) => (category, subCategory) switch
    {
        (_, null) => category.ToString(),
        (ModCategory.Animation or ModCategory.VFX, _) => $"{AnimationVfxParent}/{subCategory}",
        (ModCategory.NPC, "NPCs" or "Enemies" or "Bosses") => $"{ModCategory.NPC}/{subCategory}",
        (ModCategory.Gear, "Head" or "Top" or "Hands" or "Legs" or "Feet" or "Ears" or "Neck" or "Wrists" or "Rings")
            => $"{ModCategory.Gear}/{subCategory}",
        _ => throw new ArgumentOutOfRangeException(
            nameof(subCategory), subCategory, $"Unsupported subcategory '{subCategory}' for {category}."),
    };
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "EnrichGearSubCategory|GetFolder"`
Expected: PASS (5 `EnrichGearSubCategory` tests + 16 `GetFolder` rows + 1 throw test)

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, no regressions — `Classify`'s own tests are completely unaffected since its signature didn't change.

- [ ] **Step 8: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs
git commit -m "feat: add EnrichGearSubCategory and validated Gear subcategory folders"
```

---

## Task 5: Wire into `Plugin.RunScan()`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Repo:** `C:\Repo\PenumbraOrganizer.Plugin`, in the worktree.

**Interfaces:**
- Consumes: `ModEquipmentFileReader.ReadEquipmentSlots(DirectoryInfo) -> IReadOnlySet<EquipmentSlot>?` (Task 3), `ModTypeClassifier.EnrichGearSubCategory(ClassificationResult, IReadOnlySet<EquipmentSlot>?) -> ClassificationResult` (Task 4).

No new tests — `RunScan()` isn't unit-testable in isolation, matching this codebase's existing convention (`RunScan`/`ApplyChanges`/`ExportWorkbook` are all verified by build + full suite + in-game testing, never direct unit tests). Verification here is build + full suite + a manual in-game check noted at the end.

- [ ] **Step 1: Update `RunScan()`'s row-building code**

In `PenumbraOrganizer.Plugin/Plugin.cs`, find `RunScan()`'s mod-row-building lambda:

```csharp
        var rows = modList.Select(mod =>
        {
            var changedItemKeys = allChangedItems.TryGetValue(mod.Identifier, out var changedItems)
                ? changedItems.Keys
                : Enumerable.Empty<string>();
            var classification = ModTypeClassifier.Classify(mod.Name, changedItemKeys, npcNameMatcher);

            return new Organizer.OrganizerModRow
            {
                Identifier = mod.Identifier,
                Name = mod.Name,
                Author = mod.Author,
                CurrentPath = mod.FullPath,
                ProposedPath = mod.FullPath,
                HeliosphereManaged = Organizer.HeliosphereDetector.IsHeliosphereManaged(mod.Identifier, mod.ModPath),
                Category = classification.Category,
                SubCategory = classification.SubCategory,
```

Insert the Gear-gated enrichment step right after `Classify` is called, before the `OrganizerModRow` is constructed:

```csharp
        var rows = modList.Select(mod =>
        {
            var changedItemKeys = allChangedItems.TryGetValue(mod.Identifier, out var changedItems)
                ? changedItems.Keys
                : Enumerable.Empty<string>();
            var classification = ModTypeClassifier.Classify(mod.Name, changedItemKeys, npcNameMatcher);

            // Disk I/O only for mods the existing GetChangedItems-based rule already confirmed
            // are Gear — every other category never touches disk for this.
            if (classification.Category == ModCategory.Gear)
            {
                var equipmentSlots = Organizer.Classification.ModEquipmentFileReader.ReadEquipmentSlots(mod.ModPath);
                classification = ModTypeClassifier.EnrichGearSubCategory(classification, equipmentSlots);
            }

            return new Organizer.OrganizerModRow
            {
                Identifier = mod.Identifier,
                Name = mod.Name,
                Author = mod.Author,
                CurrentPath = mod.FullPath,
                ProposedPath = mod.FullPath,
                HeliosphereManaged = Organizer.HeliosphereDetector.IsHeliosphereManaged(mod.Identifier, mod.ModPath),
                Category = classification.Category,
                SubCategory = classification.SubCategory,
```

(Everything below `SubCategory = classification.SubCategory,` in the existing lambda — the closing of the object initializer and the `.ToList()` call — stays exactly as it is; only the two lines above are new.)

`mod.ModPath` is already a `DirectoryInfo` on the mod-list IPC result (already used a few lines below for `HeliosphereDetector.IsHeliosphereManaged`), so no new IPC call or type conversion is needed.

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
Expected: Build succeeds, 0 errors.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, no regressions.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: wire ModEquipmentFileReader/EnrichGearSubCategory into RunScan"
```

- [ ] **Step 5 (manual, in-game, not part of the automated build): verification checklist for whoever picks this up next**

- Scan a real library containing single-slot Gear mods of several different kinds (a hat, a top, gloves, boots, a ring) and confirm each gets the correct `SubCategory` and, after Sort by Type, lands in the correct `Gear/<Slot>` folder.
- Scan a real multi-piece outfit mod (several groups, genuinely different slots) and confirm it stays plain `Gear` with no subcategory, not misclassified into one arbitrary slot.
- Confirm a mod already known to be a shared-Gear-item mod that isn't in `C:\Mods`/the validation library still scans without error (no crash) even if its files don't parse as expected.
- Time a full scan on your largest available real library before and after this change; if there's a noticeable slowdown, that's the trigger to revisit the "no async" decision (see the design spec's Open Risks), not something to pre-empt now.

---

## Task 6: Final whole-branch review, `ROADMAP.md` update

**Files:**
- Modify: `docs/ROADMAP.md`

**Repo:** `C:\Repo\PenumbraOrganizer.Plugin`, in the worktree.

- [ ] **Step 1: Update `docs/ROADMAP.md`**

In the "Where we are" section at the top, bump the date and add a new bullet matching the existing convention used for the NPC classification and workbook entries (implemented, pending in-game verification):

```markdown
- **Detailed gear-slot classification — implemented, pending in-game verification.** Gear mods now
  resolve a `SubCategory` (Head/Top/Hands/Legs/Feet/Ears/Neck/Wrists/Rings) by reading the mod's own
  files directly from Penumbra's mod library on disk — data `GetChangedItems` never exposes. Reuses
  a small linked `EquipmentSlotMapper` (also fixes a latent suffix-extraction bug in the standalone
  app's own `ModPathClassifier`). See
  `docs/superpowers/specs/2026-07-18-plugin-organizer-gear-slot-classification-design.md` and
  `docs/superpowers/plans/2026-07-18-plugin-organizer-gear-slot-classification.md`.
```

Also update the "Detailed gear-slot sorting (parking lot)" section further down — change its
**Status** line from "real future feature, deferred — not a rejected idea" to "implemented, see the
'Where we are' entry above," leaving the rest of that section's research (the IPC survey, the
rejected keyword-heuristic alternative) as historical record.

- [ ] **Step 2: Run the full test suite one final time**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, full suite green.

- [ ] **Step 3: Commit**

```bash
git add docs/ROADMAP.md
git commit -m "docs: mark detailed gear-slot classification as implemented"
```

## Final whole-branch review

After Task 6, dispatch a whole-branch review (most capable model available) covering the full diff across all 6 tasks — including the standalone-app-repo changes from Tasks 1-2, which a plugin-repo-only diff would miss — before merging. Specifically double-check:
- `ModPathClassifier`'s public contract (raw lowercase `Subcategory` strings) genuinely didn't change for any case beyond the two specific fixtures already re-verified in Task 2.
- No production code path other than `ModEquipmentFileReader.ReadEquipmentSlots` reads Penumbra's mod library files.
- `Classify`'s three-argument signature is untouched anywhere in the diff.
- The fail-closed contract (`null` on any read anomaly) is actually honored everywhere `ReadEquipmentSlots` is called — there's only one call site (`Plugin.RunScan()`), confirm it doesn't coerce a `null` into an empty set anywhere before passing it to `EnrichGearSubCategory`.
