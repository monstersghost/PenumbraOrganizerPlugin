namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Plugin.Organizer;

public class WorkbookAdapterSplitJoinTests
{
    [Fact]
    public void SplitPath_RootLevelPath_ReturnsEmptyFolderAndFullLeaf()
    {
        var (folder, leaf) = WorkbookAdapter.SplitPath("Bibo+ Medieval (Penumbra)_1_1_0");

        Assert.Equal("", folder);
        Assert.Equal("Bibo+ Medieval (Penumbra)_1_1_0", leaf);
    }

    [Fact]
    public void SplitPath_NestedPath_SplitsAtLastSeparator()
    {
        var (folder, leaf) = WorkbookAdapter.SplitPath("Tsar/Gear/Bibo+ Medieval (Penumbra)_1_1_0");

        Assert.Equal("Tsar/Gear", folder);
        Assert.Equal("Bibo+ Medieval (Penumbra)_1_1_0", leaf);
    }

    [Fact]
    public void JoinPath_EmptyFolder_ReturnsLeafAlone()
    {
        Assert.Equal("Foo", WorkbookAdapter.JoinPath("", "Foo"));
    }

    [Fact]
    public void JoinPath_NonEmptyFolder_JoinsWithSeparator()
    {
        Assert.Equal("Tsar/Gear/Foo", WorkbookAdapter.JoinPath("Tsar/Gear", "Foo"));
    }

    [Theory]
    [InlineData("Bibo+ Medieval (Penumbra)_1_1_0")]
    [InlineData("Tsar/Gear/Bibo+ Medieval (Penumbra)_1_1_0")]
    [InlineData("Gear/Galateah (2)")]
    public void SplitThenJoin_RoundTrips(string path)
    {
        var (folder, leaf) = WorkbookAdapter.SplitPath(path);

        Assert.Equal(path, WorkbookAdapter.JoinPath(folder, leaf));
    }
}
