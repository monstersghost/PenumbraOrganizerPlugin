# Phase 1c: Classify by Mod Type — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "By Mod Type" sort strategy that classifies each scanned mod from its Penumbra `GetChangedItems` keys, per the approved spec `docs/superpowers/specs/2026-07-13-plugin-organizer-phase1c-classification-design.md`.

**Architecture:** Two pure layers — `ChangedItemKeyParser` (one raw key string → structured record) and `ModTypeClassifier` (a mod's parsed keys → `(ModCategory?, string? SubCategory)`) — fed by one bulk IPC call (`GetChangedItemAdapterDictionary`) during the existing `Plugin.RunScan()`. A new `SortByModType()` on `OrganizerState` mirrors the existing `SortByCreator()`. The temporary spike button gets removed first.

**Tech Stack:** C# / .NET 10, `Dalamud.NET.Sdk/15.0.0`, NuGet `Penumbra.Api` 5.15.1, xunit (existing test project `PenumbraOrganizer.Plugin.Tests`).

## Global Constraints

- The shared `ModCategory` enum (linked from `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.Core\Classification\ModCategory.cs`) must NOT be modified. It has no `Unknown` member — "unknown" is represented as `null` (`ModCategory?`).
- Never guess: an unrecognized key shape or body-part token classifies as Unknown (`ClassificationResult.Unknown`), and Unknown mods are skipped by the sort (left at their current path for manual sorting).
- All key-string matching uses `StringComparison.Ordinal` — the markers are confirmed byte-identical English literals across EN/DE/JA game clients (spec, "Localization").
- "Gear wins": any bare-name key anywhere in a mod's key set makes the whole mod `Gear`, unconditionally, before any other rule is consulted.
- Sub-categories (`Battle Animation`, `Emotes`, `Other`, `VFX`, `Animation`) are plugin-local strings producing a two-level path under a single `Animation and VFX` parent folder.
- No write IPC of any kind (`SetModPath` etc.) — this phase remains read-only; Apply stays disabled.
- Build must stay at 0 warnings / 0 errors; all existing 23 tests must keep passing.
- Run all commands from the repo root `C:\Repo\PenumbraOrganizer.Plugin`.

---

### Task 1: Remove the temporary spike button

The spike dump button (commit `3e78003`) was throwaway data-gathering code; the spec's Implementation notes require its removal. Do this first so later tasks edit clean versions of `Plugin.cs` and `MainWindow.cs`.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` (via revert)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` (via revert)

**Interfaces:**
- Consumes: nothing.
- Produces: `Plugin.RunScan()` and `MainWindow.DrawScanTab()` restored to their pre-spike shape; `Plugin.DumpChangedItemsSpike()` and the "SPIKE: Dump changed items" button no longer exist.

- [ ] **Step 1: Revert the spike commit**

```bash
git revert --no-edit 3e78003
```

Expected: a new commit "Revert \"spike: add temporary changed-items dump button for Phase 1c data\"" touching only `Plugin.cs` and `MainWindow.cs`. If the revert reports conflicts, stop and resolve manually — the two files should not have been touched since.

- [ ] **Step 2: Verify build and existing tests**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `Passed! - Failed: 0, Passed: 23`.

No separate commit — `git revert` already committed.

---

### Task 2: `ChangedItemKey` record + parser for all non-Customization shapes

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/ChangedItemKey.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/ChangedItemKeyParser.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ChangedItemKeyParserTests.cs`

**Interfaces:**
- Consumes: nothing (pure, no dependencies).
- Produces:
  - `enum ChangedItemKeyShape { Gear, Customization, Npc, Mount, Minion, Emote, Action, Icon, CategoryLiteral }`
  - `sealed record ChangedItemKey(ChangedItemKeyShape Shape, string Raw, string? ItemName = null, string? Race = null, string? Gender = null, string? BodyPart = null, string? Subtype = null, int? Number = null, string? CategoryLiteral = null)`
  - `static ChangedItemKey ChangedItemKeyParser.Parse(string key)`
  - In this task, `Parse` returns `Shape = Customization` with only `Raw` set for `"Customization: ..."` keys; Task 3 fills in the payload fields.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ChangedItemKeyParserTests.cs`. Every string below is a real key from the spike dumps (including German/Japanese payloads to lock in Ordinal matching):

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class ChangedItemKeyParserTests
{
    [Theory]
    [InlineData("Emote: Sit on Ground", "Sit on Ground")]
    [InlineData("Emote: 地面に座る", "地面に座る")]
    public void Parse_EmotePrefix_YieldsEmoteShape(string key, string expectedName)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Emote, result.Shape);
        Assert.Equal(expectedName, result.ItemName);
        Assert.Equal(key, result.Raw);
    }

    [Theory]
    [InlineData("Action: Radiant Aegis", "Radiant Aegis")]
    [InlineData("Action: Hissatsu: Guren", "Hissatsu: Guren")]
    [InlineData("Action: 大鷹", "大鷹")]
    public void Parse_ActionPrefix_YieldsActionShape(string key, string expectedName)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Action, result.Shape);
        Assert.Equal(expectedName, result.ItemName);
    }

    [Fact]
    public void Parse_IconPrefix_YieldsIconShape()
    {
        var result = ChangedItemKeyParser.Parse("Icon: 42992");

        Assert.Equal(ChangedItemKeyShape.Icon, result.Shape);
        Assert.Equal("Icon: 42992", result.Raw);
    }

    [Theory]
    [InlineData("Animation")]
    [InlineData("Vfx")]
    [InlineData("Sound")]
    [InlineData("Housing")]
    public void Parse_BareCategoryWord_YieldsCategoryLiteralShape(string key)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.CategoryLiteral, result.Shape);
        Assert.Equal(key, result.CategoryLiteral);
    }

    [Theory]
    [InlineData("Ancient Airship (Mount)", "Ancient Airship")]
    [InlineData("古式魔道船 (Mount)", "古式魔道船")]
    public void Parse_MountSuffix_YieldsMountShape(string key, string expectedName)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Mount, result.Shape);
        Assert.Equal(expectedName, result.ItemName);
    }

    [Theory]
    [InlineData("Beady Eye (Battle NPC)", "Beady Eye")]
    [InlineData("Blue-footed Booby (Companion)", "Blue-footed Booby")]
    [InlineData("Stray Gaelicat (Event NPC)", "Stray Gaelicat")]
    [InlineData("タイニーアイ (Companion)", "タイニーアイ")]
    public void Parse_MinionSuffix_YieldsMinionShape(string key, string expectedName)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Minion, result.Shape);
        Assert.Equal(expectedName, result.ItemName);
    }

    [Fact]
    public void Parse_NpcSuffix_YieldsNpcShape()
    {
        var result = ChangedItemKeyParser.Parse("Smallclothes (NPC, 9903-1, Body)");

        Assert.Equal(ChangedItemKeyShape.Npc, result.Shape);
    }

    [Theory]
    [InlineData("Street Jacket")]
    [InlineData("Moonward Samurai Blade (Sheathe)")]  // parenthetical, but not a recognized suffix
    [InlineData("Dated Canvas Bottom (Auburn)")]       // color variant, still Gear
    [InlineData("Doman Iron Claws (Offhand)")]         // slot qualifier, still Gear
    [InlineData("エンペラーズ・ニューブリーチ")]
    public void Parse_BareItemName_YieldsGearShape(string key)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Gear, result.Shape);
        Assert.Equal(key, result.ItemName);
    }

    [Fact]
    public void Parse_CustomizationPrefix_YieldsCustomizationShape()
    {
        var result = ChangedItemKeyParser.Parse("Customization: Miqo'te Female Face 101");

        Assert.Equal(ChangedItemKeyShape.Customization, result.Shape);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ChangedItemKeyParserTests"`
Expected: compilation failure — `ChangedItemKeyParser` and `ChangedItemKeyShape` do not exist.

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Classification/ChangedItemKey.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public enum ChangedItemKeyShape
{
    Gear,
    Customization,
    Npc,
    Mount,
    Minion,
    Emote,
    Action,
    Icon,
    CategoryLiteral,
}

/// <summary>
/// One parsed GetChangedItems key. Captures every field the raw string can reliably
/// yield, not just what today's classifier consumes (spec: Layer 1 preserves signal
/// like Action/Icon for future use even though Layer 2 doesn't act on it yet).
/// </summary>
public sealed record ChangedItemKey(
    ChangedItemKeyShape Shape,
    string Raw,
    string? ItemName = null,
    string? Race = null,
    string? Gender = null,
    string? BodyPart = null,
    string? Subtype = null,
    int? Number = null,
    string? CategoryLiteral = null);
```

Create `PenumbraOrganizer.Plugin/Organizer/Classification/ChangedItemKeyParser.cs`:

```csharp
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public static class ChangedItemKeyParser
{
    private const string CustomizationPrefix = "Customization: ";
    private const string EmotePrefix = "Emote: ";
    private const string ActionPrefix = "Action: ";
    private const string IconPrefix = "Icon: ";
    private const string MountSuffix = " (Mount)";

    private static readonly string[] MinionSuffixes = [" (Battle NPC)", " (Companion)", " (Event NPC)"];
    private static readonly string[] CategoryLiterals = ["Animation", "Vfx", "Sound", "Housing"];

    // e.g. "Smallclothes (NPC, 9903-1, Body)" — id and slot are not parsed further.
    private static readonly Regex NpcSuffix = new(@"\(NPC, [^)]*\)$", RegexOptions.Compiled);

    public static ChangedItemKey Parse(string key)
    {
        if (key.StartsWith(CustomizationPrefix, StringComparison.Ordinal))
            return new(ChangedItemKeyShape.Customization, key);

        if (key.StartsWith(EmotePrefix, StringComparison.Ordinal))
            return new(ChangedItemKeyShape.Emote, key, ItemName: key[EmotePrefix.Length..]);

        if (key.StartsWith(ActionPrefix, StringComparison.Ordinal))
            return new(ChangedItemKeyShape.Action, key, ItemName: key[ActionPrefix.Length..]);

        if (key.StartsWith(IconPrefix, StringComparison.Ordinal))
            return new(ChangedItemKeyShape.Icon, key);

        if (CategoryLiterals.Contains(key, StringComparer.Ordinal))
            return new(ChangedItemKeyShape.CategoryLiteral, key, CategoryLiteral: key);

        if (key.EndsWith(MountSuffix, StringComparison.Ordinal))
            return new(ChangedItemKeyShape.Mount, key, ItemName: key[..^MountSuffix.Length]);

        foreach (var suffix in MinionSuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal))
                return new(ChangedItemKeyShape.Minion, key, ItemName: key[..^suffix.Length]);
        }

        if (NpcSuffix.IsMatch(key))
            return new(ChangedItemKeyShape.Npc, key);

        return new(ChangedItemKeyShape.Gear, key, ItemName: key);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ChangedItemKeyParserTests"`
Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ChangedItemKey.cs PenumbraOrganizer.Plugin/Organizer/Classification/ChangedItemKeyParser.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ChangedItemKeyParserTests.cs
git commit -m "feat(1c): parse changed-item key shapes (all but Customization payload)"
```

---

### Task 3: Customization payload parsing

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ChangedItemKeyParser.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ChangedItemKeyParserTests.cs`

**Interfaces:**
- Consumes: `ChangedItemKey`, `ChangedItemKeyShape` from Task 2.
- Produces: `Parse` now fills `Race`, `Gender`, `BodyPart`, `Subtype`, `Number` for `Customization`-shaped keys. `BodyPart` is the reliable field (spec); `Race`/`Gender` are best-effort and `null` for `"Player ..."` keys; the literal payload `"Unknown"` yields `BodyPart = "Unknown"`.

- [ ] **Step 1: Write the failing tests**

Append to `ChangedItemKeyParserTests.cs` (all real keys from the dumps):

```csharp
    [Theory]
    // key, race, gender, bodyPart, subtype, number
    [InlineData("Customization: Miqo'te Female Face 101", "Miqo'te", "Female", "Face", null, 101)]
    [InlineData("Customization: Miqo'te Female Face (Iris) 101", "Miqo'te", "Female", "Face", "Iris", 101)]
    [InlineData("Customization: Au Ra Female Body (Skeleton) 1", "Au Ra", "Female", "Body", "Skeleton", 1)]
    [InlineData("Customization: Midlander Female Hair (Accessory) 147", "Midlander", "Female", "Hair", "Accessory", 147)]
    [InlineData("Customization: Miqo'te Male Tail 4", "Miqo'te", "Male", "Tail", null, 4)]
    [InlineData("Customization: Midlander Female Skin Textures", "Midlander", "Female", "Skin Textures", null, null)]
    public void Parse_CustomizationPayload_ExtractsFields(
        string key, string? race, string? gender, string? bodyPart, string? subtype, int? number)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Customization, result.Shape);
        Assert.Equal(race, result.Race);
        Assert.Equal(gender, result.Gender);
        Assert.Equal(bodyPart, result.BodyPart);
        Assert.Equal(subtype, result.Subtype);
        Assert.Equal(number, result.Number);
    }

    [Fact]
    public void Parse_CustomizationPlayerPayload_HasNoRaceOrGender()
    {
        var result = ChangedItemKeyParser.Parse("Customization: Player Skin Textures");

        Assert.Null(result.Race);
        Assert.Null(result.Gender);
        Assert.Equal("Skin Textures", result.BodyPart);
    }

    [Fact]
    public void Parse_CustomizationUnknownPayload_KeepsUnknownAsBodyPart()
    {
        var result = ChangedItemKeyParser.Parse("Customization: Unknown");

        Assert.Null(result.Race);
        Assert.Null(result.Gender);
        Assert.Equal("Unknown", result.BodyPart);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ChangedItemKeyParserTests"`
Expected: the new tests FAIL (fields are null/unset); Task 2's tests still pass.

- [ ] **Step 3: Implement payload parsing**

In `ChangedItemKeyParser.cs`, replace the Customization branch of `Parse`:

```csharp
        if (key.StartsWith(CustomizationPrefix, StringComparison.Ordinal))
            return ParseCustomization(key);
```

and add below `Parse`:

```csharp
    private static readonly string[] Genders = ["Female", "Male"];

    // Payload grammar per spec: "{Race} {Gender} {BodyPart}[ (Subtype)][ Number]",
    // or "Player {BodyPart}" (no race/gender), or the literal "Unknown".
    // Race may be multi-word ("Au Ra"), so the gender token is the anchor.
    private static ChangedItemKey ParseCustomization(string key)
    {
        var payload = key[CustomizationPrefix.Length..];
        var tokens = payload.Split(' ');

        string? race = null, gender = null;
        string[] bodyTokens;

        var genderIndex = Array.FindIndex(tokens, t => Genders.Contains(t, StringComparer.Ordinal));
        if (genderIndex > 0)
        {
            race = string.Join(' ', tokens[..genderIndex]);
            gender = tokens[genderIndex];
            bodyTokens = tokens[(genderIndex + 1)..];
        }
        else if (tokens.Length > 1 && tokens[0] == "Player")
        {
            bodyTokens = tokens[1..];
        }
        else
        {
            bodyTokens = tokens;
        }

        int? number = null;
        if (bodyTokens.Length > 0 && int.TryParse(bodyTokens[^1], out var parsedNumber))
        {
            number = parsedNumber;
            bodyTokens = bodyTokens[..^1];
        }

        string? subtype = null;
        if (bodyTokens.Length > 0 && bodyTokens[^1].StartsWith('(') && bodyTokens[^1].EndsWith(')'))
        {
            subtype = bodyTokens[^1][1..^1];
            bodyTokens = bodyTokens[..^1];
        }

        var bodyPart = bodyTokens.Length > 0 ? string.Join(' ', bodyTokens) : null;

        return new(ChangedItemKeyShape.Customization, key,
            Race: race, Gender: gender, BodyPart: bodyPart, Subtype: subtype, Number: number);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ChangedItemKeyParserTests"`
Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ChangedItemKeyParser.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ChangedItemKeyParserTests.cs
git commit -m "feat(1c): parse Customization payload (race/gender/bodypart/subtype/number)"
```

---

### Task 4: `ModTypeClassifier` — priority-ordered reduction to a category

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs`

**Interfaces:**
- Consumes: `ChangedItemKeyParser.Parse`, `ChangedItemKeyShape` from Tasks 2–3; `ModCategory` from the linked `PenumbraOrganizer.Core.Classification` namespace.
- Produces:
  - `sealed record ClassificationResult(ModCategory? Category, string? SubCategory)` with `static readonly ClassificationResult Unknown` (both fields null)
  - `static ClassificationResult ModTypeClassifier.Classify(IEnumerable<string> changedItemKeys)`
  - `static string ModTypeFolders.GetFolder(ModCategory category, string? subCategory)` — `"Animation and VFX/{sub}"` when a sub-category is set, else the enum name (`"Gear"`, `"Face"`, `"NPC"`, ...).

**Design note (resolves a spec under-specification):** when multiple Customization body parts appear in one mod (very common — nearly every customization mod bundles `Skin Textures` as a side effect), the priority is `Face > Hair > Body > Skin`: most-specific wins, and Skin is the weakest because it co-occurs with everything. This matches how the user's existing library is organized (Akako Face+Hair+Skin bundles live under `Face/`).

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs`. Each case mirrors a real mod from the spike dumps (noted inline):

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
            ["Appointed Gloves", "Archon Throne (Mount)", "Animation"]);

        Assert.Equal(ModCategory.Gear, result.Category);
        Assert.Null(result.SubCategory);
    }

    [Fact] // "Kigu - Face 001": customization keys + one bare item — Gear wins
    public void Classify_GearBeatsCustomization()
    {
        var result = ModTypeClassifier.Classify(
            ["Customization: Lalafell Female Face 1", "Moogle Legs"]);

        Assert.Equal(ModCategory.Gear, result.Category);
    }

    [Fact] // "Yacht_V1.0": Animation + Sound + mount key, no gear — Mount
    public void Classify_PureMountMod_IsMount()
    {
        var result = ModTypeClassifier.Classify(
            ["Ancient Airship (Mount)", "Animation", "Sound"]);

        Assert.Equal(ModCategory.Mount, result.Category);
    }

    [Fact] // "Red-Footed Booby": Battle NPC + Companion pair — Minion
    public void Classify_MinionSuffixes_AreMinion()
    {
        var result = ModTypeClassifier.Classify(
            ["Blue-footed Booby (Battle NPC)", "Blue-footed Booby (Companion)"]);

        Assert.Equal(ModCategory.Minion, result.Category);
    }

    [Fact] // Mount beats Minion when both present and no gear
    public void Classify_MountBeatsMinion()
    {
        var result = ModTypeClassifier.Classify(
            ["Spectral Statice (Mount)", "Ghido (Companion)"]);

        Assert.Equal(ModCategory.Mount, result.Category);
    }

    [Fact] // NPC-only mod (hypothetical isolation of the Smallclothes shape)
    public void Classify_NpcSuffix_IsNpc()
    {
        var result = ModTypeClassifier.Classify(["Smallclothes (NPC, 9903-1, Body)"]);

        Assert.Equal(ModCategory.NPC, result.Category);
    }

    [Fact] // "[Bard Lb3] Pashupata": Action + Animation + Vfx — Battle Animation
    public void Classify_ActionKey_IsBattleAnimation()
    {
        var result = ModTypeClassifier.Classify(
            ["Action: Arrow of Fortitude", "Animation", "Vfx"]);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Battle Animation", result.SubCategory);
    }

    [Fact] // "Toothless Dance": Emote + Sound — Emotes
    public void Classify_EmoteKey_IsEmotes()
    {
        var result = ModTypeClassifier.Classify(["Emote: Bee's Knees", "Sound"]);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Emotes", result.SubCategory);
    }

    [Fact] // Vfx + Animation, no Action/Emote — ambiguous, Other
    public void Classify_VfxAndAnimationTogether_IsOther()
    {
        var result = ModTypeClassifier.Classify(["Animation", "Vfx"]);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Other", result.SubCategory);
    }

    [Fact] // solo Vfx — VFX
    public void Classify_VfxAlone_IsVfx()
    {
        var result = ModTypeClassifier.Classify(["Vfx"]);

        Assert.Equal(ModCategory.VFX, result.Category);
        Assert.Equal("VFX", result.SubCategory);
    }

    [Fact] // "[NX] Thicc Viera Walkin For All F": bare Animation only
    public void Classify_AnimationAlone_IsAnimation()
    {
        var result = ModTypeClassifier.Classify(["Animation"]);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Animation", result.SubCategory);
    }

    [Fact] // "cleaned up phasmascapes": single Housing literal — Furniture
    public void Classify_Housing_IsFurniture()
    {
        var result = ModTypeClassifier.Classify(["Housing"]);

        Assert.Equal(ModCategory.Furniture, result.Category);
    }

    [Fact] // Sound alone — Sound
    public void Classify_SoundAlone_IsSound()
    {
        var result = ModTypeClassifier.Classify(["Sound"]);

        Assert.Equal(ModCategory.Sound, result.Category);
    }

    [Fact] // "Akako's Files 3.1.1": Face+Hair+Skin+Tail body parts — Face wins
    public void Classify_CustomizationFaceBeatsHairBodySkin()
    {
        var result = ModTypeClassifier.Classify(
        [
            "Customization: Miqo'te Female Face 101",
            "Customization: Miqo'te Female Hair 115",
            "Customization: Miqo'te Female Skin Textures",
            "Customization: Miqo'te Female Tail 3",
        ]);

        Assert.Equal(ModCategory.Face, result.Category);
    }

    [Fact] // "tail": Tail + Skin Textures — Body wins over Skin
    public void Classify_CustomizationTailBeatsSkin()
    {
        var result = ModTypeClassifier.Classify(
        [
            "Customization: Miqo'te Female Skin Textures",
            "Customization: Miqo'te Female Tail 3",
        ]);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // "akako skin": skin textures only — Skin
    public void Classify_CustomizationSkinOnly_IsSkin()
    {
        var result = ModTypeClassifier.Classify(
        [
            "Customization: Midlander Female Skin Textures",
            "Customization: Player Skin Textures",
        ]);

        Assert.Equal(ModCategory.Skin, result.Category);
    }

    [Fact] // "Akako's Glowy Eyes": Face + literal Unknown — Unknown key doesn't block Face
    public void Classify_CustomizationUnknownKeyDoesNotBlockOthers()
    {
        var result = ModTypeClassifier.Classify(
        [
            "Customization: Miqo'te Female Face (Iris) 101",
            "Customization: Unknown",
        ]);

        Assert.Equal(ModCategory.Face, result.Category);
    }

    [Fact] // only unrecognizable customization — Unknown
    public void Classify_OnlyUnknownCustomization_IsUnknown()
    {
        var result = ModTypeClassifier.Classify(["Customization: Unknown"]);

        Assert.Equal(ClassificationResult.Unknown, result);
    }

    [Fact] // "higanbana [bibo]": empty key set — Unknown
    public void Classify_EmptyKeys_IsUnknown()
    {
        var result = ModTypeClassifier.Classify([]);

        Assert.Equal(ClassificationResult.Unknown, result);
    }

    [Fact] // Icon: alone (never observed with no companion key) — Unknown, never a guess
    public void Classify_IconAlone_IsUnknown()
    {
        var result = ModTypeClassifier.Classify(["Icon: 42992"]);

        Assert.Equal(ClassificationResult.Unknown, result);
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

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ModTypeClassifierTests"`
Expected: compilation failure — `ModTypeClassifier`, `ClassificationResult`, `ModTypeFolders` do not exist.

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public sealed record ClassificationResult(ModCategory? Category, string? SubCategory)
{
    public static readonly ClassificationResult Unknown = new(null, null);
}

public static class ModTypeFolders
{
    private const string AnimationVfxParent = "Animation and VFX";

    public static string GetFolder(ModCategory category, string? subCategory) =>
        subCategory is null ? category.ToString() : $"{AnimationVfxParent}/{subCategory}";
}

/// <summary>
/// Reduces a mod's full set of GetChangedItems keys to one classification, using the
/// strictly first-match-wins priority order from the Phase 1c spec. Never guesses:
/// anything unrecognized is ClassificationResult.Unknown.
/// </summary>
public static class ModTypeClassifier
{
    public static ClassificationResult Classify(IEnumerable<string> changedItemKeys)
    {
        var keys = changedItemKeys.Select(ChangedItemKeyParser.Parse).ToList();

        // Rule 1: Gear wins unconditionally (compilation packs bundle incidental extras).
        if (keys.Any(k => k.Shape == ChangedItemKeyShape.Gear))
            return new(ModCategory.Gear, null);
        if (keys.Any(k => k.Shape == ChangedItemKeyShape.Mount))
            return new(ModCategory.Mount, null);
        if (keys.Any(k => k.Shape == ChangedItemKeyShape.Minion))
            return new(ModCategory.Minion, null);
        if (keys.Any(k => k.Shape == ChangedItemKeyShape.Npc))
            return new(ModCategory.NPC, null);

        var hasAction = keys.Any(k => k.Shape == ChangedItemKeyShape.Action);
        var hasEmote = keys.Any(k => k.Shape == ChangedItemKeyShape.Emote);
        var hasAnimation = HasLiteral(keys, "Animation");
        var hasVfx = HasLiteral(keys, "Vfx");

        if (hasAction || hasEmote || hasAnimation || hasVfx)
        {
            if (hasAction)
                return new(ModCategory.Animation, "Battle Animation");
            if (hasEmote)
                return new(ModCategory.Animation, "Emotes");
            if (hasVfx && hasAnimation)
                return new(ModCategory.Animation, "Other");
            if (hasVfx)
                return new(ModCategory.VFX, "VFX");
            return new(ModCategory.Animation, "Animation");
        }

        if (HasLiteral(keys, "Housing"))
            return new(ModCategory.Furniture, null);
        if (HasLiteral(keys, "Sound"))
            return new(ModCategory.Sound, null);

        var bodyParts = keys
            .Where(k => k.Shape == ChangedItemKeyShape.Customization && k.BodyPart is not null)
            .Select(k => k.BodyPart!)
            .ToList();
        if (bodyParts.Count > 0)
            return ClassifyCustomization(bodyParts);

        return ClassificationResult.Unknown;
    }

    private static bool HasLiteral(IEnumerable<ChangedItemKey> keys, string literal) =>
        keys.Any(k => k.Shape == ChangedItemKeyShape.CategoryLiteral
                      && string.Equals(k.CategoryLiteral, literal, StringComparison.Ordinal));

    // Face > Hair > Body > Skin: most-specific wins. Nearly every customization mod
    // bundles Skin Textures as a side effect, so Skin is the weakest signal.
    private static ClassificationResult ClassifyCustomization(IReadOnlyList<string> bodyParts)
    {
        var mapped = bodyParts
            .Select(MapBodyPart)
            .Where(c => c is not null)
            .Select(c => c!.Value)
            .ToHashSet();

        if (mapped.Contains(ModCategory.Face))
            return new(ModCategory.Face, null);
        if (mapped.Contains(ModCategory.Hair))
            return new(ModCategory.Hair, null);
        if (mapped.Contains(ModCategory.Body))
            return new(ModCategory.Body, null);
        if (mapped.Contains(ModCategory.Skin))
            return new(ModCategory.Skin, null);

        return ClassificationResult.Unknown;
    }

    private static ModCategory? MapBodyPart(string bodyPart)
    {
        if (bodyPart == "Face")
            return ModCategory.Face;
        if (bodyPart == "Hair")
            return ModCategory.Hair;
        if (bodyPart.Contains("Skin", StringComparison.Ordinal))
            return ModCategory.Skin;
        if (bodyPart is "Body" or "Tail" or "Ears")
            return ModCategory.Body;
        return null; // includes the literal "Unknown" — never a guess
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ModTypeClassifierTests"`
Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs
git commit -m "feat(1c): add priority-ordered mod-type classifier with plugin-local sub-categories"
```

---

### Task 5: `OrganizerModRow` category fields + `OrganizerState.SortByModType`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`

**Interfaces:**
- Consumes: `ModTypeFolders.GetFolder(ModCategory, string?)` from Task 4; `ModCategory` from linked Core.
- Produces:
  - `OrganizerModRow.Category` (`ModCategory?`, init) and `OrganizerModRow.SubCategory` (`string?`, init)
  - `int OrganizerState.SortByModType()` — mirrors `SortByCreator`: sets `ProposedPath = "{folder}/{Name}"` for unprotected, categorized rows; skips protected rows and Unknown (null-Category) rows; returns the count of rows it moved.

- [ ] **Step 1: Write the failing tests**

Append to `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`. The file's `MakeRow` helper doesn't take a category, so these tests build rows inline (add the two usings at the top of the file if not present: `using PenumbraOrganizer.Core.Classification;`):

```csharp
    private static OrganizerModRow MakeCategorizedRow(
        string id, string name, ModCategory? category, string? subCategory = null, bool isProtected = false) => new()
    {
        Identifier = id,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = $"Unsorted/{name}",
        ProposedPath = $"Unsorted/{name}",
        HeliosphereManaged = false,
        Category = category,
        SubCategory = subCategory,
        Protected = isProtected,
    };

    [Fact]
    public void SortByModType_GroupsByCategoryFolder()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Cool Jacket", ModCategory.Gear)], new HashSet<string>());

        var count = state.SortByModType();

        Assert.Equal(1, count);
        Assert.Equal("Gear/Cool Jacket", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModType_UsesSubCategoryAsSecondLevel()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [MakeCategorizedRow("a", "Cool Dance", ModCategory.Animation, "Emotes")],
            new HashSet<string>());

        state.SortByModType();

        Assert.Equal("Animation and VFX/Emotes/Cool Dance", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModType_SkipsUnknownCategory()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeCategorizedRow("a", "Mystery Mod", category: null)], new HashSet<string>());

        var count = state.SortByModType();

        Assert.Equal(0, count);
        Assert.Equal("Unsorted/Mystery Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void SortByModType_SkipsProtectedMods()
    {
        var state = new OrganizerState();
        var row = MakeCategorizedRow("a", "Guarded Mod", ModCategory.Gear);
        state.LoadScan([row], new HashSet<string> { "a" });

        var count = state.SortByModType();

        Assert.Equal(0, count);
        Assert.Equal("Unsorted/Guarded Mod", state.Mods.Single().ProposedPath);
    }
```

Note: `LoadScan` resets `Protected` from its own inputs, so the protected test passes the id through `previouslyProtected` rather than relying on the row's own flag.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizerStateTests"`
Expected: compilation failure — `Category`/`SubCategory` don't exist on `OrganizerModRow`, `SortByModType` doesn't exist.

- [ ] **Step 3: Implement**

`PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs` — add the two properties (and the using):

```csharp
using PenumbraOrganizer.Core.Classification;

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
}
```

`PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` — add below `SortByCreator` (and add `using PenumbraOrganizer.Plugin.Organizer.Classification;` at the top):

```csharp
    public int SortByModType()
    {
        var count = 0;
        foreach (var row in _mods.Values.Where(m => !m.Protected && m.Category is not null))
        {
            var folder = ModTypeFolders.GetFolder(row.Category!.Value, row.SubCategory);
            row.ProposedPath = $"{folder}/{row.Name}";
            count++;
        }

        return count;
    }
```

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: all tests PASS (23 pre-existing + all new ones).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat(1c): add SortByModType strategy over classified rows"
```

---

### Task 6: Wire classification into scan + "By Mod Type" button + docs

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` (the `RunScan` method)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` (the `DrawSortTab` method)
- Modify: `docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md`

**Interfaces:**
- Consumes: `ModTypeClassifier.Classify(IEnumerable<string>)` and `ClassificationResult` from Task 4; `OrganizerModRow.Category`/`SubCategory` and `OrganizerState.SortByModType()` from Task 5; `Penumbra.Api.IpcSubscribers.GetChangedItemAdapterDictionary` (constructor takes `IDalamudPluginInterface`; `Invoke()` returns `IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>` keyed by mod identifier — a plain dictionary, NOT disposable; confirmed against Penumbra.Api 5.15.1).
- Produces: scan populates categories; Sort tab gains a "By Mod Type" button. This is the only task with no unit test — the IPC surface is only verifiable in-game.

- [ ] **Step 1: Extend `Plugin.RunScan`**

In `PenumbraOrganizer.Plugin/Plugin.cs`, add the using at the top:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;
```

Replace the body of `RunScan()`:

```csharp
    public void RunScan()
    {
        // One bulk call for all mods' changed items (Approach B in the Phase 1c spec).
        // Plain dictionary, not disposable. If Penumbra is unavailable this throws and
        // surfaces through MainWindow's existing scan error handling.
        var allChangedItems = new Penumbra.Api.IpcSubscribers.GetChangedItemAdapterDictionary(PluginInterface).Invoke();

        using var modList = GetModListAdapterIpc.Invoke();

        var rows = modList.Select(mod =>
        {
            var classification = allChangedItems.TryGetValue(mod.Identifier, out var changedItems)
                ? ModTypeClassifier.Classify(changedItems.Keys)
                : ClassificationResult.Unknown;

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

- [ ] **Step 2: Add the Sort tab button**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, `DrawSortTab()`, extend the existing By Creator line:

```csharp
        if (ImGui.Button("By Creator"))
            _plugin.OrganizerState.SortByCreator(_creatorCanonicalizer.Canonicalize);

        ImGui.SameLine();
        if (ImGui.Button("By Mod Type"))
            _plugin.OrganizerState.SortByModType();
```

- [ ] **Step 3: Build and run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `0 Warning(s) 0 Error(s)`; all tests PASS.

- [ ] **Step 4: Update the handoff doc**

In `docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md`, replace the Phase 1c bullet (the one saying the design is "not yet implemented" and that the SPIKE button "is still in `main`") with:

```markdown
- **Phase 1c (by mod type) is implemented.** Scan classifies every mod from Penumbra's
  changed-items IPC per
  `docs/superpowers/specs/2026-07-13-plugin-organizer-phase1c-classification-design.md`; the Sort
  tab has a "By Mod Type" button. Unknown-category mods are left in place for manual sorting by
  design. The temporary SPIKE dump button has been removed.
```

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin/Windows/MainWindow.cs docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md
git commit -m "feat(1c): classify mods during scan, add By Mod Type sort button"
```

- [ ] **Step 6: In-game verification (user-assisted, cannot be automated)**

Ask the user to load the dev plugin and confirm, in order:
1. **Scan tab** → "Refresh mod list" completes without the inline error banner (the new bulk IPC call works).
2. **Sort tab** → "By Mod Type" → **Review Changes tab** shows proposed paths like `Gear/...`, `Face/...`, `Animation and VFX/Emotes/...`, `Minion/...`.
3. Spot-check known mods against the spec's data: a pure minion mod (e.g. "Gaelicat Minion") lands under `Minion/`; a compilation pack with an incidental mount (e.g. "Carlotta's Outfit" shape) lands under `Gear/`; "cleaned up phasmascapes" lands under `Furniture/`.
4. Protected (Heliosphere) mods show unchanged paths.
5. Mods with no changed items (e.g. "higanbana [bibo]") keep their current path (Unknown → manual).

Expected: all five checks pass. If check 1 fails on a machine whose Penumbra predates the adapter API, that's a Penumbra-version incompatibility to surface, not silently swallow.

---

## Self-review notes

- **Spec coverage:** parser fields incl. Action/Icon preservation (Tasks 2–3); full priority order incl. Gear-wins, sub-categories, Housing/Sound, Customization sub-classification, Unknown-never-guess (Task 4); two-level sort paths + Unknown/protected skipping (Task 5); Approach-B bulk IPC + error surfacing + spike removal (Tasks 1, 6). Locale-invariance is encoded as Ordinal comparisons plus DE/JA test strings.
- **Deliberate decision documented in Task 4:** Face > Hair > Body > Skin priority for multi-part customization mods — the spec defined per-token mapping but not the mod-level tie-break; this matches the user's existing library layout.
- **Type consistency check:** `ClassificationResult(ModCategory? Category, string? SubCategory)`, `ModTypeFolders.GetFolder(ModCategory, string?)`, `OrganizerModRow.Category`/`.SubCategory`, `SortByModType()` — names match across Tasks 4, 5, 6.
- **Deliberate deviation from the spec's record sketch:** the spec's Layer-1 sketch lists an `Unrecognized` shape, but with Gear as the bare-name fallback that state is unreachable — every string matches some shape. The enum here omits it (YAGNI); mod-level "Unknown" lives in `ClassificationResult`, not the key shape.
