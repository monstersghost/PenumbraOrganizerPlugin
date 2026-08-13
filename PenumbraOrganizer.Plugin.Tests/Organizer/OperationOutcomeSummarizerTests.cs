namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

public class OperationOutcomeSummarizerTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 13, 7, 3, 22, TimeSpan.Zero);

    [Theory]
    [InlineData(OperationStage.Completed, OperationCompletionStatus.Succeeded)]
    [InlineData(OperationStage.CompletedWithItemFailures, OperationCompletionStatus.PartiallySucceeded)]
    [InlineData(OperationStage.FailedBeforeMutation, OperationCompletionStatus.Failed)]
    [InlineData(OperationStage.FailedPartiallyApplied, OperationCompletionStatus.Failed)]
    [InlineData(OperationStage.Cancelled, OperationCompletionStatus.Failed)]
    public void ToStatus_MapsEveryTerminalStage(OperationStage stage, OperationCompletionStatus expected)
    {
        Assert.Equal(expected, OperationOutcomeSummarizer.ToStatus(stage));
    }

    // A diagnostics path must never throw. Every non-terminal stage still has to produce a value.
    [Theory]
    [InlineData(OperationStage.Preparing)]
    [InlineData(OperationStage.Prepared)]
    [InlineData(OperationStage.Mutating)]
    [InlineData(OperationStage.Refreshing)]
    [InlineData(OperationStage.Verifying)]
    public void ToStatus_NonTerminalStage_DoesNotThrow(OperationStage stage)
    {
        Assert.Equal(OperationCompletionStatus.Failed, OperationOutcomeSummarizer.ToStatus(stage));
    }

    [Fact]
    public void ToApplySummary_CountsFailuresAgainstProcessedNotPlanned()
    {
        // 8913 planned, only 100 reached before a cancel: 90 succeeded, 10 failed. The 8813 never
        // attempted are not failures, and reporting them as such would turn a clean cancel into an
        // apparent catastrophe in the dump.
        var summary = OperationOutcomeSummarizer.ToApplySummary(
            OperationStage.Cancelled, successfulTargets: 90, processedTargets: 100, completedAt: At);

        Assert.Equal(90, summary.Succeeded);
        Assert.Equal(10, summary.Failed);
        Assert.Equal(OperationCompletionStatus.Failed, summary.Status);
        Assert.Equal(At, summary.CompletedAt);
    }

    [Fact]
    public void ToApplySummary_CleanRun_ReportsNoFailures()
    {
        var summary = OperationOutcomeSummarizer.ToApplySummary(
            OperationStage.Completed, successfulTargets: 8913, processedTargets: 8913, completedAt: At);

        Assert.Equal(8913, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(OperationCompletionStatus.Succeeded, summary.Status);
    }

    // Defensive: successful can never exceed processed, but a negative failure count would be worse
    // than a clamped zero if the invariant ever broke.
    [Fact]
    public void ToApplySummary_NeverReportsNegativeFailures()
    {
        var summary = OperationOutcomeSummarizer.ToApplySummary(
            OperationStage.Completed, successfulTargets: 10, processedTargets: 5, completedAt: At);

        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public void DescribeCompletion_CarriesStageAndCounts()
    {
        var line = OperationOutcomeSummarizer.DescribeCompletion(
            OperationType.Apply, Guid.Empty, OperationStage.CompletedWithItemFailures,
            successfulTargets: 8910, processedTargets: 8913, totalTargets: 8913, lastError: null);

        Assert.Contains("Apply", line);
        Assert.Contains("CompletedWithItemFailures", line);
        Assert.Contains("succeeded=8910", line);
        Assert.Contains("processed=8913", line);
        Assert.Contains("planned=8913", line);
        Assert.DoesNotContain("lastError", line);
    }

    [Fact]
    public void DescribeCompletion_AppendsLastErrorWhenPresent()
    {
        var line = OperationOutcomeSummarizer.DescribeCompletion(
            OperationType.Restore, Guid.Empty, OperationStage.FailedPartiallyApplied,
            successfulTargets: 1, processedTargets: 2, totalTargets: 9, lastError: "Penumbra rejected the path");

        Assert.Contains("lastError=Penumbra rejected the path", line);
    }

    // A null id must not crash the line that exists to explain a crash.
    [Fact]
    public void DescribeCompletion_NullOperationId_IsDescribed()
    {
        var line = OperationOutcomeSummarizer.DescribeCompletion(
            OperationType.Apply, null, OperationStage.Completed, 0, 0, 0, null);

        Assert.Contains("unknown", line);
    }
}
