using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SplitTogglesTests
{
    private static readonly SplitToggles AllOff = new(
        SplitGear: false, SplitNpc: false, SplitHair: false, SplitGearBody: false, SplitBodyMods: false,
        SharedHandsFeet: true, SplitEmotes: false, RaceEnabled: false, RaceRows: RaceRows.None);

    [Fact]
    public void Most_detailed_turns_every_split_and_race_row_on()
    {
        var r = SplitToggles.MostDetailed(AllOff);
        Assert.True(r.SplitGear);
        Assert.True(r.SplitNpc);
        Assert.True(r.SplitHair);
        Assert.True(r.SplitGearBody);
        Assert.True(r.SplitBodyMods);
        Assert.True(r.SplitEmotes);
        Assert.True(r.RaceEnabled);
        Assert.Equal(RaceRows.All, r.RaceRows);
    }

    [Fact]
    public void Shared_hands_and_feet_is_turned_off_because_sharing_is_less_detailed()
    {
        Assert.False(SplitToggles.MostDetailed(AllOff).SharedHandsFeet);
    }

    [Fact]
    public void Most_detailed_is_idempotent_and_equals_itself_from_any_start()
    {
        var once = SplitToggles.MostDetailed(AllOff);
        Assert.Equal(once, SplitToggles.MostDetailed(once));

        var someOn = AllOff with { SplitGear = true, RaceRows = RaceRows.Hair | RaceRows.Face };
        Assert.Equal(once, SplitToggles.MostDetailed(someOn));
    }
}
