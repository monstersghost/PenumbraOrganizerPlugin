using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibrarySearch;

public class LibrarySearchFilterTests
{
    private static IndexedMod MakeMod(
        string name = "Test Mod",
        IEnumerable<ModCategory>? categories = null,
        bool hasUnknownFacetItems = false,
        bool matchedByNpcNameHeuristic = false,
        IEnumerable<EquipmentSlot>? equipmentSlots = null,
        IEnumerable<IndexedChangedItem>? changedItems = null) =>
        new("id", name, "Author",
            (changedItems ?? [new IndexedChangedItem("Some Item", ModCategory.Gear)]).ToList(),
            (categories ?? [ModCategory.Gear]).ToHashSet(),
            hasUnknownFacetItems, matchedByNpcNameHeuristic,
            (equipmentSlots ?? []).ToHashSet(),
            GearSlotDiagnostic.NotApplicable);

    private static LibrarySearchFilter MakeFilter(
        IEnumerable<ModCategory>? categories = null,
        bool includeUnknown = true,
        IEnumerable<EquipmentSlot>? slots = null,
        bool includeUnresolved = true,
        string nameQuery = "",
        string itemQuery = "") =>
        new(
            (categories ?? Enum.GetValues<ModCategory>()).ToHashSet(),
            includeUnknown,
            (slots ?? Enum.GetValues<EquipmentSlot>()).ToHashSet(),
            includeUnresolved,
            nameQuery,
            itemQuery);

    [Fact] // The design-review bug fix: an NPC+Gear mod must match on NPC regardless of slot state
    public void Matches_MixedGearAndNpcMod_MatchesOnNpcWithNoSlotsSelected()
    {
        var mod = MakeMod(categories: [ModCategory.Gear, ModCategory.NPC]);
        var filter = MakeFilter(categories: [ModCategory.NPC], slots: [], includeUnresolved: false);

        Assert.True(LibrarySearchEngine.Matches(mod, filter));
    }

    [Fact]
    public void Matches_GearOnlyMod_ExcludedWhenNoSlotsAndUnresolvedBothOff()
    {
        var mod = MakeMod(categories: [ModCategory.Gear], equipmentSlots: []);
        var filter = MakeFilter(categories: [ModCategory.Gear], slots: [], includeUnresolved: false);

        Assert.False(LibrarySearchEngine.Matches(mod, filter));
    }

    [Fact]
    public void Matches_NonGearMod_UnaffectedByAnySlotOrUnresolvedToggleState()
    {
        var mod = MakeMod(categories: [ModCategory.VFX]);
        var filter = MakeFilter(categories: [ModCategory.VFX], slots: [], includeUnresolved: false);

        Assert.True(LibrarySearchEngine.Matches(mod, filter));
    }

    [Fact]
    public void Matches_ZeroCategoriesSelected_NeverMatches()
    {
        var mod = MakeMod(categories: [ModCategory.Gear, ModCategory.NPC]);
        var filter = MakeFilter(categories: []);

        Assert.False(LibrarySearchEngine.Matches(mod, filter));
    }

    [Fact]
    public void Matches_MultiSlotMod_MatchesEverySlotToggleItOverlaps()
    {
        var mod = MakeMod(categories: [ModCategory.Gear], equipmentSlots: [EquipmentSlot.Top, EquipmentSlot.Feet]);

        Assert.True(LibrarySearchEngine.Matches(mod, MakeFilter(categories: [ModCategory.Gear], slots: [EquipmentSlot.Top])));
        Assert.True(LibrarySearchEngine.Matches(mod, MakeFilter(categories: [ModCategory.Gear], slots: [EquipmentSlot.Feet])));
        Assert.False(LibrarySearchEngine.Matches(mod, MakeFilter(categories: [ModCategory.Gear], slots: [EquipmentSlot.Head], includeUnresolved: false)));
    }

    [Fact]
    public void Matches_UnresolvedGearMod_HiddenWhenUnresolvedToggleOff()
    {
        var mod = MakeMod(categories: [ModCategory.Gear], equipmentSlots: []);
        var shown = MakeFilter(categories: [ModCategory.Gear], slots: [], includeUnresolved: true);
        var hidden = MakeFilter(categories: [ModCategory.Gear], slots: [], includeUnresolved: false);

        Assert.True(LibrarySearchEngine.Matches(mod, shown));
        Assert.False(LibrarySearchEngine.Matches(mod, hidden));
    }

    [Fact]
    public void Matches_UnknownFacetMod_GatedByIncludeUnknownToggle()
    {
        var mod = MakeMod(categories: [], hasUnknownFacetItems: true);

        Assert.True(LibrarySearchEngine.Matches(mod, MakeFilter(categories: [], includeUnknown: true)));
        Assert.False(LibrarySearchEngine.Matches(mod, MakeFilter(categories: [], includeUnknown: false)));
    }

