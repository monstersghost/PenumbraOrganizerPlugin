using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationBundleRetentionTests
{
    private static OperationJournal Journal(Guid id, DateTimeOffset updatedAt, Guid? recoveryOf = null) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: id,
        Type: OperationType.Apply,
        Stage: OperationStage.Completed,
        Resolution: OperationResolution.None,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: updatedAt,
        TotalSteps: 10,
        ProcessedStepCount: 10,
        LastCompletedIdentifier: null,
        SnapshotId: Guid.NewGuid(),
        PlanId: Guid.NewGuid(),
        TargetHash: "irrelevant",
        RecoveryOfOperationId: recoveryOf,
        UpdatedAt: updatedAt);

    private static void SaveCompletedBundle(string root, OperationJournal journal)
    {
        var bundleDir = OperationBundlePaths.BundleDirectory(root, active: false, journal.OperationId);
        OperationJournalCodec.Save(OperationBundlePaths.JournalPath(bundleDir), journal);
    }

    private static bool BundleExists(string root, Guid id) =>
        Directory.Exists(OperationBundlePaths.BundleDirectory(root, active: false, id));

    [Fact]
    public void RunRetentionPass_YoungBundle_IsKept()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            SaveCompletedBundle(dir.FullName, Journal(id, now.AddDays(-1)));

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.True(BundleExists(dir.FullName, id));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_OldUnreferencedUnrankedBundle_IsDeleted()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            SaveCompletedBundle(dir.FullName, Journal(id, now.AddDays(-60)));

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.False(BundleExists(dir.FullName, id));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_OldBundleWithinNewestCap_IsKept()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            SaveCompletedBundle(dir.FullName, Journal(id, now.AddDays(-60)));

            // Cap of 1 with only one bundle total means it's within the newest 1, regardless of age.
            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 1, retainAge: TimeSpan.FromDays(30));

            Assert.True(BundleExists(dir.FullName, id));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_OldBundleReferencedByARetainedChild_IsKept()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();
            SaveCompletedBundle(dir.FullName, Journal(parentId, now.AddDays(-60))); // old, would be deleted alone
            SaveCompletedBundle(dir.FullName, Journal(childId, now.AddDays(-1), recoveryOf: parentId)); // young, retained

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.True(BundleExists(dir.FullName, childId));
            Assert.True(BundleExists(dir.FullName, parentId)); // kept because the retained child references it
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_ActiveBundlesAreNeverTouched()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            var activeBundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: true, id);
            OperationJournalCodec.Save(OperationBundlePaths.JournalPath(activeBundleDir), Journal(id, now.AddDays(-90)));

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.True(Directory.Exists(activeBundleDir));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_BundleWithCorruptJournal_IsNeverDeleted()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            var bundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, id);
            Directory.CreateDirectory(bundleDir);
            File.WriteAllText(OperationBundlePaths.JournalPath(bundleDir), "not valid json");

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.True(Directory.Exists(bundleDir));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_NoCompletedDirectory_DoesNotThrow()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var exception = Record.Exception(() =>
                OperationBundleRetention.RunRetentionPass(dir.FullName, DateTimeOffset.UtcNow));

            Assert.Null(exception);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
