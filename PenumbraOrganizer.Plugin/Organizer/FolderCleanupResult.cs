namespace PenumbraOrganizer.Plugin.Organizer;

public enum FolderDetectionStatus
{
    Detected,           // lists are meaningful (possibly both empty — genuinely no orphans)
    NotScanned,         // no scan yet this session; file not read at all
    FileMissing,
    UnsupportedVersion,
    MalformedJson,
}

public sealed record FolderDetectionResult(
    IReadOnlyList<string> PlainEmpty,
    IReadOnlyList<CustomizedFolder> CustomizedEmpty,
    FolderDetectionStatus Status);

public enum FolderCleanupStatus
{
    Success,               // pruned and backed up
    SucceededBackupFailed, // pruned, but the new backup could not be written
    NothingSelected,
    NothingStillValid,
    FileMissing,
    UnsupportedVersion,
    MalformedJson,
    FileChangedDuringCleanup, // organization.json was rewritten by another process mid-operation
}

public sealed record FolderCleanupResult(
    IReadOnlyList<string> Pruned,
    IReadOnlyList<string> SkippedStale,
    FolderCleanupStatus Status);

public enum FolderRollbackStatus
{
    Restored,
    NoBackup,
    InvalidBackup,
}

public sealed record FolderRollbackResult(FolderRollbackStatus Status);
