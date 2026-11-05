using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class GearFolderLayoutTests
{
    private static readonly string[] Bodies = ["Rue+", "Bibo+", "YAB", "TBSE"];

    private static BodyPriority Order(params string[] first) => BodyPriority.Normalize(first, Bodies);

    [Fact]
    public void Bodies_AreNumberedFromTenByPriority_OnlyThoseWithGear()
    {
        var layout = GearFolderLayout.Build(["Bibo+", "Rue+", "Bibo+", null], Order("Rue+", "Bibo+"));

        Assert.Equal(10, layout.BodyNumbers["Rue+"]);
        Assert.Equal(11, layout.BodyNumbers["Bibo+"]);
        Assert.False(layout.BodyNumbers.ContainsKey("YAB")); // no gear, no number, no gap
        Assert.Equal(2, layout.BodyNumbers.Count);
    }

    [Fact]
    public void AbsentBodiesDoNotLeaveGaps()
    {
        var layout = GearFolderLayout.Build(["TBSE", "Rue+"], Order("Rue+", "Bibo+", "YAB", "TBSE"));

        Assert.Equal(10, layout.BodyNumbers["Rue+"]);
        Assert.Equal(11, layout.BodyNumbers["TBSE"]);
    }

    [Fact]
    public void NoPriority_NumbersInOrdinalLabelOrder()
    {
        var layout = GearFolderLayout.Build(["Rue+", "Bibo+"], priority: null);

        Assert.Equal(10, layout.BodyNumbers["Bibo+"]);
        Assert.Equal(11, layout.BodyNumbers["Rue+"]);
    }

    [Theory]
    [InlineData("Head", "Gear/01. Head")]
    [InlineData("Top", "Gear/02. Top")]
    [InlineData("Hands", "Gear/03. Hands")]
    [InlineData("Legs", "Gear/04. Legs")]
    [InlineData("Feet", "Gear/05. Feet")]
    [InlineData("Ears", "Gear/06. Ears")]
    [InlineData("Neck", "Gear/07. Neck")]
    [InlineData("Wrists", "Gear/08. Wrists")]
    [InlineData("Rings", "Gear/09. Rings")]
    [InlineData(null, "Gear/00. Sets")]
    public void NoBody_SlotFoldersSitDirectlyUnderGear(string? slot, string expected)
    {
        var layout = GearFolderLayout.Build([], null);

        Assert.Equal(expected, layout.Folder(slot, body: null));
    }

    [Fact]
    public void WithBody_TheSameSlotNumbersSitInsideTheBodyFolder()
    {
        var layout = GearFolderLayout.Build(["Rue+"], Order("Rue+"));

        Assert.Equal("Gear/10. Rue+/02. Top", layout.Folder("Top", "Rue+"));
        Assert.Equal("Gear/10. Rue+/00. Sets", layout.Folder(null, "Rue+"));
    }

    [Fact]
    public void ABodyTheLayoutWasNotBuiltFrom_IsABugAndThrows()
    {
        var layout = GearFolderLayout.Build(["Rue+"], Order("Rue+"));

        Assert.Throws<InvalidOperationException>(() => layout.Folder("Top", "Bibo+"));
    }

    [Fact]
    public void ANonsenseSlot_StillFailsFast()
    {
        var layout = GearFolderLayout.Build([], null);

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Folder("Bosses", null));
    }
}
