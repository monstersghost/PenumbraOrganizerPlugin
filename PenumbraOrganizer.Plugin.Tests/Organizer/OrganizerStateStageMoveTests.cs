using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateStageMoveTests
{
    private static OrganizerModRow Row(string id, string path, string? name = null) => new()
    {
        Identifier = id, Name = name ?? id, Author = "A", CurrentPath = path, ProposedPath = path,
    };

    private static OrganizerState StateWith(IEnumerable<string>? protectedIds, params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>(protectedIds ?? []));
        return state;
    }

    private static string P(OrganizerState s, string id) => s.Mods.Single(m => m.Identifier == id).ProposedPath;

    [Fact]
    public void Stages_Under_Folder_Keeping_Structure()
    {
        var state = StateWith(null, Row("x", "Gear/Top/X"));
        Assert.Equal(1, state.StageMoveUnder(new[] { "x" }, "Unused"));
        Assert.Equal("Unused/Gear/Top/X", P(state, "x"));
    }

    [Fact]
    public void Protected_Rows_Are_Skipped()
    {
        var state = StateWith(new[] { "x" }, Row("x", "X"), Row("y", "Y"));
        Assert.Equal(1, state.StageMoveUnder(new[] { "x", "y" }, "Unused"));
        Assert.Equal("X", P(state, "x"));
        Assert.Equal("Unused/Y", P(state, "y"));
    }

    [Fact]
    public void Already_Under_Folder_Is_Skipped()
    {
        var state = StateWith(null, Row("x", "Unused/X"), Row("u", "unused/U"));
        Assert.Equal(0, state.StageMoveUnder(new[] { "x", "u" }, "Unused"));
        Assert.Equal("Unused/X", P(state, "x"));
    }

    [Fact]
    public void Other_Proposals_Are_Kept()
    {
        var state = StateWith(null, Row("a", "A"), Row("b", "B"));
        Assert.True(state.AssignManual("a", "Somewhere/A"));
        state.StageMoveUnder(new[] { "b" }, "Unused");
        Assert.Equal("Somewhere/A", P(state, "a"));
        Assert.Equal("Unused/B", P(state, "b"));
    }

    [Fact]
    public void Collision_With_An_Occupied_Path_Is_Renumbered()
    {
        var state = StateWith(null, Row("a", "A"), Row("held", "Unused/A", "A"));
        state.StageMoveUnder(new[] { "a" }, "Unused");
        Assert.Equal("Unused/A (2)", P(state, "a"));
        Assert.Equal("Unused/A", P(state, "held"));
        Assert.False(state.Validate().HasIssues);
    }
}
