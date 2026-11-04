namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;

public class BodyTypeMatcherTests
{
    private static BodyTypeEntry Entry(string label, params string[] aliases) =>
        new() { Label = label, Aliases = [.. aliases] };

    /// <summary>The bundled list, so these tests exercise the data that actually ships.</summary>
    private static BodyTypeMatcher Bundled()
    {
        var parse = BodyTypeListCodec.Parse(BodyTypeListStore.ReadEmbeddedStaticList());
        Assert.Equal(BodyTypeListParseStatus.Ok, parse.Status);
        return BodyTypeListStore.BuildMatcher(parse.Data!);
    }

    [Fact]
    public void Match_Empty_IsNull() => Assert.Null(BodyTypeMatcher.Empty.Match("Bibo+ Body"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("☺☺☺")]
    public void Match_NameWithNoTokens_IsNull(string name) => Assert.Null(Bundled().Match(name));

    // ── the '+' problem: the whole reason this is not NpcNameMatcher ──

    [Fact]
    public void Match_PlusIsPartOfTheToken_NotABoundary()
    {
        // NpcNameMatcher's tokenizer splits on every non-alphanumeric, which folds "Bibo+" into
        // "Bibo". If that ever happens here, "bibo+" could never be matched as distinct from
        // "bibo" and this assertion is the thing that notices.
        var matcher = new BodyTypeMatcher([Entry("Bibo+", "bibo+")]);

        Assert.Equal("Bibo+", matcher.Match("Bibo+ Medieval")?.Label);
        Assert.Equal("bibo+", matcher.Match("Bibo+ Medieval")?.Alias);

        // "Bibo" alone does NOT tokenize to "bibo+", so an entry aliased only on "bibo+" misses it.
        Assert.Null(matcher.Match("Bibo Medieval"));
    }

    [Theory]
    [InlineData("Bibo+ Top", "Bibo+")]
    [InlineData("Bibo Top", "Bibo+")]
    [InlineData("Rue+ Dress", "Rue+")]
    [InlineData("Rue Dress", "Rue+")]
    [InlineData("LaRue Bodysuit", "Rue+")]
    [InlineData("YAB+ Gloves", "YAB")]
    [InlineData("YAB Gloves", "YAB")]
    public void Match_BundledList_PlusAndPlainSpellingsBothResolve(string modName, string expected) =>
        Assert.Equal(expected, Bundled().Match(modName)?.Label);

    // ── longest alias wins, which is what keeps prefix bodies apart ──

    [Fact]
    public void Match_LongerAliasBeatsItsOwnPrefix()
    {
        // "tbse" is a prefix of "tbse-x", and "yab" of "yab buff". Answering the shorter one would
        // file a body under its neighbour.
        var matcher = Bundled();

        Assert.Equal("TBSE-X", matcher.Match("TBSE-X Legs")?.Label);
        Assert.Equal("TBSE", matcher.Match("TBSE Legs")?.Label);
        Assert.Equal("YAB Buff", matcher.Match("YAB Buff Chest")?.Label);
        Assert.Equal("YAB", matcher.Match("YAB Chest")?.Label);
    }

    [Fact]
    public void Match_LongestAliasWinsRegardlessOfPosition()
    {
        // Position must never outrank length. Here the shorter alias appears FIRST in the title.
        var matcher = new BodyTypeMatcher([Entry("Short", "alpha"), Entry("Long", "beta gamma")]);

        Assert.Equal("Long", matcher.Match("alpha beta gamma")?.Label);
    }

    [Fact]
    public void Match_TbseXSpellings_AllResolveToTbseX()
    {
        var matcher = Bundled();

        Assert.Equal("TBSE-X", matcher.Match("TBSE-X")?.Label);
        Assert.Equal("TBSE-X", matcher.Match("TBSEX body")?.Label);
        Assert.Equal("TBSE-X", matcher.Match("The Body SE-X")?.Label);
    }

    [Fact]
    public void Match_TheBodyResolvesToTbse()
    {
        // "The Body" is TBSE - Proteus spells it "The Body SE" in its own code.
        Assert.Equal("TBSE", Bundled().Match("The Body v2")?.Label);
        Assert.Equal("TBSE", Bundled().Match("The Body SE")?.Label);
    }

    // ── punctuation is a boundary, so one alias covers every spelling ──

    [Theory]
    [InlineData("Tight&Firm Body")]
    [InlineData("Tight & Firm Body")]
    [InlineData("Tight  &  Firm")]
    public void Match_AmpersandSpellingsCollapse(string modName) =>
        Assert.Equal("Tight&Firm", Bundled().Match(modName)?.Label);

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var matcher = Bundled();

        Assert.Equal("Neolithe", matcher.Match("NEOLITHE")?.Label);
        Assert.Equal("Neolithe", matcher.Match("neolithe")?.Label);
        Assert.Equal("Neolithe", matcher.Match("NeOlItHe")?.Label);
    }

    [Fact]
    public void Match_NonAsciiLabelFolds()
    {
        // NIRÆ is spelled with Æ in the wild. ToLowerInvariant must fold it.
        Assert.Equal("NIRAE", Bundled().Match("NIRÆ chest")?.Label);
        Assert.Equal("NIRAE", Bundled().Match("nirae chest")?.Label);
    }

    [Fact]
    public void Match_TurkishLocale_StillFolds()
    {
        // Invariant lower-casing, not culture-sensitive: NpcNameMatcher's own comment records the
        // regex implementation having had exactly this bug on dotted/dotless I.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal("Citrus+", Bundled().Match("CITRUS+ body")?.Label);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    // ── token boundaries: a body name inside a longer word is not a match ──

    [Theory]
    [InlineData("Evening Gown")]
    [InlineData("Truest Form")]
    [InlineData("Museum Piece")]
    [InlineData("Rueful Glance")]
    public void Match_BodyNameAsASubstringOfAWord_IsNull(string modName) =>
        Assert.Null(Bundled().Match(modName));

    [Fact]
    public void Match_MultiTokenAliasNeedsAdjacentTokens()
    {
        var matcher = new BodyTypeMatcher([Entry("AB Body", "ab body")]);

        Assert.Equal("AB Body", matcher.Match("AB Body v2")?.Label);
        Assert.Null(matcher.Match("AB fancy Body"));
    }

    [Fact]
    public void Match_EmojiInTitle_DoesNotBreakTokenizing()
    {
        // Titles here routinely contain emoji, which is why the tokenizer walks Runes rather than
        // chars - a surrogate pair split mid-way would corrupt the token either side of it.
        Assert.Equal("Bibo+", Bundled().Match("✨ Bibo+ ✨ Dress 💜")?.Label);
    }

    // ── determinism ──

    [Fact]
    public void Match_DuplicateAliasAcrossEntries_IsDeterministic()
    {
        // An authoring mistake the codec deliberately does not silently repair. Whatever it
        // resolves to, it must not depend on declaration order.
        var forward = new BodyTypeMatcher([Entry("Alpha", "shared"), Entry("Beta", "shared")]);
        var reversed = new BodyTypeMatcher([Entry("Beta", "shared"), Entry("Alpha", "shared")]);

        Assert.Equal(forward.Match("shared body")?.Label, reversed.Match("shared body")?.Label);
    }

    [Fact]
    public void Match_UnknownBody_IsNull()
    {
        // Never a guess: an unrecognised body leaves the row flat under Body.
        Assert.Null(Bundled().Match("Totally Made Up Body 9000"));
    }
}
