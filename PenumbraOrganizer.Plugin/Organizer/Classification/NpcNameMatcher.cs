using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public enum NpcNameKind { Npc, Enemy, Boss }

public sealed record NpcNameMatch(string Name, NpcNameKind Kind);

// Matches a mod's display name against known NPC/enemy/boss names. One combined alternation
// regex per category (never one compiled Regex per name — see the design spec's performance
// section: a full wiki scrape can reach five figures of names, and constructing that many
// separate Regex objects is real overhead independent of match cost). Boundaries are defined
// explicitly as "not adjacent to a Unicode letter or digit" rather than \b, which treats
// underscore as a word character and would misclassify "_Zenos_".
public sealed class NpcNameMatcher
{
    private readonly Regex? _npcRegex;
    private readonly Regex? _enemyRegex;
    private readonly Regex? _bossRegex;

    public static readonly NpcNameMatcher Empty = new([], [], []);

    public NpcNameMatcher(IReadOnlyList<string> npcs, IReadOnlyList<string> enemies, IReadOnlyList<string> bosses)
    {
        _npcRegex = BuildRegex(npcs);
        _enemyRegex = BuildRegex(enemies);
        _bossRegex = BuildRegex(bosses);
    }

    public NpcNameMatch? Match(string modName)
    {
        var normalized = Normalize(modName);

        if (_npcRegex?.Match(normalized) is { Success: true } npcMatch)
            return new NpcNameMatch(npcMatch.Value, NpcNameKind.Npc);
        if (_bossRegex?.Match(normalized) is { Success: true } bossMatch)
            return new NpcNameMatch(bossMatch.Value, NpcNameKind.Boss);
        if (_enemyRegex?.Match(normalized) is { Success: true } enemyMatch)
            return new NpcNameMatch(enemyMatch.Value, NpcNameKind.Enemy);

        return null;
    }

    private static Regex? BuildRegex(IReadOnlyList<string> names)
    {
        var normalized = names
            .Select(Normalize)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length) // prefer a longer name over a shorter one it contains
            .Select(Regex.Escape)
            .ToList();

        if (normalized.Count == 0)
            return null;

        var pattern = $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", normalized)})(?![\p{{L}}\p{{N}}])";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    // NFC normalization + curly-to-straight apostrophe folding, so a wiki title and a mod title
    // using different apostrophe glyphs for the same name still compare equal. Character
    // normalization, not fuzzy/approximate matching.
    internal static string Normalize(string value) =>
        value.Trim().Normalize().Replace('’', '\'');
}
