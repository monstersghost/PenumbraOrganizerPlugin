using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.LibrarySearch;

public sealed record LibrarySearchFilter(
    IReadOnlySet<ModCategory> Categories,
    bool IncludeUnknown,
    IReadOnlySet<EquipmentSlot> Slots,
    bool IncludeUnresolved,
    string NameQuery,
    string ItemQuery);

public static class LibrarySearchEngine
{
    public static bool Matches(IndexedMod mod, LibrarySearchFilter filter) =>
        MatchesCategoryFilter(mod, filter) && MatchesTextFilters(mod, filter);

    public static bool MatchesCategoryFilter(IndexedMod mod, LibrarySearchFilter filter)
    {
        var matchesNonGear =
            mod.Categories.Where(c => c != ModCategory.Gear).Any(filter.Categories.Contains)
            || (filter.Categories.Contains(ModCategory.NPC) && mod.MatchedByNpcNameHeuristic)
            || (filter.IncludeUnknown && mod.HasUnknownFacetItems);

        var matchesGear =
            filter.Categories.Contains(ModCategory.Gear)
            && mod.Categories.Contains(ModCategory.Gear)
            && MatchesGearSlotFilter(mod, filter);

        return matchesNonGear || matchesGear;
    }

    public static bool MatchesGearSlotFilter(IndexedMod mod, LibrarySearchFilter filter) =>
        (filter.IncludeUnresolved && mod.EquipmentSlots.Count == 0)
        || mod.EquipmentSlots.Overlaps(filter.Slots);

    public static bool MatchesTextFilters(IndexedMod mod, LibrarySearchFilter filter)
    {
        var nameQuery = Normalize(filter.NameQuery);
        if (nameQuery.Length > 0 && !mod.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
            return false;

        var itemQuery = Normalize(filter.ItemQuery);
        if (itemQuery.Length > 0 && !mod.ChangedItems.Any(item => item.Key.Contains(itemQuery, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    public static (IReadOnlyList<IndexedChangedItem> Items, bool MatchedByNameOnly) DisplayedItems(
        IndexedMod mod, LibrarySearchFilter filter)
    {
        var itemQuery = Normalize(filter.ItemQuery);
        IReadOnlyList<IndexedChangedItem> afterItemText = itemQuery.Length > 0
            ? mod.ChangedItems.Where(item => item.Key.Contains(itemQuery, StringComparison.OrdinalIgnoreCase)).ToList()
            : mod.ChangedItems;

        var afterCategory = afterItemText
            .Where(item => (item.Facet is { } facet && filter.Categories.Contains(facet))
                            || (item.Facet is null && filter.IncludeUnknown))
            .ToList();

        var matchedByNameOnly = afterCategory.Count == 0
            && filter.Categories.Contains(ModCategory.NPC)
            && mod.MatchedByNpcNameHeuristic
            && !mod.Categories.Contains(ModCategory.NPC);

        return matchedByNameOnly ? (afterItemText, true) : (afterCategory, false);
    }

    private static string Normalize(string query) => query.Trim();
}
