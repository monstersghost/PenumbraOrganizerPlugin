using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class RestoreResultSeedCodecTests
{
    private static RollbackSnapshot SampleSnapshot() => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, "a label",
        "auto description", new Dictionary<string, string> { ["mod-a"] = "Weapons/A" });

    private static RestoreResultSeed Sample() => new(
        SampleSnapshot(), ["mod-b"], ["mod-c"], ["mod-d"]);

    [Fact]
    public void Save_ThenTryLoad_RoundTripsAllFieldsIncludingTheFullTargetSnapshot()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "restore-result-seed.json");
            var seed = Sample();

            OperationRestoreResultSeedCodec.Save(path, seed);
            var loaded = OperationRestoreResultSeedCodec.TryLoad(path, out var result);

            Assert.True(loaded);
            Assert.NotNull(result);
            Assert.Equal(seed.TargetSnapshot.Id, result!.TargetSnapshot.Id);
            Assert.Equal(seed.TargetSnapshot.ModPaths, result.TargetSnapshot.ModPaths);
            Assert.Equal(seed.UnchangedIdentifiers, result.UnchangedIdentifiers);
            Assert.Equal(seed.SkippedUninstalledIdentifiers, result.SkippedUninstalledIdentifiers);
            Assert.Equal(seed.RootRelocatedIdentifiers, result.RootRelocatedIdentifiers);
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
            var loaded = OperationRestoreResultSeedCodec.TryLoad(Path.Combine(dir.FullName, "missing.json"), out var result);

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
            var path = Path.Combine(dir.FullName, "restore-result-seed.json");
            File.WriteAllText(path, "{ not valid json");

            var loaded = OperationRestoreResultSeedCodec.TryLoad(path, out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_NullTargetSnapshot_ReturnsFalse()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "restore-result-seed.json");
            File.WriteAllText(path, """{"TargetSnapshot":null,"UnchangedIdentifiers":[],"SkippedUninstalledIdentifiers":[],"RootRelocatedIdentifiers":[]}""");

            var loaded = OperationRestoreResultSeedCodec.TryLoad(path, out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_NullClassificationList_ReturnsFalse()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "restore-result-seed.json");
            var snapshotJson = System.Text.Json.JsonSerializer.Serialize(SampleSnapshot());
            File.WriteAllText(path,
                $$"""{"TargetSnapshot":{{snapshotJson}},"UnchangedIdentifiers":null,"SkippedUninstalledIdentifiers":[],"RootRelocatedIdentifiers":[]}""");

            var loaded = OperationRestoreResultSeedCodec.TryLoad(path, out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
