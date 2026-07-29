namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary> Everything the controller needs to begin an operation, produced by the caller's
/// preparation step inside TryStart. </summary>
public sealed record PreparedOperation(OperationPlan Plan, Guid SnapshotId, string BundleDirectory);

/// <summary>
/// Structured admission outcome. Rejection is a value, not an exception, so a caller can decide
/// whether being turned away is exceptional for it - the recovery paths, for instance, treat a
/// rejected refresh scan as ordinary.
/// </summary>
public sealed record OperationStartResult(bool Started, string? RejectionReason)
{
    public static OperationStartResult Ok { get; } = new(true, null);

    public static OperationStartResult Rejected(string reason) => new(false, reason);
}
