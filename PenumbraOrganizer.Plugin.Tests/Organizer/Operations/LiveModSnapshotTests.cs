using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class LiveModSnapshotTests
{
    private static LiveMod Mod(string id, string path) => new(id, id, path, HeliosphereManaged: false);

    [Fact]
    public void Build_NoDuplicates_ProducesEmptyDuplicateSetAndAllMods()
    {
        var snapshot = LiveModSnapshotBuilder.Build([Mod("a", "Gear/A"), Mod("b", "Gear/B")]);

        Assert.Empty(snapshot.DuplicateIdentifiers);
        Assert.Equal(2, snapshot.Mods.Count);
        Assert.Equal("Gear/A", snapshot.Mods["a"].FullPath);
    }

    [Fact]
    public void Build_DuplicateIdentifier_FlagsItAndKeepsFirstOccurrence()
    {
        var snapshot = LiveModSnapshotBuilder.Build([Mod("a", "Gear/First"), Mod("a", "Gear/Second")]);

        Assert.Contains("a", snapshot.DuplicateIdentifiers);
        Assert.Single(snapshot.Mods);
        Assert.Equal("Gear/First", snapshot.Mods["a"].FullPath);
    }

    [Fact]
    public void Build_Empty_ProducesEmptySnapshot()
    {
        var snapshot = LiveModSnapshotBuilder.Build([]);

        Assert.Empty(snapshot.Mods);
        Assert.Empty(snapshot.DuplicateIdentifiers);
    }
}
