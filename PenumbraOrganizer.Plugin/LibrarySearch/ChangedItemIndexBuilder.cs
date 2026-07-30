namespace PenumbraOrganizer.Plugin.LibrarySearch;

public static class ChangedItemIndexBuilder
{
    /// <summary>
    /// Final assembly from already-processed mods. Split out of Build so the per-mod work can run on
    /// a background thread (see LibraryWork.Pure.IndexProcessor) while this stays on the framework
    /// thread. allModIdentifiers must list every mod Penumbra returned, including the zero-changed-
    /// item ones IndexProcessor excludes from indexedMods - both TotalModsSeen and the orphan count
    /// are defined over the full set, not the indexed subset.
    /// </summary>
    public static ChangedItemIndex Assemble(
        IReadOnlyList<IndexedMod> indexedMods,
        IReadOnlyList<string> allModIdentifiers,
        IReadOnlySet<string> modIdentifiersWithChangedItems)
    {
        var orphanedCount = modIdentifiersWithChangedItems
            .Except(allModIdentifiers, StringComparer.Ordinal)
            .Count();

        return new ChangedItemIndex(indexedMods, allModIdentifiers.Count, orphanedCount, DateTime.Now);
    }
}
