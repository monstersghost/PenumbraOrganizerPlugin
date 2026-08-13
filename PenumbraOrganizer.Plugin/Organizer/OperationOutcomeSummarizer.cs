using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Turns a concluded operation's stage and target counts into the persisted summary the diagnostic
/// dump reads.
/// </summary>
/// <remarks>
/// This exists because <c>Config.LastApply</c> had no writer at all. It was declared, and the dump
/// read it, but nothing ever assigned it - so the Apply section reported "(no Apply run this
/// session)" permanently, including immediately after a successful Apply. A tester's dump showed
/// exactly that next to a rollback snapshot proving an Apply had started 26 minutes earlier, which
/// is how it was found.
/// <para>
/// Pure and separate from Plugin so the stage mapping is directly testable; Plugin itself has no
/// test coverage in this codebase.
/// </para>
/// </remarks>
public static class OperationOutcomeSummarizer
{
    /// <param name="processedTargets">
    /// Processed, not total. A cancelled operation leaves targets untouched, and counting those as
    /// failures would report a clean cancel as mass failure.
    /// </param>
    public static ApplyOperationSummary ToApplySummary(
        OperationStage stage, int successfulTargets, int processedTargets, DateTimeOffset completedAt) =>
        new(completedAt,
            ToStatus(stage),
            successfulTargets,
            Math.Max(0, processedTargets - successfulTargets));

    public static OperationCompletionStatus ToStatus(OperationStage stage) => stage switch
    {
        OperationStage.Completed => OperationCompletionStatus.Succeeded,
        OperationStage.CompletedWithItemFailures => OperationCompletionStatus.PartiallySucceeded,

        // Cancelled is grouped with the failures deliberately. From the dump reader's point of view
        // the question is "did this operation do what it said it would", and a cancel did not - the
        // stage name is carried in the log line for anyone who needs the distinction.
        OperationStage.FailedBeforeMutation => OperationCompletionStatus.Failed,
        OperationStage.FailedPartiallyApplied => OperationCompletionStatus.Failed,
        OperationStage.Cancelled => OperationCompletionStatus.Failed,

        // Non-terminal stages should never reach here; treating them as Failed rather than throwing
        // keeps a diagnostics path from becoming an exception source.
        _ => OperationCompletionStatus.Failed,
    };

    /// <summary>
    /// The one-line lifecycle record written to the Dalamud log when an operation concludes.
    /// </summary>
    /// <remarks>
    /// Apply wrote nothing whatsoever to the log before this. Diagnosing a tester's report of mods
    /// moving unexpectedly meant reconstructing the run from an export and a rollback snapshot,
    /// because the only operation in the plugin that moves files left no trace at all.
    /// </remarks>
    public static string DescribeCompletion(
        OperationType kind, Guid? operationId, OperationStage stage,
        int successfulTargets, int processedTargets, int totalTargets, string? lastError)
    {
        var line = $"[{kind}:{operationId?.ToString("N")[..8] ?? "unknown"}] settled {stage} "
                   + $"succeeded={successfulTargets} processed={processedTargets} planned={totalTargets}";

        return lastError is null ? line : $"{line} lastError={lastError}";
    }
}
