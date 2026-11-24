using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Layouts;

public class LayoutModelTests
{
    private static readonly Dictionary<ModCategory, CategoryLayout> None = new();

    [Fact]
    public void Catalog_ListsEverySplitInFolderDepthOrder()
    {
        Assert.Equal(16, LayoutCatalog.Categories.Count);
        Assert.Equal(
            [LayoutSplits.BodyType, LayoutSplits.Slot, LayoutSplits.SharedHandsFeet],
            LayoutCatalog.SplitsFor(ModCategory.Gear).Select(s => s.Split));
        Assert.Equal([LayoutSplits.Race, LayoutSplits.HairNumber], LayoutCatalog.SplitsFor(ModCategory.Hair).Select(s => s.Split));
        Assert.Equal(
            [LayoutSplits.PartAndBodyType, LayoutSplits.Race, LayoutSplits.RacialScalingRace],
            LayoutCatalog.SplitsFor(ModCategory.Body).Select(s => s.Split));
        Assert.Equal([LayoutSplits.NpcKind], LayoutCatalog.SplitsFor(ModCategory.NPC).Select(s => s.Split));
        Assert.Equal([LayoutSplits.Emote], LayoutCatalog.SplitsFor(ModCategory.Animation).Select(s => s.Split));
        Assert.Equal([LayoutSplits.Race, LayoutSplits.EyesRace], LayoutCatalog.SplitsFor(ModCategory.Face).Select(s => s.Split));
        Assert.Equal([LayoutSplits.Race], LayoutCatalog.SplitsFor(ModCategory.Skin).Select(s => s.Split));
        Assert.Equal([LayoutSplits.Job], LayoutCatalog.SplitsFor(ModCategory.Weapon).Select(s => s.Split));
        foreach (var category in new[] { ModCategory.Minion, ModCategory.VFX, ModCategory.Others })
            Assert.Empty(LayoutCatalog.SplitsFor(category));
    }

    [Fact]
    public void Catalog_OnlyRaceSplitsNeedScanEvidence_AndOnlySharedHandsFeetIsNotAFolder()
    {
        var all = LayoutCatalog.Categories.SelectMany(LayoutCatalog.SplitsFor).ToList();
        // The Weapon Job split also needs scan evidence: the Weapon category and the job both come from a scan.
        Assert.All(all, s => Assert.Equal(
            s.Split is LayoutSplits.Race or LayoutSplits.EyesRace or LayoutSplits.RacialScalingRace or LayoutSplits.Job,
            s.NeedsScanEvidence));
        Assert.All(all, s => Assert.Equal(s.Split != LayoutSplits.SharedHandsFeet, s.IsFolder));
    }

    [Theory]
    [InlineData(ModCategory.Gear, "Gear")]
    [InlineData(ModCategory.VFX, "Animation and VFX/VFX")]
    [InlineData(ModCategory.Animation, "Animation and VFX")]
    [InlineData(ModCategory.Others, "Others")]
    public void DefaultFolder_IsWhereTheSortFilesTheCategory(ModCategory category, string expected) =>
        Assert.Equal(expected, LayoutCatalog.DefaultFolder(category));

    [Fact]
    public void DefaultFolder_IsTheWorkbookBuiltInFolder_ExceptOthers_WhoseCodeStaysReview()
    {
        Assert.All(LayoutCatalog.Categories.Where(c => c != ModCategory.Others), c =>
            Assert.Equal(WorkbookCatalog.FolderFor((int)c), LayoutCatalog.DefaultFolder(c)));
        Assert.Equal("Review", WorkbookCatalog.FolderFor((int)ModCategory.Others));
    }

    [Fact]
    public void SortRoot_IsTheDefaultFolder()
    {
        Assert.All(LayoutCatalog.Categories, c => Assert.Equal(LayoutCatalog.DefaultFolder(c), LayoutCatalog.SortRoot(c)));
        Assert.Equal("Others", LayoutCatalog.SortRoot(ModCategory.Others));
        Assert.Equal("Animation and VFX", LayoutCatalog.SortRoot(ModCategory.Animation));
    }

