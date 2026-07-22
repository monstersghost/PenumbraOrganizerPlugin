namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum StepResultReconciliationStatus { Consistent, Indeterminate }

public sealed record StepResultReconciliationResult(StepResultReconciliationStatus Status, string? Reason);

/// <summary>
/// The journal, not the result log, is authoritative on committed progress (design doc section 5a).
/// This checks whether results.jsonl actually substantiates journal.ProcessedStepCount: every step
/// below the cursor must have exactly one result, with a matching identifier. Results at or past the
/// cursor are expected (append-before-checkpoint can leave the log ahead after a crash) and are
/// never inspected here - they don't relax or extend what's required below the cursor.
/// </summary>
public static class StepResultReconciler
{
    public static StepResultReconciliationResult Reconcile(
        OperationJournal journal, OperationPlan plan, IReadOnlyList<OperationStepResult> results)
    {
        var resultsByStepIndex = new Dictionary<int, List<OperationStepResult>>();
        foreach (var r in results)
        {
            if (r.StepIndex >= journal.ProcessedStepCount)
                continue; // ahead of the cursor - expected, not inspected

            if (!resultsByStepIndex.TryGetValue(r.StepIndex, out var list))
            {
                list = [];
                resultsByStepIndex[r.StepIndex] = list;
            }

            list.Add(r);
        }

        var stepByIndex = plan.ExecutionSteps.ToDictionary(s => s.StepIndex);

        for (var i = 0; i < journal.ProcessedStepCount; i++)
        {
            if (!resultsByStepIndex.TryGetValue(i, out var matches) || matches.Count == 0)
                return new StepResultReconciliationResult(
                    StepResultReconciliationStatus.Indeterminate, $"Missing result for step {i}.");

            if (matches.Count > 1)
                return new StepResultReconciliationResult(
                    StepResultReconciliationStatus.Indeterminate, $"Duplicate result for step {i}.");

            var expectedIdentifier = stepByIndex.TryGetValue(i, out var step) ? step.Identifier : null;
            if (matches[0].Identifier != expectedIdentifier)
                return new StepResultReconciliationResult(
                    StepResultReconciliationStatus.Indeterminate,
                    $"Result identifier '{matches[0].Identifier}' for step {i} does not match plan identifier '{expectedIdentifier}'.");
        }

        return new StepResultReconciliationResult(StepResultReconciliationStatus.Consistent, null);
    }
}
