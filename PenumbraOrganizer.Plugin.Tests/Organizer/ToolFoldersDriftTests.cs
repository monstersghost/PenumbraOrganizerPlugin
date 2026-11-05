using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

/// <summary>
/// ToolFolders keeps its own list of the sorter's top-level folders so "sort inside their folder"
/// never produces Gear/Gear/. These pin that list to what ModTypeFolders actually emits, so a new
/// category or a renamed parent folder cannot silently drift out of it.
/// </summary>
public class ToolFoldersDriftTests
{
    [Fact]
    public void EveryTopLevelFolderTheSorterCreates_IsASorterFolder()
    {
        foreach (var category in Enum.GetValues<ModCategory>())
        {
            // Animation and VFX share one parent folder and accept any subcategory; every other
            // category is checked at its own top level.
            var folder = category is ModCategory.Animation or ModCategory.VFX
                ? ModTypeFolders.GetFolder(category, "x")
                : ModTypeFolders.GetFolder(category, null);
            var top = folder.Split('/')[0];

            Assert.True(
                ToolFolders.IsSorterFolder(top),
                $"{category} files under '{top}', which ToolFolders does not treat as a sorter folder");
        }
    }

    [Fact]
    public void TheUnclassifiedBucket_IsASorterFolder() =>
        Assert.True(ToolFolders.IsSorterFolder("Review"));

    [Fact]
    public void AnOrdinaryUserFolder_IsNotASorterFolder()
    {
        Assert.False(ToolFolders.IsSorterFolder("Proteus Upscales"));
        Assert.False(ToolFolders.IsSorterFolder("My Tools"));
    }
}