    [Theory]
    [InlineData("Gear", "Equipment/Gear", "Equipment/Gear")]
    [InlineData("Gear/10. Bibo+/02. Top", "Equipment/Gear", "Equipment/Gear/10. Bibo+/02. Top")]
    [InlineData("Gearbox/Top", "Kit", "Gearbox/Top")]
    public void ReplaceRoot_WorksOnASegmentBoundaryOnly(string path, string folder, string expected) =>
        Assert.Equal(expected, LayoutCatalog.ReplaceRoot(path, ModCategory.Gear, folder));

    [Fact]
    public void ReplaceRoot_VfxReplacesItsWholeNestedRoot_AndAnimationOnlyItsParent()
    {
        Assert.Equal("Effects", LayoutCatalog.ReplaceRoot("Animation and VFX/VFX", ModCategory.VFX, "Effects"));
        Assert.Equal("Motion/Emotes/Dance", LayoutCatalog.ReplaceRoot("Animation and VFX/Emotes/Dance", ModCategory.Animation, "Motion"));
        Assert.Equal("Animation", LayoutCatalog.ReplaceRoot("Animation", ModCategory.Animation, "Motion"));
    }

    [Fact]
    public void ReplaceRoot_OthersReplacesTheOthersRoot()
    {
        Assert.Equal("Misc/Mod", LayoutCatalog.ReplaceRoot("Others/Mod", ModCategory.Others, "Misc"));
        Assert.Equal("Review/Mod", LayoutCatalog.ReplaceRoot("Review/Mod", ModCategory.Others, "Misc"));
    }

    [Fact]
    public void Recommended_IsTodaysDefaultsPlusTheHelpfulSplits()
    {
        var r = LayoutProfile.Recommended;
        Assert.Equal(LayoutSplits.BodyType | LayoutSplits.Slot | LayoutSplits.SharedHandsFeet, r[ModCategory.Gear].Splits);
        Assert.Equal(LayoutSplits.NpcKind, r[ModCategory.NPC].Splits);
        Assert.True(r[ModCategory.Hair].Has(LayoutSplits.HairNumber));
        Assert.True(r[ModCategory.Body].Has(LayoutSplits.PartAndBodyType));
        Assert.Equal(LayoutSplits.Emote, r[ModCategory.Animation].Splits);
        Assert.Equal(LayoutSplits.None, r[ModCategory.Weapon].Splits);
        Assert.All(LayoutCatalog.Categories, c =>
        {
            Assert.Null(r[c].Creator);
            Assert.Equal(LayoutCatalog.DefaultFolder(c), r[c].Folder);
        });
        // Race rows ticked as on a fresh install; the master switch (off by default) keeps race off.
        Assert.Equal(RaceRows.All, r.ToLegacy().RaceRows);
    }

    [Fact]
    public void Recommended_CarriesEveryRaceBit()
    {
        var r = LayoutProfile.Recommended;
        Assert.True(r[ModCategory.Hair].Has(LayoutSplits.Race));
        Assert.True(r[ModCategory.Face].Has(LayoutSplits.Race | LayoutSplits.EyesRace));
        Assert.True(r[ModCategory.Body].Has(LayoutSplits.Race | LayoutSplits.RacialScalingRace));
        Assert.True(r[ModCategory.Skin].Has(LayoutSplits.Race));
    }

    [Fact]
    public void Profile_HasValueEquality()
    {
        var a = LayoutProfile.Resolve(new Dictionary<ModCategory, CategoryLayout>
            { [ModCategory.Gear] = new("Equipment/Gear", CreatorLevel.None, LayoutSplits.Slot) });
        var b = LayoutProfile.Resolve(new Dictionary<ModCategory, CategoryLayout>
            { [ModCategory.Gear] = new("Equipment/Gear", CreatorLevel.None, LayoutSplits.Slot) });
        var c = LayoutProfile.Resolve(new Dictionary<ModCategory, CategoryLayout>
            { [ModCategory.Gear] = new("Equipment/Gear", CreatorLevel.AfterType, LayoutSplits.Slot) });

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.True(a != c);
        Assert.Equal(LayoutProfile.Recommended, LayoutProfile.Resolve(None));
    }

