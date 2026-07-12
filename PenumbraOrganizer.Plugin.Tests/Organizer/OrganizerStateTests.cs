using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateTests
{
    private static OrganizerModRow MakeRow(string id, string name, bool heliosphere = false) => new()
    {
        Identifier = id,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = $"Unsorted/{name}",
        ProposedPath = $"Unsorted/{name}",
        HeliosphereManaged = heliosphere,
    };

    [Fact]
    public void LoadScan_SortsModsByName()
    {
        var state = new OrganizerState();

        state.LoadScan([MakeRow("b", "Zebra"), MakeRow("a", "Apple")], new HashSet<string>());

        Assert.Equal(["Apple", "Zebra"], state.Mods.Select(m => m.Name));
    }

    [Fact]
    public void LoadScan_AppliesPreviouslyProtectedFlag()
    {
        var state = new OrganizerState();

        state.LoadScan([MakeRow("a", "Apple")], new HashSet<string> { "a" });

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void LoadScan_AutoProtectsHeliosphereMods()
    {
        var state = new OrganizerState();

        state.LoadScan([MakeRow("a", "Apple", heliosphere: true)], new HashSet<string>());

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void LoadScan_ResetsProposedPathToCurrentPath()
    {
        var state = new OrganizerState();
        var row = MakeRow("a", "Apple");
        row.ProposedPath = "SomewhereElse";

        state.LoadScan([row], new HashSet<string>());

        Assert.Equal(state.Mods.Single().CurrentPath, state.Mods.Single().ProposedPath);
    }
}
