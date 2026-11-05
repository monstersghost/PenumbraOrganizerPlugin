using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SortLayoutTests
{
    private static SortControlStates Compute(
        SortStrategy strategy = SortStrategy.TypeThenCreator, bool splitHair = false,
        bool scrapedFeature = false, bool gateOpen = true) =>
        SortLayout.Compute(strategy, splitHair, scrapedFeature, gateOpen);

    [Fact]
    public void HairDependents_AreHiddenUntilTheHairBoxIsTicked()
    {
        var off = Compute(splitHair: false);
        var on = Compute(splitHair: true);

        Assert.False(off.PriorityRace.Visible);
        Assert.False(off.MidlanderFallback.Visible);
        Assert.True(on.PriorityRace.Visible);
        Assert.True(on.MidlanderFallback.Visible);
    }

    [Fact]
    public void TheCheckboxesThemselves_AreAlwaysVisible()
    {
        var s = Compute(strategy: SortStrategy.CreatorOnly, splitHair: false);

        Assert.True(s.SplitGear.Visible);
        Assert.True(s.SplitNpc.Visible);
        Assert.True(s.SplitHair.Visible);
        Assert.True(s.SplitGearBody.Visible);
        Assert.True(s.SplitBodyMods.Visible);
    }

    [Fact]
    public void GroupByCreator_GreysEverySplit_AndKeepsATickedHairBoxsDependentsVisibleButGreyed()
    {
        var s = Compute(strategy: SortStrategy.CreatorOnly, splitHair: true);

        Assert.False(s.SplitGear.Enabled);
        Assert.False(s.SplitNpc.Enabled);
        Assert.False(s.SplitHair.Enabled);
        Assert.False(s.SplitGearBody.Enabled);
        Assert.False(s.SplitBodyMods.Enabled);
        Assert.True(s.PriorityRace.Visible);
        Assert.False(s.PriorityRace.Enabled);
        Assert.True(s.MidlanderFallback.Visible);
        Assert.False(s.MidlanderFallback.Enabled);
    }

    [Theory]
    [InlineData(SortStrategy.TypeOnly)]
    [InlineData(SortStrategy.TypeThenCreator)]
    [InlineData(SortStrategy.CreatorThenType)]
    public void EveryTypeAwareStrategy_EnablesTheSplits(SortStrategy strategy)
    {
        var s = Compute(strategy: strategy, splitHair: true);

        Assert.True(s.SplitGear.Enabled);
        Assert.True(s.SplitNpc.Enabled);
        Assert.True(s.SplitHair.Enabled);
        Assert.True(s.PriorityRace.Enabled);
        Assert.True(s.MidlanderFallback.Enabled);
        Assert.True(s.SplitGearBody.Enabled);
        Assert.True(s.SplitBodyMods.Enabled);
    }

    [Fact]
    public void ClosedActivityGate_DisablesEverythingActionable_ButHidesNothingExtra()
    {
        var open = Compute(splitHair: true, scrapedFeature: true, gateOpen: true);
        var closed = Compute(splitHair: true, scrapedFeature: true, gateOpen: false);

        Assert.False(closed.GroupBy.Enabled);
        Assert.False(closed.SplitGear.Enabled);
        Assert.False(closed.PriorityRace.Enabled);
        Assert.False(closed.SplitBodyMods.Enabled);
        Assert.False(closed.FirstChoiceBody.Enabled);
        Assert.False(closed.FullPriorityList.Enabled);
        Assert.False(closed.ScrapedNpcCheckbox.Enabled);
        Assert.False(closed.WikiRefresh.Enabled);
        Assert.False(closed.SortButton.Enabled);

        Assert.Equal(open.PriorityRace.Visible, closed.PriorityRace.Visible);
        Assert.Equal(open.ScrapedNpcCheckbox.Visible, closed.ScrapedNpcCheckbox.Visible);
    }

    [Fact]
    public void TheWikiControls_AreHiddenWhileTheFeatureIsOff_AndShownWhenOn()
    {
        var off = Compute(scrapedFeature: false);
        var on = Compute(scrapedFeature: true);

        Assert.False(off.ScrapedNpcCheckbox.Visible);
        Assert.False(off.WikiRefresh.Visible);
        Assert.True(on.ScrapedNpcCheckbox.Visible);
        Assert.True(on.WikiRefresh.Visible);
    }

    [Fact]
    public void BodyPriorityControls_StayUsableUnderCreatorGrouping_WhenTheGateIsOpen()
    {
        // The priority is a saved preference, not a per-sort split, so it is editable even while the
        // splits are greyed; only a closed activity gate stops it.
        var s = Compute(strategy: SortStrategy.CreatorOnly);

        Assert.True(s.FirstChoiceBody.Visible);
        Assert.True(s.FirstChoiceBody.Enabled);
        Assert.True(s.FullPriorityList.Enabled);
        Assert.True(s.SortButton.Enabled);
    }
}
