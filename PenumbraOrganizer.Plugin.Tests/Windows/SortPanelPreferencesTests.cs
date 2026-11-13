using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

/// <summary>
/// The pure decisions inside SortPanel.Draw, pulled out so they can be tested without ImGui.
/// </summary>
public class SortPanelPreferencesTests
{
    // ---- hair race code <-> combo index ----

    [Fact]
    public void HairRaceIndexFor_NullCode_IsNone()
    {
        Assert.Equal(0, SortPanel.HairRaceIndexFor(null));
    }

    [Fact]
    public void HairRaceIndexFor_UnknownOrStaleCode_IsNone()
    {
        // A code saved by a build whose race table has since changed must not throw or point at a
        // random row: it falls back to None.
        Assert.Equal(0, SortPanel.HairRaceIndexFor("9999"));
        Assert.Equal(0, SortPanel.HairRaceIndexFor(""));
    }

    [Fact]
    public void HairRaceIndexFor_KnownCode_IsItsTablePositionPlusOne()
    {
        var all = HairRaceCodes.All.ToArray();

        Assert.Equal(1, SortPanel.HairRaceIndexFor(all[0].Code));
        Assert.Equal(all.Length, SortPanel.HairRaceIndexFor(all[^1].Code));
    }

    [Fact]
    public void HairRaceIndexFor_And_HairRaceCodeAt_RoundTripForEveryRace()
    {
        foreach (var race in HairRaceCodes.All)
            Assert.Equal(race.Code, SortPanel.HairRaceCodeAt(SortPanel.HairRaceIndexFor(race.Code)));

        Assert.Null(SortPanel.HairRaceCodeAt(0));
    }

    // ---- body priority re-repair decision ----

    private static readonly string[] Labels = ["Bibo+", "Rue+", "TBSE"];

    [Fact]
    public void RefreshBodyPriority_EmptyLabels_DoesNothing_AndNeverWipesASavedList()
    {
        // The guard the controller cared about: Repair against an empty list would drop every saved
        // entry and report needsSave.
        var result = SortPanel.RefreshBodyPriority([], labelsSeen: null, saved: ["TBSE", "Rue+"], out var needsSave);

        Assert.Null(result);
        Assert.False(needsSave);
    }

    [Fact]
    public void RefreshBodyPriority_SameLabelsInstanceAsBefore_DoesNothing()
    {
        IReadOnlyList<string> labels = Labels;

        var result = SortPanel.RefreshBodyPriority(labels, labelsSeen: labels, saved: ["TBSE"], out var needsSave);

        Assert.Null(result);
        Assert.False(needsSave);
    }

    [Fact]
    public void RefreshBodyPriority_ANewButEqualInstance_RepairsAgain()
    {
        // The reference check is the whole point: a rescan publishes a new instance, and that is
        // when a saved list must be re-validated against the (possibly changed) body list.
        IReadOnlyList<string> seen = [.. Labels];
        IReadOnlyList<string> published = [.. Labels];

        var result = SortPanel.RefreshBodyPriority(published, seen, saved: ["TBSE", "Rue+", "Bibo+"], out var needsSave);

        Assert.NotNull(result);
        Assert.Equal(["TBSE", "Rue+", "Bibo+"], result!.Labels);
        Assert.False(needsSave);
    }

    [Fact]
    public void RefreshBodyPriority_FirstFrame_EmptySavedList_IsDefault_AndIsNotRewritten()
    {
        var result = SortPanel.RefreshBodyPriority(Labels, labelsSeen: null, saved: [], out var needsSave);

        Assert.Equal(Labels, result!.Labels);
        Assert.False(needsSave);
    }

    [Fact]
    public void RefreshBodyPriority_StaleSavedLabel_IsDroppedAndMustBeSaved()
    {
        var result = SortPanel.RefreshBodyPriority(Labels, labelsSeen: null, saved: ["Gone", "TBSE"], out var needsSave);

        Assert.Equal(["TBSE", "Bibo+", "Rue+"], result!.Labels);
        Assert.True(needsSave);
    }

    [Fact]
    public void RefreshBodyPriority_NullSavedList_IsTheDefault_AndDoesNotThrow()
    {
        // A hand-edited config can leave the property null.
        var result = SortPanel.RefreshBodyPriority(Labels, labelsSeen: null, saved: null, out var needsSave);

        Assert.Equal(Labels, result!.Labels);
        Assert.False(needsSave);
    }

    // ---- First-choice body is always the list's rank 1 ----

    private static readonly string[] FirstChoiceLabels = ["Bibo+", "Rue+", "TBSE"];

