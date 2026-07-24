using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationBundlePathsTests
{
    private const string Root = @"C:\config\operations";
    private static readonly Guid OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void ActiveDirectory_IsRootSlashActive()
    {
        Assert.Equal(Path.Combine(Root, "active"), OperationBundlePaths.ActiveDirectory(Root));
    }

    [Fact]
    public void CompletedDirectory_IsRootSlashCompleted()
    {
        Assert.Equal(Path.Combine(Root, "completed"), OperationBundlePaths.CompletedDirectory(Root));
    }

    [Fact]
    public void DiagnosticsLogPath_IsRootSlashDiagnosticsJsonl()
    {
        Assert.Equal(Path.Combine(Root, "diagnostics.jsonl"), OperationBundlePaths.DiagnosticsLogPath(Root));
    }

    [Fact]
    public void BundleDirectory_Active_IsUnderActiveDirectoryNamedByOperationId()
    {
        var expected = Path.Combine(Root, "active", OperationId.ToString());
        Assert.Equal(expected, OperationBundlePaths.BundleDirectory(Root, active: true, OperationId));
    }

    [Fact]
    public void BundleDirectory_Completed_IsUnderCompletedDirectoryNamedByOperationId()
    {
        var expected = Path.Combine(Root, "completed", OperationId.ToString());
        Assert.Equal(expected, OperationBundlePaths.BundleDirectory(Root, active: false, OperationId));
    }

    [Fact]
    public void JournalPlanSnapshotResults_AreNamedFilesUnderTheBundleDirectory()
    {
        var bundleDir = OperationBundlePaths.BundleDirectory(Root, active: true, OperationId);

        Assert.Equal(Path.Combine(bundleDir, "journal.json"), OperationBundlePaths.JournalPath(bundleDir));
        Assert.Equal(Path.Combine(bundleDir, "plan.json"), OperationBundlePaths.PlanPath(bundleDir));
        Assert.Equal(Path.Combine(bundleDir, "snapshot.json"), OperationBundlePaths.SnapshotPath(bundleDir));
        Assert.Equal(Path.Combine(bundleDir, "results.jsonl"), OperationBundlePaths.ResultsPath(bundleDir));
        Assert.Equal(Path.Combine(bundleDir, "restore-result-seed.json"), OperationBundlePaths.RestoreResultSeedPath(bundleDir));
    }
}
