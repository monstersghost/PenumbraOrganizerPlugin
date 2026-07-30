namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateFolderLabelsTests
{
    [Fact]
    public void Create_EmptyMap_ReturnsPathUnchanged()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string>());

        Assert.Equal("Gear/Head", rename("Gear/Head"));
    }

    [Fact]
    public void Create_ExactMatch_Renames()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Others"] = "_Unsorted" });

        Assert.Equal("_Unsorted", rename("Others"));
    }

    // Prefix rewriting is the point: an author renaming "Gear" must not end up with "Equipment"
    // sitting next to an unrenamed "Gear/Head".
    [Fact]
    public void Create_PrefixMatch_RenamesDescendants()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Gear"] = "Equipment" });

        Assert.Equal("Equipment/Head", rename("Gear/Head"));
    }

    [Fact]
    public void Create_PrefixMatch_RespectsSegmentBoundaries()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Gear"] = "Equipment" });

        Assert.Equal("Gearbox", rename("Gearbox"));
        Assert.Equal("Gearbox/Head", rename("Gearbox/Head"));
    }

    [Fact]
    public void Create_SeveralMatchingKeys_LongestWins()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string>
        {
            ["Gear"] = "Equipment",
            ["Gear/Head"] = "Equipment/Headgear",
        });

        Assert.Equal("Equipment/Headgear", rename("Gear/Head"));
        Assert.Equal("Equipment/Top", rename("Gear/Top"));
    }

    // Applied once, non-recursively: a rename's output is never re-matched, so a map cannot loop.
    [Fact]
    public void Create_RenameOutput_IsNotRematched()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string>
        {
            ["Gear"] = "Weapon",
            ["Weapon"] = "Gear",
        });

        Assert.Equal("Weapon", rename("Gear"));
        Assert.Equal("Gear", rename("Weapon"));
    }

    [Fact]
    public void Create_NoMatchingKey_ReturnsPathUnchanged()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Gear"] = "Equipment" });

        Assert.Equal("Hair", rename("Hair"));
    }
}
