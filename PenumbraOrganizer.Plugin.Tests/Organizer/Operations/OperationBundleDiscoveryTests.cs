using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationBundleDiscoveryTests
{
    private static OperationJournal Journal(Guid id, OperationStage stage, Guid? recoveryOf = null) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: id,
        Type: OperationType.Apply,
        Stage: stage,
        Resolution: OperationResolution.None,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: DateTimeOffset.UtcNow,
        TotalSteps: 10,
        ProcessedStepCount: 3,
        LastCompletedIdentifier: null,
        SnapshotId: Guid.NewGuid(),
        PlanId: Guid.NewGuid(),
        TargetHash: "irrelevant",
        RecoveryOfOperationId: recoveryOf,
        UpdatedAt: DateTimeOffset.UtcNow);

    private static void SaveActiveBundle(string root, OperationJournal journal)
    {
        var bundleDir = OperationBundlePaths.BundleDirectory(root, active: true, journal.OperationId);
        OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDir), journal);
    }

    [Fact]
    public void RunStartupDiscovery_NoActiveBundles_EmptyResult()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Empty(result.Journals);
            Assert.Equal(OperationRecoveryGraphStatus.NoRecoveryNeeded, result.Graph.Status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunStartupDiscovery_OneNonTerminalActiveBundle_IsLoadedAndAuthoritative()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            SaveActiveBundle(dir.FullName, Journal(id, OperationStage.Mutating));

            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Single(result.Journals);
            Assert.True(result.Journals.ContainsKey(id));
            Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Graph.Status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunStartupDiscovery_TerminalBundleUnderActive_IsRelocatedToCompletedAndExcluded()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            SaveActiveBundle(dir.FullName, Journal(id, OperationStage.Completed)); // already terminal

            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Empty(result.Journals); // excluded from the non-terminal set
            Assert.False(Directory.Exists(OperationBundlePaths.BundleDirectory(dir.FullName, active: true, id)));
            Assert.True(File.Exists(OperationBundlePaths.JournalPath(
                OperationBundlePaths.BundleDirectory(dir.FullName, active: false, id))));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunStartupDiscovery_CorruptJournalFile_IsSkippedWithoutThrowing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var corruptId = Guid.NewGuid();
            var bundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, corruptId);
            Directory.CreateDirectory(bundleDir);
            File.WriteAllText(OperationBundlePaths.JournalPath(bundleDir), "not valid json");

            var validId = Guid.NewGuid();
            SaveActiveBundle(dir.FullName, Journal(validId, OperationStage.Mutating));

            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Single(result.Journals);
            Assert.True(result.Journals.ContainsKey(validId));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunStartupDiscovery_ParentAndChildBundles_ChildIsAuthoritative()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();
            SaveActiveBundle(dir.FullName, Journal(parentId, OperationStage.Mutating));
            SaveActiveBundle(dir.FullName, Journal(childId, OperationStage.Preparing, recoveryOf: parentId));

            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Equal(2, result.Journals.Count);
            Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Graph.Status);
            Assert.Equal([childId], result.Graph.AuthoritativeOperationIds);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunStartupDiscovery_OnlyTerminalBundlesPresent_NoRecoveryNeeded()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            SaveActiveBundle(dir.FullName, Journal(id, OperationStage.Completed));

            var result = OperationBundleDiscovery.RunStartupDiscovery(dir.FullName);

            Assert.Empty(result.Journals);
            Assert.Equal(OperationRecoveryGraphStatus.NoRecoveryNeeded, result.Graph.Status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static OperationJournal CompletedJournal(Guid id, DateTimeOffset updatedAt) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion, OperationId: id, Type: OperationType.Apply,
        Stage: OperationStage.Completed, Resolution: OperationResolution.None, SuccessorOperationId: null,
        CancellationRequested: false, StartedAt: updatedAt.AddSeconds(-5), TotalSteps: 1, ProcessedStepCount: 1,
        LastCompletedIdentifier: "mod-a", SnapshotId: Guid.NewGuid(), PlanId: Guid.NewGuid(), TargetHash: "irrelevant",
        RecoveryOfOperationId: null, UpdatedAt: updatedAt);

    [Fact]
    public void LoadRecentCompletedJournals_NoCompletedDirectory_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            Assert.Empty(OperationBundleDiscovery.LoadRecentCompletedJournals(dir.FullName, take: 10));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadRecentCompletedJournals_ReturnsNewestFirstRespectingTake()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var ids = new List<Guid>();
            for (var i = 0; i < 5; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                var journal = CompletedJournal(id, now.AddMinutes(-i)); // i=0 is newest
                var bundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, id);
                OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDir), journal);
            }

            var result = OperationBundleDiscovery.LoadRecentCompletedJournals(dir.FullName, take: 3);

            Assert.Equal(3, result.Count);
            Assert.Equal(ids[0], result[0].OperationId);
            Assert.Equal(ids[1], result[1].OperationId);
            Assert.Equal(ids[2], result[2].OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadRecentCompletedJournals_CorruptJournal_ExcludedNotFatal()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var validId = Guid.NewGuid();
            OperationJournalCodec.Save(
                OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: false, validId)),
                CompletedJournal(validId, DateTimeOffset.UtcNow));

            var corruptBundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, Guid.NewGuid());
            Directory.CreateDirectory(corruptBundleDir);
            File.WriteAllText(OperationBundlePaths.JournalPath(corruptBundleDir), "{ not valid json");

            var result = OperationBundleDiscovery.LoadRecentCompletedJournals(dir.FullName, take: 10);

            Assert.Single(result);
            Assert.Equal(validId, result[0].OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void LoadRecentCompletedJournals_TakeZeroOrNegative_ReturnsEmpty(int take)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            OperationJournalCodec.Save(
                OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: false, id)),
                CompletedJournal(id, DateTimeOffset.UtcNow));

            Assert.Empty(OperationBundleDiscovery.LoadRecentCompletedJournals(dir.FullName, take));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadRecentCompletedJournals_NonTerminalJournalPresentUnderCompleted_Excluded()
    {
        // Shouldn't happen given how relocation works, but the read function shouldn't trust the
        // directory it's found in over the journal's own IsTerminal state - same defensive posture
        // LoadNonTerminalActiveJournals already takes toward active/.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var terminalId = Guid.NewGuid();
            OperationJournalCodec.Save(
                OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: false, terminalId)),
                CompletedJournal(terminalId, DateTimeOffset.UtcNow));

            var nonTerminalId = Guid.NewGuid();
            var nonTerminalJournal = CompletedJournal(nonTerminalId, DateTimeOffset.UtcNow) with { Stage = OperationStage.Mutating, Resolution = OperationResolution.None };
            OperationJournalCodec.Save(
                OperationBundlePaths.JournalPath(OperationBundlePaths.BundleDirectory(dir.FullName, active: false, nonTerminalId)),
                nonTerminalJournal);

            var result = OperationBundleDiscovery.LoadRecentCompletedJournals(dir.FullName, take: 10);

            Assert.Single(result);
            Assert.Equal(terminalId, result[0].OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
