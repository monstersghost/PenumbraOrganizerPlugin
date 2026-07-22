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
}
