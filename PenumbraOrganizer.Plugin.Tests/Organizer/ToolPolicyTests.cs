using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class ToolPolicyTests
{
    [Fact]
    public void Default_ProtectsBothTools_AndKeepsTheManagedModProtected()
    {
        var d = ToolPolicy.Default;

        Assert.Equal(ToolMode.Protected, d.Heliosphere);
        Assert.Equal(ToolMode.Protected, d.Proteus);
        Assert.False(d.ProteusManagedAtRoot);
    }

    [Theory]
    [InlineData(0, ToolMode.Protected)]
    [InlineData(1, ToolMode.SortInside)]
    [InlineData(2, ToolMode.Normal)]
    [InlineData(3, ToolMode.Protected)]
    [InlineData(-1, ToolMode.Protected)]
    [InlineData(99, ToolMode.Protected)]
    public void From_ReadsAnUnknownStoredValueAsProtected(int stored, ToolMode expected)
    {
        var p = ToolPolicy.From(stored, stored, managedAtRoot: true);

        Assert.Equal(expected, p.Heliosphere);
        Assert.Equal(expected, p.Proteus);
        Assert.True(p.ProteusManagedAtRoot);
    }
}
