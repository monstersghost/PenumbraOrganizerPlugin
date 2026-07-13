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
