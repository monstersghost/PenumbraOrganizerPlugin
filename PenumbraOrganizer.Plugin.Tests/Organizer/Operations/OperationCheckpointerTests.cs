using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationCheckpointerTests
{
    private sealed class FakeClock : IElapsedTimeSource
    {
        private long _now;
        public long GetTimestamp() => _now;
        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
        public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    }

    private static OperationJournal Journal(int processedStepCount) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion, OperationId: Guid.NewGuid(), Type: OperationType.Apply,
        Stage: OperationStage.Mutating, Resolution: OperationResolution.None, SuccessorOperationId: null,
        CancellationRequested: false, StartedAt: DateTimeOffset.UtcNow, TotalSteps: 100,
        ProcessedStepCount: processedStepCount, LastCompletedIdentifier: null, SnapshotId: Guid.NewGuid(),
        PlanId: Guid.NewGuid(), TargetHash: "irrelevant", RecoveryOfOperationId: null, UpdatedAt: DateTimeOffset.UtcNow);

    private static int? PersistedProcessedStepCount(string bundleDirectory) =>
        OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDirectory), out var journal) && journal is not null
            ? journal.ProcessedStepCount
            : null;

    [Fact]
    public void CheckpointIfDue_BelowBothThresholds_DoesNotWrite()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var checkpointer = new OperationCheckpointer(new FakeClock(), dir.FullName);
            checkpointer.CheckpointIfDue(Journal(5)); // below the 10-item threshold, no time elapsed

            Assert.Null(PersistedProcessedStepCount(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckpointIfDue_ItemThresholdReached_Writes()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var checkpointer = new OperationCheckpointer(new FakeClock(), dir.FullName);
            checkpointer.CheckpointIfDue(Journal(10)); // exactly the 10-item threshold

            Assert.Equal(10, PersistedProcessedStepCount(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckpointIfDue_TimeThresholdReached_Writes()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var clock = new FakeClock();
            var checkpointer = new OperationCheckpointer(clock, dir.FullName);
            clock.Advance(TimeSpan.FromMilliseconds(500)); // exactly the time threshold, zero items

            checkpointer.CheckpointIfDue(Journal(1));

            Assert.Equal(1, PersistedProcessedStepCount(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckpointIfDue_Force_AlwaysWritesRegardlessOfThresholds()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var checkpointer = new OperationCheckpointer(new FakeClock(), dir.FullName);
            checkpointer.CheckpointIfDue(Journal(1), force: true); // one item, zero time elapsed - neither threshold met

            Assert.Equal(1, PersistedProcessedStepCount(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckpointIfDue_TwentySevenCallsInABurst_WritesOnlyAtTheTenStepBoundaries()
    {
        // Reproduces exactly the scenario the checkpoint-cadence review finding was about: many
        // single-step calls in one burst (as PathMutationOperation.Advance makes via its injected
        // callback), proving checkpoints land at multiples of the 10-item threshold, not once at
        // the very end of the burst.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var checkpointer = new OperationCheckpointer(new FakeClock(), dir.FullName);

            for (var processed = 1; processed <= 9; processed++)
                checkpointer.CheckpointIfDue(Journal(processed));
            Assert.Null(PersistedProcessedStepCount(dir.FullName)); // nothing written yet, below threshold

            checkpointer.CheckpointIfDue(Journal(10));
            Assert.Equal(10, PersistedProcessedStepCount(dir.FullName)); // first checkpoint at exactly 10

            for (var processed = 11; processed <= 19; processed++)
                checkpointer.CheckpointIfDue(Journal(processed));
            Assert.Equal(10, PersistedProcessedStepCount(dir.FullName)); // unchanged - not due again yet

            checkpointer.CheckpointIfDue(Journal(20));
            Assert.Equal(20, PersistedProcessedStepCount(dir.FullName)); // second checkpoint at 20

            for (var processed = 21; processed <= 27; processed++)
                checkpointer.CheckpointIfDue(Journal(processed));
            Assert.Equal(20, PersistedProcessedStepCount(dir.FullName)); // still unchanged - burst ends at 27, below the next 10-step boundary
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
