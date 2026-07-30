namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class SortFolderSelectorsTests
{
    private static OrganizerModRow Row(ModCategory? category, string? subCategory, string author) => new()
    {
        Identifier = "id",
        Name = "Some Mod",
        Author = author,
        CurrentPath = "Some Mod",
        ProposedPath = "Some Mod",
        Category = category,
        SubCategory = subCategory,
    };

    private static string Same(string value) => value;

    [Fact]
    public void Select_ModTypeDetailed_UsesSubCategory()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.ModTypeDetailed, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Gear/Head", primary);
        Assert.Null(secondary);
    }

    // The flat variants exist to collapse Gear specifically; every other category keeps its
    // subfolder behavior.
    [Fact]
    public void Select_ModType_FlattensGearSubCategory()
    {
        var (primary, _) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.ModType, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Gear", primary);
    }

    [Fact]
    public void Select_ModType_KeepsNonGearSubCategory()
    {
        var (primary, _) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.ModType, Row(ModCategory.NPC, "Bosses", "Tsar"), Same);

        Assert.Equal("NPC/Bosses", primary);
    }

    [Fact]
    public void Select_Creator_UsesCanonicalizedAuthor()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.Creator, Row(ModCategory.Gear, null, "tsar"), _ => "Tsar");

        Assert.Equal("Tsar", primary);
        Assert.Null(secondary);
    }

    [Fact]
    public void Select_TypeThenCreator_OrdersTypeFirst()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.TypeThenCreator, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Gear/Head", primary);
        Assert.Equal("Tsar", secondary);
    }

    [Fact]
    public void Select_CreatorThenTypeFlat_OrdersCreatorFirstAndFlattensGear()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.CreatorThenTypeFlat, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Tsar", primary);
        Assert.Equal("Gear", secondary);
    }

    [Fact]
    public void Select_RenameFolder_AppliesToTypeSegmentOnly()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Gear"] = "Equipment" });

        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.TypeThenCreator, Row(ModCategory.Gear, "Head", "Gear"), Same, rename);

        Assert.Equal("Equipment/Head", primary);
        Assert.Equal("Gear", secondary);   // a creator literally named "Gear" is not renamed
    }

    [Fact]
    public void Select_NullCategory_ReturnsNullPrimary()
    {
        var (primary, _) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.ModType, Row(null, null, "Tsar"), Same);

        Assert.Null(primary);
    }

    // The two type-only strategies have no creator segment, so callers pass no canonicalizer at
    // all rather than a dummy one whose result is discarded.
    [Fact]
    public void Select_TypeOnlyStrategy_NeedsNoCanonicalizer()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.ModTypeDetailed, Row(ModCategory.Gear, "Head", "Tsar"));

        Assert.Equal("Gear/Head", primary);
        Assert.Null(secondary);
    }

    [Theory]
    [InlineData("Gear", "Tsar", "Gear/Tsar")]
    [InlineData("Gear", null, "Gear")]
    [InlineData(null, "Tsar", "Tsar")]
    [InlineData(null, null, "Review")]   // matches BuildPath's own unclassified fallback
    public void FlattenToFolder_MatchesBuildPathSegmentOrder(string? primary, string? secondary, string expected)
    {
        Assert.Equal(expected, SortFolderSelectors.FlattenToFolder(primary, secondary));
    }
}
