using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class FolderCleanupExecutorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("folder-cleanup-tests").FullName;

    private string OrgPath => Path.Combine(_dir, "organization.json");
    private string BackupPath => Path.Combine(_dir, "organizer-folder-backup.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string TwoFolderFile = """
        {
          "Version": 1,
          "Folders": {
            "Old/Empty": {},
            "Creators/Alice": {}
          },
          "Separators": {}
        }
        """;

    private static IReadOnlySet<string> Set(params string[] items) =>
        items.ToHashSet(StringComparer.Ordinal);

    // --- Execute: happy path ---

    [Fact]
    public void Execute_Success_PrunesSelectedAndReturnsSuccess()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("Old/Empty"), Set("Creators/Alice"));

        Assert.Equal(FolderCleanupStatus.Success, result.Status);
        Assert.Equal(["Old/Empty"], result.Pruned);
        Assert.Empty(result.SkippedStale);
        var reparsed = OrganizationJsonCodec.Parse(File.ReadAllText(OrgPath));
        Assert.False(reparsed.Data!.Folders.ContainsKey("Old/Empty"));
        Assert.True(reparsed.Data.Folders.ContainsKey("Creators/Alice"));
    }

    [Fact]
    public void Execute_Success_BackupIsByteIdenticalToPrePruneFile()
    {
        // The regression test for the backup-source rule: backup content must be the ORIGINAL
        // bytes retained in memory before pruning — never a reread of the post-prune file.
        File.WriteAllText(OrgPath, TwoFolderFile);
        var originalBytes = File.ReadAllBytes(OrgPath);

        FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("Old/Empty"), Set("Creators/Alice"));

        Assert.Equal(originalBytes, File.ReadAllBytes(BackupPath));
    }

    // --- Execute: no-op guards ---

    [Fact]
    public void Execute_NothingSelected_TouchesNoFiles()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);
        var before = File.ReadAllBytes(OrgPath);

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set(), Set());

        Assert.Equal(FolderCleanupStatus.NothingSelected, result.Status);
        Assert.Equal(before, File.ReadAllBytes(OrgPath));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void Execute_AllSelectionsStale_TouchesNoFilesAndPreservesExistingBackup()
    {
        // "Old/Empty" is occupied at write time (a mod was moved into it via Penumbra's UI
        // after selection) and "Ghost" no longer exists in the file — nothing survives
        // re-verification. A pre-existing backup from an earlier cleanup must survive untouched.
        File.WriteAllText(OrgPath, TwoFolderFile);
        File.WriteAllText(BackupPath, "previous-backup-content");

        var result = FolderCleanupExecutor.Execute(
            OrgPath, BackupPath, Set("Old/Empty", "Ghost"), Set("Old/Empty"));

        Assert.Equal(FolderCleanupStatus.NothingStillValid, result.Status);
        Assert.Equal(2, result.SkippedStale.Count);
        Assert.Equal("previous-backup-content", File.ReadAllText(BackupPath));
        Assert.Contains("Old/Empty", File.ReadAllText(OrgPath));
    }

    [Fact]
    public void Execute_PartiallyStale_PrunesValidAndReportsStale()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("Old/Empty", "Ghost"), Set("Creators/Alice"));

        Assert.Equal(FolderCleanupStatus.Success, result.Status);
        Assert.Equal(["Old/Empty"], result.Pruned);
        Assert.Equal(["Ghost"], result.SkippedStale);
    }

    // --- Execute: file-state failures ---

    [Fact]
    public void Execute_FileMissing_ReturnsFileMissing()
    {
        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("X"), Set());

        Assert.Equal(FolderCleanupStatus.FileMissing, result.Status);
    }

    [Fact]
    public void Execute_UnsupportedVersion_ReturnsUnsupportedVersionAndTouchesNothing()
    {
        File.WriteAllText(OrgPath, """{ "Version": 2, "Folders": { "X": {} }, "Separators": {} }""");
        var before = File.ReadAllBytes(OrgPath);

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("X"), Set());

        Assert.Equal(FolderCleanupStatus.UnsupportedVersion, result.Status);
        Assert.Equal(before, File.ReadAllBytes(OrgPath));
    }

    [Fact]
    public void Execute_MalformedJson_ReturnsMalformedJsonAndTouchesNothing()
    {
        File.WriteAllText(OrgPath, "{ broken !");

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("X"), Set());

        Assert.Equal(FolderCleanupStatus.MalformedJson, result.Status);
        Assert.Equal("{ broken !", File.ReadAllText(OrgPath));
    }

    [Fact]
    public void Execute_FileWithUtf8Bom_StillParsesAndSucceeds()
    {
        // File.ReadAllText auto-detects a BOM but raw-byte decoding does not — the executor
        // must strip an EF BB BF prefix before parsing, or a BOM'd real-install file would be
        // misreported as MalformedJson. (Backup fidelity is unaffected: raw bytes, BOM and all.)
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        File.WriteAllBytes(OrgPath, [.. bom, .. System.Text.Encoding.UTF8.GetBytes(TwoFolderFile)]);

        var result = FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("Old/Empty"), Set("Creators/Alice"));

        Assert.Equal(FolderCleanupStatus.Success, result.Status);
        Assert.Equal(0xEF, File.ReadAllBytes(BackupPath)[0]); // backup preserves the original BOM
    }

    // --- Execute: concurrent external write race ---

    [Fact]
    public void Execute_FileChangedBetweenReadAndWrite_AbortsWithoutOverwritingAndReturnsFileChangedStatus()
    {
        // Simulates Penumbra's own process rewriting organization.json between this executor's
        // initial read and its write - the exact race the design's Open Risk #4 flags. The hook
        // fires at the point Execute would otherwise commit its own write.
        File.WriteAllText(OrgPath, TwoFolderFile);
        const string externalContent = """{ "Version": 1, "Folders": { "Penumbra/Wrote/This": {} }, "Separators": {} }""";

        var result = FolderCleanupExecutor.Execute(
            OrgPath, BackupPath, Set("Old/Empty"), Set("Creators/Alice"),
            beforeCommit: () => File.WriteAllText(OrgPath, externalContent));

        Assert.Equal(FolderCleanupStatus.FileChangedDuringCleanup, result.Status);
        Assert.Empty(result.Pruned);
        Assert.Equal(externalContent, File.ReadAllText(OrgPath)); // untouched since the hook wrote it
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void Execute_FileChangedBetweenReadAndWrite_PreservesExistingBackup()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);
        File.WriteAllText(BackupPath, "previous-backup-content");

        FolderCleanupExecutor.Execute(
            OrgPath, BackupPath, Set("Old/Empty"), Set("Creators/Alice"),
            beforeCommit: () => File.WriteAllText(OrgPath, "{ \"Version\": 1, \"Folders\": {}, \"Separators\": {} }"));

        Assert.Equal("previous-backup-content", File.ReadAllText(BackupPath));
    }

    [Fact]
    public void Execute_FileUnchangedAtCommitTime_ProceedsNormally()
    {
        // The hook fires but writes back byte-identical content - confirms the check compares
        // bytes, not merely "was the hook invoked".
        File.WriteAllText(OrgPath, TwoFolderFile);

        var result = FolderCleanupExecutor.Execute(
            OrgPath, BackupPath, Set("Old/Empty"), Set("Creators/Alice"),
            beforeCommit: () => File.WriteAllText(OrgPath, TwoFolderFile));

        Assert.Equal(FolderCleanupStatus.Success, result.Status);
        Assert.Equal(["Old/Empty"], result.Pruned);
    }

    // --- Execute: backup promotion failure ---

    [Fact]
    public void Execute_BackupWriteFails_PruneStandsAndReturnsSucceededBackupFailed()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);
        // A path UNDER an existing regular file is unwritable on every platform — forces the
        // backup promotion (and its temp file) to throw while the target write succeeds.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "i am a file, not a directory");
        var unwritableBackup = Path.Combine(blocker, "backup.json");

        var result = FolderCleanupExecutor.Execute(OrgPath, unwritableBackup, Set("Old/Empty"), Set("Creators/Alice"));

        Assert.Equal(FolderCleanupStatus.SucceededBackupFailed, result.Status);
        Assert.Equal(["Old/Empty"], result.Pruned);
        var reparsed = OrganizationJsonCodec.Parse(File.ReadAllText(OrgPath));
        Assert.False(reparsed.Data!.Folders.ContainsKey("Old/Empty")); // prune stands
    }

    // --- ExecuteRollback ---

    [Fact]
    public void ExecuteRollback_NoBackup_ReturnsNoBackup()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);

        var result = FolderCleanupExecutor.ExecuteRollback(OrgPath, BackupPath);

        Assert.Equal(FolderRollbackStatus.NoBackup, result.Status);
    }

    [Fact]
    public void ExecuteRollback_RestoresBytesAndDeletesBackup()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);
        var originalBytes = File.ReadAllBytes(OrgPath);
        FolderCleanupExecutor.Execute(OrgPath, BackupPath, Set("Old/Empty"), Set("Creators/Alice"));

        var result = FolderCleanupExecutor.ExecuteRollback(OrgPath, BackupPath);

        Assert.Equal(FolderRollbackStatus.Restored, result.Status);
        Assert.Equal(originalBytes, File.ReadAllBytes(OrgPath));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void ExecuteRollback_InvalidBackup_AbortsWithoutTouchingLiveFile()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);
        File.WriteAllText(BackupPath, "{ not valid json");
        var before = File.ReadAllBytes(OrgPath);

        var result = FolderCleanupExecutor.ExecuteRollback(OrgPath, BackupPath);

        Assert.Equal(FolderRollbackStatus.InvalidBackup, result.Status);
        Assert.Equal(before, File.ReadAllBytes(OrgPath));
        Assert.True(File.Exists(BackupPath)); // not deleted either
    }

    [Fact]
    public void ExecuteRollback_UnsupportedVersionBackup_TreatedAsInvalid()
    {
        File.WriteAllText(OrgPath, TwoFolderFile);
        File.WriteAllText(BackupPath, """{ "Version": 99, "Folders": {}, "Separators": {} }""");

        var result = FolderCleanupExecutor.ExecuteRollback(OrgPath, BackupPath);

        Assert.Equal(FolderRollbackStatus.InvalidBackup, result.Status);
    }
}
