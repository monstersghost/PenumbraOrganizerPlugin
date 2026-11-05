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
    [InlineData("YAB+ Gloves", "YAB")]
    [InlineData("YAB Gloves", "YAB")]
    public void Match_BundledList_PlusAndPlainSpellingsBothResolve(string modName, string expected) =>
        Assert.Equal(expected, Bundled().Match(modName)?.Label);

    // ── longest alias wins, which is what keeps prefix bodies apart ──

    [Fact]
    public void Match_EveryYabSpelling_ResolvesToYab()
    {
        // YAB Buff is a YAB variant, not a body of its own, so every flavour lands on one label -
        // the same call made for TBSE-X.
        var matcher = Bundled();

        Assert.Equal("YAB", matcher.Match("YAB Chest")?.Label);
        Assert.Equal("YAB", matcher.Match("YAB+ Chest")?.Label);
        Assert.Equal("YAB", matcher.Match("YAB Buff Chest")?.Label);
        Assert.Equal("YAB", matcher.Match("YAB+ Buff Chest")?.Label);
        Assert.Equal("YAB", matcher.Match("Yet Another Body Chest")?.Label);
    }

    [Fact]
    public void Match_LongestAliasWinsRegardlessOfPosition()
    {
        // Position must never outrank length. Here the shorter alias appears FIRST in the title.
        var matcher = new BodyTypeMatcher([Entry("Short", "alpha"), Entry("Long", "beta gamma")]);

        Assert.Equal("Long", matcher.Match("alpha beta gamma")?.Label);
    }

    [Fact]
    public void Match_EveryTbseSpelling_ResolvesToTbse()
    {
        // TBSE-X is a TBSE variant, not a body of its own, so every flavour lands on one label.
        var matcher = Bundled();

        Assert.Equal("TBSE", matcher.Match("TBSE Legs")?.Label);
        Assert.Equal("TBSE", matcher.Match("TBSE-X Legs")?.Label);
        Assert.Equal("TBSE", matcher.Match("TBSEX body")?.Label);
        Assert.Equal("TBSE", matcher.Match("The Body SE-X")?.Label);
    }

    [Fact]
    public void Match_HrBodyIsNotTbse()
    {
        // The other half of the male decision: HRBody stays separate.
        Assert.Equal("The hrBody", Bundled().Match("HRBody Shirt")?.Label);
        Assert.Equal("The hrBody", Bundled().Match("HR Body Shirt")?.Label);
    }

    [Theory]
    [InlineData("RE[DE]FINED Dress")]
    [InlineData("REDEFINED Dress")]
    [InlineData("Re-Defined Dress")]
    [InlineData("RE:DEFINED Dress")]
    [InlineData("ReLala Dress")]
    public void Match_BracketedBodyName_ResolvesHoweverItIsPunctuated(string modName) =>
        // Brackets are boundaries, so "RE[DE]FINED" tokenizes to three words while "REDEFINED" is
        // one. Both spellings are real, so both are aliased; this fails if either is dropped.
        Assert.Equal("Re[de]fined Lalafell", Bundled().Match(modName)?.Label);

    [Fact]
    public void Match_LaRue_IsLavaBod()
    {
        // LaRue is LavaBod in a Rue shape. The name says Rue; the body is LavaBod.
        Assert.Equal("LavaBod+", Bundled().Match("LaRue Bodysuit")?.Label);
    }

    [Fact]
    public void Match_Ruexb_IsNotRue()
    {
        // RUEXB+ is its own body and must not collapse into Rue+ despite the shared first letters:
        // "ruexb" and "rue" are different tokens, which is what keeps them apart.
        Assert.Equal("RUEXB+", Bundled().Match("RUEXB+ Top")?.Label);
        Assert.Equal("RUEXB+", Bundled().Match("RUEXB Top")?.Label);
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
    [InlineData("Evening Gown")]     // 'eve' inside 'evening'
    [InlineData("Truest Form")]      // 'tre' inside 'truest'
    [InlineData("Museum Piece")]     // 'muse' inside 'museum'
    [InlineData("Rueful Glance")]    // 'rue' inside 'rueful'
    public void Match_BodyNameInsideALongerWord_IsNull(string modName) =>
        // Token boundaries still hold even with bare aliases kept. A body name must be a WHOLE
        // word in the title; this is what separates the accepted false positive above ("Rue de la
        // Paix", where 'rue' is a word) from an unbounded substring search, which would drag in a
        // large slice of any library.
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
        // Never a guess: a mod naming no known body gets no body folder at all.
        Assert.Null(Bundled().Match("Totally Made Up Body 9000"));
    }

    [Fact]
    public void Match_AGarmentNamedAfterABody_Matches()
    {
        // The point of the feature, and the inversion of what an earlier design did: a dress made
        // for Bibo+ IS a Bibo+ match. Whether that becomes Gear/Bibo+/Top or Body/Bibo+ is the
        // caller's business (ScanProcessor gates on category); the matcher only reads the name.
        Assert.Equal("Bibo+", Bundled().Match("Bibo+ Medieval Dress")?.Label);
        Assert.Equal("TBSE", Bundled().Match("TBSE Leather Jacket")?.Label);
    }

    [Fact]
    public void Match_LooseAliasOnAnUnrelatedTitle_MatchesAndThatIsAccepted()
    {
        // Documents a KNOWN and deliberate false positive rather than pretending it cannot happen.
        // Bare aliases are kept for detection: a wrongly filed mod is one visible row in the review
        // step, whereas an undetected body is silently missing from the folder the user looked in.
        // If this ever needs tightening, the bare aliases are what to drop.
        Assert.Equal("Rue+", Bundled().Match("Rue de la Paix")?.Label);
    }

    [Fact]
    public void MatchAll_ReturnsEveryDistinctBodyNamed()
    {
        var matcher = new BodyTypeMatcher([Entry("Bibo+", "bibo+", "bibo"), Entry("YAB", "yab", "yab+")]);

        var labels = matcher.MatchAll("Heartbeat for Bibo & YAB+");

        Assert.Equal(["Bibo+", "YAB"], labels.OrderBy(l => l, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void MatchAll_SameBodyTwice_ReportsItOnce()
    {
        var matcher = new BodyTypeMatcher([Entry("Bibo+", "bibo+", "bibo")]);

        Assert.Equal(["Bibo+"], matcher.MatchAll("Bibo+ and bibo outfit"));
    }

    [Fact]
    public void MatchAll_ShorterAliasInsideALongerOne_IsNotReportedAsASecondBody()
    {
        // "AB Eve" must not also read as the body whose alias is just "eve".
        var matcher = new BodyTypeMatcher([Entry("AB Eve", "ab eve"), Entry("Eve", "eve")]);

        Assert.Equal(["AB Eve"], matcher.MatchAll("AB Eve Top"));
        Assert.Equal(["Eve"], matcher.MatchAll("Eve Top"));
    }

    [Fact]
    public void MatchAll_RepeatedLongAlias_ClaimsEveryOccurrence()
    {
        // The second "AB Eve" must not leak a standalone "Eve" once the first one claimed its span.
        var matcher = new BodyTypeMatcher([Entry("AB Eve", "ab eve"), Entry("Eve", "eve")]);

        Assert.Equal(["AB Eve"], matcher.MatchAll("AB Eve top / AB Eve bottom"));
        Assert.Equal(["AB Eve", "Eve"], matcher.MatchAll("AB Eve top / Eve bottom").OrderBy(l => l, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void MatchAll_SeveralAliasesOfOneBody_ClaimTheirOwnSpansAndReportItOnce()
    {
        // "bibo" and "bibo+" are both Bibo+; the longer claims its span, the shorter then matches
        // only an UNclaimed word, and the body is still reported once.
        var matcher = new BodyTypeMatcher([Entry("Bibo+", "bibo+", "bibo"), Entry("Bibo Plus Extra", "bibo plus")]);

        Assert.Equal(["Bibo Plus Extra", "Bibo+"], matcher.MatchAll("Bibo plus dress for Bibo+ and bibo")
            .OrderByDescending(l => l.Length).ToArray());
        Assert.Equal(["Bibo+"], matcher.MatchAll("bibo+ bibo bibo+"));
    }

    [Fact]
    public void MatchAll_NoTokensOrNoMatch_IsEmpty()
    {
        var matcher = new BodyTypeMatcher([Entry("Bibo+", "bibo+")]);

        Assert.Empty(matcher.MatchAll(""));
        Assert.Empty(matcher.MatchAll("Plain Robe"));
        Assert.Empty(BodyTypeMatcher.Empty.MatchAll("Bibo+"));
    }
}
