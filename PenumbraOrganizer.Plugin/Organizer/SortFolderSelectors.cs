using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Templates;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The folder-selection expressions behind OrganizerState's seven sort strategies, extracted so
/// the template planner computes fallback destinations with the same code the sorts use rather
/// than a second implementation that can drift.
///
/// Extraction only — the expressions are unchanged from the inline SortBy* bodies.
/// </summary>
public static class SortFolderSelectors
{
    /// <param name="canonicalizeCreator">
    /// Null for the strategies that do not use a creator segment (ModType, ModTypeDetailed), so
    /// those callers neither supply nor compute one. The local functions below keep every segment
    /// lazy, so an unused segment is never built.
    /// </param>
    public static (string? Primary, string? Secondary) Select(
        TemplateFallbackStrategy strategy,
        OrganizerModRow row,
        Func<string, string>? canonicalizeCreator = null,
        Func<string, string>? renameFolder = null)
    {
        string? Creator() =>
            canonicalizeCreator is null ? null : KnownSegment(canonicalizeCreator(row.Author));

        string? Detailed() => TypeFolder(row.Category, row.SubCategory, renameFolder);

        string? Flat() =>
            TypeFolder(row.Category, FlattenGearSubCategory(row.Category, row.SubCategory), renameFolder);

        return strategy switch
        {
            TemplateFallbackStrategy.Creator => (Creator(), null),
            TemplateFallbackStrategy.ModType => (Flat(), null),
            TemplateFallbackStrategy.ModTypeDetailed => (Detailed(), null),
            TemplateFallbackStrategy.TypeThenCreator => (Detailed(), Creator()),
            TemplateFallbackStrategy.TypeThenCreatorFlat => (Flat(), Creator()),
            TemplateFallbackStrategy.CreatorThenType => (Creator(), Detailed()),
            TemplateFallbackStrategy.CreatorThenTypeFlat => (Creator(), Flat()),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown fallback strategy."),
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
