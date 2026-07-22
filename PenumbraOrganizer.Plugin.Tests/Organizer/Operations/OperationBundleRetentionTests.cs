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
    public void RunRetentionPass_ThreeHopChain_GrandparentRetainedThroughTransitiveReference()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var grandparentId = Guid.NewGuid();
            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();

            // All three are old enough to be deleted on their own merits; only the child is young.
            // Grandparent and parent survive ONLY via transitive reference through the young child.
            SaveCompletedBundle(dir.FullName, Journal(grandparentId, now.AddDays(-90)));
            SaveCompletedBundle(dir.FullName, Journal(parentId, now.AddDays(-60), recoveryOf: grandparentId));
            SaveCompletedBundle(dir.FullName, Journal(childId, now.AddDays(-1), recoveryOf: parentId));

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.True(BundleExists(dir.FullName, childId));
            Assert.True(BundleExists(dir.FullName, parentId));
            Assert.True(BundleExists(dir.FullName, grandparentId)); // retained only via 2-hop transitive reference
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_ThreeHopChainButNoRetainedDescendant_AllDeleted()
    {
        // Same chain shape as above, but nothing in the chain is young/capped/directly retained -
        // the whole chain should be deleted, proving the closure doesn't retain things unconditionally.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var grandparentId = Guid.NewGuid();
            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();

            SaveCompletedBundle(dir.FullName, Journal(grandparentId, now.AddDays(-90)));
            SaveCompletedBundle(dir.FullName, Journal(parentId, now.AddDays(-60), recoveryOf: grandparentId));
            SaveCompletedBundle(dir.FullName, Journal(childId, now.AddDays(-45), recoveryOf: parentId)); // also old now

            OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));

            Assert.False(BundleExists(dir.FullName, childId));
            Assert.False(BundleExists(dir.FullName, parentId));
            Assert.False(BundleExists(dir.FullName, grandparentId));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunRetentionPass_OneUndeletableBundle_DoesNotBlockDeletionOfOthers()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var lockedId = Guid.NewGuid();
            var deletableId = Guid.NewGuid();

            SaveCompletedBundle(dir.FullName, Journal(lockedId, now.AddDays(-60)));
            SaveCompletedBundle(dir.FullName, Journal(deletableId, now.AddDays(-60)));

            // Locking journal.json itself would make TryLoad fail during the *load* phase, which
            // excludes the bundle from consideration entirely ("protected by omission") without ever
            // reaching Directory.Delete - that would pass vacuously without touching the catch block.
            // Instead, lock a second file inside the bundle directory that the retention pass never
            // reads, so the journal loads normally and the bundle is correctly judged deletable, and
            // the sharing violation only surfaces when Directory.Delete(recursive: true) tries to
            // remove that locked file.
            var lockedBundleDir = OperationBundlePaths.BundleDirectory(dir.FullName, active: false, lockedId);
            var extraFilePath = Path.Combine(lockedBundleDir, "locked-extra.bin");
            File.WriteAllText(extraFilePath, "locked");

            using (var exclusive = new FileStream(extraFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                OperationBundleRetention.RunRetentionPass(dir.FullName, now, retainNewestCount: 0, retainAge: TimeSpan.FromDays(30));
            }

            // The locked bundle survives (deletion failed and was caught); the other bundle,
            // unaffected by the lock, was still deleted despite the first failure.
            Assert.True(BundleExists(dir.FullName, lockedId));
            Assert.False(BundleExists(dir.FullName, deletableId));
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
