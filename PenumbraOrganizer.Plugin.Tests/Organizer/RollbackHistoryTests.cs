using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class RollbackHistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rollback-history-tests").FullName;

    private string HistoryPath => Path.Combine(_dir, "organizer-history.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_MissingFile_ReturnsEmptyList()
    {
        var result = RollbackHistory.Load(HistoryPath);

        Assert.Empty(result);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSnapshotContent()
    {
        var snapshot = new RollbackSnapshot(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "My Label", "5 mods moved",
            new Dictionary<string, string> { ["mod-a"] = "Creators/Alice/Mod A", ["mod-b"] = "Gear/Mod B" });

        RollbackHistory.Save(HistoryPath, [snapshot]);
        var loaded = RollbackHistory.Load(HistoryPath);

        var reloaded = Assert.Single(loaded);
        Assert.Equal(snapshot.Id, reloaded.Id);
        Assert.Equal(snapshot.Label, reloaded.Label);
        Assert.Equal(snapshot.AutoDescription, reloaded.AutoDescription);
        Assert.Equal(snapshot.ModPaths, reloaded.ModPaths);
    }

    [Fact]
    public void Save_WritesAtomically_NoLeftoverTempFile()
    {
        RollbackHistory.Save(HistoryPath, []);

        Assert.True(File.Exists(HistoryPath));
        Assert.False(File.Exists(HistoryPath + ".tmp"));
    }

    [Fact]
    public void CaptureSnapshot_BuildsModPathsFromLiveMods()
    {
        var mods = new List<LiveMod>
        {
            new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: false),
            new("mod-b", "Mod B", "Gear/Mod B", HeliosphereManaged: true),
        };

        var snapshot = RollbackHistory.CaptureSnapshot(mods, label: "Before test", autoDescription: "2 mods moved");

        Assert.Equal("Before test", snapshot.Label);
        Assert.Equal("2 mods moved", snapshot.AutoDescription);
        Assert.Equal(2, snapshot.ModPaths.Count);
        Assert.Equal("Creators/Alice/Mod A", snapshot.ModPaths["mod-a"]);
        Assert.Equal("Gear/Mod B", snapshot.ModPaths["mod-b"]);
    }

    [Fact]
    public void CaptureSnapshot_DuplicateIdentifier_Throws()
    {
        var mods = new List<LiveMod>
        {
            new("mod-a", "Mod A", "Creators/Alice/Mod A", HeliosphereManaged: false),
            new("mod-a", "Mod A Copy", "Gear/Mod A Copy", HeliosphereManaged: false),
        };

        Assert.Throws<ArgumentException>(() => RollbackHistory.CaptureSnapshot(mods, null, "n/a"));
    }

    [Fact]
    public void AppendSnapshot_AddsToExistingHistoryAndPersists()
    {
        var first = RollbackHistory.CaptureSnapshot([], null, "first");
        RollbackHistory.AppendSnapshot(HistoryPath, first);

        var second = RollbackHistory.CaptureSnapshot([], null, "second");
        var result = RollbackHistory.AppendSnapshot(HistoryPath, second);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, RollbackHistory.Load(HistoryPath).Count);
    }

    [Fact]
    public void DeleteSnapshot_RemovesOnlyMatchingIdAndPersists()
    {
        var keep = RollbackHistory.CaptureSnapshot([], null, "keep");
        var remove = RollbackHistory.CaptureSnapshot([], null, "remove");
        RollbackHistory.Save(HistoryPath, [keep, remove]);

        var result = RollbackHistory.DeleteSnapshot(HistoryPath, remove.Id);

        var remaining = Assert.Single(result);
        Assert.Equal(keep.Id, remaining.Id);
        Assert.Single(RollbackHistory.Load(HistoryPath));
    }
}
