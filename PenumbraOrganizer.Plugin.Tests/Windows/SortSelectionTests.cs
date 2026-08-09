using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SortSelectionTests
{
    [Fact]
    public void SplitsApply_IsFalseOnlyForCreatorOnly()
    {
        Assert.False(new SortSelection(SortStrategy.CreatorOnly, false, false).SplitsApply);
        Assert.True(new SortSelection(SortStrategy.TypeOnly, false, false).SplitsApply);
        Assert.True(new SortSelection(SortStrategy.TypeThenCreator, false, false).SplitsApply);
        Assert.True(new SortSelection(SortStrategy.CreatorThenType, false, false).SplitsApply);
    }

    [Fact]
    public void Groupings_CoverEveryStrategyExactlyOnce()
    {
        // An earlier draft compared only the COUNT of a labels array against the enum's length,
        // which passes with the labels in the wrong order. A single tuple array makes the
        // relationship structural, and this asserts coverage rather than arithmetic.
        Assert.Equal(
            Enum.GetValues<SortStrategy>().Order(),
            SortPanel.Groupings.Select(g => g.Strategy).Order());
    }

    [Fact]
    public void Groupings_DefaultIndexSelectsTypeThenCreator()
    {
        // The default must survive someone reordering the array. DefaultGroupingIndex is what the
        // panel's backing field initialises to, so this pins the actual default rather than a
        // literal that happens to agree with it today.
        Assert.Equal(SortStrategy.TypeThenCreator, SortPanel.Groupings[SortPanel.DefaultGroupingIndex].Strategy);
    }

    [Fact]
    public void Groupings_LabelsAreNonEmptyAndDistinct()
    {
        // ImGui identifies combo entries by label; two identical labels would be indistinguishable
        // to the user and a blank one would render as an empty row.
        Assert.All(SortPanel.Groupings, g => Assert.False(string.IsNullOrWhiteSpace(g.Label)));
        Assert.Equal(
            SortPanel.Groupings.Length,
            SortPanel.Groupings.Select(g => g.Label).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SortSelection_HasValueEquality()
    {
        // The staleness line compares the current selection against the one last sorted with. A
        // class here would compare by reference and the line would show after every sort.
        Assert.Equal(
            new SortSelection(SortStrategy.TypeOnly, true, false),
            new SortSelection(SortStrategy.TypeOnly, true, false));
        Assert.NotEqual(
            new SortSelection(SortStrategy.TypeOnly, true, false),
            new SortSelection(SortStrategy.TypeOnly, true, true));
    }
}
