using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.LibrarySearch;

public sealed record LibraryModEntry(string Identifier, string Name, string Author, DirectoryInfo ModPath);

public static class ChangedItemIndexBuilder
{
    public static ChangedItemIndex Build(
        IReadOnlyList<LibraryModEntry> mods,
        IReadOnlySet<string> modIdentifiersWithChangedItems,
        Func<string, IEnumerable<string>> changedItemKeysByIdentifier,
        NpcNameMatcher npcNameMatcher)
    {
        var indexedMods = new List<IndexedMod>();

        foreach (var mod in mods)
        {
            var changedItemKeys = changedItemKeysByIdentifier(mod.Identifier).ToList();
            if (changedItemKeys.Count == 0)
                continue; // zero-changed-item mods are excluded from the browsable index

            var changedItems = changedItemKeys
                .Select(key => new IndexedChangedItem(
                    key, ModTypeClassifier.ClassifyKeyFacet(ChangedItemKeyParser.Parse(key))))
                .ToList();

            var categories = changedItems
                .Where(item => item.Facet is not null)
                .Select(item => item.Facet!.Value)
                .ToHashSet();
            var hasUnknownFacetItems = changedItems.Any(item => item.Facet is null);
            var matchedByNpcNameHeuristic = npcNameMatcher.Match(mod.Name) is not null;

            IReadOnlySet<EquipmentSlot> equipmentSlots = new HashSet<EquipmentSlot>();
            var slotDiagnostic = GearSlotDiagnostic.NotApplicable;
            if (categories.Contains(ModCategory.Gear))
            {
                var resolvedSlots = ModEquipmentFileReader.ReadEquipmentSlots(mod.ModPath);
                slotDiagnostic = resolvedSlots switch
                {
                    null => GearSlotDiagnostic.ReadFailure,
                    { Count: 0 } when !mod.ModPath.Exists => GearSlotDiagnostic.DirectoryMissing,
                    { Count: 0 } => GearSlotDiagnostic.ZeroEvidence,
                    { Count: 1 } => GearSlotDiagnostic.Single,
                    _ => GearSlotDiagnostic.Ambiguous,
                };
                equipmentSlots = resolvedSlots ?? new HashSet<EquipmentSlot>();
            }

            indexedMods.Add(new IndexedMod(
                mod.Identifier, mod.Name, mod.Author, changedItems, categories,
                hasUnknownFacetItems, matchedByNpcNameHeuristic, equipmentSlots, slotDiagnostic));
        }

        return Assemble(indexedMods, mods.Select(m => m.Identifier).ToList(), modIdentifiersWithChangedItems);
    }

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
