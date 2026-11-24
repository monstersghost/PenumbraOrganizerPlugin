using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Windows.Widgets;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The live example path the Sort tab shows for a category: where a typical mod of that kind would go with the
/// category's effective layout, Group by and the race master switch, split into coloured pieces. It mirrors the
/// engine's segment order (creator before or after the type folders, splits in the catalog's folder-depth order,
/// race last) so Recommended mode "never lies"; <c>ExamplePathsTests</c> checks it against a real Sort.
/// </summary>
public static class ExamplePaths
{
    private sealed record Sample(string Creator, string Mod);

    private static readonly Dictionary<ModCategory, Sample> Samples = new()
    {
        [ModCategory.Gear] = new("Ann", "Medieval Dress"),
        [ModCategory.Weapon] = new("Ann", "Gunbreaker Blade"),
        [ModCategory.Face] = new("Ann", "Soft Glow Face"),
        [ModCategory.Hair] = new("Bizu", "Imeris - Solar"),
        [ModCategory.Body] = new("Ai", "Bibo+ Base Install"),
        [ModCategory.Skin] = new("Ann", "Akako Skin"),
        [ModCategory.NPC] = new("Thor", "Proto Carbuncle"),
        [ModCategory.Minion] = new("Ann", "Tiny Carbuncle"),
        [ModCategory.Mount] = new("Ann", "Chocobo Recolour"),
        [ModCategory.Pet] = new("Ann", "Fairy Glow"),
        [ModCategory.Ornament] = new("Ann", "Angel Wings"),
        [ModCategory.Furniture] = new("Ann", "Cosy Cottage Set"),
        [ModCategory.Animation] = new("Ann", "Harvest Dance"),
        [ModCategory.VFX] = new("Ann", "Glow Trails"),
        [ModCategory.Sound] = new("Ann", "Soft Menu Clicks"),
        [ModCategory.Others] = new("Ann", "Unclear Pack"),
    };

    /// <summary>The four categories Recommended mode previews.</summary>
    public static IReadOnlyList<ModCategory> RecommendedPreview { get; } =
        [ModCategory.Gear, ModCategory.Hair, ModCategory.Body, ModCategory.NPC];

    /// <summary>The creator level Group by stands for; null under Creator only, which has no type folders.</summary>
    public static CreatorLevel? InheritedCreator(SortStrategy groupBy) => groupBy switch
    {
        SortStrategy.TypeOnly => CreatorLevel.None,
        SortStrategy.TypeThenCreator => CreatorLevel.AfterType,
        SortStrategy.CreatorThenType => CreatorLevel.BeforeType,
        _ => null,
    };

    /// <summary>
    /// The creator level a category actually sorts with: its own, else Group by's. Null under Creator only, which
    /// ignores every category's level (spec 1, A4).
    /// </summary>
    public static CreatorLevel? EffectiveCreator(CategoryLayout layout, SortStrategy groupBy) =>
        groupBy == SortStrategy.CreatorOnly ? null : layout.Creator ?? InheritedCreator(groupBy);

    /// <summary>The example path's pieces for one category.</summary>
    public static IReadOnlyList<PathSegment> For(
        ModCategory category, CategoryLayout layout, SortStrategy groupBy, bool raceEnabled)
    {
        var sample = Samples[category];
        var creator = new PathSegment(sample.Creator, PathRole.Creator);
        var mod = new PathSegment(sample.Mod, PathRole.Mod);
        if (groupBy == SortStrategy.CreatorOnly)
            return [creator, mod];

        var segments = new List<PathSegment>();
        var level = EffectiveCreator(layout, groupBy);
        if (level == CreatorLevel.BeforeType)
            segments.Add(creator);

        // The folder the Sort writes: a default folder is the engine's own root (spec 1, A9).
        var folder = string.Equals(layout.Folder, LayoutCatalog.DefaultFolder(category), StringComparison.Ordinal)
            ? LayoutCatalog.SortRoot(category)
            : layout.Folder;
        segments.Add(new PathSegment(folder, PathRole.Folder));
        segments.AddRange(SplitSegments(category, layout, raceEnabled));

        if (level == CreatorLevel.AfterType)
            segments.Add(creator);
        segments.Add(mod);
        return segments;
    }

    /// <summary>The pieces joined as the real path, <c>Gear/10. Bibo+/02. Top/Ann/Medieval Dress</c>.</summary>
    public static string Text(IReadOnlyList<PathSegment> segments) => string.Join('/', segments.Select(s => s.Text));

    private static IEnumerable<PathSegment> SplitSegments(ModCategory category, CategoryLayout layout, bool raceEnabled)
    {
        PathSegment Split(string text) => new(text, PathRole.Split);
        var race = raceEnabled && layout.Has(LayoutSplits.Race);

        switch (category)
        {
            case ModCategory.Gear:
                // A dress is a Top made for one body. Both splits together give the numbered layout.
                var body = layout.Has(LayoutSplits.BodyType);
                var slot = layout.Has(LayoutSplits.Slot);
                if (body && slot)
                    return [Split("10. Bibo+"), Split("02. Top")];
                if (body)
                    return [Split("Bibo+")];
                return slot ? [Split("Top")] : [];
            case ModCategory.Hair:
                var hair = new List<PathSegment>();
                if (race)
                    hair.Add(Split("Viera"));
                if (layout.Has(LayoutSplits.HairNumber))
                    hair.Add(Split("115"));
                return hair;
            case ModCategory.Body:
                // A body replacer; the race splits only reach tails and racial scaling, so not this sample.
                return layout.Has(LayoutSplits.PartAndBodyType) ? [Split("10. Bibo+"), Split("00. Replacer")] : [];
            case ModCategory.NPC:
                return layout.Has(LayoutSplits.NpcKind) ? [Split("Bosses")] : [];
            case ModCategory.Face:
                return race ? [Split("Viera")] : [];
            case ModCategory.Skin:
                return race ? [Split("Miqo'te")] : [];
            case ModCategory.Animation:
                // The Emotes folder is the mod's subcategory, there with or without the split.
                return layout.Has(LayoutSplits.Emote)
                    ? [new PathSegment("Emotes", PathRole.Folder), Split("Dance")]
                    : [new PathSegment("Emotes", PathRole.Folder)];
            default:
                return [];
        }
    }
}
