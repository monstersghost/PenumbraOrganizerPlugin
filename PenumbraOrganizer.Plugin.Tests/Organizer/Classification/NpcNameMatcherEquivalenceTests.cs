using System.Text.RegularExpressions;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

/// <summary>
/// Unit tests check the cases someone thought of. This checks the cases nobody thought of, by
/// running the old and new implementations over the same corpus.
/// </summary>
/// <remarks>
/// It cannot assert global equality, because the rewrite deliberately changed two things - so
/// there are two corpora with opposite rules: one where the implementations must agree, and one
/// where they must differ. A temporary guard, worth deleting once this change has shipped and
/// settled.
/// </remarks>
public class NpcNameMatcherEquivalenceTests
{
    private static readonly string[] Names =
        ["Y'shtola", "Alphinaud", "Zenos", "Alka Zolka", "Alka Zolka the Slayer", "Bert", "Art", "2B"];

    // The implementation this replaces, reproduced so both can be run over the same corpus.
    private static Regex OldRegex(IEnumerable<string> names)
    {
        var normalized = names
            .Select(n => n.Trim().Normalize().Replace('’', '\''))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
            .Select(Regex.Escape);
        return new Regex(
            $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", normalized)})(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase);
    }

    [Theory]
    // Conventional punctuation: old and new must agree exactly.
    [InlineData("Y'shtola Hair Redesign", true)]
    [InlineData("_Zenos_ Glam", true)]
    [InlineData("Albert Hair", false)]
    [InlineData("Concept Art Pack", true)]     // "Art" is a real boss name and matches as a token
    [InlineData("Kawaii Outfit Bundle", false)]
    [InlineData("Alka Zolka the Slayer Mod", true)]
    [InlineData("2B Outfit", true)]
    [InlineData("Alka Zolka Hair", true)]
    [InlineData("Zenos2 Retexture", false)]    // adjacent digit is not a boundary, in either
    [InlineData("Y’shtola Curly Apostrophe", true)]
    public void LegacyCorpus_OldAndNewAgree(string modName, bool expectedMatch)
    {
        var oldMatched = OldRegex(Names).IsMatch(modName.Trim().Normalize().Replace('’', '\''));
        var newMatched = new NpcNameMatcher(Names, [], []).Match(modName) is not null;

        Assert.Equal(expectedMatch, oldMatched);
        Assert.Equal(expectedMatch, newMatched);
    }

    [Theory]
    // The documented differences. Old must NOT match; new MUST. If either side flips, the change
    // is no longer the one that was designed.
    [InlineData("Y-shtola Hair")]
    [InlineData("Y shtola Hair")]
    public void IntentionalDifferenceCorpus_SeparatorsAreNowInterchangeable(string modName)
    {
        Assert.False(OldRegex(Names).IsMatch(modName));
        Assert.NotNull(new NpcNameMatcher(Names, [], []).Match(modName));
    }

    [Fact]
    public void IntentionalDifferenceCorpus_NonBmpLetterIsNowALetter()
    {
        // The opposite direction: old matches, new does not. U+1D400 is a letter to Rune and a
        // pair of non-letter surrogates to the regex.
        const string modName = "\U0001D400Zenos";

        Assert.True(OldRegex(Names).IsMatch(modName));
        Assert.Null(new NpcNameMatcher(Names, [], []).Match(modName));
    }
}
