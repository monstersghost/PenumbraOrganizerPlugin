using System.Text.Json;
using System.Text.Json.Serialization;

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
    Cancelled,
}

// A later human/system decision applied on top of a frozen execution Stage. Kept separate so a
// superseded journal can keep an honest historical Stage while still being terminal (design doc §4).
public enum OperationResolution { None, AcceptedCurrentState, ContinuedByNewOperation, RestoredByNewOperation }

public sealed record OperationJournal(
    int SchemaVersion,
    Guid OperationId,
    OperationType Type,
    OperationStage Stage,
    OperationResolution Resolution,
    Guid? SuccessorOperationId,
    bool CancellationRequested,
    DateTimeOffset StartedAt,
    int TotalSteps,
    int ProcessedStepCount,
    string? LastCompletedIdentifier,
    Guid SnapshotId,
    Guid PlanId,
    string TargetHash,
    Guid? RecoveryOfOperationId,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 2;

    private static readonly HashSet<OperationStage> TerminalStages =
    [
        OperationStage.Completed,
        OperationStage.CompletedWithItemFailures,
        OperationStage.FailedBeforeMutation,
        OperationStage.FailedPartiallyApplied,
        OperationStage.Cancelled,
    ];

    // Terminal by either axis, independently: a later resolution, or an execution Stage that
    // itself concluded. See design doc section 4.
    public bool IsTerminal => Resolution != OperationResolution.None || TerminalStages.Contains(Stage);
}

public static class OperationJournalCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(string path, OperationJournal journal) =>
        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(journal, SerializerOptions));

    public static bool TryLoad(string path, out OperationJournal? journal)
    {
        journal = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        OperationJournal? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<OperationJournal>(contents, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (candidate is null || candidate.SchemaVersion != OperationJournal.CurrentSchemaVersion)
            return false;

        journal = candidate;
        return true;
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
