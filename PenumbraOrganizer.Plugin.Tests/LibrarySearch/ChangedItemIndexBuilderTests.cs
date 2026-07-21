using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibrarySearch;

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

    private static LibraryModEntry MakeMod(string identifier, string name, string author, DirectoryInfo? modPath = null) =>
        new(identifier, name, author, modPath ?? new DirectoryInfo(Path.Combine(Path.GetTempPath(), "nonexistent-" + identifier)));

    [Fact]
    public void Build_ModWithNoChangedItems_ExcludedFromMods_ButCountedInTotal()
    {
        var mods = new List<LibraryModEntry> { MakeMod("a", "Empty Mod", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string>(), _ => Enumerable.Empty<string>(), NpcNameMatcher.Empty);

        Assert.Empty(result.Mods);
        Assert.Equal(1, result.TotalModsSeen);
    }

    [Fact]
    public void Build_SmallclothesPlusRealGear_CategoriesContainsBothBodyAndGear()
    {
        // Deliberately diverges from ModTypeClassifier.Classify, which would return Body alone
        // (Rule 0 wins) — Categories here is a per-item union, not a first-match-wins reduction.
        var mods = new List<LibraryModEntry> { MakeMod("a", "Compilation Mod", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" },
            _ => new[] { "Smallclothes", "Appointed Gloves" },
            NpcNameMatcher.Empty);

        var mod = Assert.Single(result.Mods);
        Assert.Equal(new HashSet<ModCategory> { ModCategory.Body, ModCategory.Gear }, mod.Categories);
    }

    [Fact]
    public void Build_NpcNameHeuristicMatch_SetsFlagIndependentlyOfCategories()
    {
        var npcMatcher = new NpcNameMatcher(["Zenos"], [], []);
        var mods = new List<LibraryModEntry> { MakeMod("a", "Zenos", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" },
            _ => new[] { "Customization: Midlander Male Skin Textures" },
            npcMatcher);

        var mod = Assert.Single(result.Mods);
        Assert.True(mod.MatchedByNpcNameHeuristic);
        Assert.DoesNotContain(ModCategory.NPC, mod.Categories); // no item is itself NPC-shaped
    }

    [Fact]
    public void Build_GearModWithSingleSlot_ReadsEquipmentSlotsAndSetsDiagnostic()
    {
        var modDir = MakeTempModDirectory();
        WriteJson(modDir, "default_mod.json", """
            {"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"files/sho.mdl"},"Manipulations":[]}
            """);
        var mods = new List<LibraryModEntry> { MakeMod("a", "Boots Mod", "Someone", modDir) };

        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" }, _ => new[] { "Calfskin Rider's Shoes" }, NpcNameMatcher.Empty);

        var mod = Assert.Single(result.Mods);
        Assert.Equal(new HashSet<EquipmentSlot> { EquipmentSlot.Feet }, mod.EquipmentSlots);
        Assert.Equal(GearSlotDiagnostic.Single, mod.SlotDiagnostic);
    }

    [Fact]
    public void Build_NonGearMod_NeverReadsDisk_EquipmentSlotsEmptyNotApplicable()
    {
        // A directory that doesn't exist would fail EquipmentSlot reads if ever touched -- proves
        // the builder never calls ModEquipmentFileReader for a mod whose Categories has no Gear.
        var mods = new List<LibraryModEntry> { MakeMod("a", "Vfx Mod", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" }, _ => new[] { "Vfx" }, NpcNameMatcher.Empty);

        var mod = Assert.Single(result.Mods);
        Assert.Empty(mod.EquipmentSlots);
        Assert.Equal(GearSlotDiagnostic.NotApplicable, mod.SlotDiagnostic);
    }

    [Fact]
    public void Build_UnrecognizedKeyAlongsideRecognizedOnes_SetsHasUnknownFacetItems()
    {
        var mods = new List<LibraryModEntry> { MakeMod("a", "Mixed Mod", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" }, _ => new[] { "Vfx", "Icon: Something" }, NpcNameMatcher.Empty);

        var mod = Assert.Single(result.Mods);
        Assert.True(mod.HasUnknownFacetItems);
        Assert.Contains(ModCategory.VFX, mod.Categories);
    }

    [Fact]
    public void Build_ChangedItemEntryWithNoMatchingMod_CountedAsOrphaned()
    {
        var mods = new List<LibraryModEntry> { MakeMod("a", "Real Mod", "Someone") };
        var result = ChangedItemIndexBuilder.Build(
            mods,
            new HashSet<string> { "a", "ghost-identifier" },
            id => id == "a" ? new[] { "Appointed Gloves" } : Enumerable.Empty<string>(),
            NpcNameMatcher.Empty);

        Assert.Equal(1, result.OrphanedChangedItemEntryCount);
    }

    [Fact]
    public void Build_TotalModsSeen_CountsEveryModRegardlessOfChangedItems()
    {
        var mods = new List<LibraryModEntry>
        {
            MakeMod("a", "Has Items", "Someone"),
            MakeMod("b", "No Items", "Someone"),
        };
        var result = ChangedItemIndexBuilder.Build(
            mods, new HashSet<string> { "a" },
            id => id == "a" ? new[] { "Appointed Gloves" } : Enumerable.Empty<string>(),
            NpcNameMatcher.Empty);

        Assert.Equal(2, result.TotalModsSeen);
        Assert.Single(result.Mods);
    }
}
