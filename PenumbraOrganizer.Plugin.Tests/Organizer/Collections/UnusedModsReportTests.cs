using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Collections;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Collections;

public class UnusedModsReportTests
{
    private static OrganizerModRow Row(string id, string path, bool prot = false) => new()
    {
        Identifier = id, Name = id.ToUpperInvariant(), Author = "x", CurrentPath = path,
        ProposedPath = path, Protected = prot,
    };

    [Fact]
    public void Lists_Only_Mods_Not_Used_In_Any_Collection()
    {
        var rows = new[] { Row("a", "a"), Row("b", "b"), Row("c", "c") };
        var report = UnusedModsReport.Build(rows, new HashSet<string> { "b" });
        Assert.Equal(new[] { "a", "c" }, report.Select(e => e.Identifier));
    }

    [Fact]
    public void Matching_Is_Case_Insensitive()
    {
        var report = UnusedModsReport.Build(new[] { Row("Abc", "Abc") }, new HashSet<string> { "abc" });
        Assert.Empty(report);
    }

    [Fact]
    public void Protected_Rows_Are_Flagged_Will_Not_Move()
    {
        var report = UnusedModsReport.Build(new[] { Row("a", "a", prot: true), Row("b", "b") },
            new HashSet<string>());
        Assert.True(report.Single(e => e.Identifier == "a").Protected);
        Assert.False(report.Single(e => e.Identifier == "a").WillMove);
        Assert.True(report.Single(e => e.Identifier == "b").WillMove);
        Assert.Equal("protected - will not move", UnusedModsReport.ProtectedNote);
    }

    [Fact]
    public void Sorted_By_Current_Path()
    {
        var report = UnusedModsReport.Build(new[] { Row("z", "B/z"), Row("y", "A/y") }, new HashSet<string>());
        Assert.Equal(new[] { "y", "z" }, report.Select(e => e.Identifier));
    }
}
