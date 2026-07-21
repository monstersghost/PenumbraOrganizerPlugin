# Body-slot placeholder classification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `Smallclothes` and the five Emperor's New Clothes body-slot items always classify as
`ModCategory.Body`, unconditionally — ahead of every other classification rule (real Gear, Mount,
Minion, NPC, Customization) — instead of falling into `ModTypeClassifier`'s Gear catch-all.

**Architecture:** A new `KnownEquipmentPlaceholders` lookup table (`Dictionary<string,
ModCategory>`, six literal entries, all mapping to `Body` today) plus a new Rule 0 check in
`ModTypeClassifier.Classify`, checked before the existing Rule 1 ("Gear wins unconditionally"). Pure
in-memory classification logic — no IPC, no file I/O, no UI changes.

**Tech Stack:** C# / .NET (Dalamud plugin, `net10.0-windows7.0`), xUnit tests.

**Spec:** `docs/superpowers/specs/2026-07-15-plugin-organizer-body-slot-placeholder-classification-design.md`
— read it before starting; it contains the full rationale (why these six literals, why the override
is unconditional even over real Gear/NPC, why NPC classification itself is explicitly out of scope).

## Global Constraints

- Test command: `dotnet test PenumbraOrganizer.Plugin.Tests` (full). 171 tests pass before this plan
  starts; ends with the full suite green at 182 (171 + 11 new — 7 new test methods, but the
  5-`InlineData` `Theory` contributes 5 individual test results, not 1).
- Build command: `dotnet build PenumbraOrganizer.Plugin.sln -c Debug` — must stay at 0 warnings, 0
  errors.
