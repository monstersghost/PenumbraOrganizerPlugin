using PenumbraOrganizer.Plugin.Organizer.NewMods;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NewMods;

public class NewModPreviewTests
{
    [Fact]
    public void Build_OneRowPerMove_WithFolderPaths()
    {
        var preview = NewModPreview.Build(
            [new StagedMove("Cool Hair", "Cool Hair", "Hair/Hrothgar/Cool Hair")],
            "Will sort 1 of 40 mods: loose in root.", newMods: true);

        var row = Assert.Single(preview.Rows);
        Assert.Equal("Cool Hair", row.Name);
        Assert.Equal("(root)", row.From);
        Assert.Equal("Hair/Hrothgar", row.To);
        Assert.Equal(1, preview.Count);
        Assert.Equal("Will sort 1 of 40 mods: loose in root.", preview.ScopeText);
    }

    [Fact]
    public void Build_SortsRowsByName_IgnoringCase()
    {
        var preview = NewModPreview.Build(
            [
                new StagedMove("zeta", "zeta", "B/zeta"),
                new StagedMove("Alpha", "Alpha", "A/Alpha"),
                new StagedMove("beta", "beta", "C/beta"),
            ],
            null, newMods: true);

        Assert.Equal(["Alpha", "beta", "zeta"], preview.Rows.Select(r => r.Name));
        Assert.Equal(string.Empty, preview.ScopeText);
    }

    [Fact]
    public void Title_CountsAndNamesTheKind()
    {
        Assert.Equal("Move 1 new mod?",
            NewModPreview.Build([new StagedMove("a", "a", "X/a")], null, newMods: true).Title);
        Assert.Equal("Move 2 new mods?",
            NewModPreview.Build([new StagedMove("a", "a", "X/a"), new StagedMove("b", "b", "X/b")], null, true).Title);
        Assert.Equal("Move 2 loose mods?",
            NewModPreview.Build([new StagedMove("a", "a", "X/a"), new StagedMove("b", "b", "X/b")], null, false).Title);
    }

    [Theory]
    [InlineData("Mod", "Mod", "(root)")]
    [InlineData("A/B/Mod", "Mod", "A/B")]
    [InlineData("A/Mod (2)", "Mod", "A")] // a disambiguated leaf still drops to its folder
    [InlineData("A/Some/Name", "Some/Name", "A")] // a name containing a slash is not split
    [InlineData("", "Mod", "(root)")]
    public void FolderOf(string path, string name, string expected)
    {
        Assert.Equal(expected, NewModPreview.FolderOf(path, name));
    }

    [Fact]
    public void Build_Empty_HasNoRows()
    {
        var preview = NewModPreview.Build([], "x", newMods: false);
        Assert.Empty(preview.Rows);
        Assert.Equal("Move 0 loose mods?", preview.Title);
    }

    [Fact]
    public void Text_IsAscii()
    {
        var preview = NewModPreview.Build([new StagedMove("a", "a", "X/a")], "scope", true);
        foreach (var line in new[] { preview.Title, NewModPreview.RootLabel })
            Assert.All(line, c => Assert.True(c < 128));
    }
}
