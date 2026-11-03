using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class FolderProtectionTreeTests
{
    [Fact]
    public void Build_NestsChildrenUnderTheirParent()
    {
        var roots = FolderProtectionTree.Build(["haloween", "haloween/animation"]);

        var haloween = Assert.Single(roots);
        Assert.Equal("haloween", haloween.Path);
        Assert.Equal("haloween", haloween.Name);
        var animation = Assert.Single(haloween.Children);
        Assert.Equal("haloween/animation", animation.Path);
        Assert.Equal("animation", animation.Name);
    }

    [Fact]
    public void Build_FillsInIntermediateSegmentsThatWereNotListed()
    {
        // "hair" may be absent from the input while "hair/01" is present; a tree with a hole in it
        // would leave the subfolder unreachable.
        var roots = FolderProtectionTree.Build(["hair/01/Long"]);

        var hair = Assert.Single(roots);
        Assert.Equal("hair", hair.Path);
        var one = Assert.Single(hair.Children);
        Assert.Equal("hair/01", one.Path);
        Assert.Equal("hair/01/Long", Assert.Single(one.Children).Path);
    }

    [Fact]
    public void Build_DeduplicatesRepeatedPaths()
    {
        var roots = FolderProtectionTree.Build(["Gear", "Gear", "Gear/Feet", "Gear/Feet"]);

        var gear = Assert.Single(roots);
        Assert.Single(gear.Children);
    }

    [Fact]
    public void Build_SortsSiblingsOrdinally()
    {
        var roots = FolderProtectionTree.Build(["hair/10", "hair/02", "hair/01"]);

        Assert.Equal(["hair/01", "hair/02", "hair/10"], Assert.Single(roots).Children.Select(c => c.Path));
    }

    [Fact]
    public void Build_IgnoresBlankAndRootOnlyEntries()
    {
        Assert.Empty(FolderProtectionTree.Build(["", "   ", "/"]));
    }

    [Fact]
    public void DescendantFolderCount_CountsEveryFolderBelow()
    {
        var roots = FolderProtectionTree.Build(["hair/01", "hair/02/Long", "hair/02/Short"]);

        var hair = Assert.Single(roots);
        Assert.Equal(4, hair.DescendantFolderCount);
        Assert.Equal(0, hair.Children.Single(c => c.Path == "hair/01").DescendantFolderCount);
    }

    [Fact]
    public void MatchesFilter_MatchesOwnPathCaseInsensitively()
        => Assert.True(FolderProtectionTree.MatchesFilter(
            Assert.Single(FolderProtectionTree.Build(["haloween"])), "HALO"));

    [Fact]
    public void MatchesFilter_MatchesThroughADescendant()
        => Assert.True(FolderProtectionTree.MatchesFilter(
            Assert.Single(FolderProtectionTree.Build(["haloween/animation/Dance"])), "dance"));

    [Fact]
    public void MatchesFilter_NonMatchingBranch_IsFalse()
        => Assert.False(FolderProtectionTree.MatchesFilter(
            Assert.Single(FolderProtectionTree.Build(["haloween/animation"])), "gear"));

    [Fact]
    public void MatchesFilter_EmptyFilter_MatchesEverything()
        => Assert.True(FolderProtectionTree.MatchesFilter(
            Assert.Single(FolderProtectionTree.Build(["haloween"])), ""));
}
