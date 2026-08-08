using System.Text;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public enum NpcNameKind { Npc, Enemy, Boss }

public sealed record NpcNameMatch(string Name, NpcNameKind Kind);

[Flags]
internal enum NpcNameKinds { None = 0, Npc = 1, Enemy = 2, Boss = 4 }

internal sealed record NpcNameEntry(string Display, string[] Tokens, NpcNameKinds Kinds);

// Matches a mod's display name against known NPC/enemy/boss names using a first-token index.
//
// Deliberately NOT a regex. The previous implementation built one compiled alternation per
// category; at a full wiki scrape (20,115 distinct names) that is a 205KB pattern per category,
// costs seconds of JIT on first use and tens of megabytes. A dictionary keyed on first token turns
// "test 20,115 alternatives" into one lookup plus a median of one comparison: the real list has
// 9,886 distinct first tokens with a median bucket size of 1 and a p99 of 18.
//
// A large list also correlates with reports of the game closing during a scan. That correlation is
// not a proven cause and this is not presented as a fix for it - the mechanism is unknown. The work
// stands on classification quality and cost.
public sealed class NpcNameMatcher
{
    private readonly Dictionary<string, NpcNameEntry[]> _byFirstToken;

    public static readonly NpcNameMatcher Empty = new([], [], []);

    public NpcNameMatcher(IReadOnlyList<string> npcs, IReadOnlyList<string> enemies, IReadOnlyList<string> bosses)
    {
        // Merged rather than three parallel structures: 848 of 857 bosses also appear in Enemies
        // and 372 names appear in both NPCs and Enemies, so the same name would otherwise occupy
        // several slots in one bucket and precedence would fall out of sort order by accident.
        var merged = new Dictionary<string, NpcNameEntry>(StringComparer.OrdinalIgnoreCase);

        void Add(IReadOnlyList<string> names, NpcNameKinds kind)
        {
            foreach (var raw in names)
            {
                var tokens = Tokenize(Normalize(raw));
                if (tokens.Length == 0)
                    continue;

                var key = string.Join(' ', tokens);
                merged[key] = merged.TryGetValue(key, out var existing)
                    ? existing with { Kinds = existing.Kinds | kind }
                    : new NpcNameEntry(raw.Trim(), tokens, kind);
            }
        }

        Add(npcs, NpcNameKinds.Npc);
        Add(enemies, NpcNameKinds.Enemy);
        Add(bosses, NpcNameKinds.Boss);

        _byFirstToken = merged.Values
            .GroupBy(e => e.Tokens[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                // Ordering is defined, not implied: a match consumes tokens, so the longest match
                // is the one consuming most of them. Character length breaks ties, and an ordinal
                // comparison makes it a total order so results never depend on input file order.
                g => g.OrderByDescending(e => e.Tokens.Length)
                      .ThenByDescending(e => string.Join(' ', e.Tokens).Length)
                      .ThenBy(e => string.Join(' ', e.Tokens), StringComparer.Ordinal)
                      .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first known name found in <paramref name="modName"/>, or null.
    /// </summary>
    /// <remarks>
    /// <see cref="NpcNameMatch.Name"/> is the LIST's spelling, not the mod title's. The regex
    /// implementation this replaces returned the matched text as it appeared in the title. Nothing
    /// consumes it today - <c>ModTypeClassifier</c> uses only <see cref="NpcNameMatch.Kind"/> - but
    /// it is a real behaviour change and is recorded here rather than left to be discovered.
    /// <para>
    /// Case folding is <see cref="StringComparison.OrdinalIgnoreCase"/> throughout. The regex used
    /// <c>RegexOptions.IgnoreCase</c> without <c>CultureInvariant</c>, so it followed the current
    /// culture and diverged on Turkish dotted/dotless I.
    /// </para>
    /// </remarks>
    public NpcNameMatch? Match(string modName)
    {
        var tokens = Tokenize(Normalize(modName));
        if (tokens.Length == 0)
            return null;

        // CATEGORY ORDER IS THE OUTER LOOP. This is not a style choice.
        //
        // The regex version ran three separate regexes in category order, so any NPC match
        // anywhere in the title beat any Boss match anywhere. Scanning token positions outermost
        // instead would make the earliest-positioned name win regardless of category:
        // "Titan Slaying Y'shtola" would classify as Boss rather than NPC. With 679 bosses
        // against 133 NPCs in the shipped list, boss tokens usually appear first, so that would
        // silently reclassify a large number of mods into different folders.
        foreach (var kind in (ReadOnlySpan<NpcNameKinds>)[NpcNameKinds.Npc, NpcNameKinds.Boss, NpcNameKinds.Enemy])
        {
            for (var start = 0; start < tokens.Length; start++)
            {
                if (!_byFirstToken.TryGetValue(tokens[start], out var candidates))
                    continue;

                foreach (var candidate in candidates)
                {
                    if (candidate.Kinds.HasFlag(kind) && MatchesAt(tokens, start, candidate.Tokens))
                        return new NpcNameMatch(candidate.Display, ToKind(kind));
                }
            }
        }

        return null;
    }

    private static NpcNameKind ToKind(NpcNameKinds kind) => kind switch
    {
        NpcNameKinds.Npc => NpcNameKind.Npc,
        NpcNameKinds.Boss => NpcNameKind.Boss,
        _ => NpcNameKind.Enemy,
    };

    private static bool MatchesAt(string[] modTokens, int start, string[] nameTokens)
    {
        if (start + nameTokens.Length > modTokens.Length)
            return false;

        for (var i = 0; i < nameTokens.Length; i++)
        {
            if (!string.Equals(modTokens[start + i], nameTokens[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    // Maximal runs of letters or digits, iterated by Rune rather than char: char.IsLetterOrDigit
    // works on UTF-16 code units and mishandles anything outside the BMP, and mod titles here
    // routinely contain emoji. A boundary is "not a letter or digit", which is what the old
    // regex's (?<![\p{L}\p{N}]) meant, so underscore remains a separator.
    //
    // Comparing token SEQUENCES means the separator between tokens no longer matters: the old
    // regex matched the literal "Y'shtola" only, and this also matches "Y-shtola" and "Y shtola".
    // That is a deliberate loosening, pinned by Match_DeliberateChange_SeparatorsBetweenTokens...
    private static string[] Tokenize(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(rune.ToString());
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return [.. tokens];
    }

    // NFC normalization + curly-to-straight apostrophe folding, so a wiki title and a mod title
    // using different apostrophe glyphs for the same name still compare equal. Unchanged from the
    // regex implementation.
    internal static string Normalize(string value) =>
        value.Trim().Normalize(NormalizationForm.FormC).Replace('’', '\'');
}
