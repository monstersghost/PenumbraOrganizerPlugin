using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class LastOperationFailuresTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lof-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteBundle(OperationType type, DateTimeOffset updated, params OperationStepResult[] results)
    {
        var id = Guid.NewGuid();
        var dir = OperationBundlePaths.BundleDirectory(_root, active: false, id);
        Directory.CreateDirectory(dir);

        OperationJournalCodec.Save(OperationBundlePaths.JournalPath(dir), new OperationJournal(
            OperationJournal.CurrentSchemaVersion, id, type, OperationStage.CompletedWithItemFailures,
            OperationResolution.None, null, false, updated, 1, 1, null, Guid.NewGuid(), Guid.NewGuid(),
            "h", null, updated));

        var plan = OperationPlan.Create(
            type,
            [new OperationExecutionStep(0, "Bibo+", "Body/Bibo+/Bibo+", OperationStepKind.FinalMove, 0)],
            [new OperationRecoveryTarget("Bibo+", "Body/Bibo+", "Body/Bibo+/Bibo+", "Bibo+ Body")]);
        OperationPlanCodec.Save(OperationBundlePaths.PlanPath(dir), plan);

        foreach (var r in results)
            StepResultLog.Append(OperationBundlePaths.ResultsPath(dir), r);
    }

    private static OperationStepResult Result(string id, OperationStepDisposition d, string? ipc, string? detail) =>
        new(0, id, d, ipc, detail, DateTimeOffset.UtcNow, 0);

    [Fact]
    public void NoOperations_ReturnsNull() => Assert.Null(LastOperationFailures.Load(_root));

    [Fact]
    public void LatestOperation_ListsOnlyItsFailedSteps_WithModNameAndReason()
    {
        WriteBundle(OperationType.Apply, DateTimeOffset.UtcNow.AddHours(-1),
            Result("Old", OperationStepDisposition.Failed, "PathRenameFailed", "old"));
        WriteBundle(OperationType.Apply, DateTimeOffset.UtcNow,
            Result("Bibo+", OperationStepDisposition.Failed, "PathRenameFailed", "Penumbra returned PathRenameFailed."),
            Result("Fine", OperationStepDisposition.Succeeded, null, null));

        var report = LastOperationFailures.Load(_root)!;

        var failure = Assert.Single(report.Failures);
        Assert.Equal("Bibo+ Body", failure.ModName);
        Assert.Equal("Body/Bibo+/Bibo+", failure.TargetPath);
        Assert.Equal("PathRenameFailed", failure.Reason);
        Assert.Equal(OperationType.Apply, report.Type);
    }

    [Fact]
    public void FailureWithNoPlanEntry_FallsBackToTheIdentifier()
    {
        WriteBundle(OperationType.Apply, DateTimeOffset.UtcNow,
            Result("Unknown", OperationStepDisposition.Failed, null, "boom"));

        var failure = Assert.Single(LastOperationFailures.Load(_root)!.Failures);

        Assert.Equal("Unknown", failure.ModName);
        Assert.Equal("boom", failure.Reason);
    }
}
