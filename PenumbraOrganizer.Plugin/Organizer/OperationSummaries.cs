namespace PenumbraOrganizer.Plugin.Organizer;

public enum OperationCompletionStatus { Succeeded, PartiallySucceeded, Failed }

public sealed record ApplyOperationSummary(
    DateTimeOffset CompletedAt, OperationCompletionStatus Status, int Succeeded, int Failed);

public sealed record RestoreOperationSummary(
    DateTimeOffset CompletedAt,
    OperationCompletionStatus Status,
    int Moved,
    int Unchanged,
    int SkippedUninstalled,
    int RootRelocated,
    int Failed);

// Reuses the existing, more precise FolderCleanupStatus/FolderRollbackStatus enums (FolderCleanupResult.cs)
// rather than the generic OperationCompletionStatus above - Folder Cleanup and its rollback already have
// dedicated status enums covering exactly their own failure modes.
public sealed record FolderCleanupOperationSummary(
    DateTimeOffset CompletedAt, FolderCleanupStatus Status, int Pruned, int SkippedStale);

public sealed record FolderCleanupRollbackOperationSummary(
    DateTimeOffset CompletedAt, FolderRollbackStatus Status);
