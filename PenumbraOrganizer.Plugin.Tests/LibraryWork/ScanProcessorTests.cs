using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;
using PenumbraOrganizer.Plugin.Organizer;
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

    [Fact]
    public void HairModWithOneHairNumber_GetsThatNumberAsItsSubCategory()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"),
                """{"DefaultData":{"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"hair.mdl"}}}""");

            var row = NewProcessor().Process(
                Seed(dir.FullName, "Some Hair", "Customization: Midlander Female Hair 157"),
                CancellationToken.None);

            Assert.NotNull(row);
            Assert.Equal(ModCategory.Hair, row!.Category);
            // 115 from the files, not 157 from the changed-item key - the subcategory must come
            // from the file read.
            Assert.Equal("115", row.SubCategory);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void HairModWithTwoHairNumbers_StaysFlat()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"),
                """{"DefaultData":{"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"a.mdl","chara/human/c0101/obj/hair/h0009/model/c0101h0009_hir.mdl":"b.mdl"}}}""");

            var row = NewProcessor().Process(
                Seed(dir.FullName, "Some Hair Pack", "Customization: Midlander Female Hair 157"),
                CancellationToken.None);

            Assert.NotNull(row);
            Assert.Equal(ModCategory.Hair, row!.Category);
            Assert.Null(row.SubCategory);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GearModWithHairPaths_KeepsNoHairSubCategory()
    {
        // NOTE what this does and does not prove. It proves the OUTCOME is protected: a Gear row
        // never carries a hair number, which would throw in GetFolder the moment it reached the
        // sort. It does NOT prove the disk-I/O gate exists - it passes just as well if the hair
        // reader runs for every mod, because EnrichHairSubCategory independently refuses to touch
        // a non-Hair classification. The gate is a performance property with no seam to assert it
        // through, so it is verified by reading the code in review, not here. Inventing an
        // injection seam solely for this assertion would not be worth it.
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"),
                """{"DefaultData":{"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"hair.mdl"}}}""");

            var row = NewProcessor().Process(
                Seed(dir.FullName, changedItemKeys: "Ala Mhigan Gown"),
                CancellationToken.None);

            Assert.NotNull(row);
            Assert.Equal(ModCategory.Gear, row!.Category);
            Assert.Null(row.SubCategory);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static OrganizerModRow ScanWithFiles(string name, string changedItem, params string[] gamePaths)
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            var entries = string.Join(",", gamePaths.Select((p, i) => "\"" + p + "\":\"f" + i + ".mdl\""));
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"),
                "{\"DefaultData\":{\"Files\":{" + entries + "}}}");
            return NewProcessor().Process(Seed(dir.FullName, name, changedItem), CancellationToken.None)!;
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void HairRow_CarriesThePerRaceEvidenceForLaterSorting()
    {
        // A hair mod whose Viera female folder has a different number from the rest. The row must
        // keep both views so Sort can apply a priority without re-reading the disk.
        var row = ScanWithFiles("Some Hair Pack", "Customization: Midlander Female Hair 157",
            "chara/human/c0801/obj/hair/h0108/model/a.mdl",
            "chara/human/c1801/obj/hair/h0003/model/b.mdl");

        Assert.Equal("0801", row.HairByRaceCode!.Keys.Single(k => k == "0801"));
        Assert.Equal(new HashSet<int> { 3 }, row.HairByRaceCode!["1801"].ToHashSet());
        Assert.Equal(new HashSet<int> { 3, 108 }, row.HairFlat!.ToHashSet());
    }

    private static OrganizerModRow ProcessGearWithConfig(string name, string configJson)
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"), configJson);
            return NewProcessor().Process(Seed(dir.FullName, name, "Ala Mhigan Gown"), CancellationToken.None)!;
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Process_GearWithNoBodyInName_ButBodyInOptions_GetsItFromContent()
    {
        var row = ProcessGearWithConfig("Plain Corset", """
            {"DefaultData":{"Files":{"chara/equipment/e0001/model/c0101e0001_top.mdl":"x.mdl"},"Manipulations":[]},
             "Groups":[{"Name":"Body","Options":[{"Name":"Bibo+"},{"Name":"TBSE"}]}]}
            """);

        Assert.NotNull(row.BodyCandidates);
        Assert.Equal(["Bibo+", "TBSE"], row.BodyCandidates!.Content.Select(c => c.Label).Order());
        Assert.Equal("Bibo+", row.BodyType);
        Assert.Empty(row.BodyCandidates.Name);
    }

    [Fact]
    public void Process_GearWithBodyOnlyInName_StillFilesByName()
    {
        var row = ProcessGearWithConfig("Bibo+ Medieval Dress", """
            {"DefaultData":{"Files":{"chara/equipment/e0001/model/c0101e0001_top.mdl":"x.mdl"},"Manipulations":[]}}
            """);

        Assert.Equal("Bibo+", row.BodyType);
        Assert.Empty(row.BodyCandidates!.Content);
        Assert.Equal(["Bibo+"], row.BodyCandidates.Name.Select(c => c.Label));
    }

    [Fact]
    public void Process_UnreadableConfig_FallsBackToNameAndFlagsTheFailure()
    {
        var row = ProcessGearWithConfig("Bibo+ Dress", "{ not json");

        Assert.Equal(GearSlotDiagnostic.ReadFailure, row.GearSlotDiagnostic);
        Assert.Null(row.BodyType);
    }

    [Fact]
    public void Process_WeaponInOptionsNamedAfterABody_IsNotSearched()
    {
        var row = ProcessGearWithConfig("Artemis Bow", """
            {"DefaultData":{"Files":{"chara/weapon/w1001/obj/body/b0001/model/w1001b0001.mdl":"x.mdl"},"Manipulations":[]},
             "Groups":[{"Name":"Look","Options":[{"Name":"Artemis"}]}]}
            """);

        Assert.Null(row.BodyType);
        Assert.Null(row.BodyCandidates);
    }

    [Fact]
    public void Prepare_PublishesTheLabelsOfTheListItBuiltItsMatcherFrom()
    {
        var processor = NewProcessor();

        Assert.Contains("Bibo+", processor.BodyLabels);
        Assert.Equal(processor.BodyLabels.Count, processor.BodyLabels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void NonHairRow_CarriesNoHairEvidence()
    {
        var row = ScanWithFiles("Some Mod", "Ala Mhigan Gown", "chara/equipment/e0001/model/c0201e0001_top.mdl");
        Assert.Null(row.HairFlat);
        Assert.Null(row.HairByRaceCode);
    }
}
