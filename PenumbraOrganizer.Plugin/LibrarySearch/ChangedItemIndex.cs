using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.LibrarySearch;

public sealed record IndexedChangedItem(string Key, ModCategory? Facet); // null = unrecognized shape

public sealed record IndexedMod(
    string Identifier,
    string Name,
    string Author,
    IReadOnlyList<IndexedChangedItem> ChangedItems,
    IReadOnlySet<ModCategory> Categories,      // union of non-null ChangedItems[].Facet — item evidence ONLY
    bool HasUnknownFacetItems,                 // true if any ChangedItems[].Facet is null
    bool MatchedByNpcNameHeuristic,            // separate provenance flag, never folded into Categories
    IReadOnlySet<EquipmentSlot> EquipmentSlots,
    GearSlotDiagnostic SlotDiagnostic);

public sealed record ChangedItemIndex(
    IReadOnlyList<IndexedMod> Mods,           // only mods with >= 1 changed item
    int TotalModsSeen,                        // every mod GetModListAdapter returned, including 0-item ones
    int OrphanedChangedItemEntryCount,        // dictionary entries whose identifier matched no mod
    DateTime BuiltAt);
