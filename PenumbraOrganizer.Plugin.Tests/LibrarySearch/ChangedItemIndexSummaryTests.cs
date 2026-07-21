using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibrarySearch;

public class ChangedItemIndexSummaryTests
{
    private static IndexedMod MakeGearMod(string id, GearSlotDiagnostic diagnostic, int itemCount = 1) =>
        new(id, id, "Author",
            Enumerable.Range(0, itemCount).Select(i => new IndexedChangedItem($"Item {i}", ModCategory.Gear)).ToList(),
            new HashSet<ModCategory> { ModCategory.Gear }, false, false,
            new HashSet<EquipmentSlot>(), diagnostic);

    [Fact]
    public void Describe_ReportsIndexedAndTotalModCounts()
    {
        var index = new ChangedItemIndex(
            [MakeGearMod("a", GearSlotDiagnostic.Single)], TotalModsSeen: 5, OrphanedChangedItemEntryCount: 0, BuiltAt: DateTime.Now);

        var summary = ChangedItemIndexSummary.Describe(index);

        Assert.Contains("Indexed 1 of 5 mods", summary);
    }

    [Fact]
    public void Describe_ReportsTotalChangedItemCount()
    {
        var index = new ChangedItemIndex(
            [MakeGearMod("a", GearSlotDiagnostic.Single, itemCount: 3)], 1, 0, DateTime.Now);

        Assert.Contains("3 changed items", ChangedItemIndexSummary.Describe(index));
    }

    [Fact]
    public void Describe_BreaksDownGearModsBySlotDiagnostic()
    {
        var index = new ChangedItemIndex(
            [
                MakeGearMod("a", GearSlotDiagnostic.Single),
                MakeGearMod("b", GearSlotDiagnostic.Ambiguous),
                MakeGearMod("c", GearSlotDiagnostic.ZeroEvidence),
                MakeGearMod("d", GearSlotDiagnostic.DirectoryMissing),
                MakeGearMod("e", GearSlotDiagnostic.ReadFailure),
            ],
            TotalModsSeen: 5, OrphanedChangedItemEntryCount: 0, BuiltAt: DateTime.Now);

        var summary = ChangedItemIndexSummary.Describe(index);

        Assert.Contains("5 gear mods scanned", summary);
        Assert.Contains("1 single-slot", summary);
        Assert.Contains("1 multi-slot", summary);
        Assert.Contains("1 unresolved", summary);
        Assert.Contains("1 missing directories", summary);
        Assert.Contains("1 read failures", summary);
    }

    [Fact] // Ambiguous (multi-slot) is a SUCCESS for this feature, never described as a failure
    public void Describe_NeverUsesSorterFlavoredFailureLanguageForAmbiguous()
    {
        var index = new ChangedItemIndex([MakeGearMod("a", GearSlotDiagnostic.Ambiguous)], 1, 0, DateTime.Now);

        Assert.DoesNotContain("could not be assigned", ChangedItemIndexSummary.Describe(index));
    }

    [Fact]
    public void Describe_ZeroOrphans_OmitsOrphanClause()
    {
        var index = new ChangedItemIndex([], TotalModsSeen: 1, OrphanedChangedItemEntryCount: 0, BuiltAt: DateTime.Now);

        Assert.DoesNotContain("orphaned", ChangedItemIndexSummary.Describe(index));
    }

    [Fact]
    public void Describe_NonZeroOrphans_IncludesOrphanClause()
    {
        var index = new ChangedItemIndex([], TotalModsSeen: 1, OrphanedChangedItemEntryCount: 2, BuiltAt: DateTime.Now);

        Assert.Contains("2 orphaned changed-item entries", ChangedItemIndexSummary.Describe(index));
    }
}
