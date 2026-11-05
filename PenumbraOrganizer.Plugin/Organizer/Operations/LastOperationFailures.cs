namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>One mod an operation could not move, and what Penumbra said about it.</summary>
public sealed record OperationFailure(string ModName, string Identifier, string TargetPath, string Reason);

public sealed record LastOperationFailureReport(
    OperationType Type, DateTimeOffset UpdatedAt, IReadOnlyList<OperationFailure> Failures);

/// <summary>
/// Reads the per-mod failures of the most recently finished operation from its on-disk bundle, so the
/// UI can name what failed instead of only counting it. Pure file reads; never throws.
/// </summary>
public static class LastOperationFailures
{
    public static LastOperationFailureReport? Load(string operationsRoot)
    {
        try
        {
            var journals = OperationBundleDiscovery.LoadRecentCompletedJournals(operationsRoot, 1);
            if (journals.Count == 0)
                return null;

            var journal = journals[0];
            var dir = OperationBundlePaths.BundleDirectory(operationsRoot, active: false, journal.OperationId);

            OperationPlan? plan = null;
            if (OperationPlanCodec.TryLoad(OperationBundlePaths.PlanPath(dir), out var loaded))
                plan = loaded;

            var targets = plan?.RecoveryTargets
                .GroupBy(t => t.Identifier, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var failures = StepResultLog.ReadAll(OperationBundlePaths.ResultsPath(dir))
                .Where(r => r.Disposition == OperationStepDisposition.Failed)
                .Select(r =>
                {
                    OperationRecoveryTarget? target = null;
                    targets?.TryGetValue(r.Identifier, out target);
                    return new OperationFailure(
                        target?.ModName ?? r.Identifier,
                        r.Identifier,
                        target?.FinalRawPath ?? "",
                        r.IpcResultName ?? r.FailureDetail ?? "Unknown failure");
                })
                .ToList();

            return new LastOperationFailureReport(journal.Type, journal.UpdatedAt, failures);
        }
        // This runs inside a draw call, where anything that escapes kills the frame; a damaged bundle
        // should read as "nothing found", never as a crash.
        catch (Exception)
        {
            return null;
        }
    }
}
