# NPC/Enemy/Boss Name-Based Classification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Flag mods whose display name matches a known NPC/enemy/boss name for manual review, since no structural `GetChangedItems` signal exists for single-named-NPC face/skin mods, using a curated + wiki-refreshable name list.

**Architecture:** A new `NpcNameMatcher` does whole-word, normalized, combined-regex-per-category matching and is threaded explicitly into `ModTypeClassifier.Classify`, where it now outranks every existing structural rule. The list persists as a versioned JSON document (`NpcNameListDocument`/`NpcNameListCodec`, mirroring the existing `OrganizationJson`/`OrganizationJsonCodec` pattern) at `PluginInterface.ConfigDirectory`, seeded from an embedded resource on first run. A new, deliberately asynchronous refresh path (`NpcWikiScraper` + `NpcNameRefreshService`) fetches and paginates `consolegameswiki.com`'s three category pages via AngleSharp and additively merges results, using the plugin's existing atomic temp-file-then-`File.Move` pattern.

**Tech Stack:** C#/.NET (net10.0-windows7.0), Dalamud.NET.Sdk 15.0.0, xUnit, AngleSharp (new dependency, HTML parsing only, no network loader), `System.Net.Http.HttpClient`, `System.Text.Json`.

**Depends on / must not contradict:**
`docs/superpowers/specs/2026-07-17-plugin-organizer-npc-name-classification-design.md` (design, already reviewed and revised) and `docs/HANDOFF_NPC_CLASSIFICATION.md` (research/evidence).

## Global Constraints

- Whole-word matching uses `(?<![\p{L}\p{N}])NAME(?![\p{L}\p{N}])`, never `\b`.
- Exactly one combined alternation regex per category (NPCs/Enemies/Bosses) — never one compiled `Regex` per name.
- Multi-list priority when a name matches more than one category: **NPCs > Bosses > Enemies**.
- A name match in `ModTypeClassifier.Classify` outranks *every* other rule, including Gear/Mount/Minion/NPC-suffix and the Smallclothes/Emperor's New Clothes placeholders — this is a confirmed, deliberate trade-off, not a bug.
- Name-list file: `Path.Combine(PluginInterface.ConfigDirectory.FullName, "npc-name-list.json")`, UTF-8 no BOM, `"Version": 1`, arrays `NPCs`/`Enemies`/`Bosses`/`Excluded`.
- Refresh is the only network-touching code path in the whole plugin, and is asynchronous — Dalamud's `UiBuilder.Draw` runs on the game's render thread and must never block on HTTP.
- All file writes to the name-list path go through temp-file + `File.Move(path, dest, overwrite: true)` — the same pattern already used by `ExportWorkbook` and `WriteBackup` in `Plugin.cs`.
- Refresh is additive-only; a persisted `Excluded` list is checked before merging any name and is never re-populated automatically.
- Never cite the specific NSFW mod titles from the original research corpus in code comments, test names, or docs — bare character/NPC names only (memory `npc-content-reference-preference`).

---

## Task 1: `NpcNameMatcher` — pure name matching

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/NpcNameMatcher.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/NpcNameMatcherTests.cs`

**Interfaces:**
- Produces: `enum NpcNameKind { Npc, Enemy, Boss }`; `sealed record NpcNameMatch(string Name, NpcNameKind Kind)`; `sealed class NpcNameMatcher` with constructor `NpcNameMatcher(IReadOnlyList<string> npcs, IReadOnlyList<string> enemies, IReadOnlyList<string> bosses)`, static `NpcNameMatcher.Empty`, and `NpcNameMatch? Match(string modName)`. Task 2 consumes all of these.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class NpcNameMatcherTests
{
    [Fact]
    public void Match_WholeWordCaseInsensitive_Matches()
    {
        var matcher = new NpcNameMatcher(["Y'shtola"], [], []);

        var result = matcher.Match("Rhul of Cool: A y'SHTOLA Overhaul");

        Assert.NotNull(result);
        Assert.Equal(NpcNameKind.Npc, result!.Kind);
    }

    [Fact]
    public void Match_ShortNameInsideLongerWord_DoesNotMatch()
    {
        var matcher = new NpcNameMatcher([], ["Rat"], []);

        var result = matcher.Match("Pirate Outfit");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("_Zenos_")]
    [InlineData("Zenos2")]
    [InlineData("NotZenos")]
    [InlineData("Zenos-themed")]
    public void Match_UnicodeBoundary_RejectsAdjacentLetterOrDigit(string modName)
    {
        var matcher = new NpcNameMatcher([], [], ["Zenos"]);

        Assert.Null(matcher.Match(modName));
    }

    [Fact]
    public void Match_MultiWordName_Matches()
    {
        var matcher = new NpcNameMatcher(["Feo Ul"], [], []);

        var result = matcher.Match("A Feo Ul Overhaul");

        Assert.NotNull(result);
    }

    [Fact]
    public void Match_HyphenatedName_Matches()
    {
        var matcher = new NpcNameMatcher(["Kan-E-Senna"], [], []);

        var result = matcher.Match("HD Kan-E-Senna (Gen3)");

        Assert.NotNull(result);
    }

    [Fact]
    public void Match_CurlyApostrophe_MatchesStraightApostropheListEntry()
    {
        var matcher = new NpcNameMatcher(["Y'shtola"], [], []);

        var result = matcher.Match("Y’shtola Rework");

        Assert.NotNull(result);
    }

    [Fact]
    public void Match_RegexMetacharactersInName_DoNotBreakMatching()
    {
        var matcher = new NpcNameMatcher(["Al'Ma(rri)yya"], [], []);

        var result = matcher.Match("Al'Ma(rri)yya Retexture");

        Assert.NotNull(result);
    }

    [Fact]
    public void Match_LongerOverlappingNamePreferred_ReturnsLongerMatch()
    {
        var matcher = new NpcNameMatcher([], [], ["Zenos", "Zenos yae Galvus"]);

        var result = matcher.Match("Zenos yae Galvus Portrait");

        Assert.NotNull(result);
        Assert.Equal("Zenos yae Galvus", result!.Name);
    }

    [Fact]
    public void Match_PriorityNpcsBeatsBossesBeatsEnemies()
    {
        var matcher = new NpcNameMatcher(["Titania"], [], ["Titania"]);

        var result = matcher.Match("HD Titania (Gen3)");

        Assert.Equal(NpcNameKind.Npc, result!.Kind);
    }

    [Fact]
    public void Match_BossesBeatsEnemiesWhenNoNpcMatch()
    {
        var matcher = new NpcNameMatcher([], ["Garuda"], ["Garuda"]);

        var result = matcher.Match("Garuda Statue");

        Assert.Equal(NpcNameKind.Boss, result!.Kind);
    }

    [Fact]
    public void Match_NoListsMatch_ReturnsNull()
    {
        var matcher = new NpcNameMatcher(["Y'shtola"], [], []);

        Assert.Null(matcher.Match("Ordinary Gear Mod"));
    }

    [Fact]
    public void Empty_NeverMatchesAnything()
    {
        Assert.Null(NpcNameMatcher.Empty.Match("Y'shtola Overhaul"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter NpcNameMatcherTests`