    [Fact]
    public void Resolve_UsesTheOverride_ForThatCategoryOnly_AndDropsIllegalSplits()
    {
        var profile = LayoutProfile.Resolve(new Dictionary<ModCategory, CategoryLayout>
        {
            [ModCategory.Gear] = new("Kit", null, LayoutSplits.Slot | LayoutSplits.Emote),
        });

        Assert.Equal(new CategoryLayout("Kit", null, LayoutSplits.Slot), profile[ModCategory.Gear]);
        Assert.Equal(LayoutProfile.Recommended[ModCategory.Hair], profile[ModCategory.Hair]);
        Assert.True(profile.IsCustom(ModCategory.Gear));
        Assert.False(profile.IsCustom(ModCategory.Hair));
    }

    [Fact]
    public void Resolve_SkipsANullOverride_AndIgnoresACategoryOutsideTheSixteen()
    {
        var profile = LayoutProfile.Resolve(new Dictionary<ModCategory, CategoryLayout>
        {
            [ModCategory.Hair] = null!,
            [(ModCategory)0] = new("Zero", CreatorLevel.None, LayoutSplits.None),
            [(ModCategory)99] = new("Ninety-nine", CreatorLevel.None, LayoutSplits.None),
            [ModCategory.Gear] = new("Kit", null, LayoutSplits.Slot),
        });

        Assert.Equal(LayoutProfile.Recommended[ModCategory.Hair], profile[ModCategory.Hair]);
        Assert.Equal(new CategoryLayout("Kit", null, LayoutSplits.Slot), profile[ModCategory.Gear]);
        Assert.Equal([ModCategory.Gear], profile.Overrides().Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(-1)]
    public void TheIndexer_GivesTheDefaultLayout_ForACategoryOutsideTheSixteen(int value)
    {
        var custom = LayoutProfile.Recommended.With(ModCategory.Others, new CategoryLayout("Misc", CreatorLevel.None, LayoutSplits.None));

        Assert.Equal(LayoutCatalog.Recommended(ModCategory.Others), custom[(ModCategory)value]);
        Assert.Equal(LayoutCatalog.Recommended(ModCategory.Others), LayoutProfile.Recommended[(ModCategory)value]);
        Assert.False(custom.IsCustom((ModCategory)value));
    }

    [Fact]
    public void With_RejectsACategoryOutsideTheSixteen() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LayoutProfile.Recommended.With((ModCategory)17, new CategoryLayout("X", null, LayoutSplits.None)));

    [Fact]
    public void With_LeavesTheOriginalProfileUnchanged()
    {
        var before = LayoutProfile.Recommended[ModCategory.Gear];
        var changed = LayoutProfile.Recommended.With(ModCategory.Gear, new CategoryLayout("Kit", null, LayoutSplits.None));

        Assert.Equal(before, LayoutProfile.Recommended[ModCategory.Gear]);
        Assert.Equal("Kit", changed[ModCategory.Gear].Folder);
    }

    [Fact]
    public void Overrides_HoldOnlyTheCategoriesThatDiffer()
    {
        var profile = LayoutProfile.Recommended
            .With(ModCategory.NPC, new CategoryLayout("NPC", null, LayoutSplits.None))
            .With(ModCategory.Hair, LayoutProfile.Recommended[ModCategory.Hair]);

        var overrides = profile.Overrides();
        Assert.Equal([ModCategory.NPC], overrides.Keys);
        Assert.Equal(profile, LayoutProfile.Resolve(overrides));
    }

    [Fact]
    public void ARecommendedUpdate_ReachesUncustomisedCategories_AndNotCustomisedOnes()
    {
        var saved = LayoutProfile.Recommended
            .With(ModCategory.Gear, new CategoryLayout("Gear", null, LayoutSplits.Slot))
            .Overrides();

        // A later version improves Recommended for both Gear and NPC.
        var improved = LayoutProfile.Recommended
            .With(ModCategory.Gear, new CategoryLayout("Gear", null, LayoutSplits.BodyType))
            .With(ModCategory.NPC, new CategoryLayout("NPC", CreatorLevel.None, LayoutSplits.NpcKind));

        var profile = LayoutProfile.Resolve(saved, improved);
        Assert.Equal(new CategoryLayout("Gear", null, LayoutSplits.Slot), profile[ModCategory.Gear]);
        Assert.Equal(improved[ModCategory.NPC], profile[ModCategory.NPC]);
    }

