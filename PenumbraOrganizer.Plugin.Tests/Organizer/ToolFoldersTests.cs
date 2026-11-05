using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class ToolFoldersTests
{
    [Fact]
    public void TheModsOwnFolder_BecomesThePrefix() =>
        Assert.Equal("Proteus Upscales/Gear/Rue+/Top/Name",
            ToolFolders.ApplyAnchor("Proteus Upscales/Name", "Gear/Rue+/Top/Name"));

    [Fact]
    public void AModAtTheRoot_IsSortedNormally() =>
        Assert.Equal("Gear/Top/Name", ToolFolders.ApplyAnchor("Name", "Gear/Top/Name"));

    [Theory]
    [InlineData("Gear/Top/Name")]
    [InlineData("gear/top/Name")]
    [InlineData("Body/Name")]
    [InlineData("Hair/Name")]
    [InlineData("NPC/Name")]
    [InlineData("Animation and VFX/Name")]
    [InlineData("Review/Name")]
    public void AnAnchorThatIsOneOfTheSortersOwnFolders_IsSortedNormally(string currentPath) =>
        Assert.Equal("Gear/Top/Name", ToolFolders.ApplyAnchor(currentPath, "Gear/Top/Name"));

    [Fact]
    public void ANormalPathThatAlreadyStartsWithTheAnchor_IsUsedAsItIs()
    {
        // Group by Creator first: the sorted path already begins with the creator folder the mod sits in.
        Assert.Equal("Solona/Gear/Name", ToolFolders.ApplyAnchor("Solona/Name", "Solona/Gear/Name"));
        Assert.Equal("solona/Gear/Name", ToolFolders.ApplyAnchor("Solona/Name", "solona/Gear/Name"));
    }

    [Fact]
    public void OnlyTheFirstSegmentOfTheCurrentPathIsTheAnchor() =>
        Assert.Equal("Tools/Gear/Name", ToolFolders.ApplyAnchor("Tools/Deep/Deeper/Name", "Gear/Name"));

    [Fact]
    public void TheUnclassifiedBucket_IsPrefixedLikeAnyOtherNormalPath() =>
        Assert.Equal("Proteus Upscales/Review/Name", ToolFolders.ApplyAnchor("Proteus Upscales/Name", "Review/Name"));

    [Theory]
    [InlineData("", "Gear/Name")]
    [InlineData("/Name", "Gear/Name")]
    public void AnEmptyOrLeadingSlashCurrentPath_HasNoAnchor(string currentPath, string normal) =>
        Assert.Equal(normal, ToolFolders.ApplyAnchor(currentPath, normal));
}
