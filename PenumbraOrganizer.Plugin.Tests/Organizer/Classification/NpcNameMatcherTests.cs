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

    // ---- Index-matcher semantics. Everything above predates the rewrite and must keep passing. ----

    private static NpcNameMatcher Make(string[]? npcs = null, string[]? enemies = null, string[]? bosses = null) =>
        new(npcs ?? [], enemies ?? [], bosses ?? []);

    [Fact]
    public void Match_IsCaseInsensitive()
        // The apostrophe matters: the list entry is "Y'shtola", so the mod title must contain one
        // too. "YSHTOLA" would fail against BOTH implementations - the old regex matches an escaped
        // literal, and the new tokenizer splits on the apostrophe so the bucket key is "Y".
        => Assert.NotNull(Make(npcs: ["Y'shtola"]).Match("Y'SHTOLA hair"));

    [Fact]
    public void Match_RequiresWholeTokenBoundaries_NotSubstrings()
    {
        var m = Make(npcs: ["Bert"]);
        Assert.Null(m.Match("Albert Hair"));      // substring must not match
        Assert.NotNull(m.Match("Bert's Jacket")); // whole token must
    }

    [Fact]
    public void Match_TreatsUnderscoreAsABoundary()
    {
        // Deliberate: the old regex used (?<![\p{L}\p{N}]) rather than \b precisely so that
        // _Zenos_ matches. Underscore is not a letter or digit.
        Assert.NotNull(Make(npcs: ["Zenos"]).Match("_Zenos_ Glam"));
    }

    [Fact]
    public void Match_PrefersTheLongestMatch()
    {
        var m = Make(npcs: ["Alka Zolka", "Alka Zolka the Slayer"]);
        Assert.Equal("Alka Zolka the Slayer", m.Match("Cool Alka Zolka the Slayer Mod")!.Name);
    }

    [Fact]
    public void Match_PrefersMoreTokensOverFewer()
    {
        // Three tokens beats two at the same start position.
        var m = Make(npcs: ["Alka Zolka", "Alka Zolka Junior"]);
        Assert.Equal("Alka Zolka Junior", m.Match("Alka Zolka Junior Hair")!.Name);
    }

    [Fact]
    public void Match_CategoryOrderBeatsPosition()
    {
        // THE regression guard for this rewrite. The regex version ran three regexes in category
        // order, so an NPC anywhere beat a Boss anywhere. A position-first loop would answer Boss
        // here, and with 679 bosses against 133 NPCs in the shipped list that would silently
        // reclassify a lot of mods.
        var m = Make(npcs: ["Y'shtola"], bosses: ["Titan"]);
        Assert.Equal(NpcNameKind.Npc, m.Match("Titan Slaying Y'shtola")!.Kind);
    }

    [Fact]
    public void Match_FoldsCurlyApostrophes()
        => Assert.NotNull(Make(npcs: ["Y'shtola"]).Match("Y’shtola Redesign"));

    [Fact]
    public void Match_PrecedenceIsNpcThenBossThenEnemy()
    {
        var m = Make(npcs: ["Bahamut"], enemies: ["Bahamut"], bosses: ["Bahamut"]);
        Assert.Equal(NpcNameKind.Npc, m.Match("Bahamut Wings")!.Kind);

        var noNpc = Make(enemies: ["Bahamut"], bosses: ["Bahamut"]);
        Assert.Equal(NpcNameKind.Boss, noNpc.Match("Bahamut Wings")!.Kind);
    }

    [Fact]
    public void Match_ReturnsTheListSpelling_NotTheModTitleSpelling()
    {
        // A documented behaviour change. The regex returned the text as it appeared in the mod
        // title; the index returns the list's canonical spelling. Nothing consumes Name today
        // (ModTypeClassifier uses only Kind), but it is asserted so it is recorded rather than
        // discovered.
        Assert.Equal("Y'shtola", Make(npcs: ["Y'shtola"]).Match("y'SHTOLA hair")!.Name);
    }

    [Fact]
    public void Match_DeliberateChange_SeparatorsBetweenTokensAreInterchangeable()
    {
        // NEW behaviour. The old regex matched the literal "Y'shtola" only. Token matching
        // compares token sequences and ignores which separator sat between them. This is an
        // improvement for real mod titles and is asserted so it is a decision, not a surprise.
        var m = Make(npcs: ["Y'shtola"]);
        Assert.NotNull(m.Match("Y-shtola Hair"));
        Assert.NotNull(m.Match("Y shtola Hair"));
    }

    [Fact]
    public void Match_DeliberateChange_NonBmpLettersAreLetters()
    {
        // NEW behaviour, and it is a TIGHTENING, not a loosening. An earlier draft of this plan
        // asserted the opposite using an emoji, which proves nothing: an emoji is not a letter or
        // digit under either implementation, so both treat it as a separator and both match.
        //
        // The only inputs where char-vs-Rune actually diverge are non-BMP characters that ARE
        // letters or digits, such as U+1D400 (MATHEMATICAL BOLD CAPITAL A). The old regex tests
        // each UTF-16 surrogate, neither of which is \p{L}, so it sees a boundary and matches.
        // Rune sees a letter, so the character joins the token and "Zenos" is no longer a whole
        // token. New behaviour is correct; old behaviour was an accident of UTF-16.
        var m = Make(npcs: ["Zenos"]);
        Assert.Null(m.Match("\U0001D400Zenos"));
    }

    [Fact]
    public void Match_ReturnsNullWhenNothingMatches()
        => Assert.Null(Make(npcs: ["Zenos"]).Match("Kawaii Outfit Bundle"));

    [Fact]
    public void Match_EmptyMatcher_ReturnsNull()
        => Assert.Null(NpcNameMatcher.Empty.Match("anything at all"));
}
