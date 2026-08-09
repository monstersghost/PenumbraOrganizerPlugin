using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

public class ScanProcessorTests
{
    // An empty config directory and the opt-in off, so the matcher is built from the bundled static
    // list alone. "Zenos" below is in that list; the mod titles these tests use match nothing in it.
    private static ScanProcessor NewProcessor(string? configDirectory = null)
    {
        var processor = new ScanProcessor(
            configDirectory ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
            useScrapedNpcNameList: false);
        processor.Prepare(CancellationToken.None);
        return processor;
    }

    private static ScanSeed Seed(string modDirectoryPath, string name = "Some Mod", params string[] changedItemKeys) =>
        new("mod-dir", name, "An Author", "Gear/Some Mod", modDirectoryPath, changedItemKeys);

    [Fact]
    public void GearModWithOneSlot_GetsThatSubCategoryAndSingleDiagnostic()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"),
                """{"DefaultData":{"Files":{"chara/equipment/e0001/model/c0101e0001_top.mdl":"x.mdl"}}}""");

            var row = NewProcessor().Process(Seed(dir.FullName, changedItemKeys: "Ala Mhigan Gown"), CancellationToken.None);

            Assert.NotNull(row);
            Assert.Equal(ModCategory.Gear, row!.Category);
            Assert.Equal(GearSlotDiagnostic.Single, row.GearSlotDiagnostic);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GearModWithNoEquipmentEvidence_ReportsZeroEvidence()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"), """{"DefaultData":{"Files":{}}}""");

            var row = NewProcessor().Process(Seed(dir.FullName, changedItemKeys: "Ala Mhigan Gown"), CancellationToken.None);

            Assert.Equal(GearSlotDiagnostic.ZeroEvidence, row!.GearSlotDiagnostic);
            Assert.Null(row.SubCategory);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GearModWithMissingDirectory_ReportsDirectoryMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var row = NewProcessor().Process(Seed(missing, changedItemKeys: "Ala Mhigan Gown"), CancellationToken.None);

        Assert.Equal(GearSlotDiagnostic.DirectoryMissing, row!.GearSlotDiagnostic);
    }

    [Fact]
    public void NonGearMod_ReportsNotApplicable()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var row = NewProcessor().Process(Seed(missing, name: "Zenos Glam"), CancellationToken.None);

        Assert.Equal(GearSlotDiagnostic.NotApplicable, row!.GearSlotDiagnostic);
    }

    [Fact]
    public void NpcNameHeuristic_IsApplied()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var row = NewProcessor().Process(Seed(missing, name: "Zenos Redesign"), CancellationToken.None);

        Assert.Equal(ModCategory.NPC, row!.Category);
    }

    [Fact]
    public void HeliospherePrefix_IsDetectedFromTheIdentifier()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var seed = new ScanSeed("hs-Nightingale-1.0", "Nightingale", "Author", "Gear/N", missing, []);

        var row = NewProcessor().Process(seed, CancellationToken.None);

        Assert.True(row!.HeliosphereManaged);
    }

    [Fact]
    public void RowCarriesIdentityFieldsThroughUnchanged()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var row = NewProcessor().Process(Seed(missing), CancellationToken.None);

        Assert.Equal("mod-dir", row!.Identifier);
        Assert.Equal("Some Mod", row.Name);
        Assert.Equal("An Author", row.Author);
        Assert.Equal("Gear/Some Mod", row.CurrentPath);
        Assert.Equal("Gear/Some Mod", row.ProposedPath);
    }

    [Fact]
    public void Cancellation_IsObservedPerItem()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            NewProcessor().Process(Seed(Path.GetTempPath()), cts.Token));
    }
}
