using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibrarySearch;

/// <summary>
/// Retargeted at IndexProcessor (the per-mod work production actually runs) plus
/// ChangedItemIndexBuilder.Assemble (the framework-thread final step production actually calls).
/// ChangedItemIndexBuilder.Build and LibraryModEntry were a full duplicate of IndexProcessor with
/// zero production callers and were deleted; these tests assert the same behaviours against the
/// real implementation instead.
/// </summary>
public class ChangedItemIndexBuilderTests
{
    private static DirectoryInfo MakeTempModDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private static void WriteJson(DirectoryInfo modDirectory, string fileName, string json) =>
        File.WriteAllText(Path.Combine(modDirectory.FullName, fileName), json);

    // An empty config directory and the opt-in off, so the matcher is built from the bundled static
    // list alone. None of the mod titles below match a name in it except the deliberate one.
    private static IndexProcessor NewProcessor()
    {
        var processor = new IndexProcessor(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), useScrapedNpcNameList: false);
        processor.Prepare(CancellationToken.None);
        return processor;
    }

    private static IndexSeed MakeSeed(
        string identifier, string name, string author, DirectoryInfo? modPath, IReadOnlyList<string> changedItemKeys) =>
        new(identifier, name, author,
            (modPath ?? new DirectoryInfo(Path.Combine(Path.GetTempPath(), "nonexistent-" + identifier))).FullName,
            changedItemKeys);

    private static ChangedItemIndex RunOne(IndexProcessor processor, IndexSeed seed, IReadOnlySet<string> modIdentifiersWithChangedItems)
    {
        var result = processor.Process(seed, CancellationToken.None);
        var indexedMods = result is null ? [] : new List<IndexedMod> { result };
        return ChangedItemIndexBuilder.Assemble(indexedMods, [seed.Identifier], modIdentifiersWithChangedItems);
    }

    [Fact]
    public void ModWithNoChangedItems_ExcludedFromMods_ButCountedInTotal()
    {
        var seed = MakeSeed("a", "Empty Mod", "Someone", null, []);

        var result = RunOne(NewProcessor(), seed, new HashSet<string>());

        Assert.Empty(result.Mods);
        Assert.Equal(1, result.TotalModsSeen);
    }

    [Fact]
    public void SmallclothesPlusRealGear_CategoriesContainsBothBodyAndGear()
    {
        // Deliberately diverges from ModTypeClassifier.Classify, which would return Body alone
        // (Rule 0 wins) — Categories here is a per-item union, not a first-match-wins reduction.
        var seed = MakeSeed("a", "Compilation Mod", "Someone", null, ["Smallclothes", "Appointed Gloves"]);

        var result = RunOne(NewProcessor(), seed, new HashSet<string> { "a" });

        var mod = Assert.Single(result.Mods);
        Assert.Equal(new HashSet<ModCategory> { ModCategory.Body, ModCategory.Gear }, mod.Categories);
    }

    [Fact]
    public void NpcNameHeuristicMatch_SetsFlagIndependentlyOfCategories()
    {
        // "Zenos" is in the bundled static list, which is now the matcher's default source.
        var seed = MakeSeed("a", "Zenos", "Someone", null, ["Customization: Midlander Male Skin Textures"]);

        var result = RunOne(NewProcessor(), seed, new HashSet<string> { "a" });

        var mod = Assert.Single(result.Mods);
        Assert.True(mod.MatchedByNpcNameHeuristic);
        Assert.DoesNotContain(ModCategory.NPC, mod.Categories); // no item is itself NPC-shaped
    }

    [Fact]
    public void GearModWithSingleSlot_ReadsEquipmentSlotsAndSetsDiagnostic()
    {
        var modDir = MakeTempModDirectory();
        try
        {
            WriteJson(modDir, "default_mod.json", """
                {"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"files/sho.mdl"},"Manipulations":[]}
                """);
            var seed = MakeSeed("a", "Boots Mod", "Someone", modDir, ["Calfskin Rider's Shoes"]);

            var result = RunOne(NewProcessor(), seed, new HashSet<string> { "a" });

            var mod = Assert.Single(result.Mods);
            Assert.Equal(new HashSet<EquipmentSlot> { EquipmentSlot.Feet }, mod.EquipmentSlots);
            Assert.Equal(GearSlotDiagnostic.Single, mod.SlotDiagnostic);
        }
        finally
        {
            modDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void NonGearMod_NeverReadsDisk_EquipmentSlotsEmptyNotApplicable()
    {
        // A directory that doesn't exist would fail EquipmentSlot reads if ever touched -- proves
        // IndexProcessor never calls ModEquipmentFileReader for a mod whose Categories has no Gear.
        var seed = MakeSeed("a", "Vfx Mod", "Someone", null, ["Vfx"]);

        var result = RunOne(NewProcessor(), seed, new HashSet<string> { "a" });

        var mod = Assert.Single(result.Mods);
        Assert.Empty(mod.EquipmentSlots);
        Assert.Equal(GearSlotDiagnostic.NotApplicable, mod.SlotDiagnostic);
    }

    [Fact]
    public void UnrecognizedKeyAlongsideRecognizedOnes_SetsHasUnknownFacetItems()
    {
        var seed = MakeSeed("a", "Mixed Mod", "Someone", null, ["Vfx", "Icon: Something"]);

        var result = RunOne(NewProcessor(), seed, new HashSet<string> { "a" });

        var mod = Assert.Single(result.Mods);
        Assert.True(mod.HasUnknownFacetItems);
        Assert.Contains(ModCategory.VFX, mod.Categories);
    }

    [Fact]
    public void ChangedItemEntryWithNoMatchingMod_CountedAsOrphaned()
    {
        var seed = MakeSeed("a", "Real Mod", "Someone", null, ["Appointed Gloves"]);

        var result = RunOne(NewProcessor(), seed, new HashSet<string> { "a", "ghost-identifier" });

        Assert.Equal(1, result.OrphanedChangedItemEntryCount);
    }

    [Fact]
    public void TotalModsSeen_CountsEveryModRegardlessOfChangedItems()
    {
        var processor = NewProcessor();
        var seedA = MakeSeed("a", "Has Items", "Someone", null, ["Appointed Gloves"]);
        var seedB = MakeSeed("b", "No Items", "Someone", null, []);

        var indexedMods = new List<IndexedMod>();
        if (processor.Process(seedA, CancellationToken.None) is { } rowA)
            indexedMods.Add(rowA);
        if (processor.Process(seedB, CancellationToken.None) is { } rowB)
            indexedMods.Add(rowB);

        var result = ChangedItemIndexBuilder.Assemble(
            indexedMods, ["a", "b"], new HashSet<string> { "a" });

        Assert.Equal(2, result.TotalModsSeen);
        Assert.Single(result.Mods);
    }
}
