using PenumbraOrganizer.Plugin;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SortSelectionFactoryTests
{
    private static readonly string[] Labels = ["Bibo+", "Rue+", "TBSE"];

    [Fact]
    public void Default_Config_Gives_The_Panels_Defaults()
    {
        var s = SortSelectionFactory.FromConfig(new Configuration(), Labels);
        Assert.Equal(SortPanel.Groupings[SortPanel.DefaultGroupingIndex].Strategy, s.Strategy);
        Assert.False(s.SplitGear);
        Assert.True(s.SplitNpc);
        Assert.False(s.SplitHair);
        Assert.Null(s.HairRaceCode);
        Assert.False(s.MidlanderFallback);
        Assert.False(s.SplitGearBody);
        Assert.False(s.SplitBodyMods);
        Assert.Equal(default, s.Layout);
        Assert.False(s.RootOnly);
        Assert.Equal(BodyPriority.Default(Labels), s.BodyPriority);
    }

    [Fact]
    public void Customised_Config_Is_Carried_Over()
    {
        var race = HairRaceCodes.All.First().Code;
        var config = new Configuration
        {
            SortGrouping = SortStrategy.CreatorThenType.ToString(),
            SortSplitGear = true, SortSplitNpc = false, SortSplitHair = true,
            SortSplitGearBody = true, SortSplitBodyMods = true,
            SortSharedHandsFeet = true, SortSplitHairByRace = true, SortSplitEmotes = true,
            HairRaceCode = race, MidlanderFallback = true,
            BodyPriority = ["TBSE", "Rue+"],
        };
        var s = SortSelectionFactory.FromConfig(config, Labels);
        Assert.Equal(SortStrategy.CreatorThenType, s.Strategy);
        Assert.True(s.SplitGear);
        Assert.False(s.SplitNpc);
        Assert.True(s.SplitHair);
        Assert.True(s.SplitGearBody);
        Assert.True(s.SplitBodyMods);
        Assert.Equal(new LayoutOptions(true, true, true), s.Layout);
        Assert.Equal(race, s.HairRaceCode);
        Assert.True(s.MidlanderFallback);
        Assert.Equal(new[] { "TBSE", "Rue+", "Bibo+" }, s.BodyPriority!.Labels);
    }

    [Fact]
    public void Unknown_Strategy_Falls_Back_To_The_Default()
    {
        var s = SortSelectionFactory.FromConfig(new Configuration { SortGrouping = "Nonsense" }, Labels);
        Assert.Equal(SortPanel.Groupings[SortPanel.DefaultGroupingIndex].Strategy, s.Strategy);
    }

    [Fact]
    public void Empty_Body_Labels_Do_Not_Wipe_The_Saved_Priority_In_The_Config()
    {
        var config = new Configuration { BodyPriority = ["TBSE"] };
        SortSelectionFactory.FromConfig(config, []);
        Assert.Equal(new[] { "TBSE" }, config.BodyPriority);
    }

    [Fact]
    public void Runner_Sorts_With_The_Selection_And_Scope()
    {
        var state = new OrganizerState();
        state.LoadScan(new[]
        {
            new OrganizerModRow { Identifier = "r", Name = "R", Author = "a", CurrentPath = "R", ProposedPath = "R" },
            new OrganizerModRow { Identifier = "f", Name = "F", Author = "a", CurrentPath = "Old/F", ProposedPath = "Old/F" },
        }, new HashSet<string>());
        var selection = SortSelectionFactory.FromConfig(new Configuration(), Labels);

        SortRunner.Run(state, selection, n => n, new SortScope(RootOnly: true));

        Assert.Equal("Old/F", state.Mods.Single(m => m.Identifier == "f").ProposedPath);
        Assert.NotNull(state.LastSortSummary);
        Assert.True(state.LastSortSummary!.Scope.RootOnly);
    }
}
