using Penumbra.Api.IpcSubscribers;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;

namespace PenumbraOrganizer.Plugin.LibraryWork;

/// <summary> Framework-thread half of a Search index build. Mirrors ScanJob. </summary>
public sealed class IndexJob : ILibraryWorkJob<IndexSeed, IndexedMod>
{
    private readonly Plugin _plugin;
    private readonly string _npcNameListPath;
    private readonly string _npcNameSeedJson;
    private IndexProcessor? _processor;
    private HashSet<string> _changedItemIdentifiers = new(StringComparer.Ordinal);
    private List<string> _allModIdentifiers = [];

    public IndexJob(Plugin plugin, string npcNameListPath, string npcNameSeedJson)
    {
        _plugin = plugin;
        _npcNameListPath = npcNameListPath;
        _npcNameSeedJson = npcNameSeedJson;
    }

    public string DisplayName => "Search index";

    public LibraryWorkBatch<IndexSeed, IndexedMod> Materialize()
    {
        var allChangedItems = new GetChangedItemAdapterDictionary(Plugin.PluginInterface).Invoke();

        using var modList = _plugin.GetModListAdapterIpc.Invoke();

        var seeds = modList.Select(mod => new IndexSeed(
            Identifier: mod.Identifier,
            Name: mod.Name,
            Author: mod.Author,
            ModDirectoryPath: mod.ModPath.FullName,
            ChangedItemKeys: allChangedItems.TryGetValue(mod.Identifier, out var changedItems)
                ? changedItems.Keys.ToList()
                : [])).ToList();

        // Both are needed at publish time and neither is derivable from the processed results:
        // IndexProcessor drops zero-changed-item mods, but TotalModsSeen and the orphan count are
        // both defined over every mod Penumbra returned.
        _changedItemIdentifiers = allChangedItems.Keys.ToHashSet(StringComparer.Ordinal);
        _allModIdentifiers = seeds.Select(seed => seed.Identifier).ToList();

        _processor = new IndexProcessor(_npcNameListPath, _npcNameSeedJson);
        return new LibraryWorkBatch<IndexSeed, IndexedMod>(seeds, _processor);
    }

    public void Publish(IReadOnlyList<IndexedMod> results)
    {
        foreach (var warning in _processor?.Warnings ?? [])
            Plugin.Log.Warning(warning);

        // Atomic replacement: LibraryIndex is only assigned here, after every phase succeeded. A
        // failed or discarded run leaves the previous index and its BuiltAt timestamp exactly as
        // they were - a failed refresh must not discard a previously good result.
        _plugin.SetLibraryIndex(
            ChangedItemIndexBuilder.Assemble(results, _allModIdentifiers, _changedItemIdentifiers));
    }
}
