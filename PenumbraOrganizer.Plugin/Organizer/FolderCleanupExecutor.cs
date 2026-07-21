using System.Text;

namespace PenumbraOrganizer.Plugin.Organizer;

// All file-I/O sequencing for folder cleanup and its rollback. Deliberately no IPC and no
// Dalamud types: Plugin.cs resolves the real paths and supplies occupancy; this class is what
// the integration-style tests drive against a temp directory.
public static class FolderCleanupExecutor
{
    // UTF-8 without BOM — matches Penumbra's own JSON files. Flagged in the spec for
    // confirmation against a real install during in-game verification.
    private static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false);

    public static FolderCleanupResult Execute(
        string organizationJsonPath,
        string backupFilePath,
        IReadOnlySet<string> selectedPaths,
        IReadOnlySet<string> occupiedFolders,
        Action? beforeCommit = null)
    {
        if (selectedPaths.Count == 0)
            return new FolderCleanupResult([], [], FolderCleanupStatus.NothingSelected);

        if (!File.Exists(organizationJsonPath))
            return new FolderCleanupResult([], [], FolderCleanupStatus.FileMissing);

        // Read exactly once and retain: these bytes — never a reread — become the backup, so
        // the backup can never accidentally be built from the pruned file.
        var originalBytes = File.ReadAllBytes(organizationJsonPath);

        var parse = OrganizationJsonCodec.Parse(DecodeText(originalBytes));
        if (parse.Status == OrganizationJsonParseStatus.MalformedJson)
            return new FolderCleanupResult([], [], FolderCleanupStatus.MalformedJson);
        if (parse.Status == OrganizationJsonParseStatus.UnsupportedVersion)
            return new FolderCleanupResult([], [], FolderCleanupStatus.UnsupportedVersion);

        // Re-verify every selection against the file as it exists now and live occupancy:
        // still present, and still orphaned. Reuses DetectOrphaned so the write path can never
        // disagree with detection about what "orphaned" means.
        var (plainEmpty, customizedEmpty) = OrganizationCleanupPlanner.DetectOrphaned(parse.Data!, occupiedFolders);
        var orphanedNow = plainEmpty
            .Concat(customizedEmpty.Select(c => c.Path))
            .ToHashSet(StringComparer.Ordinal);

        var stillValid = selectedPaths.Where(orphanedNow.Contains)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var skippedStale = selectedPaths.Where(p => !orphanedNow.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

        // A no-op attempt must be indistinguishable from never clicking the button: no file
        // writes, and above all no overwrite of a previous valid rollback point.
        if (stillValid.Count == 0)
            return new FolderCleanupResult([], skippedStale, FolderCleanupStatus.NothingStillValid);

        var pruned = OrganizationCleanupPlanner.Prune(parse.Data!, stillValid.ToHashSet(StringComparer.Ordinal));
        var prunedJson = OrganizationJsonCodec.Serialize(pruned);

        beforeCommit?.Invoke(); // test seam only — no production caller passes this

        // Open Risk #4 (design spec): organization.json is owned by Penumbra's own process, which
        // can rewrite it independently of this plugin at any time. A final byte-for-byte check
        // against what we last read closes the gap between that read and this write — if anything
        // changed in between, our in-memory prune was computed against data that's no longer
        // current, so committing it would silently discard whatever Penumbra just wrote.
        if (!File.ReadAllBytes(organizationJsonPath).AsSpan().SequenceEqual(originalBytes))
            return new FolderCleanupResult([], skippedStale, FolderCleanupStatus.FileChangedDuringCleanup);

        // Target write first; backup promotion only after it succeeds. Reversed, a failed
        // target write would already have destroyed the previous backup for nothing. If this
        // write throws, the caller's error handling surfaces it — nothing has been backed up
        // over, and the atomic move means no half-written target.
        AtomicWrite(organizationJsonPath, Encoding.GetBytes(prunedJson));

        try
        {
            AtomicWrite(backupFilePath, originalBytes);
        }
        catch (Exception)
        {
            // Partial infrastructure failure, not a failed cleanup: the prune stands, but this
            // action has no rollback point. Any pre-existing backup file was left untouched
            // (the atomic temp write failed before the move) — it is now stale relative to
            // this cleanup, which the UI warns about.
            return new FolderCleanupResult(stillValid, skippedStale, FolderCleanupStatus.SucceededBackupFailed);
        }

        return new FolderCleanupResult(stillValid, skippedStale, FolderCleanupStatus.Success);
    }

    public static FolderRollbackResult ExecuteRollback(string organizationJsonPath, string backupFilePath)
    {
        if (!File.Exists(backupFilePath))
            return new FolderRollbackResult(FolderRollbackStatus.NoBackup);

        var backupBytes = File.ReadAllBytes(backupFilePath);

        // Validate before trusting: never overwrite a possibly-valid live file with bytes that
        // don't parse as a supported organization.json.
        if (OrganizationJsonCodec.Parse(DecodeText(backupBytes)).Status != OrganizationJsonParseStatus.Ok)
            return new FolderRollbackResult(FolderRollbackStatus.InvalidBackup);

        AtomicWrite(organizationJsonPath, backupBytes);
        File.Delete(backupFilePath);
        return new FolderRollbackResult(FolderRollbackStatus.Restored);
    }

    // UTF8Encoding.GetString does not strip a byte-order mark the way File.ReadAllText does —
    // without this, a BOM'd file would fail parsing as MalformedJson. The raw bytes (BOM
    // included) are still what gets backed up and restored, so fidelity is unaffected.
    private static string DecodeText(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? Encoding.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.GetString(bytes);

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }
}
