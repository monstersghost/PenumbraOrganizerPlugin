using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.LibrarySearch;

public static class ChangedItemIndexSummary
{
    public static string Describe(ChangedItemIndex index)
    {
        var totalChangedItems = index.Mods.Sum(m => m.ChangedItems.Count);
        var gearMods = index.Mods.Where(m => m.Categories.Contains(ModCategory.Gear)).ToList();
        var singleSlot = gearMods.Count(m => m.SlotDiagnostic == GearSlotDiagnostic.Single);
        var multiSlot = gearMods.Count(m => m.SlotDiagnostic == GearSlotDiagnostic.Ambiguous);
        var unresolved = gearMods.Count(m => m.SlotDiagnostic == GearSlotDiagnostic.ZeroEvidence);
        var missingDirectory = gearMods.Count(m => m.SlotDiagnostic == GearSlotDiagnostic.DirectoryMissing);
        var readFailure = gearMods.Count(m => m.SlotDiagnostic == GearSlotDiagnostic.ReadFailure);

        var summary =
            $"Indexed {index.Mods.Count} of {index.TotalModsSeen} mods · " +
            $"{totalChangedItems} changed items · " +
            $"{gearMods.Count} gear mods scanned " +
            $"({singleSlot} single-slot, {multiSlot} multi-slot, {unresolved} unresolved) · " +
            $"{missingDirectory} missing directories · {readFailure} read failures";

        return index.OrphanedChangedItemEntryCount > 0
            ? summary + $" · {index.OrphanedChangedItemEntryCount} orphaned changed-item entries"
            : summary;
    }
}
