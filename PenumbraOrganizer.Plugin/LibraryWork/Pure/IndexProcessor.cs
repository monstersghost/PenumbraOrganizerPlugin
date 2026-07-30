using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.LibraryWork.Pure;

/// <summary>
/// The Search index's per-mod work: changed-item facet classification, NPC name matching, and the
/// gear-slot disk probe. Same purity rule as ScanProcessor.
/// </summary>
public sealed class IndexProcessor : ILibraryWorkProcessor<IndexSeed, IndexedMod>
{
    private readonly string _npcNameListPath;
    private readonly string _npcNameSeedJson;
    private readonly List<string> _warnings = [];

    private NpcNameMatcher _npcNameMatcher = NpcNameMatcher.Empty;

    public IndexProcessor(string npcNameListPath, string npcNameSeedJson)
    {
        _npcNameListPath = npcNameListPath;
        _npcNameSeedJson = npcNameSeedJson;
    }

    public IReadOnlyList<string> Warnings => _warnings;

    public void Prepare(CancellationToken ct)
    {
        var result = NpcNameListStore.Load(_npcNameListPath, _npcNameSeedJson);
        if (result.Warning is not null)
            _warnings.Add(result.Warning);
        _npcNameMatcher = NpcNameListStore.BuildMatcher(result.Document);
    }

    public IndexedMod? Process(IndexSeed item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (item.ChangedItemKeys.Count == 0)
            return null; // zero-changed-item mods are excluded from the browsable index

        var changedItems = item.ChangedItemKeys
            .Select(key => new IndexedChangedItem(
                key, ModTypeClassifier.ClassifyKeyFacet(ChangedItemKeyParser.Parse(key))))
            .ToList();

        var categories = changedItems
            .Where(indexed => indexed.Facet is not null)
            .Select(indexed => indexed.Facet!.Value)
            .ToHashSet();
        var hasUnknownFacetItems = changedItems.Any(indexed => indexed.Facet is null);
        var matchedByNpcNameHeuristic = _npcNameMatcher.Match(item.Name) is not null;

        IReadOnlySet<EquipmentSlot> equipmentSlots = new HashSet<EquipmentSlot>();
        var slotDiagnostic = GearSlotDiagnostic.NotApplicable;
        if (categories.Contains(ModCategory.Gear))
        {
            var modPath = new DirectoryInfo(item.ModDirectoryPath);
            var resolvedSlots = ModEquipmentFileReader.ReadEquipmentSlots(modPath);
            slotDiagnostic = resolvedSlots switch
            {
                null => GearSlotDiagnostic.ReadFailure,
                { Count: 0 } when !modPath.Exists => GearSlotDiagnostic.DirectoryMissing,
                { Count: 0 } => GearSlotDiagnostic.ZeroEvidence,
                { Count: 1 } => GearSlotDiagnostic.Single,
                _ => GearSlotDiagnostic.Ambiguous,
            };
            equipmentSlots = resolvedSlots ?? new HashSet<EquipmentSlot>();
        }

        return new IndexedMod(
            item.Identifier, item.Name, item.Author, changedItems, categories,
            hasUnknownFacetItems, matchedByNpcNameHeuristic, equipmentSlots, slotDiagnostic);
    }
}
