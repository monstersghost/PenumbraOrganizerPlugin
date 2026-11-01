namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateTreeBuilderTests
{
    private static readonly Dictionary<string, int> NoCounts = new();

    [Fact]
    public void Build_FlatFolders_ProduceRootNodes()
    {
        var tree = TemplateTreeBuilder.Build(["Gear", "Hair"], NoCounts);

        Assert.Equal(["Gear", "Hair"], tree.Select(n => n.Segment));
        Assert.All(tree, node => Assert.Empty(node.Children));
    }

    [Fact]
    public void Build_NestedFolder_CreatesIntermediateParents()
    {
        var tree = TemplateTreeBuilder.Build(["Gear/Head"], NoCounts);

        var gear = Assert.Single(tree);
        Assert.Equal("Gear", gear.Segment);
        var head = Assert.Single(gear.Children);
        Assert.Equal("Head", head.Segment);
        Assert.Equal("Gear/Head", head.FullPath);
    }

    // The author's folder list and the planned destinations are different sets: a template can
    // declare an empty bucket, and a plan can place mods somewhere the list never mentioned.
    [Fact]
    public void Build_CountedFolderNotInFolderList_StillAppears()
    {
        var tree = TemplateTreeBuilder.Build([], new Dictionary<string, int> { ["Gear/Top"] = 3 });

        var gear = Assert.Single(tree);
        var top = Assert.Single(gear.Children);
        Assert.Equal("Gear/Top", top.FullPath);
        Assert.Equal(3, top.DirectCount);
    }

    [Fact]
    public void Build_DeclaredEmptyFolder_AppearsWithZeroCount()
    {
        var tree = TemplateTreeBuilder.Build(["Characters"], NoCounts);

        var node = Assert.Single(tree);
        Assert.Equal(0, node.DirectCount);
        Assert.Equal(0, node.TotalCount);
    }

    // TotalCount is what makes a collapsed parent meaningful.
    [Fact]
    public void Build_TotalCount_RollsUpThroughDescendants()
    {
        var counts = new Dictionary<string, int> { ["Gear"] = 1, ["Gear/Head"] = 2, ["Gear/Top"] = 3 };

        var gear = Assert.Single(TemplateTreeBuilder.Build([], counts));

        Assert.Equal(1, gear.DirectCount);
        Assert.Equal(6, gear.TotalCount);
        Assert.Equal(2, gear.Children.Count);
    }

    [Fact]
    public void Build_IsOrderedBySegmentAtEveryLevel()
    {
        var tree = TemplateTreeBuilder.Build(["Zeta", "Alpha", "Alpha/Zulu", "Alpha/Bravo"], NoCounts);

        Assert.Equal(["Alpha", "Zeta"], tree.Select(n => n.Segment));
        Assert.Equal(["Bravo", "Zulu"], tree[0].Children.Select(n => n.Segment));
    }

    [Fact]
    public void Build_DuplicateFolders_ProduceOneNode()
    {
        var tree = TemplateTreeBuilder.Build(["Gear", "Gear"], NoCounts);

        Assert.Single(tree);
    }

    [Fact]
    public void Build_NoInput_ReturnsEmpty()
    {
        Assert.Empty(TemplateTreeBuilder.Build([], NoCounts));
    }
}
