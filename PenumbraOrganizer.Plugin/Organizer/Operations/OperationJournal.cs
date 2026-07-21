using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum OperationStage
{
    Preparing,
    Prepared,
    Mutating,
    Refreshing,
    Verifying,
    Completed,
    CompletedWithItemFailures,
    FailedBeforeMutation,
    FailedPartiallyApplied,
    AcceptedCurrentState,
}

public sealed record OperationJournal(
    Guid OperationId,
    OperationType Type,
    OperationStage Status,
    DateTimeOffset StartedAt,
    int TotalItems,
    int CompletedItems,
    string? LastCompletedIdentifier,
    Guid SnapshotId,
    Guid PlanId,
    string TargetHash,
    Guid? RecoveryOfOperationId,
    DateTimeOffset UpdatedAt)
{
    private static readonly HashSet<OperationStage> TerminalStages =
    [
        OperationStage.Completed,
        OperationStage.CompletedWithItemFailures,
        OperationStage.FailedBeforeMutation,
        OperationStage.FailedPartiallyApplied,
        OperationStage.AcceptedCurrentState,
    ];

    public bool IsTerminal => TerminalStages.Contains(Status);
}

public static class OperationJournalCodec
{
    public static void Save(string path, OperationJournal journal) =>
        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(journal));

    public static bool TryLoad(string path, out OperationJournal? journal)
    {
        journal = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        try
        {
            journal = JsonSerializer.Deserialize<OperationJournal>(contents);
        }
        catch (JsonException)
        {
            return false;
        }

        return journal is not null;
    }
}

/// <summary>
/// Design doc section 6: checkpoint on whichever threshold is reached first, so a large library
/// doesn't rewrite the journal after every single mutation (filesystem churn on HDDs) while a
/// stalled operation still checkpoints promptly on wall-clock time.
/// </summary>
public static class CheckpointPolicy
{
    private const int ItemThreshold = 10;
    private static readonly TimeSpan TimeThreshold = TimeSpan.FromMilliseconds(500);

    public static bool IsDue(int completedSinceLastCheckpoint, TimeSpan elapsedSinceLastCheckpoint) =>
        completedSinceLastCheckpoint >= ItemThreshold || elapsedSinceLastCheckpoint >= TimeThreshold;
}
