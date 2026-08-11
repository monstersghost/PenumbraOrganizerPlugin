using Penumbra.Api.IpcSubscribers;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.LibraryWork;

/// <summary>
/// Framework-thread half of a scan. Materialize() is the only place Penumbra's adapters are touched,
/// and it releases both before returning - previously the mod-list adapter (a synchronized list, per
/// Penumbra's own API docs) was held across the entire per-mod disk walk.
/// </summary>
public sealed class ScanJob : ILibraryWorkJob<ScanSeed, OrganizerModRow>
{
    private readonly Plugin _plugin;
    private readonly string _configDirectory;
    private readonly bool _useScrapedNpcNameList;
    private ScanProcessor? _processor;

    public ScanJob(Plugin plugin, string configDirectory, bool useScrapedNpcNameList)
    {
        _plugin = plugin;
        _configDirectory = configDirectory;
        _useScrapedNpcNameList = useScrapedNpcNameList;
    }

    public string DisplayName => "Scan";

    public LibraryWorkBatch<ScanSeed, OrganizerModRow> Materialize()
    {
        // One bulk call for all mods' changed items. If Penumbra is unavailable this throws, and the
        // coordinator turns it into a Failed outcome with the message intact.
        var allChangedItems = new GetChangedItemAdapterDictionary(Plugin.PluginInterface).Invoke();

        using var modList = _plugin.GetModListAdapterIpc.Invoke();

        var seeds = modList.Select(mod => new ScanSeed(
            Identifier: mod.Identifier,
            Name: mod.Name,
            Author: mod.Author,
            CurrentPath: mod.FullPath,
            ModDirectoryPath: mod.ModPath.FullName,
            ChangedItemKeys: allChangedItems.TryGetValue(mod.Identifier, out var changedItems)
                ? changedItems.Keys.ToList()
                : [])).ToList();

        _processor = new ScanProcessor(_configDirectory, _useScrapedNpcNameList);
        return new LibraryWorkBatch<ScanSeed, OrganizerModRow>(seeds, _processor);
    }

    public void Publish(IReadOnlyList<OrganizerModRow> results)
    {
        foreach (var warning in _processor?.Warnings ?? [])
            Plugin.Log.Warning(warning);

        // THE COMMIT. Build-then-swap: either the whole new state is installed or none of it is.
        // Anything above this line that throws leaves the previous scan completely intact.
        _plugin.OrganizerState.ReplaceScanAtomically(
            results, _plugin.Config.ProtectedModIdentifiers, _plugin.Config.ProtectedFolderPaths);

        // POST-COMMIT. The new data is already live, so a failure here is a warning, never a failed
        // run - reporting Failed would tell the UI to say nothing was published when it was.
        _plugin.RunPostScanSideEffects();
    }
}