- Commit trailer (repo convention): `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
- All string matching for the new table: `StringComparer.Ordinal` (matches every other classifier
  literal comparison in this codebase — see `ChangedItemKeyParser`'s existing `CategoryLiterals`
  check).
- No `docs/ROADMAP.md` entry needed — confirmed in the spec's Non-goals as a same-phase
  classification refinement, not a scope boundary crossing.

---

### Task 1: `KnownEquipmentPlaceholders` table + Rule 0 in `ModTypeClassifier`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs:23-31`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs`

**Interfaces:**
- Consumes: existing `ChangedItemKeyParser.Parse` output (`ChangedItemKey` records with `Shape` and
  `ItemName`), existing `ModCategory` enum (`PenumbraOrganizer.Core.Classification`), existing
  `ClassificationResult` record.
- Produces: no new public surface — `ModTypeClassifier.Classify(IEnumerable<string>)`'s existing
  signature and return type (`ClassificationResult`) are unchanged; only its internal rule order and
  the addition of the private `KnownEquipmentPlaceholders` field.

- [ ] **Step 1: Write the failing tests**

Open `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs` and add
these seven `[Fact]` methods anywhere inside the `ModTypeClassifierTests` class (e.g. right after
`Classify_GearBeatsCustomization`, since they're a direct counterpoint to it):

```csharp
    [Fact] // Bibo+-style body mesh mod: bare Smallclothes item — Body, not Gear
    public void Classify_SmallclothesAlone_IsBody()
    {
        var result = ModTypeClassifier.Classify(["Smallclothes"]);

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
        var result = ModTypeClassifier.Classify([itemName]);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + real named Gear together — Body still wins (absolute override)
    public void Classify_SmallclothesBeatsRealGear()
    {
        var result = ModTypeClassifier.Classify(["Smallclothes", "Appointed Gloves"]);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + Face customization — Body still wins, not just a soft merge
    public void Classify_SmallclothesBeatsFaceCustomization()
    {
        var result = ModTypeClassifier.Classify(
            ["Smallclothes", "Customization: Miqo'te Female Face 101"]);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + a Mount key — Body still wins
    public void Classify_SmallclothesBeatsMount()
    {
        var result = ModTypeClassifier.Classify(["Smallclothes", "Archon Throne (Mount)"]);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + an NPC-suffixed key — Body wins; accepted trade-off, NPC is deferred
    public void Classify_SmallclothesBeatsNpcSuffix()
    {
        var result = ModTypeClassifier.Classify(
            ["Smallclothes", "Smallclothes (NPC, 9903-1, Legs)"]);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Excluded ENC accessory literal, deliberately not in the table — stays ordinary Gear
    public void Classify_EmperorsNewClothesAccessory_IsStillGear()
    {
        var result = ModTypeClassifier.Classify(["The Emperor's New Earrings"]);

        Assert.Equal(ModCategory.Gear, result.Category);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ModTypeClassifierTests"`
Expected: the six new placeholder-related tests FAIL (`Smallclothes`/`The Emperor's New ...` items
currently classify as `Gear`, not `Body`, since they fall into today's catch-all). The last test,
`Classify_EmperorsNewClothesAccessory_IsStillGear`, PASSES already (nothing to change there — it's
a baseline/regression guard, included now so Step 4 shows exactly which ones flipped).

- [ ] **Step 3: Write the implementation**

Replace lines 23-31 of `PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs`
(from `public static class ModTypeClassifier` through the existing Rule 1 `return new(ModCategory.Gear, null);`)
with:

```csharp
public static class ModTypeClassifier
{
    // Real, named GetChangedItems entries that are body-slot placeholders, not actual equipment —
    // Smallclothes is FFXIV's single bare-body item (covers Body/Hands/Legs/Feet as one
    // conceptual item, unlike a real equipment set); the five Emperor's New Clothes pieces are its
    // per-slot equivalent. Confirmed against Penumbra's own item-association browser and Changed
    // Items tab — never guessed. Every entry maps to Body today; a future entry (e.g. a Skin case)
    // is a one-line addition here, not a new rule.
    private static readonly Dictionary<string, ModCategory> KnownEquipmentPlaceholders =
        new(StringComparer.Ordinal)
        {
            ["Smallclothes"] = ModCategory.Body,
            ["The Emperor's New Hat"] = ModCategory.Body,
            ["The Emperor's New Robe"] = ModCategory.Body,
            ["The Emperor's New Gloves"] = ModCategory.Body,
            ["The Emperor's New Breeches"] = ModCategory.Body,
            ["The Emperor's New Boots"] = ModCategory.Body,
        };

    public static ClassificationResult Classify(IEnumerable<string> changedItemKeys)
    {
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
                return new(placeholderCategory, null);
        }

        // Rule 1: Gear wins unconditionally (compilation packs bundle incidental extras).
        if (keys.Any(k => k.Shape == ChangedItemKeyShape.Gear))
            return new(ModCategory.Gear, null);
```

Leave everything after this point in the file (Rule 2 onward: Mount, Minion, NPC, Action/Emote/
Animation/VFX, Housing, Sound, Customization body-part priority, and the private helper methods)
completely unchanged.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ModTypeClassifierTests"`
Expected: all tests in the class PASS — the existing ones (unaffected, confirming no regression:
`Classify_GearBeatsIncidentalMount`, `Classify_GearBeatsCustomization`, `Classify_NpcSuffix_IsNpc`,
etc.) plus the 11 new test results from Step 1 (7 methods; the 5-case `Theory` counts as 5).

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: 182 tests pass (171 existing + 11 new), 0 failures.
Run: `dotnet build PenumbraOrganizer.Plugin.sln -c Debug`
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModTypeClassifierTests.cs
git commit -m "feat: classify Smallclothes/Emperor's New Clothes body-slot items as Body"
```

---

## Self-review notes (plan vs. spec)

- Spec coverage: `KnownEquipmentPlaceholders` table (Architecture) → Task 1 Step 3. Rule 0 ahead of
  every existing rule (Architecture, "Explicit trade-off") → Task 1 Step 3's `foreach` placement,
  before Rule 1. All six literals, both directions of the exclusion list (ENC accessories NOT
  added) → Task 1 Steps 1 and 3. Testing section's exact case list (six placeholders alone, beats
  real Gear, beats Face, beats Mount, beats NPC, excluded-accessory baseline) → Task 1 Step 1, one
  test per case. "No `ROADMAP.md` entry" (Non-goals) → not present anywhere in this plan.
- Placeholder scan: no TBD/TODO; every step shows complete, runnable code.
- Type consistency: `ModCategory`, `ClassificationResult`, `ChangedItemKeyShape.Gear`,
  `ChangedItemKey.ItemName` all match the existing file's names exactly (verified by reading the
  current source, not assumed).
- Single task, not multiple: this is one cohesive change to one method plus its one test file — no
  natural seam a reviewer could accept/reject independently, so it doesn't warrant further
  decomposition per the Task Right-Sizing rule.
