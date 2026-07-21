namespace PenumbraOrganizer.Plugin.Organizer;

// Pure formatting, deliberately separated from MainWindow (which has no test coverage in this
// codebase) so the session-vs-persisted precedence and fallback formatting can be verified
// directly, rather than only by inspecting MainWindow's diagnostic-dump output.
public static class DiagnosticSummaryFormatter
{
    public static string FormatApplySection(IReadOnlyList<ApplyResult>? sessionResults, ApplyOperationSummary? persisted)
    {
        if (sessionResults is not null)
        {
            var succeeded = sessionResults.Count(r => r.Success);
            var sb = new System.Text.StringBuilder();
            sb.Append($"{succeeded} succeeded, {sessionResults.Count - succeeded} failed");
            foreach (var failure in sessionResults.Where(r => !r.Success))
                sb.Append($"\n  FAILED: {failure.Identifier}: {failure.FailureReason}");
            return sb.ToString();
        }

        return persisted is null
            ? "(no Apply run this session)"
            : $"(no Apply run this session; last known from a prior session: {persisted.CompletedAt:u} — "
              + $"{persisted.Status}, {persisted.Succeeded} succeeded, {persisted.Failed} failed)";
    }

    public static string FormatRestoreSection(IReadOnlyList<RestoreResult>? sessionResults, RestoreOperationSummary? persisted)
    {
        if (sessionResults is not null)
        {
            var outcomeLines = sessionResults
                .GroupBy(r => r.Outcome)
                .OrderBy(g => g.Key)
                .Select(g => $"  {g.Key}: {g.Count()}");
            var failureLines = sessionResults
                .Where(r => r.Outcome == RestoreOutcome.Failed)
                .OrderBy(f => f.Identifier, StringComparer.Ordinal)
                .Select(f => $"  FAILED: {f.Identifier}: {f.FailureReason}");
            return string.Join("\n", outcomeLines.Concat(failureLines));
        }

        return persisted is null
            ? "(no Restore run this session)"
            : $"(no Restore run this session; last known from a prior session: {persisted.CompletedAt:u} — "
              + $"{persisted.Status}, {persisted.Moved} moved, {persisted.Unchanged} unchanged, "
              + $"{persisted.SkippedUninstalled} skipped uninstalled, {persisted.RootRelocated} relocated to root, {persisted.Failed} failed)";
    }

    public static string FormatFolderCleanupSection(FolderCleanupResult? sessionResult, FolderCleanupOperationSummary? persisted)
    {
        if (sessionResult is not null)
            return $"Status={sessionResult.Status}, Pruned={sessionResult.Pruned.Count}, SkippedStale={sessionResult.SkippedStale.Count}";

        return persisted is null
            ? "(no Folder Cleanup run this session)"
            : $"(no Folder Cleanup run this session; last known from a prior session: {persisted.CompletedAt:u} — "
              + $"Status={persisted.Status}, Pruned={persisted.Pruned}, SkippedStale={persisted.SkippedStale})";
    }

    public static string FormatFolderCleanupRollbackSection(
        FolderRollbackResult? sessionResult, FolderCleanupRollbackOperationSummary? persisted)
    {
        if (sessionResult is not null)
            return $"Status={sessionResult.Status}";

        return persisted is null
            ? "(no Folder Cleanup Rollback run this session)"
            : $"(no Folder Cleanup Rollback run this session; last known from a prior session: {persisted.CompletedAt:u} — Status={persisted.Status})";
    }
}