Expected: FAIL (compile error — `NpcNameMatcher` doesn't exist yet)

- [ ] **Step 3: Implement `NpcNameMatcher`**

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public enum NpcNameKind { Npc, Enemy, Boss }

public sealed record NpcNameMatch(string Name, NpcNameKind Kind);

// Matches a mod's display name against known NPC/enemy/boss names. One combined alternation
// regex per category (never one compiled Regex per name — see the design spec's performance
// section: a full wiki scrape can reach five figures of names, and constructing that many
// separate Regex objects is real overhead independent of match cost). Boundaries are defined
// explicitly as "not adjacent to a Unicode letter or digit" rather than \b, which treats
// underscore as a word character and would misclassify "_Zenos_".
public sealed class NpcNameMatcher
{
    private readonly Regex? _npcRegex;
    private readonly Regex? _enemyRegex;
    private readonly Regex? _bossRegex;

    public static readonly NpcNameMatcher Empty = new([], [], []);

    public NpcNameMatcher(IReadOnlyList<string> npcs, IReadOnlyList<string> enemies, IReadOnlyList<string> bosses)
    {
        _npcRegex = BuildRegex(npcs);
        _enemyRegex = BuildRegex(enemies);
        _bossRegex = BuildRegex(bosses);
    }

    public NpcNameMatch? Match(string modName)
    {
        var normalized = Normalize(modName);

        if (_npcRegex?.Match(normalized) is { Success: true } npcMatch)
            return new NpcNameMatch(npcMatch.Value, NpcNameKind.Npc);
        if (_bossRegex?.Match(normalized) is { Success: true } bossMatch)
            return new NpcNameMatch(bossMatch.Value, NpcNameKind.Boss);
        if (_enemyRegex?.Match(normalized) is { Success: true } enemyMatch)
            return new NpcNameMatch(enemyMatch.Value, NpcNameKind.Enemy);

        return null;
    }

    private static Regex? BuildRegex(IReadOnlyList<string> names)
    {
        var normalized = names
            .Select(Normalize)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length) // prefer a longer name over a shorter one it contains
            .Select(Regex.Escape)
            .ToList();

        if (normalized.Count == 0)
            return null;

        var pattern = $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", normalized)})(?![\p{{L}}\p{{N}}])";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    // NFC normalization + curly-to-straight apostrophe folding, so a wiki title and a mod title
    // using different apostrophe glyphs for the same name still compare equal. Character
    // normalization, not fuzzy/approximate matching.
    internal static string Normalize(string value) =>
        value.Trim().Normalize(NormalizationForm.FormC).Replace('’', '\'');
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter NpcNameMatcherTests`
Expected: PASS (12 tests)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/NpcNameMatcher.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/NpcNameMatcherTests.cs
git commit -m "feat: add NpcNameMatcher for whole-word NPC/enemy/boss name matching"
```

---

## Task 2: Wire the matcher into `ModTypeClassifier.Classify`, reorder priority, migrate existing tests

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs`

**Interfaces:**
- Consumes: `NpcNameMatcher`, `NpcNameMatcher.Empty`, `NpcNameMatch`, `NpcNameKind` (Task 1).
- Produces: `enum ClassificationSource { Structural, NameHeuristic, Unknown }`; `ClassificationResult(ModCategory? Category, string? SubCategory, ClassificationSource Source)`; `ModTypeClassifier.Classify(string modName, IEnumerable<string> changedItemKeys, NpcNameMatcher npcNameMatcher)`. Task 5 (`Plugin.cs` wiring) consumes this exact signature.

This task changes an existing public signature used by ~25 existing tests — every call site in the test file must be updated in the same commit, or the project won't compile.

- [ ] **Step 1: Update `ClassificationResult` and `Classify`'s signature/priority in `ModTypeClassifier.cs`**

Replace the top of the file (the `ClassificationResult` record) with:

```csharp
public enum ClassificationSource { Structural, NameHeuristic, Unknown }

public sealed record ClassificationResult(ModCategory? Category, string? SubCategory, ClassificationSource Source)
{
    public static readonly ClassificationResult Unknown = new(null, null, ClassificationSource.Unknown);
}
```

Replace the `Classify` method (keep `ModTypeFolders` and everything below `Classify` as-is for now — `ModTypeFolders` is touched in Task 3):

```csharp
public static ClassificationResult Classify(
    string modName, IEnumerable<string> changedItemKeys, NpcNameMatcher npcNameMatcher)
{
    // Rule -1 (NEW): a known NPC/enemy/boss name match outranks every structural rule below,
    // including Rule 0's own "always wins no matter what" — a deliberate, user-confirmed
    // trade-off (see the design spec's "Accepted trade-off" section). No structural signal
    // exists for single-named-NPC face/skin mods, so the name is the only signal available.
    if (npcNameMatcher.Match(modName) is { } nameMatch)
        return new(ModCategory.NPC, SubCategoryFor(nameMatch.Kind), ClassificationSource.NameHeuristic);

    var keys = changedItemKeys.Select(ChangedItemKeyParser.Parse).ToList();

    // Rule 0: known body-slot placeholders win unconditionally, ahead of every other rule —
    // including real Gear, Mount, Minion, NPC, and Customization. User-confirmed absolute
    // override (spec: "should always go to body no matter what... even over real gear"), not
    // a soft priority merge. Accepted trade-off: a mod combining a bare Smallclothes key with
    // an NPC-suffixed key now resolves to Body, not NPC — NPC classification is deliberately
    // out of scope here (see the spec's Non-goals).
    foreach (var key in keys)
    {
        if (key.Shape == ChangedItemKeyShape.Gear
            && KnownEquipmentPlaceholders.TryGetValue(key.ItemName!, out var placeholderCategory))
            return new(placeholderCategory, null, ClassificationSource.Structural);
    }

    // Rule 1: Gear wins unconditionally (compilation packs bundle incidental extras).
    if (keys.Any(k => k.Shape == ChangedItemKeyShape.Gear))
        return new(ModCategory.Gear, null, ClassificationSource.Structural);
    if (keys.Any(k => k.Shape == ChangedItemKeyShape.Mount))
        return new(ModCategory.Mount, null, ClassificationSource.Structural);
    if (keys.Any(k => k.Shape == ChangedItemKeyShape.Minion))
        return new(ModCategory.Minion, null, ClassificationSource.Structural);
    if (keys.Any(k => k.Shape == ChangedItemKeyShape.Npc))
        return new(ModCategory.NPC, null, ClassificationSource.Structural);

    var hasAction = keys.Any(k => k.Shape == ChangedItemKeyShape.Action);
    var hasEmote = keys.Any(k => k.Shape == ChangedItemKeyShape.Emote);
    var hasAnimation = HasLiteral(keys, "Animation");
    var hasVfx = HasLiteral(keys, "Vfx");

    if (hasAction || hasEmote || hasAnimation || hasVfx)
    {
        if (hasAction)
            return new(ModCategory.Animation, "Battle Animation", ClassificationSource.Structural);
        if (hasEmote)
            return new(ModCategory.Animation, "Emotes", ClassificationSource.Structural);
        if (hasVfx && hasAnimation)
            return new(ModCategory.Animation, "Other", ClassificationSource.Structural);
        if (hasVfx)
            return new(ModCategory.VFX, "VFX", ClassificationSource.Structural);
        return new(ModCategory.Animation, "Animation", ClassificationSource.Structural);
    }

    if (HasLiteral(keys, "Housing"))
        return new(ModCategory.Furniture, null, ClassificationSource.Structural);
    if (HasLiteral(keys, "Sound"))
        return new(ModCategory.Sound, null, ClassificationSource.Structural);

    var bodyParts = keys
        .Where(k => k.Shape == ChangedItemKeyShape.Customization && k.BodyPart is not null)
        .Select(k => k.BodyPart!)
        .ToList();
    if (bodyParts.Count > 0)
        return ClassifyCustomization(bodyParts);

    return ClassificationResult.Unknown;
}

private static string SubCategoryFor(NpcNameKind kind) => kind switch
{
    NpcNameKind.Npc => "NPCs",
    NpcNameKind.Enemy => "Enemies",
    NpcNameKind.Boss => "Bosses",
    _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
};
```

Update `ClassifyCustomization` to stamp `ClassificationSource.Structural` and `Unknown` on its own returns:

```csharp
private static ClassificationResult ClassifyCustomization(IReadOnlyList<string> bodyParts)
{
    var mapped = bodyParts
        .Select(MapBodyPart)
        .Where(c => c is not null)
        .Select(c => c!.Value)
        .ToHashSet();

    if (mapped.Contains(ModCategory.Face))
        return new(ModCategory.Face, null, ClassificationSource.Structural);
    if (mapped.Contains(ModCategory.Hair))
        return new(ModCategory.Hair, null, ClassificationSource.Structural);
    if (mapped.Contains(ModCategory.Body))
        return new(ModCategory.Body, null, ClassificationSource.Structural);
    if (mapped.Contains(ModCategory.Skin))
        return new(ModCategory.Skin, null, ClassificationSource.Structural);

    return ClassificationResult.Unknown;
}
```

`MapBodyPart`, `HasLiteral`, `KnownEquipmentPlaceholders`, and the `using` directive stay unchanged. Add `using PenumbraOrganizer.Plugin.Organizer.Classification;` — no, this file already lives in that namespace; no new `using` is required since `NpcNameMatcher`/`NpcNameMatch`/`NpcNameKind` are in the same namespace as `ModTypeClassifier`.

- [ ] **Step 2: Replace `ModTypeClassifierTests.cs`'s `Classify` call sites**

Every existing test calls `ModTypeClassifier.Classify(keys)`. Replace the whole file's content with the migrated version below — each call becomes `ModTypeClassifier.Classify(modName, keys, NpcNameMatcher.Empty)`, using the mod name from that test's own comment where one exists (an ordinary, non-NSFW name), or `"Test Mod"` where the original comment didn't name one. `GetFolder` tests are left as-is here; Task 3 extends them.

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class ModTypeClassifierTests
{
    [Fact] // "Carlotta's Outfit": 30 gear items + one incidental mount — Gear wins
    public void Classify_GearBeatsIncidentalMount()
    {
        var result = ModTypeClassifier.Classify(
            "Carlotta's Outfit",
            ["Appointed Gloves", "Archon Throne (Mount)", "Animation"],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Gear, result.Category);
        Assert.Null(result.SubCategory);
    }

    [Fact] // "Kigu - Face 001": customization keys + one bare item — Gear wins
    public void Classify_GearBeatsCustomization()
    {
        var result = ModTypeClassifier.Classify(
            "Kigu - Face 001",
            ["Customization: Lalafell Female Face 1", "Moogle Legs"],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Gear, result.Category);
    }

    [Fact] // Bibo+-style body mesh mod: bare Smallclothes item — Body, not Gear
    public void Classify_SmallclothesAlone_IsBody()
    {
        var result = ModTypeClassifier.Classify("Bibo+ Body Mesh", ["Smallclothes"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
        Assert.Null(result.SubCategory);
    }

    [Theory] // Emperor's New Clothes body-slot pieces — each alone is Body
    [InlineData("The Emperor's New Hat")]
    [InlineData("The Emperor's New Robe")]
    [InlineData("The Emperor's New Gloves")]
    [InlineData("The Emperor's New Breeches")]
    [InlineData("The Emperor's New Boots")]
    public void Classify_EmperorsNewClothesBodySlotAlone_IsBody(string itemName)
    {
        var result = ModTypeClassifier.Classify("Test Mod", [itemName], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + real named Gear together — Body still wins (absolute override)
    public void Classify_SmallclothesBeatsRealGear()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Smallclothes", "Appointed Gloves"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + Face customization — Body still wins, not just a soft merge
    public void Classify_SmallclothesBeatsFaceCustomization()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Smallclothes", "Customization: Miqo'te Female Face 101"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + a Mount key — Body still wins
    public void Classify_SmallclothesBeatsMount()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Smallclothes", "Archon Throne (Mount)"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + an NPC-suffixed key — Body wins; accepted trade-off, NPC is deferred
    public void Classify_SmallclothesBeatsNpcSuffix()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Smallclothes", "Smallclothes (NPC, 9903-1, Legs)"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Excluded ENC accessory literal, deliberately not in the table — stays ordinary Gear
    public void Classify_EmperorsNewClothesAccessory_IsStillGear()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["The Emperor's New Earrings"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Gear, result.Category);
    }

    [Fact] // "Yacht_V1.0": Animation + Sound + mount key, no gear — Mount
    public void Classify_PureMountMod_IsMount()
    {
        var result = ModTypeClassifier.Classify(
            "Yacht_V1.0", ["Ancient Airship (Mount)", "Animation", "Sound"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Mount, result.Category);
    }

    [Fact] // "Red-Footed Booby": Battle NPC + Companion pair — Minion
    public void Classify_MinionSuffixes_AreMinion()
    {
        var result = ModTypeClassifier.Classify(
            "Red-Footed Booby",
            ["Blue-footed Booby (Battle NPC)", "Blue-footed Booby (Companion)"],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Minion, result.Category);
    }

    [Fact] // Mount beats Minion when both present and no gear
    public void Classify_MountBeatsMinion()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Spectral Statice (Mount)", "Ghido (Companion)"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Mount, result.Category);
    }

    [Fact] // NPC-only mod (hypothetical isolation of the Smallclothes shape)
    public void Classify_NpcSuffix_IsNpc()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Smallclothes (NPC, 9903-1, Body)"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.NPC, result.Category);
        Assert.Equal(ClassificationSource.Structural, result.Source);
    }

    [Fact] // "[Bard Lb3] Pashupata": Action + Animation + Vfx — Battle Animation
    public void Classify_ActionKey_IsBattleAnimation()
    {
        var result = ModTypeClassifier.Classify(
            "[Bard Lb3] Pashupata", ["Action: Arrow of Fortitude", "Animation", "Vfx"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Battle Animation", result.SubCategory);
    }

    [Fact] // "Toothless Dance": Emote + Sound — Emotes
    public void Classify_EmoteKey_IsEmotes()
    {
        var result = ModTypeClassifier.Classify(
            "Toothless Dance", ["Emote: Bee's Knees", "Sound"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Emotes", result.SubCategory);
    }

    [Fact] // Vfx + Animation, no Action/Emote — ambiguous, Other
    public void Classify_VfxAndAnimationTogether_IsOther()
    {
        var result = ModTypeClassifier.Classify("Test Mod", ["Animation", "Vfx"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Other", result.SubCategory);
    }

    [Fact] // solo Vfx — VFX
    public void Classify_VfxAlone_IsVfx()
    {
        var result = ModTypeClassifier.Classify("Test Mod", ["Vfx"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.VFX, result.Category);
        Assert.Equal("VFX", result.SubCategory);
    }

    [Fact] // "[NX] Thicc Viera Walkin For All F": bare Animation only
    public void Classify_AnimationAlone_IsAnimation()
    {
        var result = ModTypeClassifier.Classify(
            "[NX] Thicc Viera Walkin For All F", ["Animation"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Animation", result.SubCategory);
    }

    [Fact] // "cleaned up phasmascapes": single Housing literal — Furniture
    public void Classify_Housing_IsFurniture()
    {
        var result = ModTypeClassifier.Classify("cleaned up phasmascapes", ["Housing"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Furniture, result.Category);
    }

    [Fact] // Sound alone — Sound
    public void Classify_SoundAlone_IsSound()
    {
        var result = ModTypeClassifier.Classify("Test Mod", ["Sound"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Sound, result.Category);
    }

    [Fact] // "Akako's Files 3.1.1": Face+Hair+Skin+Tail body parts — Face wins
    public void Classify_CustomizationFaceBeatsHairBodySkin()
    {
        var result = ModTypeClassifier.Classify(
            "Akako's Files 3.1.1",
            [
                "Customization: Miqo'te Female Face 101",
                "Customization: Miqo'te Female Hair 115",
                "Customization: Miqo'te Female Skin Textures",
                "Customization: Miqo'te Female Tail 3",
            ],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Face, result.Category);
    }

    [Fact] // "tail": Tail + Skin Textures — Body wins over Skin
    public void Classify_CustomizationTailBeatsSkin()
    {
        var result = ModTypeClassifier.Classify(
            "tail",
            [
                "Customization: Miqo'te Female Skin Textures",
                "Customization: Miqo'te Female Tail 3",
            ],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // "akako skin": skin textures only — Skin
    public void Classify_CustomizationSkinOnly_IsSkin()
    {
        var result = ModTypeClassifier.Classify(
            "akako skin",
            [
                "Customization: Midlander Female Skin Textures",
                "Customization: Player Skin Textures",
            ],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Skin, result.Category);
    }

    [Fact] // "Akako's Glowy Eyes": Face + literal Unknown — Unknown key doesn't block Face
    public void Classify_CustomizationUnknownKeyDoesNotBlockOthers()
    {
        var result = ModTypeClassifier.Classify(
            "Akako's Glowy Eyes",
            [
                "Customization: Miqo'te Female Face (Iris) 101",
                "Customization: Unknown",
            ],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Face, result.Category);
    }

    [Fact] // only unrecognizable customization — Unknown
    public void Classify_OnlyUnknownCustomization_IsUnknown()
    {
        var result = ModTypeClassifier.Classify("Test Mod", ["Customization: Unknown"], NpcNameMatcher.Empty);

        Assert.Equal(ClassificationResult.Unknown, result);
    }

    [Fact] // "higanbana [bibo]": empty key set — Unknown
    public void Classify_EmptyKeys_IsUnknown()
    {
        var result = ModTypeClassifier.Classify("higanbana [bibo]", [], NpcNameMatcher.Empty);

        Assert.Equal(ClassificationResult.Unknown, result);
    }

    [Fact] // Icon: alone (never observed with no companion key) — Unknown, never a guess
    public void Classify_IconAlone_IsUnknown()
    {
        var result = ModTypeClassifier.Classify("Test Mod", ["Icon: 42992"], NpcNameMatcher.Empty);

        Assert.Equal(ClassificationResult.Unknown, result);
    }

    // --- New: name-heuristic behavior ---

    [Fact] // Confirmed real case: a Y'shtola-named mod with only generic customization keys —
           // structurally this would be Face, the name heuristic must override that to NPC.
    public void Classify_NameMatchOverridesCustomizationFace()
    {
        var matcher = new NpcNameMatcher(["Y'shtola"], [], []);

        var result = ModTypeClassifier.Classify(
            "Rhul of Cool: A Y'shtola Overhaul",
            ["Customization: Miqo'te Female Face 201", "Customization: Miqo'te Female Skin Textures"],
            matcher);

        Assert.Equal(ModCategory.NPC, result.Category);
        Assert.Equal("NPCs", result.SubCategory);
        Assert.Equal(ClassificationSource.NameHeuristic, result.Source);
    }

    [Fact] // Confirmed accepted trade-off: name match overrides even a real, structurally-correct
           // Gear classification (e.g. a shared-coat mod named after an NPC).
    public void Classify_NameMatchOverridesGear()
    {
        var matcher = new NpcNameMatcher(["Alphinaud"], [], []);

        var result = ModTypeClassifier.Classify(
            "Slightly Better Alphinaud", ["Didact's Coat (696-1)"], matcher);

        Assert.Equal(ModCategory.NPC, result.Category);
        Assert.Equal(ClassificationSource.NameHeuristic, result.Source);
    }

    [Fact] // Name match overrides the Smallclothes placeholder too — outranks even Rule 0.
    public void Classify_NameMatchOverridesSmallclothesPlaceholder()
    {
        var matcher = new NpcNameMatcher([], [], ["Titania"]);

        var result = ModTypeClassifier.Classify("Titania Smallclothes Replacer", ["Smallclothes"], matcher);

        Assert.Equal(ModCategory.NPC, result.Category);
        Assert.Equal("Bosses", result.SubCategory);
    }

    [Fact] // Name match overrides a structural NPC-suffix result too (still NPC, but the
           // subcategory and Source now reflect the heuristic, not the structural signal).
    public void Classify_NameMatchOverridesStructuralNpcSuffix()
    {
        var matcher = new NpcNameMatcher(["Zenos"], [], []);

        var result = ModTypeClassifier.Classify(
            "Zenos Custom NPC Body", ["Smallclothes (NPC, 9903-1, Body)"], matcher);

        Assert.Equal(ModCategory.NPC, result.Category);
        Assert.Equal("NPCs", result.SubCategory);
        Assert.Equal(ClassificationSource.NameHeuristic, result.Source);
    }

    [Fact] // A mod with zero GetChangedItems entries must still run the name check.
    public void Classify_EmptyChangedItemsStillChecksName()
    {
        var matcher = new NpcNameMatcher(["Y'shtola"], [], []);

        var result = ModTypeClassifier.Classify("Y'shtola Portrait", [], matcher);

        Assert.Equal(ModCategory.NPC, result.Category);
    }

    [Fact] // A non-matching mod's classification is completely unaffected by a non-empty matcher.
    public void Classify_NoNameMatch_StructuralClassificationUnaffected()
    {
        var matcher = new NpcNameMatcher(["Y'shtola", "Thancred"], ["Titania"], ["Zenos"]);

        var result = ModTypeClassifier.Classify("Carlotta's Outfit", ["Appointed Gloves"], matcher);

        Assert.Equal(ModCategory.Gear, result.Category);
        Assert.Equal(ClassificationSource.Structural, result.Source);
    }

    [Theory]
    [InlineData(ModCategory.Gear, null, "Gear")]
    [InlineData(ModCategory.NPC, null, "NPC")]
    [InlineData(ModCategory.Animation, "Battle Animation", "Animation and VFX/Battle Animation")]
    [InlineData(ModCategory.VFX, "VFX", "Animation and VFX/VFX")]
    public void GetFolder_MapsCategoryAndSubCategory(ModCategory category, string? sub, string expected)
    {
        Assert.Equal(expected, ModTypeFolders.GetFolder(category, sub));
    }
}
```

- [ ] **Step 3: Run all classifier tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter ModTypeClassifierTests`
Expected: PASS (32 tests: 25 migrated + 7 new)

- [ ] **Step 4: Run the full test suite to check for other broken callers**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS. (`Plugin.cs:101` is the only production call site and isn't touched until Task 8 — it still compiles against the old two-argument signature failing, so also grep for it now.)

Run: `grep -rn "ModTypeClassifier.Classify(" PenumbraOrganizer.Plugin/`
Expected: one match, `Plugin.cs`, still on the old signature — this will not compile until Task 8. If the build is run as part of `dotnet test` above and fails only on `Plugin.cs`, that failure is expected and resolved in Task 8, not here. Confirm the *test project* itself builds and its tests pass by running with `--no-restore` scoped to the test filter above if the full-solution build fails on `Plugin.cs`.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs
git commit -m "feat: wire NpcNameMatcher into ModTypeClassifier, name match outranks all structural rules"
```

---

## Task 3: `ModTypeFolders.GetFolder` generalization

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs`

**Interfaces:**
- Produces: `ModTypeFolders.GetFolder(ModCategory category, string? subCategory)` now handles `ModCategory.NPC` + `"NPCs"/"Enemies"/"Bosses"` and throws on any other unsupported subcategory pairing. Task 5 (`Plugin.cs`'s `RunScan`, via `OrganizerState`'s existing folder-building code) relies on this not throwing for any `ClassificationResult` the classifier can actually produce.

- [ ] **Step 1: Add the failing tests**

Add these cases to the existing `GetFolder_MapsCategoryAndSubCategory` theory in `ModTypeClassifierTests.cs` and a new fact below it:

```csharp
    [Theory]
    [InlineData(ModCategory.Gear, null, "Gear")]
    [InlineData(ModCategory.NPC, null, "NPC")]
    [InlineData(ModCategory.Animation, "Battle Animation", "Animation and VFX/Battle Animation")]
    [InlineData(ModCategory.VFX, "VFX", "Animation and VFX/VFX")]
    [InlineData(ModCategory.NPC, "NPCs", "NPC/NPCs")]
    [InlineData(ModCategory.NPC, "Enemies", "NPC/Enemies")]
    [InlineData(ModCategory.NPC, "Bosses", "NPC/Bosses")]
    public void GetFolder_MapsCategoryAndSubCategory(ModCategory category, string? sub, string expected)
    {
        Assert.Equal(expected, ModTypeFolders.GetFolder(category, sub));
    }

    [Fact]
    public void GetFolder_UnsupportedSubCategoryPairing_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModTypeFolders.GetFolder(ModCategory.Gear, "Bosses"));
    }
