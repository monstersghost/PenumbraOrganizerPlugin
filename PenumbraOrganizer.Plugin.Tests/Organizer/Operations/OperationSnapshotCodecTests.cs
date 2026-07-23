using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationSnapshotCodecTests
{
    private static RollbackSnapshot Sample() => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, "a label",
        "auto description", new Dictionary<string, string> { ["mod-a"] = "Weapons/A" });

    [Fact]
    public void Save_ThenTryLoad_RoundTripsExactly()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "snapshot.json");
            var snapshot = Sample();

            OperationSnapshotCodec.Save(path, snapshot);
            var loaded = OperationSnapshotCodec.TryLoad(path, out var result);

            Assert.True(loaded);
            Assert.Equal(snapshot, result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_FileDoesNotExist_ReturnsFalse()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var loaded = OperationSnapshotCodec.TryLoad(Path.Combine(dir.FullName, "missing.json"), out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_CorruptJson_ReturnsFalseRatherThanThrowing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "snapshot.json");
            File.WriteAllText(path, "{ not valid json");

            var loaded = OperationSnapshotCodec.TryLoad(path, out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Save_CreatesTheParentDirectoryIfMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "nested", "bundle", "snapshot.json");

            OperationSnapshotCodec.Save(path, Sample());

            Assert.True(File.Exists(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
