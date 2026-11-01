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
    public void Select_TypeOnlyWithGearSplit_UsesSubCategory()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, splitGear: true, splitNpc: true, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Gear/Head", primary);
        Assert.Null(secondary);
    }

    // splitGear collapses Gear specifically; every other category keeps its subfolder behavior.
    [Fact]
    public void Select_TypeOnlyWithoutGearSplit_FlattensGearSubCategory()
    {
        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, splitGear: false, splitNpc: true, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Gear", primary);
    }

    [Fact]
    public void Select_WithoutGearSplit_KeepsNonGearSubCategory()
    {
        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, splitGear: false, splitNpc: true, Row(ModCategory.NPC, "Bosses", "Tsar"), Same);

        Assert.Equal("NPC/Bosses", primary);
    }

    // The whole reason the fallback carries two flags rather than a flattened strategy name: the
    // previous seven-member enum had no way to express this at all, so an imported template always
    // re-split NPC mods no matter what the importer had chosen.
    [Fact]
    public void Select_WithoutNpcSplit_FlattensNpcSubCategory()
    {
        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, splitGear: true, splitNpc: false, Row(ModCategory.NPC, "Bosses", "Tsar"), Same);

        Assert.Equal("NPC", primary);
    }

    [Fact]
    public void Select_WithoutNpcSplit_KeepsNonNpcSubCategory()
    {
        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, splitGear: true, splitNpc: false, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Gear/Head", primary);
    }

    // The two flags are independent, so all four combinations are reachable for one row.
    [Theory]
    [InlineData(true, true, "Gear/Head")]
    [InlineData(false, true, "Gear")]
    [InlineData(true, false, "Gear/Head")]
    [InlineData(false, false, "Gear")]
    public void Select_SplitFlagsAreIndependent(bool splitGear, bool splitNpc, string expected)
    {
        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, splitGear, splitNpc, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal(expected, primary);
    }

    [Fact]
    public void Select_CreatorOnly_UsesCanonicalizedAuthor()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            SortStrategy.CreatorOnly, splitGear: false, splitNpc: true, Row(ModCategory.Gear, null, "tsar"),
            _ => "Tsar");

        Assert.Equal("Tsar", primary);
        Assert.Null(secondary);
    }

    // CreatorOnly never consults the category, so neither flag can change its answer.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Select_CreatorOnly_IgnoresBothSplits(bool splitGear, bool splitNpc)
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            SortStrategy.CreatorOnly, splitGear, splitNpc, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Tsar", primary);
        Assert.Null(secondary);
    }

    [Fact]
    public void Select_TypeThenCreator_OrdersTypeFirst()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            SortStrategy.TypeThenCreator, splitGear: true, splitNpc: true, Row(ModCategory.Gear, "Head", "Tsar"),
            Same);

        Assert.Equal("Gear/Head", primary);
        Assert.Equal("Tsar", secondary);
    }

    [Fact]
    public void Select_CreatorThenType_OrdersCreatorFirstAndHonoursGearSplit()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            SortStrategy.CreatorThenType, splitGear: false, splitNpc: true, Row(ModCategory.Gear, "Head", "Tsar"),
            Same);

        Assert.Equal("Tsar", primary);
        Assert.Equal("Gear", secondary);
    }

    [Fact]
    public void Select_RenameFolder_AppliesToTypeSegmentOnly()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Gear"] = "Equipment" });

        var (primary, secondary) = SortFolderSelectors.Select(
            SortStrategy.TypeThenCreator, splitGear: true, splitNpc: true, Row(ModCategory.Gear, "Head", "Gear"),
            Same, rename);

        Assert.Equal("Equipment/Head", primary);
        Assert.Equal("Gear", secondary);   // a creator literally named "Gear" is not renamed
    }

    [Fact]
    public void Select_NullCategory_ReturnsNullPrimary()
    {
        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, splitGear: false, splitNpc: true, Row(null, null, "Tsar"), Same);

        Assert.Null(primary);
    }

    // A type-only strategy has no creator segment, so callers pass no canonicalizer at all rather
    // than a dummy one whose result is discarded.
    [Fact]
    public void Select_TypeOnlyStrategy_NeedsNoCanonicalizer()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, splitGear: true, splitNpc: true, Row(ModCategory.Gear, "Head", "Tsar"));

        Assert.Equal("Gear/Head", primary);
        Assert.Null(secondary);
    }

    [Fact]
    public void Select_UndefinedStrategy_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SortFolderSelectors.Select(
            (SortStrategy)99, splitGear: true, splitNpc: true, Row(ModCategory.Gear, "Head", "Tsar"), Same));
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
