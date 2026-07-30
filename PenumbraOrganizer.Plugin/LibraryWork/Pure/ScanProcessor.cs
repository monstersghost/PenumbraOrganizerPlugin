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
    private readonly string _npcNameListPath;
    private readonly string _npcNameSeedJson;
    private readonly List<string> _warnings = [];

    private NpcNameMatcher _npcNameMatcher = NpcNameMatcher.Empty;

    public ScanProcessor(string npcNameListPath, string npcNameSeedJson)
    {
        _npcNameListPath = npcNameListPath;
        _npcNameSeedJson = npcNameSeedJson;
    }

    /// <summary> Framework thread reads this after the run completes. </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public void Prepare(CancellationToken ct)
    {
        var result = NpcNameListStore.Load(_npcNameListPath, _npcNameSeedJson);
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
            HeliosphereManaged = HeliosphereDetector.IsHeliosphereManaged(item.Identifier, modPath),
            Category = classification.Category,
            SubCategory = classification.SubCategory,
            GearSlotDiagnostic = gearSlotDiagnostic,
        };
    }
}