```

(Replace the existing `GetFolder_MapsCategoryAndSubCategory` theory added in Task 2 with this expanded version — same method name, more `InlineData` rows.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter GetFolder`
Expected: FAIL — the 3 new `InlineData` rows produce `"Animation and VFX/NPCs"` etc. instead of `"NPC/NPCs"`, and the throw test fails because nothing throws today.

- [ ] **Step 3: Generalize `GetFolder`**

In `ModTypeClassifier.cs`, replace:

```csharp
public static class ModTypeFolders
{
    private const string AnimationVfxParent = "Animation and VFX";

    public static string GetFolder(ModCategory category, string? subCategory) =>
        subCategory is null ? category.ToString() : $"{AnimationVfxParent}/{subCategory}";
}
```

with:

```csharp
public static class ModTypeFolders
{
    private const string AnimationVfxParent = "Animation and VFX";

    // Valid (category, subCategory) pairings are enumerated explicitly rather than falling
    // through to an open-ended $"{category}/{subCategory}" — that would silently accept a
    // nonsense combination a classifier bug could produce (e.g. Gear + "Bosses") instead of
    // failing fast during development.
    public static string GetFolder(ModCategory category, string? subCategory) => (category, subCategory) switch
    {
        (_, null) => category.ToString(),
        (ModCategory.Animation or ModCategory.VFX, _) => $"{AnimationVfxParent}/{subCategory}",
        (ModCategory.NPC, "NPCs" or "Enemies" or "Bosses") => $"{ModCategory.NPC}/{subCategory}",
        _ => throw new ArgumentOutOfRangeException(
            nameof(subCategory), subCategory, $"Unsupported subcategory '{subCategory}' for {category}."),
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter GetFolder`
Expected: PASS (8 cases: 4 original + 3 new NPC rows + 1 throw test)

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs
git commit -m "feat: generalize ModTypeFolders.GetFolder for validated NPC subcategories"
```

---

## Task 4: `NpcNameListDocument` + `NpcNameListCodec`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcNameListDocument.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcNameListCodec.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcNameListCodecTests.cs`