    [Fact]
    public void FirstChoice_IsTheRankOneLabel()
    {
        Assert.Equal("Bibo+", SortPanel.FirstChoice(BodyPriority.Default(FirstChoiceLabels)));
        Assert.Null(SortPanel.FirstChoice(BodyPriority.Default([])));
    }

    [Fact]
    public void FirstChoice_FollowsArrowMoves_Reset_AndAPickFromTheDropdown()
    {
        var p = BodyPriority.Default(FirstChoiceLabels);

        Assert.Equal("Rue+", SortPanel.FirstChoice(p.MoveDown(0)));
        Assert.Equal("Bibo+", SortPanel.FirstChoice(p.MoveDown(0).MoveUp(1)));
        Assert.Equal("TBSE", SortPanel.FirstChoice(p.MoveToTop("TBSE")));
        Assert.Equal("Bibo+", SortPanel.FirstChoice(
            BodyPriority.Default(FirstChoiceLabels)));
    }

    [Fact]
    public void FirstChoice_FollowsABodyListRefresh()
    {
        var saved = new[] { "TBSE", "Rue+", "Bibo+" };
        var refreshed = new[] { "Bibo+", "Rue+" };

        var repaired = SortPanel.RefreshBodyPriority(refreshed, FirstChoiceLabels, saved, out _);

        Assert.Equal("Rue+", SortPanel.FirstChoice(repaired!));
    }

    [Fact]
    public void GroupingIndexFor_KnownStrategyName_FindsItsIndex() =>
        Assert.Equal(1, SortPanel.GroupingIndexFor("TypeOnly"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NoSuchStrategy")]
    public void GroupingIndexFor_MissingOrUnknown_IsTheDefault(string? saved) =>
        Assert.Equal(SortPanel.DefaultGroupingIndex, SortPanel.GroupingIndexFor(saved));

    [Fact]
    public void SortChoices_DefaultToTheSessionDefaults()
    {
        var config = new Configuration();

        Assert.Null(config.SortGrouping);
        Assert.True(config.SortSplitNpc);
        Assert.False(config.SortSplitGear);
        Assert.False(config.SortSplitHair);
        Assert.False(config.SortSplitGearBody);
        Assert.False(config.SortSplitBodyMods);
    }

    // ---- first-frame load through the factory ----

    [Fact]
    public void FirstFrameLoad_DefaultConfig_MatchesTheOldInlineLoad()
    {
        var config = new Configuration();
        var loaded = SortSelectionFactory.FromConfig(config, Labels);

        Assert.Equal(SortPanel.GroupingIndexFor(config.SortGrouping),
            Array.FindIndex(SortPanel.Groupings, g => g.Strategy == loaded.Strategy));
        Assert.Equal(config.SortSplitGear, loaded.SplitGear);
        Assert.Equal(config.SortSplitNpc, loaded.SplitNpc);
        Assert.Equal(config.SortSplitHair, loaded.SplitHair);
        Assert.Equal(SortPanel.HairRaceIndexFor(config.HairRaceCode), SortPanel.HairRaceIndexFor(loaded.HairRaceCode));
        Assert.False(loaded.RootOnly);
    }

    [Fact]
    public void FirstFrameLoad_CustomisedConfig_MatchesTheOldInlineLoad()
    {
        var race = HairRaceCodes.All.Last().Code;
        var config = new Configuration
        {
            SortGrouping = "CreatorOnly", SortSplitGear = true, SortSplitNpc = false, SortSplitHair = true,
            HairRaceCode = race, MidlanderFallback = true, BodyPriority = ["TBSE"],
        };
        var loaded = SortSelectionFactory.FromConfig(config, Labels);

        Assert.Equal(SortPanel.GroupingIndexFor("CreatorOnly"),
            Array.FindIndex(SortPanel.Groupings, g => g.Strategy == loaded.Strategy));
        Assert.True(loaded.SplitGear);
        Assert.False(loaded.SplitNpc);
        Assert.True(loaded.SplitHair);
        Assert.Equal(SortPanel.HairRaceIndexFor(race), SortPanel.HairRaceIndexFor(loaded.HairRaceCode));
        Assert.True(loaded.MidlanderFallback);
        Assert.Equal("TBSE", SortPanel.FirstChoice(loaded.BodyPriority!));
        Assert.False(loaded.RootOnly);
    }

    [Fact]
    public void ScopeFor_WholeLibraryIsNull_RootOnlyIsRootScope()
    {
        Assert.Null(SortPanel.ScopeFor(false));
        Assert.True(SortPanel.ScopeFor(true)!.RootOnly);
    }
}
