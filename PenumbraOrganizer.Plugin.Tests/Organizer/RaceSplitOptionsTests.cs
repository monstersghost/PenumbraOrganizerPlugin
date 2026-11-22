using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class RaceSplitOptionsTests
{
    private static RaceSplitOptions On(RaceRows rows) => new(true, rows, true, RacePriority.Empty);

    [Fact]
    public void Off_IsMasterOffAllRowsTickedToggleOnNoPriority()
    {
        Assert.False(RaceSplitOptions.Off.Enabled);
        Assert.Equal(RaceRows.All, RaceSplitOptions.Off.Rows);
        Assert.True(RaceSplitOptions.Off.MultiRaceToMultiRace);
        Assert.Empty(RaceSplitOptions.Off.Priority.Ranked);
    }

    [Fact]
    public void InEffect_NeedsTheMasterTheTickAndForTwoRowsTheBodySplit()
    {
        Assert.False(RaceSplitOptions.Off.InEffect(RaceRows.Face, bodySplit: true));
        Assert.True(On(RaceRows.Face).InEffect(RaceRows.Face, bodySplit: false));
        Assert.False(On(RaceRows.Hair).InEffect(RaceRows.Face, bodySplit: true));

        Assert.False(On(RaceRows.All).InEffect(RaceRows.TailsEarsHorns, bodySplit: false));
        Assert.True(On(RaceRows.All).InEffect(RaceRows.TailsEarsHorns, bodySplit: true));
        Assert.False(On(RaceRows.All).InEffect(RaceRows.RacialScaling, bodySplit: false));
        Assert.True(On(RaceRows.All).InEffect(RaceRows.RacialScaling, bodySplit: true));
    }

    [Fact]
    public void RowNames_RoundTripAndNullMeansAll()
    {
        Assert.Equal(RaceRows.All, RaceRowNames.Parse(null));
        Assert.Equal(RaceRows.None, RaceRowNames.Parse([]));
        Assert.Equal(RaceRows.Hair | RaceRows.Skin, RaceRowNames.Parse(["Hair", "Skin", "Bogus"]));
        Assert.Equal(new[] { "Hair", "Skin" }, RaceRowNames.ToNames(RaceRows.Hair | RaceRows.Skin));
        Assert.Equal(6, RaceRowNames.All.Count);
    }

    [Fact]
    public void Equality_ComparesThePriorityBySequence()
    {
        var a = new RaceSplitOptions(true, RaceRows.All, true, RacePriority.Empty.Add("Viera"));
        var b = new RaceSplitOptions(true, RaceRows.All, true, RacePriority.Normalize(["Viera"]));
        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { MultiRaceToMultiRace = false });
    }
}
