using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public enum ClassificationSource { Structural, NameHeuristic, Unknown }

public sealed record ClassificationResult(ModCategory? Category, string? SubCategory, ClassificationSource Source)
{
    public static readonly ClassificationResult Unknown = new(null, null, ClassificationSource.Unknown);
}

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
        (ModCategory.Gear, "Head" or "Top" or "Hands" or "Legs" or "Feet" or "Ears" or "Neck" or "Wrists" or "Rings")
            => $"{ModCategory.Gear}/{subCategory}",
        _ => throw new ArgumentOutOfRangeException(
            nameof(subCategory), subCategory, $"Unsupported subcategory '{subCategory}' for {category}."),
    };
}

/// <summary>
/// Reduces a mod's full set of GetChangedItems keys to one classification, using the
/// strictly first-match-wins priority order from the Phase 1c spec. Never guesses:
/// anything unrecognized is ClassificationResult.Unknown.
/// </summary>
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
        // No playable character can be a child model — a "(Child)" race-variant customization
        // key is exclusively an NPC signal, unconditional like the NPC-suffix rule above.
        if (keys.Any(k => k.Shape == ChangedItemKeyShape.Customization && k.Subtype == "Child"))
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
            return new(ModCategory.Face, null, ClassificationSource.Structural);
        if (mapped.Contains(ModCategory.Hair))
            return new(ModCategory.Hair, null, ClassificationSource.Structural);
        if (mapped.Contains(ModCategory.Body))
            return new(ModCategory.Body, null, ClassificationSource.Structural);
        if (mapped.Contains(ModCategory.Skin))
            return new(ModCategory.Skin, null, ClassificationSource.Structural);

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
