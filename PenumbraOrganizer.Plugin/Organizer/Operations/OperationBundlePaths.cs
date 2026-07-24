namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// The single source of truth for the operation storage directory layout (design doc section 4a).
/// One directory per operation ID makes duplicate-ID collisions structurally impossible - the
/// filesystem itself is the uniqueness constraint, no runtime check needed for that failure mode.
/// </summary>
public static class OperationBundlePaths
{
    public static string ActiveDirectory(string operationsRoot) => Path.Combine(operationsRoot, "active");

    public static string CompletedDirectory(string operationsRoot) => Path.Combine(operationsRoot, "completed");

    public static string DiagnosticsLogPath(string operationsRoot) => Path.Combine(operationsRoot, "diagnostics.jsonl");

    public static string BundleDirectory(string operationsRoot, bool active, Guid operationId) =>
        Path.Combine(active ? ActiveDirectory(operationsRoot) : CompletedDirectory(operationsRoot), operationId.ToString());

    public static string JournalPath(string bundleDirectory) => Path.Combine(bundleDirectory, "journal.json");

    public static string PlanPath(string bundleDirectory) => Path.Combine(bundleDirectory, "plan.json");

    public static string SnapshotPath(string bundleDirectory) => Path.Combine(bundleDirectory, "snapshot.json");

    public static string ResultsPath(string bundleDirectory) => Path.Combine(bundleDirectory, "results.jsonl");

    public static string RestoreResultSeedPath(string bundleDirectory) => Path.Combine(bundleDirectory, "restore-result-seed.json");
}
