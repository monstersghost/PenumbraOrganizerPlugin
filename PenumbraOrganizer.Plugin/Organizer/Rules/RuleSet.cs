using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Rules;

public sealed record RuleEntry(string Text, bool Enabled);

public sealed class CompiledRule
{
    public required int Ordinal { get; init; }
    public required string Text { get; init; }
    public required bool Enabled { get; init; }
    public Rule? Rule { get; init; }
    public RuleParseError? Error { get; init; }

    public bool Runs => Enabled && Rule is not null;
}

public sealed record RuleMatch(CompiledRule Rule);

/// <summary>
/// An ordered, compiled rule list with value equality. Equality and <see cref="Digest"/> follow EVERY
/// entry (disabled and invalid ones too, so editing them greys a staged export); the race options count
/// only when a running rule reads race.
/// </summary>
public sealed class RuleSet : IEquatable<RuleSet>
{
    public const int MaxRules = 100;

    private static readonly Regex RaceToken = new(@"\{race\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static readonly RuleSet Empty = new([], RaceSplitOptions.Off);

    private RuleSet(IReadOnlyList<CompiledRule> rules, RaceSplitOptions race)
    {
        Rules = rules;
        Race = race;
        Digest = string.Concat(rules.Select(r => string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{(r.Enabled ? 1 : 0)}{r.Text.Length}:{r.Text}")));
        DigestHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Digest)));
        UsesRace = rules.Any(r => r.Runs && (ReadsRace(r.Rule!.Condition) || RaceToken.IsMatch(r.Rule.FolderTemplate)));
        Broken = rules.Where(r => r.Enabled && r.Error is not null).ToList();
    }

    public IReadOnlyList<CompiledRule> Rules { get; }
    public RaceSplitOptions Race { get; }
    public string Digest { get; }

    /// <summary>
    /// The SHA-256 (hex) of <see cref="Digest"/>: what <c>SortSelection.Fingerprint</c> carries, so the
    /// fingerprint stays short however many and however long the rules are. Computed once, here.
    /// </summary>
    public string DigestHash { get; }
    public bool UsesRace { get; }
    public IReadOnlyList<CompiledRule> Broken { get; }
    public bool HasRules => Rules.Count > 0;
    public bool AnyRuns => Rules.Any(r => r.Runs);

    public static RuleSet Compile(IReadOnlyList<RuleEntry> entries, RaceSplitOptions race)
    {
        var compiled = new List<CompiledRule>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (i >= MaxRules)
            {
                compiled.Add(new CompiledRule
                {
                    Ordinal = i + 1, Text = entry.Text, Enabled = entry.Enabled,
                    Error = new RuleParseError(0, $"Only the first {MaxRules} rules run."),
                });
                continue;
            }

            var parsed = RuleParser.Parse(entry.Text);
            compiled.Add(new CompiledRule
            {
                Ordinal = i + 1, Text = entry.Text, Enabled = entry.Enabled,
                Rule = parsed.Rule, Error = parsed.Error,
            });
        }

        return new RuleSet(compiled, race);
    }

    public RuleMatch? Match(RuleFieldValues values)
    {
        foreach (var rule in Rules)
        {
            if (rule.Runs && RuleEvaluator.Matches(rule.Rule!.Condition, values))
                return new RuleMatch(rule);
        }

        return null;
    }

    private static bool ReadsRace(RuleExpr expr) => expr switch
    {
        RuleCompare c => c.Field == RuleField.Race,
        RuleNot n => ReadsRace(n.Inner),
        RuleAnd a => ReadsRace(a.Left) || ReadsRace(a.Right),
        RuleOr o => ReadsRace(o.Left) || ReadsRace(o.Right),
        _ => false,
    };

    public bool Equals(RuleSet? other) =>
        other is not null && Digest == other.Digest && (!UsesRace || Race.Equals(other.Race));

    public override bool Equals(object? obj) => Equals(obj as RuleSet);

    public override int GetHashCode() => Digest.GetHashCode(StringComparison.Ordinal);
}