    [Fact]
    public void Matches_NameQuery_IsOrdinalCaseInsensitiveAndTrimmed()
    {
        var mod = MakeMod(name: "Carlotta's Outfit");

        Assert.True(LibrarySearchEngine.Matches(mod, MakeFilter(nameQuery: "  CARLOTTA  ")));
        Assert.False(LibrarySearchEngine.Matches(mod, MakeFilter(nameQuery: "Nonexistent")));
    }

    [Fact]
    public void Matches_ItemQuery_RequiresAtLeastOneMatchingKey()
    {
        var mod = MakeMod(changedItems: [new IndexedChangedItem("Calfskin Rider's Shoes", ModCategory.Gear)]);

        Assert.True(LibrarySearchEngine.Matches(mod, MakeFilter(itemQuery: "shoes")));
        Assert.False(LibrarySearchEngine.Matches(mod, MakeFilter(itemQuery: "boots")));
    }

    [Fact]
    public void DisplayedItems_CategoryFilter_NarrowsToMatchingFacetItemsOnly()
    {
        var mod = MakeMod(
            categories: [ModCategory.Gear, ModCategory.VFX],
            changedItems:
            [
                new IndexedChangedItem("Calfskin Rider's Shoes", ModCategory.Gear),
                new IndexedChangedItem("Vfx", ModCategory.VFX),
            ]);
        var filter = MakeFilter(categories: [ModCategory.Gear]);

        var (items, matchedByNameOnly) = LibrarySearchEngine.DisplayedItems(mod, filter);

        Assert.Equal(["Calfskin Rider's Shoes"], items.Select(i => i.Key));
        Assert.False(matchedByNameOnly);
    }

    [Fact]
    public void DisplayedItems_ItemTextFilter_NarrowsToMatchingKeyItemsOnly()
    {
        var mod = MakeMod(changedItems:
        [
            new IndexedChangedItem("Calfskin Rider's Shoes", ModCategory.Gear),
            new IndexedChangedItem("Faerie Tale Prince's Vest", ModCategory.Gear),
        ]);
        var filter = MakeFilter(itemQuery: "shoes");

        var (items, _) = LibrarySearchEngine.DisplayedItems(mod, filter);

        Assert.Equal(["Calfskin Rider's Shoes"], items.Select(i => i.Key));
    }

    [Fact]
    public void DisplayedItems_NpcNameHeuristicOnlyMatch_ShowsAllItemsFlaggedAsNameMatch()
    {
        var mod = MakeMod(
            categories: [ModCategory.Face], // no item's own Facet is NPC
            matchedByNpcNameHeuristic: true,
            changedItems: [new IndexedChangedItem("Customization: Miqo'te Female Face 101", ModCategory.Face)]);
        var filter = MakeFilter(categories: [ModCategory.NPC]);

        var (items, matchedByNameOnly) = LibrarySearchEngine.DisplayedItems(mod, filter);

        Assert.Single(items);
        Assert.True(matchedByNameOnly);
    }

    [Fact]
    public void DisplayedItems_UnknownFacetItem_ShownOnlyWhenIncludeUnknownSelected()
    {
        var mod = MakeMod(
            categories: [ModCategory.Gear, ModCategory.VFX],
            hasUnknownFacetItems: true,
            changedItems:
            [
                new IndexedChangedItem("Calfskin Rider's Shoes", ModCategory.Gear),
                new IndexedChangedItem("Vfx", ModCategory.VFX),
                new IndexedChangedItem("Icon: Something", null),
            ]);

        var (withUnknown, _) = LibrarySearchEngine.DisplayedItems(mod, MakeFilter(includeUnknown: true));
        var (withoutUnknown, _) = LibrarySearchEngine.DisplayedItems(mod, MakeFilter(includeUnknown: false));

        Assert.Contains(withUnknown, i => i.Key == "Icon: Something");
        Assert.DoesNotContain(withoutUnknown, i => i.Key == "Icon: Something");
    }

    [Fact] // Slot filtering never narrows which items display -- only whether the mod appears at all
    public void DisplayedItems_GearMatchedViaSlot_ShowsAllCategoryMatchedItemsRegardlessOfSlot()
    {
        var mod = MakeMod(
            categories: [ModCategory.Gear],
            equipmentSlots: [EquipmentSlot.Feet],
            changedItems:
            [
                new IndexedChangedItem("Calfskin Rider's Shoes", ModCategory.Gear),
                new IndexedChangedItem("Faerie Tale Prince's Vest", ModCategory.Gear),
            ]);
        var filter = MakeFilter(categories: [ModCategory.Gear], slots: [EquipmentSlot.Feet]);

        var (items, _) = LibrarySearchEngine.DisplayedItems(mod, filter);

        Assert.Equal(2, items.Count);
    }
}
