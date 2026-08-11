using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Templates;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The folder-selection expressions behind every sort strategy, in one public place so that
/// OrganizerState's sorts and the template planner's fallback compute destinations with the same
/// code rather than two implementations that can drift.
/// </summary>
/// <remarks>
/// The two split flags are applied in sequence rather than switched over, because they are
/// independent decisions about the same subcategory: composing them is what makes all four
/// combinations reachable without writing four variants of each strategy.
/// </remarks>
public static class SortFolderSelectors
{
    /// <param name="canonicalizeCreator">
    /// Null for callers that cannot produce a creator segment, which then resolves to null and
    /// falls through to the "Review" bucket. The local functions below keep every segment lazy, so
    /// an unused segment is never built.
    /// </param>
    /// <param name="renameFolder">
    /// Applied by the template planner to map a type folder onto the template author's own label
    /// for it. Null for ordinary sorts, which rename nothing.
    /// </param>
    public static (string? Primary, string? Secondary) Select(
        SortStrategy strategy,
        bool splitGear,
        bool splitNpc,
        OrganizerModRow row,
        Func<string, string>? canonicalizeCreator = null,
        Func<string, string>? renameFolder = null)
    {
        string? Creator() =>
            canonicalizeCreator is null ? null : KnownSegment(canonicalizeCreator(row.Author));

        string? Type()
        {
            var sub = row.SubCategory;
            if (!splitGear) sub = FlattenGearSubCategory(row.Category, sub);
            if (!splitNpc) sub = FlattenNpcSubCategory(row.Category, sub);
            return TypeFolder(row.Category, sub, renameFolder);
        }

        return strategy switch
        {
            SortStrategy.CreatorOnly => (Creator(), null),
            SortStrategy.TypeOnly => (Type(), null),
            SortStrategy.TypeThenCreator => (Type(), Creator()),
            SortStrategy.CreatorThenType => (Creator(), Type()),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown sort strategy."),
        };
    }

    /// <summary>
    /// Collapses a (primary, secondary) pair into the single folder a template plan carries.
    /// Deliberately mirrors BuildPath's segment order and its "Review" fallback for an
    /// unclassified row, so a planned folder and a sorted folder agree.
    /// </summary>
    public static string FlattenToFolder(string? primary, string? secondary)
    {
        if (primary is not null && secondary is not null)
            return $"{primary}/{secondary}";
        if (primary is not null)
            return primary;
        if (secondary is not null)
            return secondary;
        return "Review";
    }

    // Gear only: always the flat folder, ignoring any resolved slot subcategory. Every other
    // category keeps its normal subfolder behavior.
    public static string? FlattenGearSubCategory(ModCategory? category, string? subCategory) =>
        category == ModCategory.Gear ? null : subCategory;

    // The NPC mirror of the above. Unlike gear, this had no button before the split checkboxes:
    // NPC mods were always split into NPC/NPCs, NPC/Bosses and NPC/Enemies. Turning it off yields
    // plain "NPC".
    public static string? FlattenNpcSubCategory(ModCategory? category, string? subCategory) =>
        category == ModCategory.NPC ? null : subCategory;

    public static string? TypeFolder(ModCategory? category, string? subCategory, Func<string, string>? renameFolder)
    {
        if (category is null)
            return null;

        var folder = ModTypeFolders.GetFolder(category.Value, subCategory);
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        // Renaming happens strictly after GetFolder, so no template value can reach GetFolder's
        // deliberate throw for a nonsense (category, subcategory) pairing.
        return renameFolder is null ? folder : renameFolder(folder);
    }

    // Single dynamic segments (creator names) mirror Penumbra's FixName so what we propose is
    // what Penumbra will actually store. Multi-level type folders must NOT be FixName'd — that
    // would turn their '/' separator into '\'.
    public static string? KnownSegment(string? segment) =>
        string.IsNullOrWhiteSpace(segment) ? null : PenumbraPathSemantics.FixName(segment);
}
