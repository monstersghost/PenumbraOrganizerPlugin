using System.Text.Json;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Layouts;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Layouts;

public class ConfigurationCategoryLayoutsTests
{
    private static Configuration Legacy(Action<Configuration>? set = null)
    {
        var config = new Configuration();
        set?.Invoke(config);
        return config;
    }

    // The load sequence: the existing migration (race split first, whose result the layout migration reads), then
    // the layout migration. Called explicitly because the layout migration is wired into MigrateToCurrentVersion
    // only together with the readers' switch; once it is, the second call is a no-op (the list exists).
    private static void Migrate(Configuration config)
    {
        config.MigrateToCurrentVersion();
        config.MigrateCategoryLayouts();
    }

    [Fact]
    public void AConfigWithoutTheField_IsNotMigratedYet_AndReadsTheLegacyFields()
    {
        var config = JsonSerializer.Deserialize<Configuration>("{\"SortSplitGear\":true}")!;
        Assert.Null(config.CategoryLayouts);
        Assert.True(LayoutProfileCache.For(config)[ModCategory.Gear].Has(LayoutSplits.Slot));
    }

    [Fact]
    public void AnUnmigratedConfig_FollowsItsLegacyFieldsOnEveryRead()
    {
        var config = Legacy();
        Assert.False(LayoutProfileCache.For(config)[ModCategory.Gear].Has(LayoutSplits.Slot));
        config.SortSplitGear = true;
        Assert.True(LayoutProfileCache.For(config)[ModCategory.Gear].Has(LayoutSplits.Slot));
    }

    [Fact]
    public void Migration_StoresOverridesThatReproduceTheLegacyFieldsExactly()
    {
        for (var bits = 0; bits < 128; bits++)
        foreach (var rows in new List<string>?[] { null, [], ["Hair"], ["Face", "Eyes", "RacialScaling"] })
        {
            var config = Legacy(c =>
            {
                c.SortSplitGear = (bits & 1) != 0;
                c.SortSplitNpc = (bits & 2) != 0;
                c.SortSplitHair = (bits & 4) != 0;
                c.SortSplitGearBody = (bits & 8) != 0;
                c.SortSplitBodyMods = (bits & 16) != 0;
                c.SortSharedHandsFeet = (bits & 32) != 0;
                c.SortSplitEmotes = (bits & 64) != 0;
                c.RaceSplitEnabled = true;
                c.RaceSplitRows = rows;
            });
            var before = LegacyLayout.ReadFields(config);

            Migrate(config);

            Assert.NotNull(config.CategoryLayouts);
            Assert.Equal(before, LayoutProfileCache.For(config).ToLegacy());
            Assert.Equal(before, LegacyLayout.ReadFields(config)); // the legacy fields are untouched
        }
    }

    [Fact]
    public void Migration_StoresOnlyTheCategoriesThatDifferFromRecommended()
    {
        // Today's fresh defaults: gear, hair, body and emote splits off, NPC on, every race row ticked.
        var config = Legacy();
        Migrate(config);

        Assert.Equal(
            ["Gear", "Hair", "Body", "Animation"],
            config.CategoryLayouts!.Select(e => e.Category));
        var gear = config.CategoryLayouts!.First();
        Assert.Equal("Gear", gear.Folder);
        Assert.Null(gear.Creator);
        Assert.Empty(gear.Splits!);
        Assert.Equal(["Race"], config.CategoryLayouts!.Single(e => e.Category == "Hair").Splits!);
    }

    [Fact]
    public void Migration_OfALegacyHairRaceUser_KeepsOnlyHairRace()
    {
        var config = Legacy(c => c.SortSplitHairByRace = true);
        Migrate(config);

        Assert.True(config.RaceSplitEnabled);
        Assert.Equal(RaceRows.Hair, LayoutProfileCache.For(config).ToLegacy().RaceRows);
        Assert.Equal(RaceRows.Hair, RaceSplitOptions.FromConfig(config).Rows);
    }

    [Fact]
    public void Migration_RunsOnlyWhileTheListIsNull()
    {
        var config = Legacy(c => c.SortSplitGear = true);
        config.CategoryLayouts = [];
        Migrate(config);

        Assert.Empty(config.CategoryLayouts);
        Assert.Equal(LayoutProfile.Recommended, LayoutProfileCache.For(config));
    }

    [Fact]
    public void Migration_BumpsTheVersion()
    {
        var config = Legacy();
        var version = config.CategoryLayoutsVersion;
        config.MigrateCategoryLayouts();
        Assert.True(config.CategoryLayoutsVersion > version);
    }

