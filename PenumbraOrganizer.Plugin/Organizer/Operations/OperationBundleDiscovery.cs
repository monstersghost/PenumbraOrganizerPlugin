namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public sealed record OperationDiscoveryResult(
    OperationRecoveryGraphResult Graph,
    IReadOnlyDictionary<Guid, OperationJournal> Journals);

/// <summary>
/// Design doc section 4a. Startup entry point: relocate any already-terminal bundle sitting under
/// active/ to completed/ (self-healing, not a recovery condition), then load the remaining
/// non-terminal journals and hand them to OperationRecoveryGraph. A journal that fails to load is
/// logged (by the caller, in a later plan - this class has no logging dependency) and excluded, not
/// treated as fatal to startup.
/// </summary>
public static class OperationBundleDiscovery
{
    public static OperationDiscoveryResult RunStartupDiscovery(string operationsRoot)
    {
        RelocateTerminalActiveBundles(operationsRoot);
        var journals = LoadNonTerminalActiveJournals(operationsRoot);
        var graph = OperationRecoveryGraph.Analyze(journals.Values.ToList());
        return new OperationDiscoveryResult(graph, journals);
    }

    private static void RelocateTerminalActiveBundles(string operationsRoot)
    {
        var activeDir = OperationBundlePaths.ActiveDirectory(operationsRoot);
        if (!Directory.Exists(activeDir))
            return;

        foreach (var bundleDir in Directory.GetDirectories(activeDir))
        {
            if (!OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDir), out var journal) || journal is null)
                continue; // corrupt/unparseable - leave it for a human, not our concern here
            if (!journal.IsTerminal)
                continue;

            var completedBundleDir = OperationBundlePaths.BundleDirectory(operationsRoot, active: false, journal.OperationId);
            try
            {
                if (Directory.Exists(completedBundleDir))
                    continue; // already relocated by something else - don't clobber it
                Directory.CreateDirectory(OperationBundlePaths.CompletedDirectory(operationsRoot));
                Directory.Move(bundleDir, completedBundleDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One unmovable bundle must not block startup or the rest of this pass.
            }
        }
    }

    private static Dictionary<Guid, OperationJournal> LoadNonTerminalActiveJournals(string operationsRoot)
    {
        var result = new Dictionary<Guid, OperationJournal>();
        var activeDir = OperationBundlePaths.ActiveDirectory(operationsRoot);
        if (!Directory.Exists(activeDir))
            return result;

        foreach (var bundleDir in Directory.GetDirectories(activeDir))
        {
            if (!OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDir), out var journal) || journal is null)
                continue;
            if (journal.IsTerminal)
                continue; // should have been relocated already; defensively excluded either way

            result[journal.OperationId] = journal;
        }

        return result;
    }

    public static IReadOnlyList<OperationJournal> LoadRecentCompletedJournals(string operationsRoot, int take)
    {
        if (take <= 0)
            return [];

        var completedDir = OperationBundlePaths.CompletedDirectory(operationsRoot);
        if (!Directory.Exists(completedDir))
            return [];

        var journals = new List<OperationJournal>();
        foreach (var bundleDir in Directory.GetDirectories(completedDir))
        {
            if (OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDir), out var journal) && journal is not null && journal.IsTerminal)
                journals.Add(journal);
        }

        return journals.OrderByDescending(j => j.UpdatedAt).Take(take).ToList();
    }
}
