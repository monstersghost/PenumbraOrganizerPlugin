using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Workbook;

public class WorkbookCatalogTests
{
    [Fact]
    public void BuiltIn_HasSixteenCodesInOrder() =>
        Assert.Equal(Enumerable.Range(1, 16), WorkbookCatalog.BuiltIn.Select(c => c.Code));

    [Theory]
    [InlineData(1, "Gear")]
    [InlineData(2, "Weapon")]
    [InlineData(3, "Face")]
    [InlineData(4, "Hair")]
    [InlineData(5, "Body")]
    [InlineData(6, "Skin")]
    [InlineData(7, "NPC")]
    [InlineData(8, "Minion")]
    [InlineData(9, "Mount")]
    [InlineData(10, "Pet")]
    [InlineData(11, "Ornament")]
    [InlineData(12, "Furniture")]
    [InlineData(13, "Animation and VFX/VFX")]
    [InlineData(14, "Sound")]
    [InlineData(15, "Animation and VFX")]
    [InlineData(16, "Review")]
    public void VersionThreeFolders_FollowTheSortTab(int code, string folder) =>
        Assert.Equal(folder, WorkbookCatalog.FolderFor(code));

    // The catalog and the sorter must agree on where things go, so these tie the table to the sorter.
    [Fact]
    public void TheAnimationAndVfxFolders_MatchWhatSortFilesThemUnder()
    {
        Assert.StartsWith(
            WorkbookCatalog.FolderFor(15) + "/", ModTypeFolders.GetFolder(ModCategory.Animation, "Emotes"));
        Assert.Equal(WorkbookCatalog.FolderFor(13), ModTypeFolders.GetFolder(ModCategory.VFX, "VFX"));
    }

    [Fact]
    public void TheReviewFolder_MatchesTheSortersFallback() =>
        Assert.Equal(WorkbookCatalog.FolderFor(16), SortFolderSelectors.FlattenToFolder(null, null));

    [Fact]
    public void TheSingleSegmentCategories_AreTheCategoryNameTheSorterUses()
    {
        foreach (var code in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14 })
            Assert.Equal(((ModCategory)code).ToString(), WorkbookCatalog.FolderFor(code));
    }

    [Theory]
    [InlineData("gear", true, 1)]
    [InlineData("VFX", true, 13)]
    [InlineData("others", true, 16)]
    [InlineData("Mine", false, 0)]
    public void TryGetByName_IsCaseInsensitive(string name, bool found, int code)
    {
        Assert.Equal(found, WorkbookCatalog.TryGetByName(name, out var category));
        if (found)
            Assert.Equal(code, category.Code);
    }
}
