using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SortPanelLayoutAdapterTests
{
    private static readonly string[] Labels = ["Bibo+", "Rue+", "TBSE"];
    private static readonly string Grouping = SortStrategy.TypeThenCreator.ToString();

    private static Configuration Migrated(Action<Configuration>? set = null)
    {
        var config = new Configuration();
        set?.Invoke(config);
        config.MigrateToCurrentVersion();
        return config;
    }

    // A config with the layouts present (as Task 4 and later save it), settled on the given Group by.
    private static Configuration Settled(Configuration config)
    {
        config.MigrateToCurrentVersion();
        config.SortGrouping = Grouping;
        return config;
    }

    private static (bool Save, SplitToggles Shown) Persist(Configuration config, SplitToggles toggles) =>
        SortPanelLayoutAdapter.Persist(config, toggles, Grouping, toggles.RaceEnabled,
            config.SendMultiRaceToMultiRace, RacePriority.Normalize(config.RacePriority));

    private static SplitToggles Shown(Configuration config) =>
        SortPanelLayoutAdapter.TogglesFor(LayoutProfileCache.For(config), config.RaceSplitEnabled == true);

    private static Configuration NewtonsoftRoundTrip(Configuration config)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(config);
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(json)!;
        back.MigrateToCurrentVersion();
        return back;
    }

    private static Configuration SystemTextRoundTrip(Configuration config)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var back = System.Text.Json.JsonSerializer.Deserialize<Configuration>(json)!;
        back.MigrateToCurrentVersion();
        return back;
    }

    [Fact]
    public void TogglesFor_ShowsTheProfilesSplits_AndTheGivenMaster()
    {
        var toggles = SortPanelLayoutAdapter.TogglesFor(LayoutProfile.Recommended, raceEnabled: true);
        Assert.Equal(new SplitToggles(true, true, true, true, true, true, true, true, RaceRows.All), toggles);
        Assert.True(SortPanelLayoutAdapter.Matches(LayoutProfile.Recommended, toggles));
        Assert.False(SortPanelLayoutAdapter.Matches(LayoutProfile.Recommended, toggles with { SplitNpc = false }));
    }

    // Each checkbox of the tab, flipped from the Recommended state, and the one category it must change.
    public static TheoryData<string> Ticks() => new()
    {
        "SplitGear", "SplitGearBody", "SharedHandsFeet", "SplitNpc", "SplitHair", "SplitBodyMods", "SplitEmotes",
        "Race-Hair", "Race-Face", "Race-Eyes", "Race-Skin", "Race-TailsEarsHorns", "Race-RacialScaling", "Master",
    };

    private static SplitToggles Flip(SplitToggles t, string tick) => tick switch
    {
        "SplitGear" => t with { SplitGear = !t.SplitGear },
        "SplitGearBody" => t with { SplitGearBody = !t.SplitGearBody },
        "SharedHandsFeet" => t with { SharedHandsFeet = !t.SharedHandsFeet },
        "SplitNpc" => t with { SplitNpc = !t.SplitNpc },
        "SplitHair" => t with { SplitHair = !t.SplitHair },
        "SplitBodyMods" => t with { SplitBodyMods = !t.SplitBodyMods },
        "SplitEmotes" => t with { SplitEmotes = !t.SplitEmotes },
        "Master" => t with { RaceEnabled = !t.RaceEnabled },
        _ => t with { RaceRows = t.RaceRows ^ Enum.Parse<RaceRows>(tick["Race-".Length..]) },
    };

    [Fact]
    public void EachCheckbox_WritesTheOverrideOfItsOwnCategoryOnly()
    {
        var start = SortPanelLayoutAdapter.TogglesFor(LayoutProfile.Recommended, false);
        var cases = new (SplitToggles Toggles, ModCategory Category, LayoutSplits Splits)[]
        {
            (start with { SplitGear = false }, ModCategory.Gear, LayoutSplits.BodyType | LayoutSplits.SharedHandsFeet),
            (start with { SplitGearBody = false }, ModCategory.Gear, LayoutSplits.Slot | LayoutSplits.SharedHandsFeet),
            (start with { SharedHandsFeet = false }, ModCategory.Gear, LayoutSplits.BodyType | LayoutSplits.Slot),
            (start with { SplitNpc = false }, ModCategory.NPC, LayoutSplits.None),
            (start with { SplitHair = false }, ModCategory.Hair, LayoutSplits.Race),
            (start with { SplitBodyMods = false }, ModCategory.Body, LayoutSplits.Race | LayoutSplits.RacialScalingRace),
            (start with { SplitEmotes = false }, ModCategory.Animation, LayoutSplits.None),
            (start with { RaceRows = RaceRows.All & ~RaceRows.Eyes }, ModCategory.Face, LayoutSplits.Race),
            (start with { RaceRows = RaceRows.All & ~RaceRows.Skin }, ModCategory.Skin, LayoutSplits.None),
            (start with { RaceRows = RaceRows.All & ~RaceRows.RacialScaling }, ModCategory.Body,
                LayoutSplits.PartAndBodyType | LayoutSplits.Race),
        };

        foreach (var (toggles, category, splits) in cases)
        {
            var next = SortPanelLayoutAdapter.WithToggles(LayoutProfile.Recommended, toggles);
            Assert.Equal([category], next.Overrides().Keys);
            Assert.Equal(splits, next[category].Splits);
            Assert.True(SortPanelLayoutAdapter.Matches(next, toggles));
        }
    }

    [Fact]
    public void TickingBackToRecommended_LeavesNoOverride()
    {
        var start = SortPanelLayoutAdapter.TogglesFor(LayoutProfile.Recommended, false);
        var off = SortPanelLayoutAdapter.WithToggles(LayoutProfile.Recommended, start with { SplitNpc = false });
        var back = SortPanelLayoutAdapter.WithToggles(off, start);
        Assert.Equal(LayoutProfile.Recommended, back);
        Assert.Empty(back.Overrides());
    }

    [Fact]
    public void ACategoryWithACustomFolderOrCreatorLevel_IsReadOnly_AndKeepsItsSplits()
    {
        var custom = LayoutProfile.Recommended
            .With(ModCategory.Gear, new CategoryLayout("Equipment/Gear", null, LayoutSplits.Slot))
            .With(ModCategory.NPC, new CategoryLayout("NPC", CreatorLevel.None, LayoutSplits.NpcKind));

        var readOnly = SortPanelLayoutAdapter.ReadOnlyCategories(custom);
        Assert.Equal(new HashSet<ModCategory> { ModCategory.Gear, ModCategory.NPC }, readOnly);
        Assert.Equal("Custom (read-only here): Gear, NPC.", SortPanelLayoutAdapter.CustomNote(readOnly));

        // Select all ticks everything; the read-only categories keep what they had, the others follow the boxes.
        var all = SplitToggles.MostDetailed(SortPanelLayoutAdapter.TogglesFor(custom, false));
        var next = SortPanelLayoutAdapter.WithToggles(custom, all with { SplitNpc = false });
        Assert.Equal(custom[ModCategory.Gear], next[ModCategory.Gear]);
        Assert.Equal(custom[ModCategory.NPC], next[ModCategory.NPC]);
        Assert.Equal(all.RaceRows, next.ToLegacy().RaceRows);
    }

    [Fact]
    public void SplitChangesOnly_NeverMakeACategoryReadOnly()
    {
        var splitsOnly = LayoutProfile.Recommended.With(
            ModCategory.Gear, LayoutProfile.Recommended[ModCategory.Gear] with { Splits = LayoutSplits.None });
        Assert.Empty(SortPanelLayoutAdapter.ReadOnlyCategories(splitsOnly));
        Assert.Null(SortPanelLayoutAdapter.CustomNote(new HashSet<ModCategory>()));
    }

    [Theory]
    [InlineData(RaceRows.Hair, ModCategory.Hair)]
    [InlineData(RaceRows.Face, ModCategory.Face)]
    [InlineData(RaceRows.Eyes, ModCategory.Face)]
    [InlineData(RaceRows.Skin, ModCategory.Skin)]
    [InlineData(RaceRows.TailsEarsHorns, ModCategory.Body)]
    [InlineData(RaceRows.RacialScaling, ModCategory.Body)]
    public void EveryRaceRow_BelongsToOneCategory(RaceRows row, ModCategory category) =>
        Assert.Equal(category, SortPanelLayoutAdapter.CategoryOf(row));

    [Fact]
    public void FromConfig_ReadsTheSplitsFromTheLayouts_NotTheLegacyFields()
    {
        var config = Migrated();
        config.SaveLayoutProfile(LayoutProfile.Recommended);
        // A stale legacy field must not leak in once the layouts exist.
        config.SortSplitNpc = false;

        var selection = SortSelectionFactory.FromConfig(config, Labels);
        Assert.True(selection.SplitNpc);
        Assert.True(selection.SplitGear);
        Assert.True(selection.Layout.SharedHandsAndFeet);
        Assert.Equal(RaceRows.All, RaceSplitOptions.FromConfig(config).Rows);
    }

    [Fact]
    public void FromConfig_OfAMigratedLegacyConfig_EqualsTheUnmigratedSelection()
    {
        var legacy = new Configuration
        {
            SortSplitGear = true, SortSplitNpc = false, SortSplitBodyMods = true,
            RaceSplitEnabled = true, RaceSplitRows = ["Face", "TailsEarsHorns"],
        };
        var before = SortSelectionFactory.FromConfig(legacy, Labels);
        legacy.MigrateToCurrentVersion();
        var after = SortSelectionFactory.FromConfig(legacy, Labels);

        Assert.NotNull(legacy.CategoryLayouts);
        Assert.Equal(before, after);
        Assert.Equal(before.Fingerprint(), after.Fingerprint());
    }

    [Fact]
    public void FromConfig_OfAFreshInstall_IsRecommended()
    {
        var selection = SortSelectionFactory.FromConfig(Configuration.CreateFresh(), Labels);
        Assert.True(selection.SplitGear);
        Assert.True(selection.SplitGearBody);
        Assert.True(selection.SplitHair);
        Assert.True(selection.SplitBodyMods);
        Assert.True(selection.Layout.SharedHandsAndFeet);
        Assert.True(selection.Layout.SplitEmotes);
        Assert.Null(selection.Layout.Race); // master off
        Assert.Equal(SortStrategy.TypeThenCreator, selection.Strategy);
    }

    [Fact]
    public void MigrateToCurrentVersion_WiresTheLayoutMigration()
    {
        var config = new Configuration();
        config.MigrateToCurrentVersion();
        Assert.Equal(["Gear", "Hair", "Body", "Animation"], config.CategoryLayouts!.Select(e => e.Category));
    }

    [Fact]
    public void MigrateToCurrentVersion_RewritesAStaleMirror_OnlyWhenTheListAlreadyExisted()
    {
        // The list came from this build; the legacy fields were edited by hand (or are stale).
        var existing = new Configuration { CategoryLayouts = [], SortSplitGear = false, RaceSplitEnabled = false };
        existing.MigrateToCurrentVersion();
        Assert.True(existing.SortSplitGear);
        Assert.Equal(RaceRowNames.ToNames(RaceRows.All), existing.RaceSplitRows);

        // First migration: the legacy fields are left exactly as they were.
        var first = new Configuration { SortSplitGear = false };
        first.MigrateToCurrentVersion();
        Assert.False(first.SortSplitGear);
        Assert.Null(first.RaceSplitRows);
    }

    [Fact]
    public void Persist_SavesNothing_WhenNothingChanged()
    {
        var config = Settled(Configuration.CreateFresh());
        var version = config.CategoryLayoutsVersion;

        var result = Persist(config, Shown(config));

        Assert.False(result.Save);
        Assert.Equal(Shown(config), result.Shown);
        Assert.Equal(version, config.CategoryLayoutsVersion);
    }

    [Fact]
    public void Persist_SavesAChangedGroupBy()
    {
        var config = Settled(Configuration.CreateFresh());
        var result = SortPanelLayoutAdapter.Persist(config, Shown(config), SortStrategy.CreatorOnly.ToString(),
            false, true, RacePriority.Empty);
        Assert.True(result.Save);
        Assert.Equal(SortStrategy.CreatorOnly.ToString(), config.SortGrouping);
    }

    [Theory]
    [MemberData(nameof(Ticks))]
    public void Persist_EachTickSaves_KeepsTheMirrorTrue_AndSurvivesAJsonRoundTrip(string tick)
    {
        foreach (var start in new[] { Configuration.CreateFresh(), new Configuration() })
        {
            var config = Settled(start);
            var toggles = Flip(Shown(config), tick);

            var result = Persist(config, toggles);

            Assert.True(result.Save);
            Assert.Equal(toggles, result.Shown);
            Assert.Equal(toggles, Shown(config));
            Assert.Equal(LayoutProfileCache.For(config).ToLegacy(), LegacyLayout.ReadFields(config)); // the mirror
            Assert.Equal(config.EffectiveHairRaceSplit, config.SortSplitHairByRace);
            Assert.False(Persist(config, toggles).Save); // settled: the next frame writes nothing

            var expected = SortSelectionFactory.FromConfig(config, Labels);
            Assert.Equal(expected, SortSelectionFactory.FromConfig(NewtonsoftRoundTrip(config), Labels));
            Assert.Equal(expected, SortSelectionFactory.FromConfig(SystemTextRoundTrip(config), Labels));
            Assert.Equal(expected.Fingerprint(), SortSelectionFactory.FromConfig(NewtonsoftRoundTrip(config), Labels).Fingerprint());
        }
    }

    [Fact]
    public void Persist_OfAnUnmigratedConfig_WritesTheLegacyFieldsAsBefore()
    {
        var config = new Configuration { SortGrouping = Grouping, RaceSplitEnabled = false };
        var toggles = Shown(config) with { SplitGear = true, RaceRows = RaceRows.Hair };

        var result = Persist(config, toggles);

        Assert.True(result.Save);
        Assert.Null(config.CategoryLayouts);
        Assert.True(config.SortSplitGear);
        Assert.Equal(["Hair"], config.RaceSplitRows!);
        Assert.Equal(toggles, Shown(config));
    }

    [Fact]
    public void Persist_SelectAll_SkipsAReadOnlyCategory()
    {
        var config = Settled(Configuration.CreateFresh());
        var custom = LayoutProfile.Recommended
            .With(ModCategory.Gear, new CategoryLayout("Equipment/Gear", null, LayoutSplits.Slot))
            .With(ModCategory.Hair, LayoutProfile.Recommended[ModCategory.Hair] with { Creator = CreatorLevel.None, Splits = LayoutSplits.None });
        config.SaveLayoutProfile(custom);
        var shownBefore = Shown(config);

        var all = SplitToggles.MostDetailed(shownBefore);
        var result = Persist(config, all);

        var profile = LayoutProfileCache.For(config);
        Assert.True(result.Save); // the race master and the others changed
        Assert.Equal(custom[ModCategory.Gear], profile[ModCategory.Gear]);
        Assert.Equal(custom[ModCategory.Hair], profile[ModCategory.Hair]);
        Assert.Equal(LayoutSplits.NpcKind, profile[ModCategory.NPC].Splits);
        Assert.Equal(LayoutSplits.Emote, profile[ModCategory.Animation].Splits);

        // The boxes of the read-only categories are put back to what is saved.
        Assert.False(result.Shown.SplitGearBody);
        Assert.False(result.Shown.SharedHandsFeet);
        Assert.False(result.Shown.SplitHair);
        Assert.False(result.Shown.RaceRows.HasFlag(RaceRows.Hair));
        Assert.True(result.Shown.RaceEnabled);
        Assert.Equal(result.Shown, Shown(config));
        Assert.False(Persist(config, result.Shown).Save);
        Assert.False(config.SortSplitHairByRace); // the Hair row is not in effect
    }

    [Fact]
    public void Persist_AHandEditedHairWithoutRace_ThenOnlyTheMasterChanges_LeavesTheLegacyHairRaceOff()
    {
        // A hand-edited file: Hair without Race, the race rows mirror absent (null reads as "all ticked").
        var config = new Configuration
        {
            CategoryLayouts = [new() { Category = "Hair", Folder = "Hair", Splits = ["HairNumber"] }],
            RaceSplitEnabled = false,
        };
        config.SortGrouping = Grouping;
        config.RaceSplitRows = null;
        config.CategoryLayoutsVersion++;

        var toggles = Shown(config) with { RaceEnabled = true };
        var result = Persist(config, toggles);

        Assert.True(result.Save);
        Assert.True(config.RaceSplitEnabled);
        Assert.False(config.SortSplitHairByRace);
        Assert.DoesNotContain("Hair", config.RaceSplitRows!);
        Assert.False(config.EffectiveHairRaceSplit);
    }

    [Fact]
    public void ASplitChangedThroughTheAdapter_SurvivesASaveAndReload()
    {
        // A config as the previous build of this branch saved it: the layouts are present.
        var saved = Configuration.CreateFresh();
        saved.SaveLayoutProfile(LayoutProfile.Recommended.With(
            ModCategory.Gear, LayoutProfile.Recommended[ModCategory.Gear] with { Splits = LayoutSplits.Slot }));
        var file = Newtonsoft.Json.JsonConvert.SerializeObject(saved);

        var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(file)!;
        loaded.MigrateToCurrentVersion();
        loaded.SortGrouping = Grouping;
        Assert.Equal(LayoutSplits.Slot, LayoutProfileCache.For(loaded)[ModCategory.Gear].Splits);

        var toggles = Shown(loaded) with { SplitNpc = false };
        Assert.True(Persist(loaded, toggles).Save);

        var reloaded = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            Newtonsoft.Json.JsonConvert.SerializeObject(loaded))!;
        reloaded.MigrateToCurrentVersion();

        var profile = LayoutProfileCache.For(reloaded);
        Assert.Equal(LayoutSplits.None, profile[ModCategory.NPC].Splits);
        Assert.Equal(LayoutSplits.Slot, profile[ModCategory.Gear].Splits);
        Assert.False(reloaded.SortSplitNpc);
        Assert.Equal(toggles, Shown(reloaded));
    }
}
