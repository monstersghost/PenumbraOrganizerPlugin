using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

/// <summary>
/// What the Sort tab saves now that the checkbox adapter is gone (spec 2): Group by and the global race settings
/// through <see cref="SortPanel.PersistGroupingAndRace"/>, the category splits through the layout editor's persist
/// callback (<see cref="Configuration.SaveLayoutProfile"/>). These keep the guarantees the adapter's Persist tests
/// pinned: nothing is written on a frame with no change, the legacy fields stay a true rollback mirror, and every
/// change survives a save and reload.
/// </summary>
public class SortPanelPersistTests
{
    private static readonly string[] Labels = ["Bibo+", "Rue+", "TBSE"];
    private static readonly string Grouping = SortStrategy.TypeThenCreator.ToString();

    private static Configuration Settled(Configuration config)
    {
        config.MigrateToCurrentVersion();
        config.SortGrouping = Grouping;
        return config;
    }

    // The tab's per-frame call with the values the config already holds.
    private static bool PersistUnchanged(Configuration config, bool? raceEnabled = null) =>
        SortPanel.PersistGroupingAndRace(config, Grouping, raceEnabled ?? config.RaceSplitEnabled == true,
            config.SendMultiRaceToMultiRace, RacePriority.Normalize(config.RacePriority));

    private static Configuration NewtonsoftRoundTrip(Configuration config)
    {
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            Newtonsoft.Json.JsonConvert.SerializeObject(config))!;
        back.MigrateToCurrentVersion();
        return back;
    }

    private static Configuration SystemTextRoundTrip(Configuration config)
    {
        var back = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
            System.Text.Json.JsonSerializer.Serialize(config))!;
        back.MigrateToCurrentVersion();
        return back;
    }

    [Fact]
    public void SavesNothing_WhenNothingChanged()
    {
        var config = Settled(Configuration.CreateFresh());
        config.RaceSplitEnabled = false;
        var version = config.CategoryLayoutsVersion;

        Assert.False(PersistUnchanged(config));
        Assert.Equal(version, config.CategoryLayoutsVersion);
    }

    [Fact]
    public void SavesAChangedGroupBy()
    {
        var config = Settled(Configuration.CreateFresh());
        Assert.True(SortPanel.PersistGroupingAndRace(
            config, SortStrategy.CreatorOnly.ToString(), false, true, RacePriority.Empty));
        Assert.Equal(SortStrategy.CreatorOnly.ToString(), config.SortGrouping);
        Assert.False(SortPanel.PersistGroupingAndRace(
            config, SortStrategy.CreatorOnly.ToString(), false, true, RacePriority.Empty));
    }

    [Fact]
    public void SavesTheMultiRaceSwitchAndTheRacePriority()
    {
        var config = Settled(Configuration.CreateFresh());
        var priority = RacePriority.Empty.Add(RaceNames.All[0]);

        Assert.True(SortPanel.PersistGroupingAndRace(config, Grouping, false, false, priority));
        Assert.False(config.SendMultiRaceToMultiRace);
        Assert.Equal(priority.Ranked, config.RacePriority);
        Assert.False(SortPanel.PersistGroupingAndRace(config, Grouping, false, false, priority));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRaceMaster_Saves_AndKeepsTheMirrorTrue(bool fresh)
    {
        var config = Settled(fresh ? Configuration.CreateFresh() : new Configuration());
        var master = config.RaceSplitEnabled != true;

        Assert.True(PersistUnchanged(config, master));

        Assert.Equal(master, config.RaceSplitEnabled);
        Assert.Equal(LayoutProfileCache.For(config).ToLegacy(), LegacyLayout.ReadFields(config));
        Assert.Equal(config.EffectiveHairRaceSplit, config.SortSplitHairByRace);
        Assert.False(PersistUnchanged(config)); // settled: the next frame writes nothing

        var expected = SortSelectionFactory.FromConfig(config, Labels);
        Assert.Equal(expected, SortSelectionFactory.FromConfig(NewtonsoftRoundTrip(config), Labels));
        Assert.Equal(expected, SortSelectionFactory.FromConfig(SystemTextRoundTrip(config), Labels));
    }

    [Fact]
    public void AHandEditedHairWithoutRace_ThenOnlyTheMasterChanges_LeavesTheLegacyHairRaceOff()
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

        Assert.True(PersistUnchanged(config, raceEnabled: true));

        Assert.True(config.RaceSplitEnabled);
        Assert.False(config.SortSplitHairByRace);
        Assert.DoesNotContain("Hair", config.RaceSplitRows!);
        Assert.False(config.EffectiveHairRaceSplit);
    }

    [Fact]
    public void AnUnmigratedConfig_GetsTheRaceSettings_AndKeepsItsLegacySplitFields()
    {
        var config = new Configuration { SortGrouping = Grouping, RaceSplitEnabled = false, SortSplitGear = true };

        Assert.True(PersistUnchanged(config, raceEnabled: true));

        Assert.Null(config.CategoryLayouts);
        Assert.True(config.RaceSplitEnabled);
        Assert.True(config.SortSplitGear);
        Assert.False(PersistUnchanged(config));
    }

    // Every split row of every category, flipped through the layout editor as the Advanced screen does it.
    public static TheoryData<ModCategory, LayoutSplits> EverySplit()
    {
        var data = new TheoryData<ModCategory, LayoutSplits>();
        foreach (var category in LayoutCatalog.Categories)
            foreach (var info in LayoutCatalog.SplitsFor(category))
                data.Add(category, info.Split);
        return data;
    }

    [Theory]
    [MemberData(nameof(EverySplit))]
    public void EachSplitFlippedInTheEditor_Saves_KeepsTheMirrorTrue_AndSurvivesAJsonRoundTrip(
        ModCategory category, LayoutSplits split)
    {
        foreach (var start in new[] { Configuration.CreateFresh(), new Configuration() })
        {
            var config = Settled(start);
            var saves = 0;
            var editor = new CategoryLayoutEditor(LayoutProfileCache.For(config), profile =>
            {
                config.SaveLayoutProfile(profile);
                saves++;
            });
            editor.Select(category);
            var wasOn = editor.Layout.Has(split);

            editor.SetSplit(split, !wasOn);

            Assert.Equal(1, saves);
            Assert.Equal(!wasOn, LayoutProfileCache.For(config)[category].Has(split));
            Assert.Equal(LayoutProfileCache.For(config).ToLegacy(), LegacyLayout.ReadFields(config)); // the mirror
            Assert.Equal(config.EffectiveHairRaceSplit, config.SortSplitHairByRace);
            Assert.False(PersistUnchanged(config)); // the splits alone give the per-frame persist nothing to write

            var expected = SortSelectionFactory.FromConfig(config, Labels);
            Assert.Equal(expected, SortSelectionFactory.FromConfig(NewtonsoftRoundTrip(config), Labels));
            Assert.Equal(expected, SortSelectionFactory.FromConfig(SystemTextRoundTrip(config), Labels));
            Assert.Equal(expected.Fingerprint(), SortSelectionFactory.FromConfig(NewtonsoftRoundTrip(config), Labels).Fingerprint());
        }
    }
}
