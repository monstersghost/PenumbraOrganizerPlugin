using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

/// <summary>
/// The headless selection reads the category layouts. Moved unchanged from SortPanelLayoutAdapterTests when the
/// Sort tab's checkbox adapter was removed (spec 2): these never tested the adapter. The two
/// MigrateToCurrentVersion tests moved with them for the same reason: they pin the configuration, not the adapter.
/// </summary>
public class SortSelectionFactoryLayoutTests
{
    private static readonly string[] Labels = ["Bibo+", "Rue+", "TBSE"];

    private static Configuration Migrated(Action<Configuration>? set = null)
    {
        var config = new Configuration();
        set?.Invoke(config);
        config.MigrateToCurrentVersion();
        return config;
    }

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
    public void ChangingOnlyTheRaceMaster_KeepsTheHairMirrorTrue_ToTheHairLayout()
    {
        // What the Sort tab's PersistSortChoices does when only the race master changes (spec 2 removed the
        // adapter that used to pin this): the legacy hair-race mirror follows the Hair layout, not the master alone.
        var config = Configuration.CreateFresh();
        config.SaveLayoutProfile(LayoutProfile.Recommended.With(ModCategory.Hair,
            LayoutProfile.Recommended[ModCategory.Hair] with { Splits = LayoutSplits.HairNumber }));

        config.RaceSplitEnabled = true;
        config.SyncLegacyLayoutFields();
        Assert.False(config.SortSplitHairByRace);

        config.SaveLayoutProfile(LayoutProfile.Recommended);
        config.SyncLegacyLayoutFields();
        Assert.True(config.SortSplitHairByRace);
    }
}
