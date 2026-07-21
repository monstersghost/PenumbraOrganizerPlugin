using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests;

public class ConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_HasVersionOneAndEmptyProtectedSet()
    {
        var config = new Configuration();

        Assert.Equal(1, config.Version);
        Assert.Empty(config.ProtectedModIdentifiers);
    }

    [Fact]
    public void ProtectedModIdentifiers_IsMutable()
    {
        var config = new Configuration();

        config.ProtectedModIdentifiers.Add("hs-Nightingale-1.0");

        Assert.Contains("hs-Nightingale-1.0", config.ProtectedModIdentifiers);
    }

    [Fact]
    public void DefaultConfiguration_HasNullLastOperationSummaries()
    {
        var config = new Configuration();

        Assert.Null(config.LastApply);
        Assert.Null(config.LastRestore);
        Assert.Null(config.LastFolderCleanup);
        Assert.Null(config.LastFolderCleanupRollback);
    }

    [Fact]
    public void Configuration_RoundTripsOperationSummariesThroughJson()
    {
        var config = new Configuration
        {
            LastApply = new ApplyOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:00:00Z"), OperationCompletionStatus.PartiallySucceeded, 3, 1),
            LastRestore = new RestoreOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:05:00Z"), OperationCompletionStatus.Succeeded, 2, 1, 0, 1, 0),
            LastFolderCleanup = new FolderCleanupOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:10:00Z"), FolderCleanupStatus.Success, 5, 0),
            LastFolderCleanupRollback = new FolderCleanupRollbackOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:15:00Z"), FolderRollbackStatus.Restored),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<Configuration>(json);

        Assert.Equal(config.LastApply, roundTripped!.LastApply);
        Assert.Equal(config.LastRestore, roundTripped.LastRestore);
        Assert.Equal(config.LastFolderCleanup, roundTripped.LastFolderCleanup);
        Assert.Equal(config.LastFolderCleanupRollback, roundTripped.LastFolderCleanupRollback);
    }

    [Fact]
    public void Configuration_DeserializesLegacyJsonWithoutSummaryFields()
    {
        const string legacyJson = """{"Version":1,"ProtectedModIdentifiers":["a"],"ProtectedFolderPaths":["Gear"]}""";

        var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>(legacyJson);

        Assert.NotNull(config);
        Assert.Contains("a", config!.ProtectedModIdentifiers);
        Assert.Null(config.LastApply);
        Assert.Null(config.LastRestore);
        Assert.Null(config.LastFolderCleanup);
        Assert.Null(config.LastFolderCleanupRollback);
    }
}
