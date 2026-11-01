namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

public class OrganizerStateScanGenerationTests
{
    private static OrganizerModRow Row(string identifier) => new()
    {
        Identifier = identifier,
        Name = identifier,
        Author = "Tsar",
        CurrentPath = identifier,
        ProposedPath = identifier,
        Category = ModCategory.Gear,
    };

    [Fact]
    public void ScanGeneration_StartsAtZero()
    {
        Assert.Equal(0, new OrganizerState().ScanGeneration);
    }

    [Fact]
    public void ScanGeneration_IncrementsOnEveryScan()
    {
        var state = new OrganizerState();

        state.LoadScan([Row("a")], new HashSet<string>());
        Assert.Equal(1, state.ScanGeneration);

        state.LoadScan([Row("a")], new HashSet<string>());
        Assert.Equal(2, state.ScanGeneration);
    }

    // Sorting stages proposals against the same rows -- it is not a new library, so a plan
    // computed before a sort is still about the rows it described.
    [Fact]
    public void ScanGeneration_IsUnchangedBySorting()
    {
        var state = new OrganizerState();
        state.LoadScan([Row("a")], new HashSet<string>());
        var generation = state.ScanGeneration;

        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: true, name => name);

        Assert.Equal(generation, state.ScanGeneration);
    }
}
