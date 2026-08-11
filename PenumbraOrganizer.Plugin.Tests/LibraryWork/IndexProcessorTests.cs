using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

public class IndexProcessorTests
{
    // An empty config directory and the opt-in off, so the matcher is built from the bundled static
    // list alone. "Zenos" below is in that list; the mod titles these tests use match nothing in it.
    private static IndexProcessor NewProcessor()
    {
        var processor = new IndexProcessor(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), useScrapedNpcNameList: false);
        processor.Prepare(CancellationToken.None);
        return processor;
    }

    private static IndexSeed Seed(string name = "Some Mod", params string[] keys) =>
        new("mod-dir", name, "An Author", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), keys);

    [Fact]
    public void ModWithNoChangedItems_IsExcluded()
    {
        Assert.Null(NewProcessor().Process(Seed(), CancellationToken.None));
    }

    [Fact]
    public void ModWithChangedItems_IsIncludedWithItsFacets()
    {
        var indexed = NewProcessor().Process(Seed("Some Mod", "Ala Mhigan Gown"), CancellationToken.None);

        Assert.NotNull(indexed);
        Assert.Equal("mod-dir", indexed!.Identifier);
        Assert.Contains(ModCategory.Gear, indexed.Categories);
    }

    [Fact]
    public void GearModWithMissingDirectory_ReportsDirectoryMissing()
    {
        var indexed = NewProcessor().Process(Seed("Some Mod", "Ala Mhigan Gown"), CancellationToken.None);

        Assert.Equal(GearSlotDiagnostic.DirectoryMissing, indexed!.SlotDiagnostic);
    }

    [Fact]
    public void NpcNameHeuristic_IsRecorded()
    {
        var indexed = NewProcessor().Process(Seed("Zenos Redesign", "Ala Mhigan Gown"), CancellationToken.None);

        Assert.True(indexed!.MatchedByNpcNameHeuristic);
    }

    [Fact]
    public void Cancellation_IsObservedPerItem()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            NewProcessor().Process(Seed("Some Mod", "Ala Mhigan Gown"), cts.Token));
    }
}
