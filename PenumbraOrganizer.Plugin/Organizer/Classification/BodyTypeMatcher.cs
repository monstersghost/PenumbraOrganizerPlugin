using System.Text;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>Which body a mod's name says it is, and the alias that said so.</summary>
public sealed record BodyTypeMatch(string Label, string Alias);

/// <summary>
/// Matches a mod's display name against the known body replacers.
/// </summary>
/// <remarks>
/// Answers "which body is this mod FOR", which for a gear mod is the only question there is: a
/// garment made for Bibo+ replaces its own equipment set, not the body's models, so no structural
/// signal exists to read. Proteus resolves it by mesh and UV correspondence against the body's own
/// meshes - accurate, and far more work than a sort should do. The name is what is left, and mod
/// authors do reliably put the body in the title.
/// <para>
/// So a match is a STATED intent, not a verified fact, and the cost of that is accepted
/// deliberately: a loose alias can match a title that merely contains the word, and "Rue de la
/// Paix" will be read as a Rue+ garment. Detection is preferred over precision here because the
/// Sort tab's review step shows every proposed move before anything happens, and a wrongly filed
/// mod is one visible row to correct - whereas a body that is never detected is silently absent
/// from the folder the user went looking in.
/// </para>
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
            // LONGEST ALIAS FIRST, AND THIS IS NOT A STYLE CHOICE. Body names are routinely
            // prefixes of one another - "yab" of "yab buff", "tbse" of "tbse-x", "eve" of
            // "ab eve", "rue" of "ruexb" - so a scan in list order would answer the shorter one
            // and file a body under its neighbour.
            //
            // No SHIPPED pair currently differs in label on that basis: the variants that used to
            // (YAB Buff, TBSE-X) are now aliases of their parents, so today the longest match and
            // the shortest agree on the answer. That makes this ordering load-bearing for the
            // NEXT variant added as its own body rather than for the current list, which is
            // exactly when it would be easy to drop and hard to notice -
            // Match_LongestAliasWinsRegardlessOfPosition pins it on synthetic entries so the
            // guarantee does not depend on the data happening to exercise it.
            //
            // Token count first, then character length, then ordinal on the alias and label so
            // the order is TOTAL - two aliases of equal shape must not resolve by input file
            // order, which is what makes a duplicate alias across two entries deterministic
            // rather than arbitrary.
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

    /// <summary>
    /// Every distinct body <paramref name="text"/> names. Same alias list, boundary rule and
    /// longest-alias-first order as <see cref="Match"/>; the difference is that it keeps going.
    /// </summary>
    /// <remarks>
    /// Every accepted occurrence claims its tokens, and an occurrence that overlaps a span a longer
    /// alias already claimed is skipped, so a body whose alias is a word of another body's alias
    /// ("eve" in "ab eve") is not reported as a second body - including from a REPEAT of the long
    /// alias, which is why the scan does not stop at an alias's first match. Only the returned
    /// labels are deduplicated.
    /// </remarks>
    public IReadOnlyList<string> MatchAll(string text)
    {
        var tokens = Tokenize(Normalize(text));
        if (tokens.Length == 0)
            return [];

        var claimed = new bool[tokens.Length];
        var labels = new List<string>();

        foreach (var alias in _aliases)
        {
            for (var start = 0; start + alias.Tokens.Length <= tokens.Length; start++)
            {
                if (!MatchesAt(tokens, start, alias.Tokens))
                    continue;

                var overlaps = false;
                for (var i = start; i < start + alias.Tokens.Length; i++)
                    overlaps |= claimed[i];
                if (overlaps)
                    continue;

                for (var i = start; i < start + alias.Tokens.Length; i++)
                    claimed[i] = true;
                if (!labels.Contains(alias.Label))
                    labels.Add(alias.Label);
            }
        }

        return labels;
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
