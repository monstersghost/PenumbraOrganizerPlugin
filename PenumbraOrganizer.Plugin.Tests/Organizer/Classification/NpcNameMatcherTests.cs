using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class NpcNameMatcherTests
{
    [Fact]
    public void Match_WholeWordCaseInsensitive_Matches()
    {
        var matcher = new NpcNameMatcher(["Y'shtola"], [], []);

        var result = matcher.Match("Rhul of Cool: A y'SHTOLA Overhaul");

        Assert.NotNull(result);
        Assert.Equal(NpcNameKind.Npc, result!.Kind);
    }

    [Fact]
    public void Match_ShortNameInsideLongerWord_DoesNotMatch()
    {
        var matcher = new NpcNameMatcher([], ["Rat"], []);

        var result = matcher.Match("Pirate Outfit");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("Zenos2")]
    [InlineData("NotZenos")]
    public void Match_UnicodeBoundary_RejectsAdjacentLetterOrDigit(string modName)
    {
        var matcher = new NpcNameMatcher([], [], ["Zenos"]);

        Assert.Null(matcher.Match(modName));
    }

    [Theory]
    [InlineData("_Zenos_")]
    [InlineData("Zenos-themed")]
    public void Match_HyphenOrUnderscoreAdjacent_StillMatches(string modName)
    {
        var matcher = new NpcNameMatcher([], [], ["Zenos"]);

        Assert.NotNull(matcher.Match(modName));
    }

    [Fact]
    public void Match_MultiWordName_Matches()
    {
        var matcher = new NpcNameMatcher(["Feo Ul"], [], []);

        var result = matcher.Match("A Feo Ul Overhaul");

        Assert.NotNull(result);
    }

    [Fact]
    public void Match_HyphenatedName_Matches()
    {
        var matcher = new NpcNameMatcher(["Kan-E-Senna"], [], []);

        var result = matcher.Match("HD Kan-E-Senna (Gen3)");

        Assert.NotNull(result);
    }

    [Fact]
    public void Match_CurlyApostrophe_MatchesStraightApostropheListEntry()
    {
        var matcher = new NpcNameMatcher(["Y'shtola"], [], []);

        var result = matcher.Match("Y’shtola Rework");

        Assert.NotNull(result);
    }

    [Fact]
    public void Match_RegexMetacharactersInName_DoNotBreakMatching()
    {
        var matcher = new NpcNameMatcher(["Al'Ma(rri)yya"], [], []);

        var result = matcher.Match("Al'Ma(rri)yya Retexture");

        Assert.NotNull(result);
    }

    [Fact]
    public void Match_LongerOverlappingNamePreferred_ReturnsLongerMatch()
    {
        var matcher = new NpcNameMatcher([], [], ["Zenos", "Zenos yae Galvus"]);

        var result = matcher.Match("Zenos yae Galvus Portrait");

        Assert.NotNull(result);
        Assert.Equal("Zenos yae Galvus", result!.Name);
    }

    [Fact]
    public void Match_PriorityNpcsBeatsBossesBeatsEnemies()
    {
        var matcher = new NpcNameMatcher(["Titania"], [], ["Titania"]);

        var result = matcher.Match("HD Titania (Gen3)");

        Assert.Equal(NpcNameKind.Npc, result!.Kind);
    }

    [Fact]
    public void Match_BossesBeatsEnemiesWhenNoNpcMatch()
    {
        var matcher = new NpcNameMatcher([], ["Garuda"], ["Garuda"]);

        var result = matcher.Match("Garuda Statue");

        Assert.Equal(NpcNameKind.Boss, result!.Kind);
    }

    [Fact]
    public void Match_NoListsMatch_ReturnsNull()
    {
        var matcher = new NpcNameMatcher(["Y'shtola"], [], []);

        Assert.Null(matcher.Match("Ordinary Gear Mod"));
    }

    [Fact]
    public void Empty_NeverMatchesAnything()
    {
        Assert.Null(NpcNameMatcher.Empty.Match("Y'shtola Overhaul"));
    }
}
