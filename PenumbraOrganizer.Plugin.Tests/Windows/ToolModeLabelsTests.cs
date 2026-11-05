using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class ToolModeLabelsTests
{
    [Fact]
    public void TheDropdownHasOneLabelPerMode_InTheOrderTheEnumDefines()
    {
        Assert.Equal(
            ["Protected", "Sort inside their folder", "Treat as normal"],
            ToolModeLabels.All);
        Assert.Equal(Enum.GetValues<ToolMode>().Length, ToolModeLabels.All.Count);
    }

    [Fact]
    public void IndexAndMode_RoundTrip_ForEveryMode()
    {
        foreach (var mode in Enum.GetValues<ToolMode>())
            Assert.Equal(mode, ToolModeLabels.FromIndex(ToolModeLabels.IndexOf(mode)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void AnOutOfRangeIndex_ReadsAsProtected(int index) =>
        Assert.Equal(ToolMode.Protected, ToolModeLabels.FromIndex(index));
}
