using System.Text;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>Which body a mod's name says it is, and the alias that said so.</summary>
public sealed record BodyTypeMatch(string Label, string Alias);

/// <summary>
/// Matches a mod's display name against the known body replacers.
/// </summary>
/// <remarks>
/// NEVER call this on a mod's name alone. A name match is only trustworthy for a mod already known
/// STRUCTURALLY to be a body replacer (<see cref="ModBodyFileReader"/>), because the overwhelming
/// majority of mods carrying a body's name are garments MADE FOR that body rather than the body
/// itself - "Bibo+ Medieval Dress" is a dress. Proteus reaches the same conclusion from the other
/// direction: it finds bodies by their files and uses names only as a tie-break among mods already
/// holding a body's model files, with the note that a loose word must not be allowed to "pull in an
/// outfit". <c>ScanProcessor</c> is where that gate lives.
/// <para>
/// A plain ordered scan, NOT the first-token index <see cref="NpcNameMatcher"/> builds. That index
/// earns its complexity against ~20,000 names; this list is 26 bodies and 53 aliases, where an
/// index would be machinery with nothing to do.
/// </para>
/// </remarks>
public sealed class BodyTypeMatcher
{
    /// <param name="Tokens">The alias, tokenized once at construction.</param>
    private sealed record Alias(string Label, string Text, string[] Tokens);

    private readonly Alias[] _aliases;

    public static readonly BodyTypeMatcher Empty = new([]);

    public BodyTypeMatcher(IReadOnlyList<BodyTypeEntry> bodies)
    {
        _aliases = bodies
            .SelectMany(b => b.Aliases.Select(a => new Alias(b.Label, a, Tokenize(Normalize(a)))))
            .Where(a => a.Tokens.Length > 0)
            // LONGEST ALIAS FIRST, AND THIS IS NOT A STYLE CHOICE. Several bodies' names are
            // prefixes of another's: "tbse" is a prefix of "tbse-x", and "yab" of "yab buff". A
            // scan in list order would answer TBSE for a TBSE-X mod and YAB for a YAB Buff mod,
            // silently filing a body under its neighbour. Token count first, then character
            // length, then ordinal on the alias and label so the order is TOTAL - two aliases of
            // equal shape must not resolve by input file order, which is what makes a duplicate
            // alias across two entries deterministic rather than arbitrary.
            .OrderByDescending(a => a.Tokens.Length)
            .ThenByDescending(a => a.Text.Length)
            .ThenBy(a => a.Text, StringComparer.Ordinal)
            .ThenBy(a => a.Label, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The body <paramref name="modName"/> names, or null. The longest alias wins wherever it sits
    /// in the title; position never outranks length.
    /// </summary>
    public BodyTypeMatch? Match(string modName)
    {
        var tokens = Tokenize(Normalize(modName));
        if (tokens.Length == 0)
            return null;

        foreach (var alias in _aliases)
        {
            for (var start = 0; start + alias.Tokens.Length <= tokens.Length; start++)
            {
                if (MatchesAt(tokens, start, alias.Tokens))
                    return new BodyTypeMatch(alias.Label, alias.Text);
            }
        }

        return null;
    }

    private static bool MatchesAt(string[] modTokens, int start, string[] aliasTokens)
    {
        for (var i = 0; i < aliasTokens.Length; i++)
        {
            if (!string.Equals(modTokens[start + i], aliasTokens[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    // Invariant lower-casing, so a Turkish locale cannot make "BIBO" stop matching "bibo" - the bug
    // NpcNameMatcher's own comment records the regex implementation having had.
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    /// <summary>
    /// Maximal runs of letters, digits and <c>'+'</c>, iterated by <see cref="Rune"/>.
    /// </summary>
    /// <remarks>
    /// <c>'+'</c> IS A TOKEN CHARACTER, and that is the whole reason this does not just call
    /// <see cref="NpcNameMatcher"/>'s tokenizer. That one treats every non-alphanumeric as a
    /// boundary, which folds "Bibo+" into "Bibo" and "Rue+" into "Rue" - and <c>ModNameNormalizer</c>
    /// already records that '+' is load-bearing in this ecosystem. Keeping it attached is what lets
    /// "YAB+" and "YAB" be told apart at all.
    /// <para>
    /// Everything else is a boundary, '&amp;' and '-' included, which is deliberate: it collapses
    /// "Tight&amp;Firm" and "Tight &amp; Firm" onto the same tokens, and "TBSE-X" onto ["tbse", "x"],
    /// so one alias covers every way an author punctuates the same name.
    /// </para>
    /// <para>
    /// By Rune rather than char because <see cref="char.IsLetterOrDigit(char)"/> works on UTF-16
    /// code units and mishandles anything outside the BMP, and mod titles here routinely contain
    /// emoji - the same hazard NpcNameMatcher's tokenizer calls out.
    /// </para>
    /// </remarks>
    private static string[] Tokenize(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) || rune.Value == '+')
            {
                current.Append(rune.ToString());
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return [.. tokens];
    }
}
