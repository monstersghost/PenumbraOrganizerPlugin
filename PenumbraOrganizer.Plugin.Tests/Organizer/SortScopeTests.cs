using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class SortScopeTests
{
    private static OrganizerModRow Row(string id, string path, bool prot = false) =>
        new() { Identifier = id, Name = id, Author = "a", CurrentPath = path, ProposedPath = path, Protected = prot };

    [Fact]
    public void WholeLibrary_IncludesEveryUnprotectedRow()
    {
        Assert.True(SortScope.WholeLibrary.Includes(Row("a", "a")));
        Assert.True(SortScope.WholeLibrary.Includes(Row("b", "A/b")));
        Assert.False(SortScope.WholeLibrary.Includes(Row("c", "c", prot: true)));
    }

    [Fact]
    public void RootOnly_ExcludesRowsInFolders()
    {
        var scope = new SortScope(true);

        Assert.True(scope.Includes(Row("a", "a")));
        Assert.False(scope.Includes(Row("b", "A/b")));
    }

    [Fact]
    public void RootOnly_ExcludesProtectedRootRows() =>
        Assert.False(new SortScope(true).Includes(Row("a", "a", prot: true)));

    [Fact]
    public void IdentifierFilter_Limits()
    {
        var scope = new SortScope(false, new HashSet<string> { "a" });

        Assert.True(scope.Includes(Row("a", "x/a")));
        Assert.False(scope.Includes(Row("b", "x/b")));
    }

    [Fact]
    public void BothFlags_Intersect()
    {
        var scope = new SortScope(true, new HashSet<string> { "a", "b" });

        Assert.True(scope.Includes(Row("a", "a")));
        Assert.False(scope.Includes(Row("b", "F/b")));
        Assert.False(scope.Includes(Row("c", "c")));
    }

    [Fact]
    public void IsAtRoot_FollowsVirtualParent()
    {
        Assert.True(SortScope.IsAtRoot("mod"));
        Assert.False(SortScope.IsAtRoot("F/mod"));
    }
}
