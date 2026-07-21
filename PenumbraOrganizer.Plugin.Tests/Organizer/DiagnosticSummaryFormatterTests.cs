using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class DiagnosticSummaryFormatterTests
{
    [Fact]
    public void FormatApplySection_SessionResultsPresent_FormatsCountsAndFailures()
    {
        var results = new List<ApplyResult> { new("a", true, null), new("b", false, "PenumbraApiEc.NothingChanged") };

        var text = DiagnosticSummaryFormatter.FormatApplySection(results, persisted: null);

        Assert.Equal("1 succeeded, 1 failed\n  FAILED: b: PenumbraApiEc.NothingChanged", text);
    }

    [Fact]
    public void FormatApplySection_NoSessionResult_FallsBackToPersistedSummary()
    {
        var persisted = new ApplyOperationSummary(
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"), OperationCompletionStatus.PartiallySucceeded, 3, 1);

        var text = DiagnosticSummaryFormatter.FormatApplySection(sessionResults: null, persisted);

        Assert.Equal(
            "(no Apply run this session; last known from a prior session: 2026-07-20 10:00:00Z — PartiallySucceeded, 3 succeeded, 1 failed)",
            text);
    }

    [Fact]
    public void FormatApplySection_NeitherSessionNorPersisted_ReportsNoApplyRun()
    {
        var text = DiagnosticSummaryFormatter.FormatApplySection(sessionResults: null, persisted: null);

        Assert.Equal("(no Apply run this session)", text);
    }

    [Fact]
    public void FormatApplySection_SessionResultPresent_TakesPrecedenceOverPersisted()
    {
        var results = new List<ApplyResult> { new("a", true, null) };
        var persisted = new ApplyOperationSummary(DateTimeOffset.UtcNow, OperationCompletionStatus.Failed, 0, 99);

        var text = DiagnosticSummaryFormatter.FormatApplySection(results, persisted);

        Assert.Equal("1 succeeded, 0 failed", text);
    }

    [Fact]
    public void FormatRestoreSection_SessionResultsPresent_GroupsByOutcomeInDeterministicOrder()
    {
        var results = new List<RestoreResult>
        {
            new("a", RestoreOutcome.Moved, null),
            new("b", RestoreOutcome.Unchanged, null),
            new("c", RestoreOutcome.Failed, "PenumbraApiEc.PathRenameFailed"),
        };

        var text = DiagnosticSummaryFormatter.FormatRestoreSection(results, persisted: null);

        Assert.Equal("  Moved: 1\n  Unchanged: 1\n  Failed: 1\n  FAILED: c: PenumbraApiEc.PathRenameFailed", text);
    }

    [Fact]
    public void FormatRestoreSection_NoSessionResult_FallsBackToPersistedSummary()
    {
        var persisted = new RestoreOperationSummary(
            DateTimeOffset.Parse("2026-07-20T10:05:00Z"), OperationCompletionStatus.Succeeded, 2, 1, 0, 1, 0);

        var text = DiagnosticSummaryFormatter.FormatRestoreSection(sessionResults: null, persisted);

        Assert.Equal(
            "(no Restore run this session; last known from a prior session: 2026-07-20 10:05:00Z — Succeeded, "
            + "2 moved, 1 unchanged, 0 skipped uninstalled, 1 relocated to root, 0 failed)",
            text);
    }

    [Fact]
    public void FormatRestoreSection_NeitherSessionNorPersisted_ReportsNoRestoreRun()
    {
        var text = DiagnosticSummaryFormatter.FormatRestoreSection(sessionResults: null, persisted: null);

        Assert.Equal("(no Restore run this session)", text);
    }

    [Fact]
    public void FormatFolderCleanupSection_SessionResultPresent_FormatsStatusAndCounts()
    {
        var result = new FolderCleanupResult(["Gear/Old"], [], FolderCleanupStatus.Success);

        var text = DiagnosticSummaryFormatter.FormatFolderCleanupSection(result, persisted: null);

        Assert.Equal("Status=Success, Pruned=1, SkippedStale=0", text);
    }

    [Fact]
    public void FormatFolderCleanupSection_NoSessionResult_FallsBackToPersistedSummary()
    {
        var persisted = new FolderCleanupOperationSummary(
            DateTimeOffset.Parse("2026-07-20T10:10:00Z"), FolderCleanupStatus.Success, 5, 0);

        var text = DiagnosticSummaryFormatter.FormatFolderCleanupSection(sessionResult: null, persisted);

        Assert.Equal(
            "(no Folder Cleanup run this session; last known from a prior session: 2026-07-20 10:10:00Z — Status=Success, Pruned=5, SkippedStale=0)",
            text);
    }

    [Fact]
    public void FormatFolderCleanupSection_NeitherSessionNorPersisted_ReportsNoCleanupRun()
    {
        var text = DiagnosticSummaryFormatter.FormatFolderCleanupSection(sessionResult: null, persisted: null);

        Assert.Equal("(no Folder Cleanup run this session)", text);
    }

    [Fact]
    public void FormatFolderCleanupRollbackSection_SessionResultPresent_FormatsStatus()
    {
        var result = new FolderRollbackResult(FolderRollbackStatus.Restored);

        var text = DiagnosticSummaryFormatter.FormatFolderCleanupRollbackSection(result, persisted: null);

        Assert.Equal("Status=Restored", text);
    }

    [Fact]
    public void FormatFolderCleanupRollbackSection_NoSessionResult_FallsBackToPersistedSummary()
    {
        var persisted = new FolderCleanupRollbackOperationSummary(
            DateTimeOffset.Parse("2026-07-20T10:15:00Z"), FolderRollbackStatus.NoBackup);

        var text = DiagnosticSummaryFormatter.FormatFolderCleanupRollbackSection(sessionResult: null, persisted);

        Assert.Equal(
            "(no Folder Cleanup Rollback run this session; last known from a prior session: 2026-07-20 10:15:00Z — Status=NoBackup)",
            text);
    }

    [Fact]
    public void FormatFolderCleanupRollbackSection_NeitherSessionNorPersisted_ReportsNoRollbackRun()
    {
        var text = DiagnosticSummaryFormatter.FormatFolderCleanupRollbackSection(sessionResult: null, persisted: null);

        Assert.Equal("(no Folder Cleanup Rollback run this session)", text);
    }
}
