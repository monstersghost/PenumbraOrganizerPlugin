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

    /// <summary>
    /// The folder a mod belongs in: its category, then the body it is for, then its subcategory.
    /// </summary>
    /// <param name="bodyType">
    /// The body replacer this mod is for, or null for none. Valid only for Gear and Body - the two
    /// categories a body type means anything for - and rejected for any other, which keeps a
    /// classifier bug from inventing an "Animation/Bibo+" folder.
    /// </param>
    /// <remarks>
    /// The body segment sits ABOVE the subcategory, so one body's gear gathers in one place
    /// (Gear/Bibo+/Top, Gear/Bibo+/Legs) rather than scattering across the nine slot folders. That
    /// is the whole point of the feature: after switching bodies you need to find what still fits.
    /// <para>
    /// Both categories that accept one are single-segment folders named after the category itself,
    /// so inserting the body after that segment cannot disturb a multi-level folder the way it
    /// would for Animation's "Animation and VFX/..." parent.
    /// </para>
    /// </remarks>
    public static string GetFolder(ModCategory category, string? subCategory, string? bodyType)
    {
        var withoutBody = GetFolder(category, subCategory);
        if (bodyType is null)
            return withoutBody;

        // Shape, not membership - see IsBodyTypeLabel. The category check is the real guard here:
        // a body type is meaningless outside Gear and Body, and silently accepting one would put a
        // body folder under a category that can never have been matched for it.
        if (!IsBodyTypeLabel(bodyType))
            throw new ArgumentOutOfRangeException(
                nameof(bodyType), bodyType, $"Malformed body type '{bodyType}'.");
        if (category is not (ModCategory.Gear or ModCategory.Body))
            throw new ArgumentOutOfRangeException(
                nameof(bodyType), bodyType, $"A body type is not meaningful for {category}.");

        var categorySegment = category.ToString();
        var tail = withoutBody.Length > categorySegment.Length
            ? withoutBody[(categorySegment.Length + 1)..]
            : null;

        return tail is null
            ? $"{categorySegment}/{bodyType}"
            : $"{categorySegment}/{bodyType}/{tail}";
    }

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
        // Hair cannot be enumerated the way the nine gear slots and three NPC kinds are - there are
        // several hundred vanilla hair numbers. The shape is validated instead, which keeps the
        // fail-fast intent above: Hair + "Bosses" still throws.
        (ModCategory.Hair, _) when IsHairNumber(subCategory) => $"{ModCategory.Hair}/{subCategory}",
        _ => throw new ArgumentOutOfRangeException(
            nameof(subCategory), subCategory, $"Unsupported subcategory '{subCategory}' for {category}."),
    };

    // One to four ASCII digits, no leading zero - that is, 1..9999 written plainly.
    //
    // This agrees with EnrichHairSubCategory because ModHairFileReader FILTERS its numbers to
    // 1..9999, not because formatting an int guarantees anything: ToString gives you a well-formed
    // decimal string, and "0" and "10000" are both well-formed and both rejected here. The range
    // check in the reader is what makes the two sides line up, and this guard is what turns a
    // future producer that skips it into a loud failure rather than a "Hair/0115" folder sitting
    // beside an existing "Hair/115" one.
    private static bool IsHairNumber(string? subCategory) =>
        subCategory is { Length: >= 1 and <= 4 }
        && subCategory[0] is >= '1' and <= '9'
        && subCategory.All(char.IsAsciiDigit);

    // A body label as the loaded list spells it: "Bibo+", "Tight&Firm", "Re[de]fined Lalafell".
    //
    // A SHAPE check, not a membership check. The set of body replacers is fixed by what the
    // community releases, not by the game, and the list holding it is loaded at runtime and
    // extensible by the user (BodyTypeListStore.UserFileName), so enumerating it here would go
    // stale every time a new body ships. This cannot tell a real body from any other word.
    //
    // Membership is guaranteed upstream instead: the only thing that ever sets a row's BodyType is
    // a match against the loaded list, and BodyTypeListStaticContentTests checks every shipped
    // label against this method.
    //
    // The separator rejection is the load-bearing half. A label containing '/' would silently
    // nest a folder under Body and break the flat-folder guarantee the Sort tab promises; the
    // codec drops such a label on parse, and this is the second guard behind it.
    //
    // The permitted punctuation is exactly what real body names use - '+' (Bibo+), '&'
    // (Tight&Firm), '-' (Rue+ / hyphenated variants) and square brackets (RE[DE]FINED) - and all
    // of them survive PenumbraPathSemantics.FixName unchanged, which matters because a type folder
    // is deliberately never FixName'd: if FixName rewrote one of these, the folder we propose would
    // not be the folder Penumbra stores. FixName only trims and turns '/' into '\\', so the one
    // thing that must stay rejected is a path separator.
    private static bool IsBodyTypeLabel(string? subCategory) =>
        subCategory is { Length: >= 1 and <= 40 }
        && subCategory.Trim() == subCategory
        && !subCategory.Contains('/')
        && !subCategory.Contains('\\')
        && subCategory.All(c => char.IsLetterOrDigit(c)
                                || c is ' ' or '+' or '-' or '&' or '\'' or '.' or '[' or ']');
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
    // which ScanProcessor.Process gates on Category == Gear (see ScanProcessor.cs).
    public static ClassificationResult EnrichGearSubCategory(
        ClassificationResult baseResult, IReadOnlySet<EquipmentSlot>? equipmentSlots)
    {
        if (baseResult.Category != ModCategory.Gear || equipmentSlots is null || equipmentSlots.Count != 1)
            return baseResult; // not Gear, read failed, no evidence, or ambiguous (>1 slot) — untouched
        return baseResult with { SubCategory = EquipmentSlotMapper.FolderName(equipmentSlots.Single()) };
    }

    // The hair mirror of EnrichGearSubCategory, with the same guard shape and the same caller
    // contract: a second pass the caller runs only when Classify already returned Category: Hair,
    // never a new top-level classification path. Disk I/O happens only where the caller chooses to
    // call this, which ScanProcessor gates on Category == Hair.
    public static ClassificationResult EnrichHairSubCategory(
        ClassificationResult baseResult, IReadOnlySet<int>? hairNumbers)
    {
        if (baseResult.Category != ModCategory.Hair || hairNumbers is null || hairNumbers.Count != 1)
            return baseResult; // not Hair, read failed, no evidence, or ambiguous (>1 number) - untouched

        // Invariant, and unpadded: ModTypeFolders.IsHairNumber rejects a leading zero, so a
        // culture-dependent or padded format here would throw inside the sort rather than quietly
        // creating a second folder for the same hair.
        //
        // The VALUE is in range because ModHairFileReader filtered it to 1..9999. This method does
        // not re-check that: one owner for the domain, and the reader is it. If this ever takes
        // numbers from somewhere else, that source must filter too.
        return baseResult with
        {
            SubCategory = hairNumbers.Single().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
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