    [Fact]
    public void Fresh_StartsOnRecommended_AndMirrorsItIntoTheLegacyFields()
    {
        var config = Configuration.CreateFresh();
        Migrate(config);

        Assert.Empty(config.CategoryLayouts!);
        Assert.Equal(LayoutProfile.Recommended, LayoutProfileCache.For(config));
        Assert.Equal(LayoutProfile.Recommended.ToLegacy(), LegacyLayout.ReadFields(config));
        Assert.True(config.SortSplitGear);
        Assert.True(config.SortSplitGearBody);
        Assert.True(config.SortSharedHandsFeet);
        Assert.True(config.SortSplitHair);
        Assert.True(config.SortSplitBodyMods);
        Assert.True(config.SortSplitEmotes);
        Assert.False(config.RaceSplitEnabled);
        Assert.False(config.SortSplitHairByRace);
    }

    [Fact]
    public void SaveLayoutProfile_StoresDifferencesOnly_BumpsTheVersion_AndWritesTheMirror()
    {
        var config = Configuration.CreateFresh();
        var version = config.CategoryLayoutsVersion;
        var profile = LayoutProfile.Recommended
            .With(ModCategory.Gear, new CategoryLayout("Equipment/Gear", CreatorLevel.None, LayoutSplits.Slot))
            .With(ModCategory.Hair, LayoutProfile.Recommended[ModCategory.Hair] with { Splits = LayoutSplits.HairNumber });

        config.SaveLayoutProfile(profile);

        Assert.Equal(["Gear", "Hair"], config.CategoryLayouts!.Select(e => e.Category));
        Assert.Equal(new[] { "Slot" }, config.CategoryLayouts![0].Splits!);
        Assert.Equal("None", config.CategoryLayouts![0].Creator);
        Assert.True(config.CategoryLayoutsVersion > version);
        Assert.Equal(profile, LayoutProfileCache.For(config));
        Assert.True(config.SortSplitGear);
        Assert.False(config.SortSplitGearBody);
        Assert.False(config.SortSharedHandsFeet);
        Assert.DoesNotContain("Hair", config.RaceSplitRows!);
    }

    [Fact]
    public void Write_ListsSplitsInFolderDepthOrder_AndLeavesCreatorNullWhenItFollowsGroupBy()
    {
        var profile = LayoutProfile.Recommended.With(
            ModCategory.Body, new CategoryLayout("Body", null, LayoutSplits.RacialScalingRace | LayoutSplits.PartAndBodyType));

        var entry = Assert.Single(LayoutOverrides.Write(profile));
        Assert.Equal("Body", entry.Category);
        Assert.Null(entry.Creator);
        Assert.Equal(["PartAndBodyType", "RacialScalingRace"], entry.Splits!);
    }

    [Fact]
    public void SavingRecommendedAgain_LeavesNoOverrides()
    {
        var config = Legacy();
        Migrate(config);
        Assert.NotEmpty(config.CategoryLayouts!);

        config.SaveLayoutProfile(LayoutProfile.Recommended);
        Assert.Empty(config.CategoryLayouts!);
    }

    [Fact]
    public void RewritingTheProfile_DropsEntriesTheReadIgnored()
    {
        var config = Configuration.CreateFresh();
        config.CategoryLayouts =
        [
            new() { Category = "Gaer", Folder = "Typo", Splits = [] },
            new() { Category = "Gear", Folder = "Kit", Splits = ["Slot"] },
            new() { Category = "Gear", Folder = "Second", Splits = [] },
        ];
        config.CategoryLayoutsVersion++;

        config.SaveLayoutProfile(LayoutProfileCache.For(config));

        var entry = Assert.Single(config.CategoryLayouts!);
        Assert.Equal("Gear", entry.Category);
        Assert.Equal("Kit", entry.Folder);
    }

    [Fact]
    public void TheListSurvivesASerializerRoundTrip_AndTheVersionIsNotSaved()
    {
        var config = Configuration.CreateFresh();
        config.SaveLayoutProfile(LayoutProfile.Recommended.With(
            ModCategory.NPC, new CategoryLayout("People", CreatorLevel.BeforeType, LayoutSplits.None)));

        var json = JsonSerializer.Serialize(config);
        Assert.DoesNotContain("CategoryLayoutsVersion", json);
        var back = JsonSerializer.Deserialize<Configuration>(json)!;
        Assert.Equal(LayoutProfileCache.For(config), LayoutProfileCache.For(back));

        var newtonsoft = Newtonsoft.Json.JsonConvert.SerializeObject(config);
        Assert.DoesNotContain("CategoryLayoutsVersion", newtonsoft);
        var backNewtonsoft = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(newtonsoft)!;
        Assert.Equal(LayoutProfileCache.For(config), LayoutProfileCache.For(backNewtonsoft));
    }

