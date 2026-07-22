namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Design doc section 4a retention rules. Only touches completed/ - active/ bundles are retained
/// indefinitely and never considered here. A bundle whose journal fails to load is excluded from
/// the loaded set entirely, which means it can never be a deletion candidate - protection by
/// omission, matching "never delete when reference analysis is inconclusive".
/// </summary>
public static class OperationBundleRetention
{
    private const int DefaultRetainNewestCount = 50;
    private static readonly TimeSpan DefaultRetainAge = TimeSpan.FromDays(30);

    public static void RunRetentionPass(
        string operationsRoot, DateTimeOffset now,
        int retainNewestCount = DefaultRetainNewestCount, TimeSpan? retainAge = null)
    {
        var age = retainAge ?? DefaultRetainAge;
        var completedDir = OperationBundlePaths.CompletedDirectory(operationsRoot);
        if (!Directory.Exists(completedDir))
            return;

        var loaded = new Dictionary<Guid, OperationJournal>();
        foreach (var bundleDir in Directory.GetDirectories(completedDir))
        {
            if (OperationJournalCodec.TryLoad(OperationBundlePaths.JournalPath(bundleDir), out var journal) && journal is not null)
                loaded[journal.OperationId] = journal;
        }

        var newest = loaded.Values
            .OrderByDescending(j => j.UpdatedAt)
            .Take(retainNewestCount)
            .Select(j => j.OperationId)
            .ToHashSet();

        var retained = new HashSet<Guid>();
        foreach (var (id, journal) in loaded)
        {
            if (now - journal.UpdatedAt <= age || newest.Contains(id))
                retained.Add(id);
        }

        // Transitive closure: a bundle referenced by a retained bundle is retained too, however far up the chain.
        var toProcess = new Queue<Guid>(retained);
        while (toProcess.Count > 0)
        {
            var id = toProcess.Dequeue();
            if (loaded.TryGetValue(id, out var journal)
                && journal.RecoveryOfOperationId is { } parentId
                && loaded.ContainsKey(parentId)
                && retained.Add(parentId))
            {
                toProcess.Enqueue(parentId);
            }
        }

        foreach (var id in loaded.Keys)
        {
            if (retained.Contains(id))
                continue;

            try
            {
                Directory.Delete(OperationBundlePaths.BundleDirectory(operationsRoot, active: false, id), recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One undeletable bundle must not prevent plugin startup or block cleanup of the rest.
            }
        }
    }
}
