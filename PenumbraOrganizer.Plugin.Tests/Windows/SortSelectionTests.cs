using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SortSelectionTests
{
    [Fact]
    public void SplitsApply_IsFalseOnlyForCreatorOnly()
    {
        Assert.False(new SortSelection(SortStrategy.CreatorOnly, false, false, false).SplitsApply);
        Assert.True(new SortSelection(SortStrategy.TypeOnly, false, false, false).SplitsApply);
        Assert.True(new SortSelection(SortStrategy.TypeThenCreator, false, false, false).SplitsApply);
        Assert.True(new SortSelection(SortStrategy.CreatorThenType, false, false, false).SplitsApply);
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
            new SortSelection(SortStrategy.TypeOnly, true, false, false),
            new SortSelection(SortStrategy.TypeOnly, true, false, false));
        Assert.NotEqual(
            new SortSelection(SortStrategy.TypeOnly, true, false, false),
            new SortSelection(SortStrategy.TypeOnly, true, true, false));
    }

    [Fact]
    public void SortSelection_ValueEquality_CoversSplitHair()
    {
        // Pins exactly one thing: that two selections differing only in SplitHair compare unequal,
        // i.e. that SplitHair is a positional member the generated equality covers.
        //
        // It does NOT pin the staleness behaviour it enables, and should not be counted as
        // coverage of it. The staleness line lives in SortPanel, which builds the selection and
        // assigns _lastSorted; a mutation there - a selection built with SplitHair hard-coded, or
        // from a stale _splitHair - passes this test unchanged. SortPanel.Draw is ImGui-bound with
        // private fields and no seam, so that path is on the in-game checklist instead.
        Assert.NotEqual(
            new SortSelection(SortStrategy.TypeOnly, false, false, false),
            new SortSelection(SortStrategy.TypeOnly, false, false, true));
    }

    [Fact]
    public void SplitsApply_IgnoresEveryFlag_AndReadsOnlyTheStrategy()
    {
        // The hair checkbox is disabled under Creator-only for the same reason as the other two:
        // that strategy never consults the category at all.
        Assert.False(new SortSelection(SortStrategy.CreatorOnly, true, true, true).SplitsApply);
        Assert.True(new SortSelection(SortStrategy.TypeOnly, false, false, true).SplitsApply);
    }

    // Deliberately NOT here: a "SplitHair defaults off" test on the record. Passing false in and
    // asserting false comes out only proves the record stores its constructor argument - it cannot
    // see SortPanel's _splitHair initialiser, which is the thing that would actually be wrong.
    // Those fields are private with no seam, so the fresh-panel default is on the in-game
    // checklist instead, where it is observable.

    [Fact]
    public void SortSelection_ValueEquality_CoversThePriorityFields()
    {
        var a = new SortSelection(SortStrategy.TypeOnly, false, true, true, "1801", false);
        var b = new SortSelection(SortStrategy.TypeOnly, false, true, true, "0201", false);
        var c = new SortSelection(SortStrategy.TypeOnly, false, true, true, "1801", true);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void SortSelection_HairPriority_IsNoneWhenTheRaceIsNull()
    {
        var selection = new SortSelection(SortStrategy.TypeOnly, false, true, true);
        Assert.Equal(new HairPriority(null, false), selection.HairPriority);
    }
}
