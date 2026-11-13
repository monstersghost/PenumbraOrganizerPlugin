using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class SortSummaryTextTests
{
    [Fact]
    public void RootOnly_NamesCountsAndProtected()
    {
        var text = SortSummaryText.Describe(new SortSummary(new SortScope(true), 3, 10, 2));

        Assert.Equal("Will sort 3 of 10 mods: loose in root. 2 protected root mods stay put.", text);
    }

    [Fact]
    public void RootOnly_ZeroProtected_OmitsSecondSentence()
    {
        var text = SortSummaryText.Describe(new SortSummary(new SortScope(true), 3, 10, 0));

        Assert.Equal("Will sort 3 of 10 mods: loose in root.", text);
    }

    [Fact]
    public void RootOnly_OneProtected_IsSingular()
    {
        var text = SortSummaryText.Describe(new SortSummary(new SortScope(true), 3, 10, 1));

        Assert.Contains("1 protected root mod stays put.", text);
    }

    [Fact]
    public void WholeLibrary_Wording()
    {
        var text = SortSummaryText.Describe(new SortSummary(SortScope.WholeLibrary, 8, 10, 0));

        Assert.Equal("Will sort 8 of 10 mods: whole library.", text);
    }

    [Fact]
    public void Text_IsAscii()
    {
        var text = SortSummaryText.Describe(new SortSummary(new SortScope(true), 3, 10, 2));

        Assert.All(text, c => Assert.True(c < 128));
    }
}
