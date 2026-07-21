using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationJournalTests
{
    private static OperationJournal SampleJournal(OperationStage status = OperationStage.Mutating) => new(
        OperationId: Guid.NewGuid(),
        Type: OperationType.Apply,
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        Status: status,
        StartedAt: DateTimeOffset.UtcNow,
        TotalItems: 401,
        CompletedItems: 173,
        LastCompletedIdentifier: "mod-173",
        SnapshotId: Guid.NewGuid(),
        PlanId: Guid.NewGuid(),
        TargetHash: "abc123",
        RecoveryOfOperationId: null,
        UpdatedAt: DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(OperationStage.Preparing, false)]
    [InlineData(OperationStage.Prepared, false)]
    [InlineData(OperationStage.Mutating, false)]
    [InlineData(OperationStage.Refreshing, false)]
    [InlineData(OperationStage.Verifying, false)]
    [InlineData(OperationStage.Completed, true)]
    [InlineData(OperationStage.CompletedWithItemFailures, true)]
    [InlineData(OperationStage.FailedBeforeMutation, true)]
    [InlineData(OperationStage.FailedPartiallyApplied, true)]
    [InlineData(OperationStage.AcceptedCurrentState, true)]
    public void IsTerminal_MatchesDesignedTerminalSet(OperationStage status, bool expectedTerminal)
    {
        var journal = SampleJournal(status);

        Assert.Equal(expectedTerminal, journal.IsTerminal);
    }

    [Fact]
    public void SaveThenTryLoad_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            var journal = SampleJournal();

            OperationJournalCodec.Save(path, journal);
            var loaded = OperationJournalCodec.TryLoad(path, out var result);

            Assert.True(loaded);
            Assert.Equal(journal, result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenFileMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var loaded = OperationJournalCodec.TryLoad(Path.Combine(dir.FullName, "missing.json"), out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenSchemaVersionMismatched()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            var journal = SampleJournal();
            OperationJournalCodec.Save(path, journal);

            var tamperedJson = File.ReadAllText(path)
                .Replace("\"SchemaVersion\":1", "\"SchemaVersion\":999");
            File.WriteAllText(path, tamperedJson);

            var loaded = OperationJournalCodec.TryLoad(path, out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Save_WritesEnumsAsStringsNotNumbers()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            var journal = SampleJournal(OperationStage.Mutating);

            OperationJournalCodec.Save(path, journal);
            var rawJson = File.ReadAllText(path);

            Assert.Contains("\"Status\":\"Mutating\"", rawJson);
            Assert.DoesNotContain("\"Status\":2", rawJson);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class CheckpointPolicyTests
{
    [Fact]
    public void IsDue_TrueWhenItemCountThresholdReached()
    {
        Assert.True(CheckpointPolicy.IsDue(completedSinceLastCheckpoint: 10, elapsedSinceLastCheckpoint: TimeSpan.Zero));
    }

    [Fact]
    public void IsDue_TrueWhenTimeThresholdReached()
    {
        Assert.True(CheckpointPolicy.IsDue(completedSinceLastCheckpoint: 1, elapsedSinceLastCheckpoint: TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void IsDue_FalseWhenNeitherThresholdReached()
    {
        Assert.False(CheckpointPolicy.IsDue(completedSinceLastCheckpoint: 5, elapsedSinceLastCheckpoint: TimeSpan.FromMilliseconds(200)));
    }
}
