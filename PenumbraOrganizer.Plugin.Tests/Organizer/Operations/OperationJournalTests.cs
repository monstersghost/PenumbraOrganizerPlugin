using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationJournalTests
{
    private static OperationJournal Sample(
        OperationStage stage = OperationStage.Mutating,
        OperationResolution resolution = OperationResolution.None) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: Guid.NewGuid(),
        Type: OperationType.Apply,
        Stage: stage,
        Resolution: resolution,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: DateTimeOffset.UtcNow,
        TotalSteps: 401,
        ProcessedStepCount: 173,
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
    [InlineData(OperationStage.Cancelled, true)]
    public void IsTerminal_FollowsTheStageTerminalSetWhenResolutionIsNone(OperationStage stage, bool expected)
    {
        Assert.Equal(expected, Sample(stage).IsTerminal);
    }

    [Fact]
    public void IsTerminal_TrueWhenResolutionIsSetEvenIfStageIsNonTerminal()
    {
        // A superseded journal keeps an honest frozen Stage (e.g. Mutating) but is terminal via Resolution.
        var journal = Sample(OperationStage.Mutating, OperationResolution.ContinuedByNewOperation);
        Assert.True(journal.IsTerminal);
    }

    [Fact]
    public void SaveThenTryLoad_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            var journal = Sample();

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
    public void Save_WritesStageAsAString()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            OperationJournalCodec.Save(path, Sample(OperationStage.Mutating));

            var json = File.ReadAllText(path);
            Assert.Contains("\"Mutating\"", json);
            Assert.DoesNotContain("\"Stage\":2", json);
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
    public void TryLoad_ReturnsFalseWhenSchemaVersionIsNotCurrent()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            OperationJournalCodec.Save(path, Sample());

            File.WriteAllText(path, File.ReadAllText(path).Replace("\"SchemaVersion\":2", "\"SchemaVersion\":1"));

            var loaded = OperationJournalCodec.TryLoad(path, out var result);
            Assert.False(loaded);
            Assert.Null(result);
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
