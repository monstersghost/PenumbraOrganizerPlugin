using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.LibraryWork.Pure;

/// <summary>
/// The whole of the scan's per-mod work: classification, NPC name matching, and the gear-slot and
/// Heliosphere disk probes. Lifted verbatim from the old synchronous Plugin.RunScan body, with the
/// Penumbra adapter reads left behind on the framework thread in ScanJob.
///
/// May not reference Dalamud or Penumbra types - LibraryWorkPurityTests enforces this. Warnings are
/// collected rather than logged so the framework thread can log them at publish time instead of this
/// class reaching for IPluginLog off-thread.
/// </summary>
public sealed class ScanProcessor : ILibraryWorkProcessor<ScanSeed, OrganizerModRow>
{
    private readonly string _configDirectory;
    private readonly bool _useScrapedNpcNameList;
    private readonly IReadOnlySet<string> _knownHeliosphereIdentifiers;
    private readonly List<string> _warnings = [];

    private NpcNameMatcher _npcNameMatcher = NpcNameMatcher.Empty;

    /// <param name="useScrapedNpcNameList">
    /// Already the conjunction of the config flag and the compile-time feature gate - ScanJob
    /// resolves it on the framework thread. This class never reads Configuration itself.
    /// </param>
    /// <param name="knownHeliosphereIdentifiers">
    /// Identifiers an earlier scan already resolved as Heliosphere-managed. Carried so a mod whose
    /// heliosphere.json is momentarily absent - which is precisely what an in-progress Heliosphere
    /// update looks like - is not silently unprotected and then moved by the next Apply. Defaults to
    /// empty so existing tests construct this unchanged.
    /// </param>
    public ScanProcessor(
        string configDirectory,
        bool useScrapedNpcNameList,
        IReadOnlySet<string>? knownHeliosphereIdentifiers = null)
    {
        _configDirectory = configDirectory;
        _useScrapedNpcNameList = useScrapedNpcNameList;
        _knownHeliosphereIdentifiers = knownHeliosphereIdentifiers ?? new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary> Framework thread reads this after the run completes. </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public void Prepare(CancellationToken ct)
    {
        var result = NpcNameListStore.LoadForMatching(_configDirectory, _useScrapedNpcNameList);
        if (result.Warning is not null)
            _warnings.Add(result.Warning);
        _npcNameMatcher = NpcNameListStore.BuildMatcher(result.Document);
    }

    public OrganizerModRow? Process(ScanSeed item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var modPath = new DirectoryInfo(item.ModDirectoryPath);
        var classification = ModTypeClassifier.Classify(item.Name, item.ChangedItemKeys, _npcNameMatcher);

        // Disk I/O only for mods the changed-items rule already confirmed are Gear - every other
        // category never touches disk for this.
        var gearSlotDiagnostic = GearSlotDiagnostic.NotApplicable;
        if (classification.Category == ModCategory.Gear)
        {
            var equipmentSlots = ModEquipmentFileReader.ReadEquipmentSlots(modPath);
            classification = ModTypeClassifier.EnrichGearSubCategory(classification, equipmentSlots);

            gearSlotDiagnostic = equipmentSlots switch
            {
                null => GearSlotDiagnostic.ReadFailure,
                { Count: 0 } when !modPath.Exists => GearSlotDiagnostic.DirectoryMissing,
                { Count: 0 } => GearSlotDiagnostic.ZeroEvidence,
                { Count: 1 } => GearSlotDiagnostic.Single,
                _ => GearSlotDiagnostic.Ambiguous,
            };
        }

        return new OrganizerModRow
        {
            Identifier = item.Identifier,
            Name = item.Name,
            Author = item.Author,
            CurrentPath = item.CurrentPath,
            ProposedPath = item.CurrentPath,
            HeliosphereManaged = HeliosphereDetector.IsHeliosphereManaged(
                item.Identifier, modPath, item.Name, _knownHeliosphereIdentifiers),
            Category = classification.Category,
            SubCategory = classification.SubCategory,
            GearSlotDiagnostic = gearSlotDiagnostic,
        };
    }
}
