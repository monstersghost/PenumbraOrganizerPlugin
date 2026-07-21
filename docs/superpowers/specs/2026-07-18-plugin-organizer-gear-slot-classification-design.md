# Design: Detailed gear-slot classification (Head/Top/Hands/Legs/Feet + accessories)

**Status:** design approved by user in conversation, awaiting written-spec review.
**Depends on:** `docs/ROADMAP.md`'s "Detailed gear-slot sorting (parking lot)" section — that section's
data-source blocker (no Penumbra IPC exposes real game paths or slot data) is resolved here by
reading Penumbra's mod library directly off disk, the same mechanism the standalone app already
uses successfully.

## Context

### The gap

`ModTypeClassifier.Classify` (`PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`)
resolves any mod with a real named Gear item to `Category: Gear, SubCategory: null` — every piece of
equipment lands in one flat bucket regardless of whether it's a hat, a full-body outfit, gloves,
boots, or a ring. The ROADMAP has tracked this as a real, wanted feature ("detailed gear-slot
sorting") since Phase 1e, previously blocked on a genuine data-source gap: Penumbra's
`GetChangedItems` IPC (this plugin's only classification signal until now) returns human-readable
item names, never real internal game file paths or slot manipulation data. A full survey of every
`Penumbra.Api` 5.15.1 IPC subscriber (2026-07-14) confirmed no per-mod call exposes that data.

### The unblock

The standalone WPF app (`C:\Repo\PenumbraOrganizer`) already solves exactly this by reading
Penumbra's mod library **directly off disk** — `meta.json`, `default_mod.json`, `group_*.json` per
mod, fully offline, no game or Penumbra process required — and mapping the resulting file-path
suffixes (`_met`, `_top`, `_glv`, `_dwn`, `_sho`, plus accessory suffixes) and
`Manipulations[].Slot` values to equipment slots (`PenumbraOrganizer.Core/Classification/
ModPathClassifier.cs`). This plugin already reads a different Penumbra on-disk file today —
`organization.json`, for Folder Cleanup (shipped 2026-07-15, `Plugin.cs`'s `PenumbraConfigDirectory`)
— so reading Penumbra's files directly off disk is an established pattern for this plugin, not a new
category of capability.

**Two distinct directories, worth being explicit about since this feature touches a new one:**
- **Penumbra's plugin config directory** (`pluginConfigs/Penumbra/`) — settings, `organization.json`,
  `mod_data.db`. Already read today (Folder Cleanup).
  Corresponds to `Plugin.cs`'s `PenumbraConfigDirectory`.
- **Penumbra's mod library / "ModRoot"** — a separate, user-configured directory where each mod's own
  folder lives (`meta.json`, `default_mod.json`, `group_*.json`, and the actual redirected game
  files). This is where the `Files`/`Manipulations` data this feature needs lives — **not yet read by
  this plugin anywhere.** No new capability is needed to reach it, though: `GetModDirectory()` IPC
  (already called in `Plugin.cs`'s `BuildInstallation()`) gives the library root, and — more
  directly — `OrganizerModRow`'s per-mod `DirectoryInfo` from the mod-list IPC call (`mod.ModPath`,
  already used for Heliosphere detection) points straight at each individual mod's own folder. No
  new IPC call, no root-plus-directory-name concatenation needed.

### Capability/policy research (informs this design, not just a footnote)

- **Dalamud plugins are technically unsandboxed** — full local file-system access like any other
  process. Reading another plugin's on-disk data is not a technical restriction.
- **Dalamud's plugin-publishing guidelines** (dalamud.dev/plugin-publishing/restrictions) have no
  explicit rule against a plugin reading another plugin's local config files — the one relevant
  restriction ("no hard dependency on a plugin that violates the guidelines") doesn't apply here.
  Their own guidance is to ask the approval team before doing anything unusual, which only matters
  once/if this plugin is ever submitted to the official repository — currently unresearched and not
  planned (see `docs/ROADMAP.md`'s Phase 3 section).
- **Square Enix's ToS** blanket-prohibits all third-party tools, already fully priced in by using
  Dalamud + Penumbra at all. Reading Penumbra's own local files doesn't add a new category of risk on
  top of that baseline.

## Goal

For a mod already classified as `Category: Gear` (via the existing `GetChangedItems`-based rule,
unchanged), additionally resolve `SubCategory` to one of 9 friendly slot names — `Head`, `Top`,
`Hands`, `Legs`, `Feet`, `Ears`, `Neck`, `Wrists`, `Rings` — by reading the mod's own on-disk files,
so `Sort by Type` can nest Gear mods into slot-level subfolders (`Gear/Head`, `Gear/Top`, ...) the
same way Animation/VFX and NPC subcategories already nest.

## Non-goals

- **Not a wholesale replacement of the existing classifier.** The standalone app's
  `ModPathClassifier` is a much more capable, general-purpose path-based classification pipeline —
  it also resolves NPC race codes, VFX, Sound, Animation, Body/Face/Hair customization, and
  monster/demihuman creatures, essentially covering everything this plugin's `GetChangedItems`-based
  classifier already does today, via a different (arguably stronger) signal. **This is a real,
  noted-for-later possibility** — a future "path-based reclassification" project, evaluating whether
  reading mod library files should become a second signal source (or eventually replace parts of the
  `GetChangedItems` approach) for categories beyond Gear. Explicitly out of scope for this design;
  don't rediscover this framing next time, just pick up this note.
- **Not changing which mods land in `Category: Gear`.** The existing `GetChangedItems`-based Rule 1
  ("Gear wins") is untouched. This feature only enriches `SubCategory` for mods that rule already
  puts in Gear.
- **Not a guess for ambiguous cases.** A mod whose files resolve to more than one slot (a full
  outfit/armor set bundling Top + Hands + Legs + Feet) gets no subcategory — falls back to plain
  `Gear`, matching the classifier's existing "never guess" principle, the same as the missing/
  unreadable-file case.
- **Not reading any file outside `chara/equipment/`/`chara/accessory/` paths or their matching
  manipulation slots.** No VFX/Sound/Animation/customization path handling — that's the noted-for-
  later possibility above, not this design.

## Architecture

### 1. `EquipmentSlot` enum + `EquipmentSlotMapper` (new, extracted + linked)

Slot identity is a typed enum internally, not raw strings passed around between components — the
same pattern already used for `NpcNameKind` in the NPC classification feature. Strings only appear
at the one boundary where `SubCategory` is actually constructed (`ModTypeFolders`/`ClassificationResult`
already store it as `string?`, matching every other category).

```csharp
namespace PenumbraOrganizer.Core.Classification;

public enum EquipmentSlot { Head, Top, Hands, Legs, Feet, Ears, Neck, Wrists, Rings }
```

A small new pure class in the standalone app's Core project,
`PenumbraOrganizer.Core/Classification/EquipmentSlotMapper.cs`, extracted from `ModPathClassifier`'s
equipment-specific logic (not the whole class — `ModPathClassifier` also handles NPC/VFX/Sound/
creature paths this design doesn't need). Linked into the plugin via `<Compile Include>`, the same
pattern already used for `ModCategory.cs`/`ScanIdentity.cs`. **`ModPathClassifier` itself is updated
to delegate to this mapper rather than keeping its own duplicate copy of the suffix table** — the
standalone app's existing classification test fixtures must still pass unchanged after this
refactor, confirming the extraction didn't alter its behavior.

**Real constraint found while checking those existing fixtures, not just assumed:**
`ModPathClassifier`'s own `Subcategory` output is contractually the **raw, lowercase matched suffix
token** (`"sho"`, `"top"`), not a friendly folder name — confirmed by two real, already-passing
standalone-app tests: `ModPathClassifierTests.Resolve_GearTarget_CarriesRawSlotSuffixAsSubcategory`
(expects `"sho"`) and `PenumbraScanServiceTests`' two Gear-mod scan fixtures (expect `"top"` and
`"sho"` respectively). The plugin wants the opposite shape — a typed `EquipmentSlot` and a friendly
name (`"Feet"`) for folder nesting. Both are real, legitimate needs, so `EquipmentSlotMapper`
exposes the raw-token step as its own public method, and everything else composes from it — nobody
has to choose one shape over the other:

```csharp
// Extracts the raw, lowercase slot token from a filename (e.g. "sho") by searching for a known
// slot code as a delimited token anywhere in the name — not just after the final underscore,
// which fails on texture/material filenames with a trailing token (e.g. "..._sho_b_d.tex"; see
// the suffix-extraction fix below). Returns null if no known slot token appears anywhere.
public static string? ExtractRawSuffixToken(string fileName);
```

`ModPathClassifier.BuildCanonicalTarget`'s own private `ExtractFileSuffix` (the exact same
"last-underscore" logic this design already found and fixed independently — the bug exists in both
places because the plugin's earlier draft copied it from here) is deleted and replaced with a call
to `EquipmentSlotMapper.ExtractRawSuffixToken`. Verified against both real fixtures above by hand:
`c0201e1234_top.mdl` and `c0101e0387_sho.mdl` have no trailing token after the slot code, so the
old and new logic produce identical output (`"top"`, `"sho"`) for these exact tests — the fix is
safe for the existing suite and simultaneously corrects the same latent bug in the standalone app
for the texture/material cases those specific fixtures don't happen to cover.

**Confirmed against two real mod libraries via a purpose-built validation script before finalizing
this table** — `C:\Mods` (247 mods) and a second, much larger ~2,035-mod library: recognized suffix
codes are the complete real FFXIV equipment/accessory vocabulary and don't change (zero unrecognized
suffixes found across ~59,000 combined real equipment/accessory paths at both scales). `FileSwaps`
(a sibling of `Files` on every option) is rare but not literally zero — 0 non-empty instances in the
247-mod library, 6 in the 2,035-mod one (~0.3% of mods). Deliberately still not handled: at that
prevalence, and given `FileSwaps` redirects one already-existing game path to another rather than
adding new file content, the practical cost is a small number of mods staying at plain `Gear`
instead of gaining a subcategory — consistent with every other "insufficient evidence → don't
guess" case in this design, not a correctness risk.

**`Manipulations` — genuinely load-bearing, but not in the shape originally assumed.** An earlier
manual spot-check (a handful of files, via text search) found only empty arrays and concluded this
path was dead weight. Running the full library through the validation script instead found 50+
mods with real, non-empty `Manipulations`, and — more importantly — the real element shape is
completely different from a bare `{"Slot": "..."}`:

```json
{"Type":"Eqp","Manipulation":{"Entry":16129,"SetId":6040,"Slot":"Body"}}
{"Type":"Eqdp","Manipulation":{"Entry":0,"Gender":"Male","Race":"Midlander","SetId":53,"Slot":"Ears"}}
{"Type":"Imc","Manipulation":{"Entry":{...},"PrimaryId":227,"Variant":0,"EquipSlot":"Feet","BodySlot":"Unknown"}}
{"Type":"Est","Manipulation":{"Entry":161,"Gender":"Female","Race":"Miqote","SetId":157,"Slot":"Hair"}}
```

Every element has a `Type` discriminator, and the actual slot lives nested one level deeper, under
`Manipulation` — not as a direct property of the array element (what the original draft's code
checked for, meaning it would never have matched anything real). The field is even named
differently depending on `Type`: `Eqp`/`Eqdp` use `Slot`, `Imc` uses `EquipSlot`. `Est` also has a
`Slot`, but it means a *customization* slot (`Hair`, `Face`) — not equipment, and must be excluded
explicitly rather than relying on it simply not matching the equipment-slot vocabulary (harmless
today since `Hair`/`Face` aren't in that vocabulary either way, but filtering by `Type` first is the
correct fix, not a coincidence of non-overlapping vocabularies).

Re-running the corrected extraction against the same 247-mod library found it contributes real,
additional slot evidence — 1,394 slot signals recovered that the `Files`-path signal alone missed,
including at least one mod that flipped from "wrongly single-slot" to correctly multi-slot once the
manipulation-derived slot was counted. This is not a nice-to-have; skipping it would mean equipment
mods that only reveal their slot via a manipulation-only Penumbra option (no full model swap, just a
parameter change) never get a subcategory at all.

```csharp
namespace PenumbraOrganizer.Core.Classification;

public static class EquipmentSlotMapper
{
    // Matches a known slot code as a delimited token anywhere in a filename, not just as the
    // final segment — see "Suffix extraction" below for why that distinction is load-bearing.
    private static readonly Regex SlotTokenPattern = new(
        @"(?:^|_)(met|top|glv|dwn|sho|ear|nek|wrs|ril|rir)(?:_|\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Extracts the raw, lowercase slot token from a filename (e.g. "sho" from
    // "c0101e0387_sho.mdl", or from "c0101e6116_sho_b_d.tex" despite the trailing tokens).
    // Returns null if no known slot token appears anywhere in the filename. This is the raw-
    // string contract ModPathClassifier's own Subcategory field already exposes and has real,
    // passing tests pinning it to (see below) — kept separate from the enum-returning methods
    // below so both consumers get the shape they actually need.
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

    // Composes the two methods above: the plugin's actual entry point for "what slot does this
    // file belong to," as a typed EquipmentSlot rather than a raw string.
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

Left/right ring slots (`ril`/`rir` path suffixes, `RFinger`/`LFinger` manipulation slots) merge into
one `Rings` folder — this organizer deliberately groups both ring slots into a single library
category because users generally organize ring mods by item type rather than equipped side. (Both
slots genuinely are distinct in the game's data model — this is a product/organizing choice, not a
technical equivalence claim.)

### 1a. Suffix extraction — confirmed real gap, fixed with a token search, not "last underscore"

The naive "text after the last underscore" approach (what `ModPathClassifier.ExtractFileSuffix`
itself already does, and what an earlier draft of this design copied) **fails on real files** —
confirmed directly against `C:\Mods\Air Force 1 - by Solona`. Its second option group ("Print") is
entirely texture/material paths:

```
chara/equipment/e6116/texture/c0101e6116_sho_b_d.tex
chara/equipment/e6116/material/v0001/mt_c0101e6116_sho_b.mtrl
```

"Last underscore" extracts `d` / `b` — neither is a recognized slot code, so every path in that
whole option group would be silently ignored. This particular mod still resolves correctly overall
only because its *other* group has `.mdl` model files with clean suffixes — a mod built entirely
from textures/materials (a plain recolor with no custom mesh, a common real pattern) would find
zero recognized paths and incorrectly fall back to plain `Gear` even though it's unambiguously a
single-slot mod.

**Fix:** `EquipmentSlotMapper.ExtractRawSuffixToken`/`ExtractSlotFromFileName` (defined in §1 above)
search for a known slot code as a delimited token anywhere in the filename, not just as the final
segment. Verified by hand against every real filename shape found in `C:\Mods`'s sample mods:
`c0101e6116_sho.mdl`, `v01_c0101e6116_sho_m.tex`, `mt_c0101e6116_sho_a.mtrl`,
`c0101e6116_sho_b_d.tex` (two trailing tokens), `mt_c0101e6116_sho_b.mtrl` — the token search
matches `sho` in every one of them; "last underscore" only got the first.

**`ModPathClassifier` picks up the same fix as part of the extraction.** Its private
`ExtractFileSuffix` (`BuildCanonicalTarget`'s helper) is the exact same "last-underscore" logic,
copied independently — the bug exists in both places because the plugin's earlier draft copied it
from here in the first place. Deleted and replaced with a direct call:

```csharp
// In ModPathClassifier.BuildCanonicalTarget, replacing the private ExtractFileSuffix(fileName) call:
var suffix = EquipmentSlotMapper.ExtractRawSuffixToken(fileName);
```

`ModPathClassifier`'s private `ExtractFileSuffix` method is deleted entirely — no other caller
remains. `target.Suffix` (renamed nowhere, still a `string?`) keeps flowing into `ClassifyGamePath`'s
Gear/Weapon branches exactly as before, so `ModPathClassifier`'s own public contract (`Subcategory`
as a raw lowercase string) is completely unchanged — only how that string gets computed changes,
and only for inputs where the old and new logic actually disagree (which, verified above, is not
either of the two existing fixtures pinning this behavior).

### 2. `ModEquipmentFileReader` (new, plugin-native, not linked)

`PenumbraOrganizer.Plugin/Organizer/Classification/ModEquipmentFileReader.cs`. Given a mod's
`DirectoryInfo` (already available as `mod.ModPath` from the existing mod-list IPC call — no new IPC
needed), reads `default_mod.json` plus every `group_*.json` in that directory, and walks each one's
JSON the same shape confirmed against real files in `C:\Mods`: top-level `Files` (dictionary keys are
raw game paths) and `Manipulations[].Slot`, plus the same walk over `Options[]`/`Containers[]`
(genuinely recursive now — see below).

**Fail-closed, not partial-collect.** The original draft skipped an unreadable/malformed file and
kept whatever the other files found — but that can produce a *confident, wrong* single-slot result:
if `default_mod.json` resolves to `Top` and a `group_*.json` that would have resolved to `Feet`
fails to read, the mod would wrongly report `Top` alone. That directly violates this classifier's
own "never guess" principle. The fix: if *any* file in the mod can't be read, parsed, or enumerated,
the whole mod's result is untrustworthy — return `null` (distinct from an empty, successfully-read
set) and the caller treats that exactly like "no subcategory."

```csharp
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
        // step) — real mods checked in C:\Mods never nest beyond one level, but this doesn't
        // silently stop early if a future/unusual mod does.
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

                // Real shape, confirmed against C:\Mods: the slot lives nested under
                // "Manipulation", not as a direct property of the array element, and the field
                // name depends on "Type" — Eqp/Eqdp use "Slot", Imc uses "EquipSlot". "Est" also
                // has a "Slot", but it names a customization slot (Hair/Face), not equipment —
                // deliberately excluded by only recognizing the three equipment-relevant types.
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

### 3. Wiring — a separate enrichment step, not a new `Classify` parameter

**Corrected from an earlier draft of this design**, which had `Classify` take the resolved slot set
as a fourth parameter, with the caller resolving it up front for every mod before calling `Classify`
at all. That's a real bug: it means disk I/O happens for *every* mod — VFX, Hair, NPC, Sound,
everything — not only ones that turn out to be Gear, directly contradicting this same design's own
"gated strictly on the existing Gear rule already having fired" claim a few paragraphs earlier.

The fix is a genuine second pass, not a parameter: `Classify`'s existing three-argument signature is
completely unchanged — no fourth parameter, no migration needed for any of the ~40 existing
`ModTypeClassifierTests` call sites. A new, separate pure function enriches an already-produced
`ClassificationResult`:

```csharp
// In ModTypeClassifier — Classify itself is untouched.
public static ClassificationResult EnrichGearSubCategory(
    ClassificationResult baseResult, IReadOnlySet<EquipmentSlot>? equipmentSlots)
{
    if (baseResult.Category != ModCategory.Gear || equipmentSlots is null || equipmentSlots.Count != 1)
        return baseResult; // not Gear, read failed, no evidence, or ambiguous (>1 slot) — untouched
    return baseResult with { SubCategory = EquipmentSlotMapper.FolderName(equipmentSlots.Single()) };
}
```

`Plugin.RunScan()` calls `Classify` exactly as it does today, and only when the result is
`Category: Gear` does it call `ModEquipmentFileReader.ReadEquipmentSlots(mod.ModPath)` and pass the
result through `EnrichGearSubCategory`:

```csharp
var classification = ModTypeClassifier.Classify(mod.Name, changedItemKeys, npcNameMatcher);
if (classification.Category == ModCategory.Gear)
{
    var equipmentSlots = ModEquipmentFileReader.ReadEquipmentSlots(mod.ModPath);
    classification = ModTypeClassifier.EnrichGearSubCategory(classification, equipmentSlots);
}
```

Disk I/O now only ever happens for mods already confirmed Gear by the existing, unchanged rule —
exactly what this design always intended, and provably so from the call site itself.

### 4. `ModTypeFolders.GetFolder` — third validated case

Extends the switch expression (already generalized for `NPC` subcategories) with `Gear`:

```csharp
(ModCategory.Gear, "Head" or "Top" or "Hands" or "Legs" or "Feet" or "Ears" or "Neck" or "Wrists" or "Rings")
    => $"{ModCategory.Gear}/{subCategory}",
```

## Data flow

```
RunScan() (unchanged trigger, still fully synchronous):
  per mod: Classify(modName, changedItemKeys, npcNameMatcher) -- exactly as today, no new parameter
  -> Category == Gear?
       ModEquipmentFileReader.ReadEquipmentSlots(mod.ModPath) -- disk I/O only happens here,
       only for mods already confirmed Gear by the unchanged existing rule
       -> EnrichGearSubCategory: exactly one distinct slot, read fully succeeded? assign it
                                 anything else (null read, 0 or >1 slots)? leave SubCategory null
  -> every other category (Face, Hair, NPC, VFX, Mount, Minion, ...) never touches disk at all
```

## Error handling

| Situation | Behavior |
|---|---|
| Mod's directory doesn't exist | Treated as "no evidence" (empty set, not a failure) — falls back to plain `Gear`, `SubCategory: null`. |
| `default_mod.json`/every `group_*.json` missing | Same as above — empty set, plain `Gear`. |
| Directory enumeration fails (permission error, etc.), or any individual file fails to open/read/parse | The **entire mod's read** is untrustworthy — returns `null`, treated exactly like "no subcategory." Never reports a confident slot built from only the files that happened to succeed. |
| Files resolve to more than one distinct slot | `SubCategory: null` — an explicit, deliberate "don't guess" outcome for multi-slot outfit/armor sets, not an error. Confirmed against a real 20-group outfit mod (`C:\Mods`) that genuinely spans Top + Legs + more — this is the desired outcome, not a bug. |
| A `Files` path suffix isn't recognized, or a `Manipulations` entry's `Type` isn't `Eqp`/`Eqdp`/`Imc`, or its slot field value isn't recognized | Ignored, doesn't contribute a slot — doesn't block other paths/entries in the same mod from resolving one, and does not itself count as a read failure. |

## Testing

- **`EquipmentSlotMapper`** — pure unit tests, no I/O: every recognized suffix/manipulation-slot
  value maps to the correct `EquipmentSlot`; unrecognized inputs return `null`; `RFinger`/`LFinger`
  and `ril`/`rir` both resolve to `Rings`. `ExtractSlotFromFileName` specifically covers the real
  filename shapes confirmed against `C:\Mods`: a bare model (`c0101e6116_sho.mdl`), a texture with
  one trailing token (`v01_c0101e6116_sho_m.tex`), a material with one trailing token
  (`mt_c0101e6116_sho_a.mtrl`), and a texture with two trailing tokens
  (`c0101e6116_sho_b_d.tex`) — all four must resolve to `Feet`.
- **`ModEquipmentFileReader`** — tests against real-shaped fixture JSON written to real temp
  directories — no mocked filesystem. Covers: single-slot resolution from `Files`, single-slot
  resolution from a real-shaped `Manipulations` entry (`{"Type":"Eqp","Manipulation":{"Slot":"Body"}}`
  → `Top`, and `{"Type":"Imc","Manipulation":{"EquipSlot":"Feet"}}` → `Feet` — both real shapes
  confirmed against `C:\Mods`), an `Est`-type manipulation with a `Slot` of `Hair` contributing
  nothing (proving the `Type` filter, not vocabulary non-overlap, is what excludes it), multi-slot
  mod resolving multiple distinct entries, missing directory (empty set, not null), missing
  `default_mod.json`/no `group_*.json` files (empty set), a malformed `group_*.json` among
  otherwise-valid files (**must return `null`, not the partial result from the valid ones** — this
  is the fail-closed fix, needs its own explicit test), a non-equipment path (e.g. `chara/human/...`)
  contributing nothing, a manipulation missing `Type` or `Manipulation` entirely (ignored, not a
  crash), nested `Options`/`Containers` two levels deep (proving the traversal is genuinely
  recursive, not just one level).
- **`ModTypeClassifier.EnrichGearSubCategory`** — a Gear result with exactly one resolved slot gets
  that `SubCategory`; a Gear result with a `null` slot-read result, an empty set, or more than one
  slot stays `SubCategory: null`; a non-Gear result is returned completely unchanged regardless of
  what `equipmentSlots` contains (proving the gating is real, not just conventionally followed).
  `Classify` itself needs no new tests beyond what already exists — its signature didn't change.
- **`Plugin.RunScan()` wiring** — verify (by construction/code inspection, since `RunScan()` isn't
  unit-testable in isolation per this codebase's existing convention) that `ReadEquipmentSlots` is
  only ever called when `Classify`'s result is already `Category: Gear`.
- **`ModTypeFolders.GetFolder`** — the 9 new `(Gear, slotName)` pairs each produce `Gear/{slotName}`;
  confirms the existing "unsupported pairing throws" behavior still holds for anything not in this
  set or the two prior validated categories (`Animation`/`VFX`, `NPC`).
- **`EquipmentSlotMapper.ExtractRawSuffixToken`** (standalone app, `PenumbraOrganizer.Tests/Classification/EquipmentSlotMapperTests.cs`)
  — pure unit tests, no I/O: same real filename shapes as `ExtractSlotFromFileName`'s tests above,
  but asserting the raw lowercase string return (`"sho"`, `"top"`), not the enum — this is the exact
  contract `ModPathClassifier`'s own tests depend on.
- **Standalone app regression** — after `ModPathClassifier.BuildCanonicalTarget` is updated to call
  `EquipmentSlotMapper.ExtractRawSuffixToken` in place of its deleted private `ExtractFileSuffix`,
  the standalone app's full existing test suite (`PenumbraOrganizer.Tests`) must still pass
  unchanged — specifically `ModPathClassifierTests.Resolve_GearTarget_CarriesRawSlotSuffixAsSubcategory`
  (expects `"sho"`) and `PenumbraScanServiceTests`' two Gear-mod fixtures (expect `"top"` and `"sho"`)
  — confirming the extraction didn't alter the standalone app's observable behavior.

## Open risks (carried forward, not blocking)

- Penumbra's `group_*.json`/`default_mod.json` format could change between Penumbra versions. No
  formal schema-fingerprinting is planned (that's more machinery than this codebase uses anywhere
  else — `NpcNameListCodec`'s plain `Version` integer check is the closest precedent, and this
  design doesn't even have a version field to check since it doesn't own the file format). The
  fail-closed behavior (any read anomaly → no subcategory, never a wrong confident one) is the
  actual mitigation — a schema change that breaks parsing degrades safely; a schema change that
  *partially* parses in a misleading way is the harder case fail-closed doesn't fully solve, but no
  designed system in this codebase solves that either.
- Reading a small number of JSON files per Gear mod during every scan adds real file I/O to
  `RunScan()` — now scoped to only Gear mods (fixed from an earlier draft that read for every mod
  regardless of category), which meaningfully reduces the actual volume. No performance test is
  planned, consistent with how every other synchronous, locally-verified feature in this plugin
  ships (Folder Cleanup, NPC classification, the workbook feature) — if a real slowdown surfaces
  during in-game verification against a large library, that's the trigger to revisit, not a
  speculative reason to add async/caching machinery now.
- `EquipmentSlotMapper`'s suffix table is the complete real FFXIV equipment/accessory slot
  vocabulary as it exists today; a future game expansion adding a genuinely new equipment slot type
  would need a table update, same as any other hardcoded game-data table in this codebase.

## Validation methodology

Every "confirmed against `C:\Mods`" claim in this spec comes from a purpose-built, read-only
PowerShell script (`gear-slot-validation.ps1`) that walks a real mod library and reports: parse
failures, how often the old vs. new suffix extraction disagree, whether `FileSwaps`/`Manipulations`
are ever populated (and captures real examples of their shape), any equipment/accessory suffix not
in the known table, whether `Options`/`Containers` ever nest beyond one level, and the real
single-slot/multi-slot/zero-evidence distribution under the corrected design — not just assumptions
read off one or two sample mods.

**Run 1 — `C:\Mods`, 247 mods (~1,150 config files):** 0 parse failures, 0 unrecognized suffixes, 0
nesting beyond one level, 0 non-empty `FileSwaps`, 200+ suffix-extraction disagreements between old
and new logic, 1,394 real slot signals recovered from `Manipulations`, ~79 single-slot / ~114
multi-slot / ~54 zero-evidence mods.

**Run 2 — a second, independent ~2,035-mod library (9,541 config files, 78.6 MB, scanned in 93
seconds):** confirms every finding from Run 1 holds at roughly 8x the scale, across a different,
independently-curated mod collection (different authors, different Penumbra/tool versions):
- 0 unrecognized equipment/accessory suffixes across ~53,000 real paths — the slot vocabulary is
  confirmed complete, not an artifact of one library's mod selection.
- 0 `Options`/`Containers` nesting beyond one level, across 9,541 files — the single-level walk
  (made genuinely recursive as a safety net, never actually needed) is confirmed sufficient.
- 200+ (capped) old-vs-new suffix disagreements, spanning many different mod authors' own naming
  conventions — confirms the fix isn't specific to one author's file-naming habits.
- 10,550 real slot signals recovered from the corrected `Manipulations` extraction — proportionally
  consistent with Run 1, across every `Type` this design recognizes (`Eqp`, `Eqdp`, `Imc`) plus
  confirmed real `Est` examples (customization slots like `Hair`/`Face`) correctly excluded by the
  `Type` filter, not by coincidental vocabulary non-overlap.
- **`FileSwaps` is rare but not exactly zero at this scale** — 6 non-empty instances out of 2,035
  mods (~0.3%). Corrected the claim above from "never populated" to "rare, deliberately still out of
  scope" — see the callout in Architecture §1.
- **1 JSON parse failure** (`Miyabi Voice for Samurai`'s `default_mod.json`): PowerShell's
  `ConvertFrom-Json` rejected it over two `Files` keys differing only in casing
  (`sound/MiyabiOver.scd` vs `sound/miyabiover.scd`) — a real quirk in that specific mod's file, but
  a PowerShell-specific artifact, not evidence of a `System.Text.Json.JsonDocument` failure: `Files`
  keys are game *paths*, not the equipment paths this design filters for anyway (a `sound/` path
  isn't `chara/equipment/`/`chara/accessory/`), and `JsonDocument`/`JsonElement.EnumerateObject()`
  don't unify object keys case-insensitively the way PowerShell's object-graph deserialization does
  — this file would very likely parse cleanly under the actual C# implementation. Confirms
  `JsonDocument` (raw token access, no case-insensitive property binding) was the right choice over
  deserializing into a typed/dynamic object graph, independent of this one example.
- The "zero-slot-evidence" mod count (446/2,035, ~22%) is **not** directly "how many real Gear mods
  stay unclassified" — the validation script scans every mod indiscriminately, including ones that
  aren't Gear at all in the real pipeline (Hair, Face, poses, VFX, ...) and would never reach
  `EnrichGearSubCategory` to begin with. This number overstates the real-world gap; the actual rate
  is only knowable by running the real classifier's Gear rule first, which is exactly what
  `EnrichGearSubCategory`'s gating already does in production and this validation script doesn't
  replicate (deliberately — the script's job was checking the file-reading assumptions, not
  reimplementing the whole classifier).

Both runs' scripts and full reports are preserved outside this repo (not committed — they're
one-off validation artifacts, not project source).
