namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum ArtifactCheckStatus { Unchecked, Valid, Missing, Invalid }

/// <summary>
/// Checked at most once per discovered recovery bundle (OperationController's PendingRecoveryContext
/// tracks the result so it's never re-checked) - a missing or corrupt artifact is permanent for that
/// bundle's lifetime, so repeating this file I/O every frame would do real work for no benefit.
/// </summary>
public static class ArtifactStatusChecker
{
    public static (ArtifactCheckStatus Status, OperationPlan? Plan) CheckPlan(string bundleDirectory)
    {
        var path = OperationBundlePaths.PlanPath(bundleDirectory);
        if (!File.Exists(path))
            return (ArtifactCheckStatus.Missing, null);
        return OperationPlanCodec.TryLoad(path, out var plan)
            ? (ArtifactCheckStatus.Valid, plan)
            : (ArtifactCheckStatus.Invalid, null);
    }

    public static (ArtifactCheckStatus Status, RollbackSnapshot? Snapshot) CheckSnapshot(string bundleDirectory)
    {
        var path = OperationBundlePaths.SnapshotPath(bundleDirectory);
        if (!File.Exists(path))
            return (ArtifactCheckStatus.Missing, null);
        return OperationSnapshotCodec.TryLoad(path, out var snapshot)
            ? (ArtifactCheckStatus.Valid, snapshot)
            : (ArtifactCheckStatus.Invalid, null);
    }
}
