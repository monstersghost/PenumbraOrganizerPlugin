namespace PenumbraOrganizer.Plugin.LibraryWork;

/// <summary>
/// The body of the external activity gate Plugin injects into OperationController. A pure function
/// rather than a lambda written inline at the construction site, so the one rule ("no operation
/// while library work runs") is testable without Dalamud. This is NOT an admission authority - the
/// controller is; this only answers the controller's question about the one thing it cannot see.
/// </summary>
public static class LibraryActivityGate
{
    /// <summary> Null when no library work blocks admission; otherwise the reason. </summary>
    public static string? Reason(LibraryWorkStateSnapshot scan, LibraryWorkStateSnapshot index)
    {
        if (scan.IsRunning)
            return $"{scan.JobDisplayName ?? "A scan"} is still running.";
        if (index.IsRunning)
            return $"{index.JobDisplayName ?? "An index build"} is still running.";

        return null;
    }
}