    [Fact]
    public void Reset_RestoresOneCategory_AndResettingEveryOneGivesRecommended()
    {
        var custom = LayoutProfile.Recommended
            .With(ModCategory.Gear, new CategoryLayout("Kit", CreatorLevel.None, LayoutSplits.None))
            .With(ModCategory.Hair, new CategoryLayout("Hair", null, LayoutSplits.None));

        var oneReset = custom.Reset(ModCategory.Gear);
        Assert.False(oneReset.IsCustom(ModCategory.Gear));
        Assert.True(oneReset.IsCustom(ModCategory.Hair));
        Assert.Equal(LayoutProfile.Recommended, LayoutCatalog.Categories.Aggregate(custom, (p, c) => p.Reset(c)));
    }

    [Fact]
    public void Legacy_RoundTripsEveryCombination()
    {
        for (var bits = 0; bits < 128; bits++)
        for (var rows = 0; rows <= (int)RaceRows.All; rows++)
        {
            var legacy = new LegacyLayout(
                (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0,
                (bits & 32) != 0, (bits & 64) != 0, (RaceRows)rows);
            var profile = LayoutProfile.FromLegacy(legacy);
            Assert.Equal(legacy, profile.ToLegacy());
            Assert.All(LayoutCatalog.Categories, c =>
            {
                Assert.Null(profile[c].Creator);
                Assert.Equal(LayoutCatalog.DefaultFolder(c), profile[c].Folder);
            });
        }
    }

    [Fact]
    public void Legacy_MapsEachFlagToItsCategory()
    {
        var profile = LayoutProfile.FromLegacy(new LegacyLayout(
            SplitGear: true, SplitNpc: false, SplitHair: true, SplitGearBody: false, SplitBodyMods: true,
            SharedHandsFeet: true, SplitEmotes: false, RaceRows: RaceRows.Eyes | RaceRows.RacialScaling));

        Assert.Equal(LayoutSplits.Slot | LayoutSplits.SharedHandsFeet, profile[ModCategory.Gear].Splits);
        Assert.Equal(LayoutSplits.None, profile[ModCategory.NPC].Splits);
        Assert.Equal(LayoutSplits.HairNumber, profile[ModCategory.Hair].Splits);
        Assert.Equal(LayoutSplits.PartAndBodyType | LayoutSplits.RacialScalingRace, profile[ModCategory.Body].Splits);
        Assert.Equal(LayoutSplits.EyesRace, profile[ModCategory.Face].Splits);
        Assert.Equal(LayoutSplits.None, profile[ModCategory.Skin].Splits);
    }

    [Fact]
    public void ReadFields_ReadsTheRaceRowsTheWayRaceSplitOptionsAlwaysHas()
    {
        Assert.Equal(RaceRows.All, LegacyLayout.ReadFields(new Configuration()).RaceRows);
        Assert.Equal(RaceRows.Hair, LegacyLayout.ReadFields(new Configuration { SortSplitHairByRace = true }).RaceRows);
        Assert.Equal(RaceRows.Face | RaceRows.Skin,
            LegacyLayout.ReadFields(new Configuration { RaceSplitEnabled = false, RaceSplitRows = ["Face", "Skin"] }).RaceRows);
        Assert.Equal(RaceRowNames.Parse(null), LegacyLayout.ReadFields(new Configuration { RaceSplitEnabled = true }).RaceRows);
    }

    [Theory]
    [InlineData(CreatorLevel.None, SortStrategy.TypeOnly)]
    [InlineData(CreatorLevel.BeforeType, SortStrategy.CreatorThenType)]
    [InlineData(CreatorLevel.AfterType, SortStrategy.TypeThenCreator)]
    public void CreatorLevel_MapsToTheMatchingGroupBy(CreatorLevel level, SortStrategy strategy) =>
        Assert.Equal(strategy, LayoutCatalog.StrategyFor(level));
}
