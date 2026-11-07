using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SortLayoutOptionsTests
{
    [Fact]
    public void SharedHandsFeet_IsHiddenUntilTheSlotSplitIsOn()
    {
        Assert.False(SortLayout.Compute(SortStrategy.TypeOnly, false, false, true, splitGear: false).SharedHandsFeet.Visible);
        Assert.True(SortLayout.Compute(SortStrategy.TypeOnly, false, false, true, splitGear: true).SharedHandsFeet.Visible);
    }

    [Fact]
    public void BothOptions_AreGreyedUnderCreatorGrouping()
    {
        var s = SortLayout.Compute(SortStrategy.CreatorOnly, false, false, true, splitGear: true);
        Assert.False(s.SharedHandsFeet.Enabled);
        Assert.False(s.SplitHairByRace.Enabled);
        Assert.True(s.SplitHairByRace.Visible);
    }

    [Fact]
    public void Configuration_DefaultsBothOptionsOff()
    {
        var config = new Configuration();
        Assert.False(config.SortSharedHandsFeet);
        Assert.False(config.SortSplitHairByRace);
    }

    [Fact]
    public void SortSelection_ComparesTheOptions()
    {
        var a = new SortSelection(SortStrategy.TypeOnly, true, false, false);
        var b = a with { Layout = new LayoutOptions(SharedHandsAndFeet: true) };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SplitEmotes_IsAlwaysVisible_AndGreyedUnderCreatorGrouping()
    {
        var typeStates = SortLayout.Compute(SortStrategy.TypeOnly, false, false, true);
        Assert.Equal(new ControlState(true, true), typeStates.SplitEmotes);

        var creatorStates = SortLayout.Compute(SortStrategy.CreatorOnly, false, false, true);
        Assert.Equal(new ControlState(true, false), creatorStates.SplitEmotes);
    }
}