    [Fact]
    public void AHandEditedList_IsReadForgivingly()
    {
        var config = Configuration.CreateFresh();
        config.CategoryLayouts =
        [
            new() { Category = "Gear", Folder = "../Up", Creator = "Sideways", Splits = ["Slot", "Emote", "Bogus"] },
            new() { Category = "Gear", Folder = "Second", Splits = [] },
            new() { Category = "5", Folder = "Numbered" },
            new() { Category = "Nonsense", Folder = "X" },
            new() { Category = "Hair", Folder = @" Looks\Hair ", Creator = "AfterType", Splits = ["HairNumber"] },
        ];
        config.CategoryLayoutsVersion++;

        var profile = LayoutProfileCache.For(config);
        Assert.Equal(new CategoryLayout("Gear", null, LayoutSplits.Slot), profile[ModCategory.Gear]);
        Assert.Equal(LayoutProfile.Recommended[ModCategory.Body], profile[ModCategory.Body]);
        Assert.Equal(new CategoryLayout("Looks/Hair", CreatorLevel.AfterType, LayoutSplits.HairNumber), profile[ModCategory.Hair]);
    }

    [Fact]
    public void TheCache_ReturnsTheSameInstance_UntilTheListOrVersionChanges()
    {
        var config = Configuration.CreateFresh();
        var first = LayoutProfileCache.For(config);
        Assert.Same(first, LayoutProfileCache.For(config));

        config.CategoryLayouts!.Add(new CategoryLayoutEntry { Category = "Weapon", Folder = "Arms" });
        Assert.Same(first, LayoutProfileCache.For(config)); // an in-place edit without a bump is not seen
        config.CategoryLayoutsVersion++;
        var second = LayoutProfileCache.For(config);
        Assert.NotSame(first, second);
        Assert.Equal("Arms", second[ModCategory.Weapon].Folder);

        config.CategoryLayouts = [];
        Assert.Equal(LayoutProfile.Recommended, LayoutProfileCache.For(config));
    }

    [Fact]
    public void TheCache_IsPerConfig()
    {
        var gear = Configuration.CreateFresh();
        gear.SaveLayoutProfile(LayoutProfile.Recommended.With(
            ModCategory.Gear, LayoutProfile.Recommended[ModCategory.Gear] with { Splits = LayoutSplits.None }));
        var fresh = Configuration.CreateFresh();

        // Same version number on both, different lists: neither sees the other's profile.
        fresh.CategoryLayoutsVersion = gear.CategoryLayoutsVersion;
        Assert.Equal(LayoutSplits.None, LayoutProfileCache.For(gear)[ModCategory.Gear].Splits);
        Assert.Equal(LayoutProfile.Recommended, LayoutProfileCache.For(fresh));
    }

    [Fact]
    public void TheCache_GivesOneProfileToConcurrentReaders()
    {
        var config = Configuration.CreateFresh();
        config.SaveLayoutProfile(LayoutProfile.Recommended.With(
            ModCategory.NPC, LayoutProfile.Recommended[ModCategory.NPC] with { Splits = LayoutSplits.None }));

        var profiles = new LayoutProfile[64];
        Parallel.For(0, profiles.Length, i => profiles[i] = LayoutProfileCache.For(config));

        Assert.All(profiles, p => Assert.Equal(LayoutSplits.None, p[ModCategory.NPC].Splits));
        Assert.Same(LayoutProfileCache.For(config), LayoutProfileCache.For(config));
    }

    [Fact]
    public void SyncLegacyLayoutFields_DoesNothingBeforeMigration()
    {
        var config = Legacy(c => c.SortSplitNpc = false);
        config.SyncLegacyLayoutFields();
        Assert.False(config.SortSplitNpc);
        Assert.Null(config.RaceSplitRows);
    }

    [Fact]
    public void ADowngradeThatDropsTheList_MigratesAgainFromTheMirror()
    {
        var config = Configuration.CreateFresh();
        config.SaveLayoutProfile(LayoutProfile.Recommended.With(
            ModCategory.Gear, LayoutProfile.Recommended[ModCategory.Gear] with { Splits = LayoutSplits.Slot }));

        // An older build re-saves the file without the field; then the user changes a split there.
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(config);
        var older = Newtonsoft.Json.Linq.JObject.Parse(json);
        older.Remove("CategoryLayouts");
        older["SortSplitNpc"] = false;
        var reloaded = older.ToObject<Configuration>()!;

        Migrate(reloaded);

        var profile = LayoutProfileCache.For(reloaded);
        Assert.Equal(LayoutSplits.Slot, profile[ModCategory.Gear].Splits);
        Assert.Equal(LayoutSplits.None, profile[ModCategory.NPC].Splits);
    }
}