**Interfaces:**
- Produces: `sealed class NpcNameListDocument { int Version; List<string> NPCs; List<string> Enemies; List<string> Bosses; List<string> Excluded; }`; `enum NpcNameListParseStatus { Ok, MalformedJson, UnsupportedVersion }`; `sealed record NpcNameListParseResult(NpcNameListDocument? Data, NpcNameListParseStatus Status)`; `static class NpcNameListCodec` with `Parse(string json)`, `Serialize(NpcNameListDocument data)`, `MergeAdditive(NpcNameListDocument existing, IReadOnlyList<string> newNpcs, IReadOnlyList<string> newEnemies, IReadOnlyList<string> newBosses)`. Tasks 5 and 7 consume all of these.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NpcNames;

public class NpcNameListCodecTests
{
    private const string ValidJson = """
        {"Version":1,"NPCs":["Y'shtola","Thancred"],"Enemies":["Titania"],"Bosses":["Zenos"],"Excluded":[]}
        """;

    [Fact]
    public void Parse_ValidDocument_ReturnsOk()
    {
        var result = NpcNameListCodec.Parse(ValidJson);

        Assert.Equal(NpcNameListParseStatus.Ok, result.Status);
        Assert.NotNull(result.Data);
        Assert.Contains("Y'shtola", result.Data!.NPCs);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsMalformedJson()
    {
        var result = NpcNameListCodec.Parse("{ not json");

        Assert.Equal(NpcNameListParseStatus.MalformedJson, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Parse_UnsupportedVersion_ReturnsUnsupportedVersion()
    {
        var result = NpcNameListCodec.Parse("""{"Version":99,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":[]}""");

        Assert.Equal(NpcNameListParseStatus.UnsupportedVersion, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Parse_TrimsBlankAndOverLongEntries()
    {
        var overLong = new string('a', 200);
        var json = $$"""{"Version":1,"NPCs":["  Y'shtola  ","","{{overLong}}"],"Enemies":[],"Bosses":[],"Excluded":[]}""";

        var result = NpcNameListCodec.Parse(json);

        Assert.Equal(["Y'shtola"], result.Data!.NPCs);
    }

    [Fact]
    public void Parse_DeduplicatesCaseInsensitivelyWithinAnArray()
    {
        var json = """{"Version":1,"NPCs":["Zenos","ZENOS","zenos"],"Enemies":[],"Bosses":[],"Excluded":[]}""";

        var result = NpcNameListCodec.Parse(json);

        Assert.Single(result.Data!.NPCs);
    }

    [Fact]
    public void Parse_AllowsSameNameAcrossDifferentArrays()
    {
        var json = """{"Version":1,"NPCs":[],"Enemies":["Titania"],"Bosses":["Titania"],"Excluded":[]}""";

        var result = NpcNameListCodec.Parse(json);

        Assert.Contains("Titania", result.Data!.Enemies);
        Assert.Contains("Titania", result.Data!.Bosses);
    }

    [Fact]
    public void Serialize_IsDeterministic_RepeatedCallsProduceIdenticalOutput()
    {
        var doc = NpcNameListCodec.Parse(ValidJson).Data!;

        var first = NpcNameListCodec.Serialize(doc);
        var second = NpcNameListCodec.Serialize(doc);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Serialize_SortsEntriesDeterministically()
    {
        var doc = NpcNameListCodec.Parse(
            """{"Version":1,"NPCs":["Thancred","Alphinaud","Y'shtola"],"Enemies":[],"Bosses":[],"Excluded":[]}""").Data!;

        var serialized = NpcNameListCodec.Serialize(doc);
        var reparsed = NpcNameListCodec.Parse(serialized).Data!;

        Assert.Equal(["Alphinaud", "Thancred", "Y'shtola"], reparsed.NPCs);
    }

    [Fact]
    public void MergeAdditive_UnionsNewNamesIntoEachCategory()
    {
        var existing = NpcNameListCodec.Parse(ValidJson).Data!;

        var merged = NpcNameListCodec.MergeAdditive(
            existing, newNpcs: ["Alphinaud"], newEnemies: ["Garuda"], newBosses: []);

        Assert.Contains("Alphinaud", merged.NPCs);
        Assert.Contains("Y'shtola", merged.NPCs); // nothing already present is removed
        Assert.Contains("Garuda", merged.Enemies);
    }

    [Fact]
    public void MergeAdditive_NeverRemovesExistingNames()
    {
        var existing = NpcNameListCodec.Parse(ValidJson).Data!;

        var merged = NpcNameListCodec.MergeAdditive(existing, newNpcs: [], newEnemies: [], newBosses: []);

        Assert.Contains("Y'shtola", merged.NPCs);
        Assert.Contains("Thancred", merged.NPCs);
        Assert.Contains("Titania", merged.Enemies);
        Assert.Contains("Zenos", merged.Bosses);
    }

    [Fact]
    public void MergeAdditive_PreservesExcludedList()
    {
        var existing = NpcNameListCodec.Parse(
            """{"Version":1,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":["Bad Entry"]}""").Data!;

        var merged = NpcNameListCodec.MergeAdditive(existing, newNpcs: [], newEnemies: [], newBosses: []);

        Assert.Contains("Bad Entry", merged.Excluded);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter NpcNameListCodecTests`
Expected: FAIL (compile error — types don't exist yet)

- [ ] **Step 3: Implement `NpcNameListDocument`**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

public sealed class NpcNameListDocument
{
    public int Version { get; set; } = NpcNameListCodec.CurrentVersion;
    public List<string> NPCs { get; set; } = [];
    public List<string> Enemies { get; set; } = [];
    public List<string> Bosses { get; set; } = [];
    public List<string> Excluded { get; set; } = [];
}
```

- [ ] **Step 4: Implement `NpcNameListCodec`**

```csharp
using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

public enum NpcNameListParseStatus { Ok, MalformedJson, UnsupportedVersion }

public sealed record NpcNameListParseResult(NpcNameListDocument? Data, NpcNameListParseStatus Status);

public static class NpcNameListCodec
{
    public const int CurrentVersion = 1;
    private const int MaxNameLength = 128;

    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    // Never throws. Data is non-null exactly when Status == Ok. MalformedJson and
    // UnsupportedVersion stay distinct so scan-time and refresh-time callers can report them
    // differently if they choose to (mirrors OrganizationJsonCodec's own convention).
    public static NpcNameListParseResult Parse(string json)
    {
        if (json is null)
            return new NpcNameListParseResult(null, NpcNameListParseStatus.MalformedJson);

        NpcNameListDocument? data;
        try
        {
            data = JsonSerializer.Deserialize<NpcNameListDocument>(json);
        }
        catch (JsonException)
        {
            return new NpcNameListParseResult(null, NpcNameListParseStatus.MalformedJson);
        }

        if (data is null)
            return new NpcNameListParseResult(null, NpcNameListParseStatus.MalformedJson);
        if (data.Version != CurrentVersion)
            return new NpcNameListParseResult(null, NpcNameListParseStatus.UnsupportedVersion);

        return new NpcNameListParseResult(Sanitize(data), NpcNameListParseStatus.Ok);
    }

    public static string Serialize(NpcNameListDocument data) =>
        JsonSerializer.Serialize(Sanitize(data), SerializeOptions);

    // Additive only: everything already in `existing` is kept verbatim; only genuinely new names
    // are unioned in. Excluded is carried through unchanged — refresh never modifies it.
    public static NpcNameListDocument MergeAdditive(
        NpcNameListDocument existing,
        IReadOnlyList<string> newNpcs,
        IReadOnlyList<string> newEnemies,
        IReadOnlyList<string> newBosses)
    {
        var excluded = new HashSet<string>(existing.Excluded, StringComparer.OrdinalIgnoreCase);

        return Sanitize(new NpcNameListDocument
        {
            Version = existing.Version,
            NPCs = [.. existing.NPCs, .. newNpcs.Where(n => !excluded.Contains(n))],
            Enemies = [.. existing.Enemies, .. newEnemies.Where(n => !excluded.Contains(n))],
            Bosses = [.. existing.Bosses, .. newBosses.Where(n => !excluded.Contains(n))],
            Excluded = existing.Excluded,
        });
    }

    // Applied on every parse, serialize, and merge so the document is always normalized before
    // use: trimmed, blank/over-length entries dropped, de-duplicated case-insensitively within
    // (not across) each array, sorted deterministically so repeated writes with no real change
    // produce byte-identical output.
    private static NpcNameListDocument Sanitize(NpcNameListDocument data) => new()
    {
        Version = CurrentVersion,
        NPCs = SanitizeList(data.NPCs),
        Enemies = SanitizeList(data.Enemies),
        Bosses = SanitizeList(data.Bosses),
        Excluded = SanitizeList(data.Excluded),
    };

    private static List<string> SanitizeList(List<string>? names) =>
        (names ?? [])
            .Select(n => n?.Trim() ?? string.Empty)
            .Where(n => n.Length > 0 && n.Length <= MaxNameLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter NpcNameListCodecTests`
Expected: PASS (11 tests)

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcNameListDocument.cs PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcNameListCodec.cs PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcNameListCodecTests.cs
git commit -m "feat: add versioned NpcNameListDocument/Codec with sanitization and additive merge"
```

---

## Task 5: `NpcNameListStore` (load/seed/atomic write) and wiring into `Plugin.RunScan()`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcNameListStore.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcNameListStoreTests.cs`

**Interfaces:**
- Consumes: `NpcNameListCodec`, `NpcNameListDocument` (Task 4); `NpcNameMatcher` (Task 1).
- Produces: `sealed record NpcNameListLoadResult(NpcNameListDocument Document, string? Warning)`; `static class NpcNameListStore` with `Load(string path, string embeddedSeedJson)`, `WriteAtomic(string path, string content)`, `BuildMatcher(NpcNameListDocument document)`. Task 7 (`NpcNameRefreshService`) consumes `WriteAtomic`.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NpcNames;

public class NpcNameListStoreTests
{
    private const string SeedJson = """
        {"Version":1,"NPCs":["Y'shtola"],"Enemies":[],"Bosses":[],"Excluded":[]}
        """;

    private static string MakeTempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "npc-name-list.json");
    }

    [Fact]
    public void Load_MissingFile_SeedsExactlyOnce()
    {
        var path = MakeTempPath();

        var first = NpcNameListStore.Load(path, SeedJson);
        var writtenAfterFirst = File.ReadAllText(path);
        var second = NpcNameListStore.Load(path, SeedJson);

        Assert.True(File.Exists(path));
        Assert.Contains("Y'shtola", first.Document.NPCs);
        Assert.Null(first.Warning);
        Assert.Equal(writtenAfterFirst, File.ReadAllText(path)); // second Load() didn't rewrite it
        Assert.Contains("Y'shtola", second.Document.NPCs);
    }

    [Fact]
    public void Load_ValidExistingFile_IsNeverOverwritten()
    {
        var path = MakeTempPath();
        var customContent = """{"Version":1,"NPCs":["Custom Entry"],"Enemies":[],"Bosses":[],"Excluded":[]}""";
        File.WriteAllText(path, customContent);

        var result = NpcNameListStore.Load(path, SeedJson);

        Assert.Contains("Custom Entry", result.Document.NPCs);
        Assert.DoesNotContain("Y'shtola", result.Document.NPCs);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Load_CorruptedFile_FallsBackToSeedInMemoryWithoutTouchingDisk()
    {
        var path = MakeTempPath();
        File.WriteAllText(path, "{ not json");
        var onDiskBefore = File.ReadAllText(path);

        var result = NpcNameListStore.Load(path, SeedJson);

        Assert.Contains("Y'shtola", result.Document.NPCs);
        Assert.NotNull(result.Warning);
        Assert.Equal(onDiskBefore, File.ReadAllText(path)); // disk untouched
    }

    [Fact]
    public void Load_UnsupportedVersion_FallsBackToSeedWithWarning()
    {
        var path = MakeTempPath();
        File.WriteAllText(path, """{"Version":99,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":[]}""");

        var result = NpcNameListStore.Load(path, SeedJson);

        Assert.Contains("Y'shtola", result.Document.NPCs);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void WriteAtomic_LeavesOriginalIntactIfDirectoryMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "nested", "npc-name-list.json");

        NpcNameListStore.WriteAtomic(path, SeedJson);

        Assert.Equal(SeedJson, File.ReadAllText(path));
    }

    [Fact]
    public void BuildMatcher_ProducesAMatcherThatMatchesLoadedNames()
    {
        var document = NpcNameListStore.Load(MakeTempPath(), SeedJson).Document;

        var matcher = NpcNameListStore.BuildMatcher(document);

        Assert.NotNull(matcher.Match("Y'shtola Overhaul"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter NpcNameListStoreTests`
Expected: FAIL (compile error — `NpcNameListStore` doesn't exist yet)

- [ ] **Step 3: Implement `NpcNameListStore`**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

public sealed record NpcNameListLoadResult(NpcNameListDocument Document, string? Warning);

public static class NpcNameListStore
{
    // Never throws for a missing/corrupted on-disk file — always returns a usable document,
    // falling back to the bundled seed. Scan-time corruption never touches disk (Warning is
    // reported by the caller via IPluginLog; nothing here writes over an unreadable file).
    public static NpcNameListLoadResult Load(string path, string embeddedSeedJson)
    {
        var seedParse = NpcNameListCodec.Parse(embeddedSeedJson);
        if (seedParse.Status != NpcNameListParseStatus.Ok)
            throw new InvalidOperationException(
                "Bundled NPC name-list seed is not valid JSON — this is a packaging bug, not a runtime condition.");
        var seed = seedParse.Data!;

        if (!File.Exists(path))
        {
            WriteAtomic(path, NpcNameListCodec.Serialize(seed));
            return new NpcNameListLoadResult(seed, null);
        }

        var parse = NpcNameListCodec.Parse(File.ReadAllText(path));
        return parse.Status switch
        {
            NpcNameListParseStatus.Ok => new NpcNameListLoadResult(parse.Data!, null),
            NpcNameListParseStatus.MalformedJson => new NpcNameListLoadResult(
                seed, $"{path} is not valid JSON; using the bundled NPC name list for this session."),
            NpcNameListParseStatus.UnsupportedVersion => new NpcNameListLoadResult(
                seed, $"{path} has an unsupported Version; using the bundled NPC name list for this session."),
            _ => new NpcNameListLoadResult(seed, "Unrecognized NPC name-list state; using the bundled list."),
        };
    }

    // Shared by scan-time seeding (first run) and refresh-time writes (Task 7) — temp-file then
    // atomic replace, the same pattern Plugin.cs already uses for ExportWorkbook/WriteBackup.
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    public static Classification.NpcNameMatcher BuildMatcher(NpcNameListDocument document) =>
        new(document.NPCs, document.Enemies, document.Bosses);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter NpcNameListStoreTests`
Expected: PASS (6 tests)

- [ ] **Step 5: Wire the matcher into `Plugin.RunScan()`**

In `Plugin.cs`, add `using PenumbraOrganizer.Plugin.Organizer.NpcNames;` to the top of the file, add this property near `WorkbookFilePath`:

```csharp
private string NpcNameListPath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "npc-name-list.json");
```

For now (embedded seed reading is added in Task 8), add a temporary minimal seed constant just above `RunScan()` so this task compiles and is independently testable — Task 8 replaces this with the real embedded-resource-backed seed:

```csharp
// Replaced with the real embedded, curated seed in Task 8.
private const string NpcNameListSeedJsonPlaceholder =
    """{"Version":1,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":[]}""";
```

Replace `RunScan()`'s body:

```csharp
public void RunScan()
{
    // One bulk call for all mods' changed items (Approach B in the Phase 1c spec).
    // Plain dictionary, not disposable. If Penumbra is unavailable this throws and
    // surfaces through MainWindow's existing scan error handling.
    var allChangedItems = new Penumbra.Api.IpcSubscribers.GetChangedItemAdapterDictionary(PluginInterface).Invoke();

    using var modList = GetModListAdapterIpc.Invoke();

    var npcNameListResult = NpcNameListStore.Load(NpcNameListPath, NpcNameListSeedJsonPlaceholder);
    if (npcNameListResult.Warning is not null)
        Log.Warning(npcNameListResult.Warning);
    var npcNameMatcher = NpcNameListStore.BuildMatcher(npcNameListResult.Document);

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
        };
    }).ToList();

    OrganizerState.LoadScan(rows, Config.ProtectedModIdentifiers);
    SaveProtectionState();
}
```

- [ ] **Step 6: Build the plugin project to confirm it compiles**

Run: `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
Expected: Build succeeds (this is the call site Task 2 left non-compiling; it now matches the 3-argument signature).

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, no regressions.

- [ ] **Step 8: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcNameListStore.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcNameListStoreTests.cs
git commit -m "feat: add NpcNameListStore and wire the name matcher into RunScan"
```

---

## Task 6: `NpcWikiScraper`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcWikiScraper.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcWikiScraperTests.cs`
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj` (AngleSharp dependency only — the embedded seed resource is added in Task 8)

**Interfaces:**
- Produces: `sealed record NpcWikiScrapeResult(IReadOnlyList<string> Names, string? FailureReason)`; `sealed class NpcWikiScraper` with constructor `NpcWikiScraper(HttpClient httpClient)` and `Task<NpcWikiScrapeResult> ScrapeCategoryAsync(Uri startUrl, CancellationToken cancellationToken)`. Task 7 consumes this.

Real page structure (confirmed live against `https://consolegameswiki.com/wiki/Category:NPCs`, which currently lists 11,018 total members across many pages): the member listing is MediaWiki's standard `<div id="mw-pages">` container, holding `<div class="mw-category-group">` sections per letter, each with `<li><a href="/wiki/Name" title="Name">Name</a></li>` entries. The "next page" link's visible text contains `(next page)` and its `href` is a relative URL (e.g. `/mediawiki/index.php?title=Category:NPCs&pagefrom=...`) that must be resolved against the current page's host.

- [ ] **Step 1: Add the AngleSharp dependency**

Run: `dotnet add PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj package AngleSharp`
Expected: `PenumbraOrganizer.Plugin.csproj` gains a `<PackageReference Include="AngleSharp" Version="..." />` line (whatever the latest stable version NuGet resolves).

Run: `dotnet restore PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
Expected: `PenumbraOrganizer.Plugin/packages.lock.json` is regenerated to include AngleSharp and its transitive dependencies (this project uses `RestorePackagesWithLockFile` — confirm the lock file diff includes an `AngleSharp` entry).

- [ ] **Step 2: Write the failing tests**

```csharp
using System.Net;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NpcNames;

public class NpcWikiScraperTests
{
    private const string PageOneHtml = """
        <html><body>
        <div id="mw-pages">
          <div class="mw-category-group">
            <h3>A</h3>
            <ul><li><a href="/wiki/Alphinaud" title="Alphinaud">Alphinaud</a></li></ul>
          </div>
        </div>
        <a href="/mediawiki/index.php?title=Category:NPCs&amp;pagefrom=Alphinaud#mw-pages">(next page)</a>
        </body></html>
        """;

    private const string PageTwoHtml = """
        <html><body>
        <div id="mw-pages">
          <div class="mw-category-group">
            <h3>T</h3>
            <ul><li><a href="/wiki/Thancred" title="Thancred">Thancred</a></li></ul>
          </div>
        </div>
        </body></html>
        """;

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private static HttpClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpMessageHandler(responder));

    [Fact]
    public async Task ScrapeCategoryAsync_SinglePage_ReturnsMembers()
    {
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PageTwoHtml) });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Equal(["Thancred"], result.Names);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_FollowsPaginationAcrossMultiplePages()
    {
        var requestedUrls = new List<string>();
        var client = MakeClient(req =>
        {
            requestedUrls.Add(req.RequestUri!.ToString());
            var html = requestedUrls.Count == 1 ? PageOneHtml : PageTwoHtml;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Equal(["Alphinaud", "Thancred"], result.Names);
        Assert.Equal(2, requestedUrls.Count);
        Assert.Contains("pagefrom=Alphinaud", requestedUrls[1]);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_RepeatedNextPageUrl_StopsWithFailureReason()
    {
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // "next page" link always points back at itself — a pagination loop.
            Content = new StringContent("""
                <html><body>
                <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/X" title="X">X</a></li></div></div>
                <a href="/wiki/Category:NPCs">(next page)</a>
                </body></html>
                """),
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Contains("loop", result.FailureReason);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_NextPageLinkPointsOffHost_StopsWithFailureReason()
    {
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <html><body>
                <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/X" title="X">X</a></li></div></div>
                <a href="https://evil.example.com/steal">(next page)</a>
                </body></html>
                """),
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Contains("off-host", result.FailureReason);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_MissingCategoryContainer_ReturnsFailureReason()
    {
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><p>Not a category page</p></body></html>"),
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.Names);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_HttpRequestFails_ReturnsFailureReason()
    {
        var client = MakeClient(_ => throw new HttpRequestException("connection refused"));
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Contains("connection refused", result.FailureReason);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_EmptyCategoryContainer_SucceedsWithNoNames()
    {
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><div id=\"mw-pages\"></div></body></html>"),
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Empty(result.Names);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_DuplicateMembersAcrossPages_AreBothReturned()
    {
        // MergeAdditive/Codec dedupe on the persistence side (Task 4); the scraper itself is a
        // faithful raw extraction and is not responsible for cross-page dedup.
        var pageOne = """
            <html><body>
            <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/Zenos" title="Zenos">Zenos</a></li></div></div>
            <a href="/mediawiki/index.php?title=Category:Bosses&amp;pagefrom=Zenos#mw-pages">(next page)</a>
            </body></html>
            """;
        var pageTwo = """
            <html><body>
            <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/Zenos" title="Zenos">Zenos</a></li></div></div>
            </body></html>
            """;
        var requestCount = 0;
        var client = MakeClient(_ =>
        {
            requestCount++;
            var html = requestCount == 1 ? pageOne : pageTwo;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:Bosses"), CancellationToken.None);

        Assert.Equal(["Zenos", "Zenos"], result.Names);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_NonSuccessStatusCode_ReturnsFailureReason()
    {
        // Distinct from a connection-level exception: this is a real HTTP response that
        // completes, but with a non-2xx status. HttpClient.GetStringAsync's own
        // EnsureSuccessStatusCode() call turns this into an HttpRequestException, caught by the
        // same branch as a connection failure, but it's worth its own test since it exercises a
        // different path through the stub (a real HttpResponseMessage, not a thrown exception).
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.Names);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_SubcategoryLinks_AreExcludedFromMemberNames()
    {
        // Real MediaWiki category pages can have a sibling "#mw-subcategories" div listing
        // child categories, separate from "#mw-pages" (the actual member listing). Scoping link
        // extraction to inside "#mw-pages" only means subcategory links are excluded by
        // construction, not by any extra filtering logic — this test documents and guards that.
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <html><body>
                <div id="mw-subcategories"><div class="mw-category-group"><li><a href="/wiki/Category:Raid_Bosses" title="Category:Raid Bosses">Raid Bosses</a></li></div></div>
                <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/Zenos" title="Zenos">Zenos</a></li></div></div>
                </body></html>
                """),
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:Bosses"), CancellationToken.None);

        Assert.Equal(["Zenos"], result.Names);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter NpcWikiScraperTests`
Expected: FAIL (compile error — `NpcWikiScraper` doesn't exist yet)

- [ ] **Step 4: Implement `NpcWikiScraper`**

```csharp
using AngleSharp.Html.Parser;

namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

public sealed record NpcWikiScrapeResult(IReadOnlyList<string> Names, string? FailureReason);

// The only piece of this feature that touches the network. Each call scrapes one paginated
// MediaWiki category page end to end, with defensive termination: a visited-URL set (catches
// pagination loops), a hard page ceiling, and same-host/HTTPS-only link following. A null
// FailureReason with a populated Names list means the category was fully and successfully
// scraped to its last page; a non-null FailureReason means something stopped early (the caller
// still gets whatever names were gathered before the failure).
public sealed class NpcWikiScraper
{
    private const int MaxPagesPerCategory = 100;
    private static readonly HtmlParser Parser = new();

    private readonly HttpClient _httpClient;

    public NpcWikiScraper(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<NpcWikiScrapeResult> ScrapeCategoryAsync(Uri startUrl, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var visited = new HashSet<Uri>();
        var host = startUrl.Host;
        Uri? currentUrl = startUrl;
        var pagesFetched = 0;

        while (currentUrl is not null)
        {
            if (pagesFetched >= MaxPagesPerCategory)
                return new NpcWikiScrapeResult(names, $"Stopped after reaching the {MaxPagesPerCategory}-page limit.");

            if (!visited.Add(currentUrl))
                return new NpcWikiScrapeResult(names, $"Pagination loop detected at {currentUrl}.");

            if (!string.Equals(currentUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentUrl.Host, host, StringComparison.OrdinalIgnoreCase))
                return new NpcWikiScrapeResult(names, $"Refused to follow off-host or non-HTTPS link: {currentUrl}.");

            string html;
            try
            {
                html = await _httpClient.GetStringAsync(currentUrl, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new NpcWikiScrapeResult(names, $"Failed to fetch {currentUrl}: {ex.Message}");
            }

            pagesFetched++;

            var document = Parser.ParseDocument(html);
            var container = document.QuerySelector("#mw-pages");
            if (container is null)
                return new NpcWikiScrapeResult(names, $"Category-member container not found on {currentUrl}.");

            foreach (var link in container.QuerySelectorAll("a"))
            {
                var name = link.GetAttribute("title") ?? link.TextContent;
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name.Trim());
            }

            // A missing "next page" link is the normal, successful termination condition for
            // the last page of a category — not a failure. Only the conditions above (loop,
            // off-host, ceiling, fetch/parse error, missing container) are failures.
            var nextHref = document
                .QuerySelectorAll("a")
                .FirstOrDefault(a => a.TextContent.Contains("next page", StringComparison.OrdinalIgnoreCase))
                ?.GetAttribute("href");

            currentUrl = nextHref is null
                ? null
                : (Uri.TryCreate(currentUrl, nextHref, out var resolved) ? resolved : null);
        }

        return new NpcWikiScrapeResult(names, null);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter NpcWikiScraperTests`
Expected: PASS (10 tests)

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, no regressions.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj PenumbraOrganizer.Plugin/packages.lock.json PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcWikiScraper.cs PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcWikiScraperTests.cs
git commit -m "feat: add NpcWikiScraper with bounded, defensive MediaWiki category pagination"
```

---

## Task 7: `NpcNameRefreshService`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcNameRefreshService.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcNameRefreshServiceTests.cs`

**Interfaces:**
- Consumes: `NpcWikiScraper` (Task 6); `NpcNameListCodec`, `NpcNameListStore.WriteAtomic` (Tasks 4/5).
- Produces: `sealed record NpcNameRefreshCategoryResult(string CategoryName, int AddedCount, string? FailureReason)`; `sealed record NpcNameRefreshResult(IReadOnlyList<NpcNameRefreshCategoryResult> Categories, bool RecoveredFromCorruption)`; `sealed class NpcNameRefreshService` with constructor `NpcNameRefreshService(NpcWikiScraper scraper)` and `Task<NpcNameRefreshResult> RefreshAsync(string path, string embeddedSeedJson, CancellationToken cancellationToken)`. Task 8 (`Plugin.cs`) consumes this.

Since `NpcWikiScraper` isn't itself mockable at the HTTP layer from this test (it already has its own tests), these tests construct real `NpcWikiScraper` instances backed by a stub `HttpMessageHandler`, exactly like Task 6 — this tests the real integration between the two classes, not a mock of the scraper.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NpcNames;

public class NpcNameRefreshServiceTests
{
    private const string SeedJson = """
        {"Version":1,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":[]}
        """;

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private static string MakeTempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "npc-name-list.json");
    }

    private static string CategoryPageHtml(string name) => $"""
        <html><body>
        <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/{name}" title="{name}">{name}</a></li></div></div>
        </body></html>
        """;

    private static NpcNameRefreshService MakeService(Func<Uri, string> htmlForUrl)
    {
        var handler = new StubHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(htmlForUrl(req.RequestUri!)) });
        return new NpcNameRefreshService(new NpcWikiScraper(new HttpClient(handler)));
    }

    [Fact]
    public async Task RefreshAsync_MergesNewlyScrapedNamesIntoEachCategory()
    {
        var path = MakeTempPath();
        var service = MakeService(url => url.ToString().Contains("Bosses")
            ? CategoryPageHtml("Zenos")
            : url.ToString().Contains("Enemies")
                ? CategoryPageHtml("Garuda")
                : CategoryPageHtml("Alphinaud"));

        var result = await service.RefreshAsync(path, SeedJson, CancellationToken.None);

        Assert.False(result.RecoveredFromCorruption);
        Assert.All(result.Categories, c => Assert.Null(c.FailureReason));
        Assert.Equal(3, result.Categories.Count);
        Assert.All(result.Categories, c => Assert.Equal(1, c.AddedCount));

        var written = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;
        Assert.Contains("Alphinaud", written.NPCs);
        Assert.Contains("Garuda", written.Enemies);
        Assert.Contains("Zenos", written.Bosses);
    }

    [Fact]
    public async Task RefreshAsync_OneCategoryFailing_StillMergesTheOthers()
    {
        var path = MakeTempPath();
        var service = MakeService(url => url.ToString().Contains("Enemies")
            ? throw new HttpRequestException("timed out")
            : CategoryPageHtml("Alphinaud"));

        var result = await service.RefreshAsync(path, SeedJson, CancellationToken.None);

        var enemies = result.Categories.Single(c => c.CategoryName == "Enemies");
        var npcs = result.Categories.Single(c => c.CategoryName == "NPCs");
        Assert.NotNull(enemies.FailureReason);
        Assert.Null(npcs.FailureReason);
        Assert.Equal(1, npcs.AddedCount);

        var written = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;
        Assert.Contains("Alphinaud", written.NPCs);
        Assert.Empty(written.Enemies);
    }

    [Fact]
    public async Task RefreshAsync_ExcludedName_IsNeverReAdded()
    {
        var path = MakeTempPath();
        var seedWithExclusion = """{"Version":1,"NPCs":["Excluded Guy"],"Enemies":[],"Bosses":[],"Excluded":["Excluded Guy"]}""";
        // Seed already contains the excluded name from a prior manual edit removing it — but
        // Excluded blocks it from being *re-added* by a future scrape; it does not retroactively
        // remove an already-present entry (that's still a manual edit). Simulate the real
        // "already removed by hand" state instead: excluded, and NOT present in NPCs.
        var seed = """{"Version":1,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":["Excluded Guy"]}""";
        var service = MakeService(_ => CategoryPageHtml("Excluded Guy"));

        var result = await service.RefreshAsync(path, seed, CancellationToken.None);

        var written = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;
        Assert.DoesNotContain("Excluded Guy", written.NPCs);
    }

    [Fact]
    public async Task RefreshAsync_NeverRemovesExistingNames()
    {
        var path = MakeTempPath();
        var seed = """{"Version":1,"NPCs":["Y'shtola"],"Enemies":[],"Bosses":[],"Excluded":[]}""";
        // The scrape this run finds nothing under NPCs (simulating a temporarily-empty/changed page).
        var service = MakeService(url => url.ToString().Contains("NPCs")
            ? "<html><body><div id=\"mw-pages\"></div></body></html>"
            : CategoryPageHtml("Placeholder"));

        await service.RefreshAsync(path, seed, CancellationToken.None);

        var written = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;
        Assert.Contains("Y'shtola", written.NPCs); // still present, not silently dropped
    }

    [Fact]
    public async Task RefreshAsync_CorruptedExistingFile_PreservesBackupAndRecoversFromSeed()
    {
        var path = MakeTempPath();
        File.WriteAllText(path, "{ not json");
        var service = MakeService(_ => CategoryPageHtml("Alphinaud"));

        var result = await service.RefreshAsync(path, SeedJson, CancellationToken.None);

        Assert.True(result.RecoveredFromCorruption);
        var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*.json");
        Assert.Single(backups);
        Assert.Equal("{ not json", File.ReadAllText(backups[0]));

        var written = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;
        Assert.Contains("Alphinaud", written.NPCs);
    }

    [Fact]
    public async Task RefreshAsync_MissingFile_StartsFromSeedNotRecoveryFlag()
    {
        var path = MakeTempPath(); // MakeTempPath only creates the directory, not the file
        var service = MakeService(_ => CategoryPageHtml("Alphinaud"));

        var result = await service.RefreshAsync(path, SeedJson, CancellationToken.None);

        Assert.False(result.RecoveredFromCorruption); // missing file is a normal first run, not corruption
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter NpcNameRefreshServiceTests`
Expected: FAIL (compile error — `NpcNameRefreshService` doesn't exist yet)

- [ ] **Step 3: Implement `NpcNameRefreshService`**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

public sealed record NpcNameRefreshCategoryResult(string CategoryName, int AddedCount, string? FailureReason);

public sealed record NpcNameRefreshResult(IReadOnlyList<NpcNameRefreshCategoryResult> Categories, bool RecoveredFromCorruption);

// Orchestrates the only network-touching action in the plugin: scrape all three wiki categories
// independently (one failing category doesn't block the others), additively merge whatever
// succeeded into the on-disk list (respecting Excluded, never removing anything already
// present), and write atomically. Corrupted-file recovery is distinct from missing-file
// first-run: a corrupted file is preserved as a timestamped backup before starting fresh from
// the seed, so nothing already there is silently lost.
public sealed class NpcNameRefreshService
{
    private static readonly (string CategoryName, Uri Url)[] Categories =
    [
        ("NPCs", new Uri("https://consolegameswiki.com/wiki/Category:NPCs")),
        ("Enemies", new Uri("https://consolegameswiki.com/wiki/Category:Enemies")),
        ("Bosses", new Uri("https://consolegameswiki.com/wiki/Category:Bosses")),
    ];

    private readonly NpcWikiScraper _scraper;

    public NpcNameRefreshService(NpcWikiScraper scraper) => _scraper = scraper;

    public async Task<NpcNameRefreshResult> RefreshAsync(
        string path, string embeddedSeedJson, CancellationToken cancellationToken)
    {
        var (existing, recovered) = LoadForRefresh(path, embeddedSeedJson);

        var scraped = new Dictionary<string, NpcWikiScrapeResult>();
        foreach (var (categoryName, url) in Categories)
            scraped[categoryName] = await _scraper.ScrapeCategoryAsync(url, cancellationToken);

        var excluded = new HashSet<string>(existing.Excluded, StringComparer.OrdinalIgnoreCase);
        var merged = NpcNameListCodec.MergeAdditive(
            existing,
            newNpcs: scraped["NPCs"].Names.Where(n => !excluded.Contains(n)).ToList(),
            newEnemies: scraped["Enemies"].Names.Where(n => !excluded.Contains(n)).ToList(),
            newBosses: scraped["Bosses"].Names.Where(n => !excluded.Contains(n)).ToList());

        NpcNameListStore.WriteAtomic(path, NpcNameListCodec.Serialize(merged));

        var categoryResults = Categories
            .Select(c => new NpcNameRefreshCategoryResult(
                c.CategoryName,
                AddedCount: CategoryCount(merged, c.CategoryName) - CategoryCount(existing, c.CategoryName),
                scraped[c.CategoryName].FailureReason))
            .ToList();

        return new NpcNameRefreshResult(categoryResults, recovered);
    }

    private static (NpcNameListDocument Document, bool Recovered) LoadForRefresh(string path, string embeddedSeedJson)
    {
        var seed = NpcNameListCodec.Parse(embeddedSeedJson).Data!;

        if (!File.Exists(path))
            return (seed, false);

        var parse = NpcNameListCodec.Parse(File.ReadAllText(path));
        if (parse.Status == NpcNameListParseStatus.Ok)
            return (parse.Data!, false);

        var backupPath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
        File.Copy(path, backupPath, overwrite: true);
        return (seed, true);
    }

    private static int CategoryCount(NpcNameListDocument document, string categoryName) => categoryName switch
    {
        "NPCs" => document.NPCs.Count,
        "Enemies" => document.Enemies.Count,
        "Bosses" => document.Bosses.Count,
        _ => throw new ArgumentOutOfRangeException(nameof(categoryName), categoryName, null),
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter NpcNameRefreshServiceTests`
Expected: PASS (6 tests)

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcNameRefreshService.cs PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcNameRefreshServiceTests.cs
git commit -m "feat: add NpcNameRefreshService orchestrating the wiki scrape/merge/write flow"
```

---

## Task 8: Curate the seed list, embed it, and wire the refresh action into `Plugin.cs`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/NpcNames/npc-name-list-seed.json`
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: `NpcNameRefreshService`, `NpcNameListStore` (Tasks 5, 7).
- Produces: `Plugin.RefreshNpcNamesAsync(CancellationToken)` returning `Task<NpcNameRefreshResult>`. Task 9 (`MainWindow.cs`) consumes this.

The seed is a small, deliberately incomplete starting point (per the design's Non-goals — the list is a best-effort trail, grown over time via the refresh button) using only bare character/NPC names, never any of the specific mod titles from the original research corpus.

- [ ] **Step 1: Write the seed content**

```json
{
  "Version": 1,
  "NPCs": [
    "Y'shtola", "Thancred", "Alphinaud", "Alisaie", "Urianger", "Krile",
    "G'raha Tia", "Estinien", "Yshtola", "Ryne", "Feo Ul", "Emet-Selch",
    "Zenos yae Galvus", "Minfilia", "Papalymo", "Moenbryda", "Haurchefant",
    "Lyse", "Y'mhitra"
  ],
  "Enemies": [
    "Titania", "Garuda", "Ifrit", "Shiva", "Ramuh", "Leviathan", "Ravana",
    "Bismarck", "Sephirot", "Sophia", "Zurvan", "Susano", "Lakshmi",
    "Shinryu", "Tsukuyomi"
  ],
  "Bosses": [
    "Zenos yae Galvus", "Nidhogg", "Omega", "Shinryu", "Zurvan",
    "The Cloud of Darkness", "King Thordan", "Fatebreaker", "Barbariccia",
    "Halone", "Ser Charibert"
  ],
  "Excluded": []
}
```

Save this to `PenumbraOrganizer.Plugin/Organizer/NpcNames/npc-name-list-seed.json`.

- [ ] **Step 2: Embed the seed resource in the csproj**

In `PenumbraOrganizer.Plugin.csproj`, add a new `ItemGroup`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Organizer\NpcNames\npc-name-list-seed.json" />
  </ItemGroup>
```

- [ ] **Step 3: Build to confirm the resource embeds correctly**

Run: `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
Expected: Build succeeds. `EmbeddedResource` entries are a build-time-only concern (there's no compile error possible from a missing or misnamed one — a lookup miss only surfaces as a null stream at runtime), so this step only confirms the csproj edit itself is well-formed XML that the build accepts. The actual manifest-resource-name — `PenumbraOrganizer.Plugin.Organizer.NpcNames.npc-name-list-seed.json` — is only really exercised once `ReadEmbeddedNpcNameSeed()` is wired up and called in Step 4 below; that's where a wrong resource name would actually surface (as an `InvalidOperationException` the first time `RunScan()` or `RefreshNpcNamesAsync()` runs). Task 9's manual in-game checklist is the first point this plan actually calls that code path.

- [ ] **Step 4: Replace the placeholder seed constant and wire `RefreshNpcNamesAsync` into `Plugin.cs`**

Remove the `NpcNameListSeedJsonPlaceholder` constant added in Task 5. Add near the top of the `Plugin` class (with the other fields):

```csharp
private readonly HttpClient _npcHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
private readonly Organizer.NpcNames.NpcNameRefreshService _npcNameRefreshService;
```

In the constructor, after `_workbookService = new WorkbookWorkflowService(...)`:

```csharp
_npcHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
    "PenumbraOrganizer.Plugin/1.0 (+https://github.com/monstersghost/PenumbraOrganizer.Plugin)");
_npcNameRefreshService = new Organizer.NpcNames.NpcNameRefreshService(
    new Organizer.NpcNames.NpcWikiScraper(_npcHttpClient));
```

In `Dispose()`, after `CommandManager.RemoveHandler(CommandName);`:

```csharp
_npcHttpClient.Dispose();
```

Add this helper near `WorkbookFilePath`/`NpcNameListPath`:

```csharp
private static string ReadEmbeddedNpcNameSeed()
{
    var assembly = typeof(Plugin).Assembly;
    const string resourceName = "PenumbraOrganizer.Plugin.Organizer.NpcNames.npc-name-list-seed.json";
    using var stream = assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}
```

In `RunScan()`, replace `NpcNameListSeedJsonPlaceholder` with `ReadEmbeddedNpcNameSeed()`:

```csharp
var npcNameListResult = NpcNameListStore.Load(NpcNameListPath, ReadEmbeddedNpcNameSeed());
```

Add the refresh entry point near `ExportWorkbook`/`ImportWorkbook`:

```csharp
internal async Task<Organizer.NpcNames.NpcNameRefreshResult> RefreshNpcNamesAsync(CancellationToken cancellationToken)
{
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeoutCts.CancelAfter(TimeSpan.FromMinutes(5)); // generous: NPCs alone can span 50+ pages
    return await _npcNameRefreshService.RefreshAsync(NpcNameListPath, ReadEmbeddedNpcNameSeed(), timeoutCts.Token);
}
```

- [ ] **Step 5: Build and run the full test suite**

Run: `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
Expected: Build succeeds.

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/NpcNames/npc-name-list-seed.json PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: curate seed NPC/enemy/boss name list and wire RefreshNpcNamesAsync into Plugin"
```

---

## Task 9: UI — Sort tab "Refresh NPC list from wiki" button

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `Plugin.RefreshNpcNamesAsync(CancellationToken)` (Task 8).

This is the first genuinely asynchronous UI action in the plugin — every existing action calls a synchronous `_plugin.*` method directly from inside a button's `if` block. `Draw()` runs every frame on the game's render thread, so the button starts a `Task` and stores it; `Draw()` reads `IsCompleted` each frame to decide whether to show the button as disabled, and reads the result fields once the task completes. No test is added for this step — `MainWindow.cs` has no existing unit-test coverage anywhere in this codebase (ImGui `Draw()` methods aren't unit-tested); verification is manual, in-game, per the note at the end of this task.

- [ ] **Step 1: Add the new fields**

In `MainWindow.cs`, alongside the other `_last*`/result fields near the top of the class:

```csharp
private Task? _npcRefreshTask;
private Organizer.NpcNames.NpcNameRefreshResult? _npcRefreshResult;
```

- [ ] **Step 2: Add the button and result display to `DrawSortTab()`**

In `DrawSortTab()`, after the existing `if (ImGui.Button("Import Workbook")) { ... }` block and its `_lastWorkbookImportResult` display (i.e. right before `ImGui.Spacing(); ImGui.TextUnformatted("Start Manually: ...")`), insert:

```csharp
        ImGui.Spacing();
        var npcRefreshInFlight = _npcRefreshTask is { IsCompleted: false };
        ImGui.BeginDisabled(npcRefreshInFlight);
        if (ImGui.Button("Refresh NPC list from wiki"))
        {
            _npcRefreshResult = null;
            _lastError = null;
            _npcRefreshTask = RefreshNpcNamesAsync();
        }
        ImGui.EndDisabled();

        if (npcRefreshInFlight)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("Refreshing... (this can take a few minutes for a full scrape)");
        }

        if (_npcRefreshResult is not null)
        {
            if (_npcRefreshResult.RecoveredFromCorruption)
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    "The existing NPC name list was unreadable and has been reset from the bundled "
                    + "seed list; the old file was preserved alongside it as a timestamped backup.");

            foreach (var category in _npcRefreshResult.Categories)
            {
                if (category.FailureReason is not null)
                    ImGui.TextColored(ImGuiColors.DalamudRed, $"  {category.CategoryName} failed: {category.FailureReason}");
                else
                    ImGui.TextUnformatted($"  {category.CategoryName}: +{category.AddedCount}");
            }
        }
```

- [ ] **Step 3: Add the async wrapper method**

Add this near `ExportWorkbook`/`ImportWorkbook` at the bottom of the class:

```csharp
    private async Task RefreshNpcNamesAsync()
    {
        try
        {
            _npcRefreshResult = await _plugin.RefreshNpcNamesAsync(CancellationToken.None);
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"NPC list refresh failed: {ex.Message}";
        }
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
Expected: Build succeeds.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, no regressions (this task adds no new automated tests).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add Refresh NPC list from wiki button to the Sort tab"
```

- [ ] **Step 7 (manual, in-game, not part of the automated build): verification checklist for whoever picks this up next**

- Load the plugin in-game, open the Sort tab, confirm the button renders and is initially enabled.
- Click it; confirm it disables immediately and re-enables once the network round-trip completes (this can legitimately take up to a few minutes given the real category sizes — 11,000+ entries under NPCs alone).
- Confirm the game does not visibly freeze/hitch while the refresh is in flight (the whole point of Task 9's async design).
- Confirm a per-category result line appears, and that `%APPDATA%`'s plugin config directory now has `npc-name-list.json` with real scraped entries.
- Run a Scan afterward and confirm a mod known to be named after a scraped NPC/enemy/boss now classifies as `NPC` and sorts under `NPC/NPCs` (or `/Enemies`/`/Bosses`) rather than its previous category.
- Temporarily disconnect networking and click Refresh again; confirm per-category failure reasons display in red and nothing already in `npc-name-list.json` is lost (re-open the file to confirm).

---

## Task 10: Perf sanity test, final whole-branch review, handoff doc

**Files:**
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/NpcNameMatcherPerfTests.cs`
- Modify: `docs/HANDOFF_NPC_CLASSIFICATION.md`
- Modify: `docs/ROADMAP.md`

**Interfaces:**
- Consumes: `NpcNameMatcher` (Task 1).

- [ ] **Step 1: Write the perf sanity test**

A regression guard for the compiled-regex-per-name mistake the design deliberately avoided (see the spec's revision history) — not a formal benchmark suite, just a generous time bound so a future change that reintroduces per-name regex compilation gets caught.

```csharp
using System.Diagnostics;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class NpcNameMatcherPerfTests
{
    [Fact]
    public void ConstructionAndFullScanPass_CompletesWithinAGenerousBound()
    {
        var npcs = Enumerable.Range(0, 4000).Select(i => $"Test Npc Name {i}").ToList();
        var enemies = Enumerable.Range(0, 4000).Select(i => $"Test Enemy Name {i}").ToList();
        var bosses = Enumerable.Range(0, 4000).Select(i => $"Test Boss Name {i}").ToList();
        var modNames = Enumerable.Range(0, 500).Select(i => $"Some Ordinary Mod {i}").ToList();

        var stopwatch = Stopwatch.StartNew();
        var matcher = new NpcNameMatcher(npcs, enemies, bosses);
        foreach (var modName in modNames)
            matcher.Match(modName);
        stopwatch.Stop();

        // Generous on purpose: this guards against reintroducing thousands of separate
        // compiled Regex objects (which was seconds-to-tens-of-seconds slow), not against
        // ordinary variance in a single combined-regex-per-category build.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Matcher construction + scan took {stopwatch.Elapsed}, expected under 5s.");
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter NpcNameMatcherPerfTests`
Expected: PASS

- [ ] **Step 3: Run the entire test suite one final time**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, full suite green.

- [ ] **Step 4: Update the handoff doc**

Append a new section to the end of `docs/HANDOFF_NPC_CLASSIFICATION.md`:

```markdown
## Update, 2026-07-17 — name-heuristic classification implemented

Implemented per `docs/superpowers/specs/2026-07-17-plugin-organizer-npc-name-classification-design.md`
and `docs/superpowers/plans/2026-07-17-plugin-organizer-npc-name-classification.md`. Summary:

- `NpcNameMatcher` (whole-word, Unicode-boundary, combined-regex-per-category matching) now
  outranks every structural rule in `ModTypeClassifier.Classify`, including the Smallclothes/
  Emperor's New Clothes placeholder override — a deliberate, user-confirmed trade-off.
- The name list persists at `<plugin config dir>/npc-name-list.json`, seeded from a small curated
  embedded resource on first run, and is additively grown via a manual "Refresh NPC list from
  wiki" button on the Sort tab that scrapes `consolegameswiki.com`'s NPCs/Enemies/Bosses
  categories (the only network-touching code path in the plugin).
- The child-race-variant classifier gap (memory `child-race-variant-classification-gap`) remains
  separate, unrelated, and not yet fixed.
- Full in-game verification is still outstanding — see Task 9's manual checklist in the
  implementation plan linked above.
```

- [ ] **Step 5: Update the roadmap**

In `docs/ROADMAP.md`, update the "Where we are" date to 2026-07-17 and mark NPC/enemy/boss name-based classification as shipped, pending in-game verification (match whatever heading/list format the existing roadmap entries already use for the workbook feature).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin.Tests/Organizer/Classification/NpcNameMatcherPerfTests.cs docs/HANDOFF_NPC_CLASSIFICATION.md docs/ROADMAP.md
git commit -m "test: add NpcNameMatcher perf sanity check; update NPC classification handoff docs"
```

---

## Final whole-branch review

After Task 10, dispatch a whole-branch review (most capable model available) covering the full diff across all 10 tasks before merging, same as the workbook feature's execution. Specifically double-check:
- Task 2's priority reordering didn't silently change behavior for any of the 25 migrated tests beyond adding the `modName`/`npcNameMatcher` arguments.
- No production code path other than `NpcWikiScraper`/`NpcNameRefreshService` (Tasks 6-7) makes a network call.
- Every write to `npc-name-list.json` goes through `NpcNameListStore.WriteAtomic`.
- `docs/HANDOFF_NPC_CLASSIFICATION.md`'s "never cite the specific NSFW mod titles" constraint was actually honored in every new file, comment, and test name introduced across all 10 tasks.
