using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests;

public class ConfigurationRaceSplitTests
{
    [Fact]
    public void Fresh_MigratesToMasterOffAllRowsTicked()
    {
        var config = new Configuration();
        config.MigrateToCurrentVersion();

        Assert.False(config.RaceSplitEnabled);
        Assert.Null(config.RaceSplitRows);
        Assert.True(config.SendMultiRaceToMultiRace);
        Assert.Empty(config.RacePriority);

        var options = RaceSplitOptions.FromConfig(config);
        Assert.False(options.Enabled);
        Assert.Equal(RaceRows.All, options.Rows);
    }

    [Fact]
    public void OldHairRaceUser_MigratesToMasterOnWithOnlyHairTicked()
    {
        var config = new Configuration { SortSplitHairByRace = true };
        config.MigrateToCurrentVersion();

        Assert.True(config.RaceSplitEnabled);
        Assert.Equal(new[] { "Hair" }, config.RaceSplitRows);
        Assert.True(config.EffectiveHairRaceSplit);
    }

    [Fact]
    public void Migration_RunsOnlyWhileTheMasterIsNull()
    {
        var config = new Configuration { SortSplitHairByRace = true, RaceSplitEnabled = false };
        config.MigrateToCurrentVersion();

        Assert.False(config.RaceSplitEnabled);
        Assert.Null(config.RaceSplitRows);
    }

    [Fact]
    public void FromConfig_BeforeMigration_StillReadsTheLegacyCheckbox()
    {
        var options = RaceSplitOptions.FromConfig(new Configuration { SortSplitHairByRace = true });
        Assert.True(options.Enabled);
        Assert.Equal(RaceRows.Hair, options.Rows);
    }

    [Fact]
    public void LegacyField_IsDerivedFromTheEffectiveHairRowNotTheMasterAlone()
    {
        var config = new Configuration { RaceSplitEnabled = true, RaceSplitRows = ["Face"] };
        config.SyncLegacyRaceFields();
        Assert.False(config.SortSplitHairByRace);

        config.RaceSplitRows = ["Face", "Hair"];
        config.SyncLegacyRaceFields();
        Assert.True(config.SortSplitHairByRace);

        config.RaceSplitRows = null; // all ticked
        config.SyncLegacyRaceFields();
        Assert.True(config.SortSplitHairByRace);

        config.RaceSplitEnabled = false;
        config.SyncLegacyRaceFields();
        Assert.False(config.SortSplitHairByRace);
    }

    [Fact]
    public void HairPriorityRaceAndMidlanderFallback_AreNeverTouched()
    {
        var config = new Configuration
        {
            SortSplitHairByRace = true, HairRaceCode = "1801", MidlanderFallback = true,
        };
        config.MigrateToCurrentVersion();
        config.RaceSplitEnabled = true;
        config.RaceSplitRows = ["Face"];
        config.SyncLegacyRaceFields();

        Assert.Equal("1801", config.HairRaceCode);
        Assert.True(config.MidlanderFallback);
    }

    [Fact]
    public void FromConfig_NormalizesThePriority()
    {
        var config = new Configuration { RaceSplitEnabled = true, RacePriority = ["Viera", "Bogus", "Viera"] };
        Assert.Equal(new[] { "Viera" }, RaceSplitOptions.FromConfig(config).Priority.Ranked);
    }
}
