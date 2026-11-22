using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Rules;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Rules;

public class RuleSetTests
{
    private static readonly RuleFieldValues Mod =
        new("Hrothgar Body", "Ann", "Old", "Body", "", "Hrothgar", "");

    private static RuleSet Compile(params (string, bool)[] rules) =>
        RuleSet.Compile(rules.Select(r => new RuleEntry(r.Item1, r.Item2)).ToList(), RaceSplitOptions.Off);

    [Fact]
    public void First_enabled_valid_match_wins_and_order_matters()
    {
        var set = Compile(("name contains 'body' -> A", true), ("name contains 'hroth' -> B", true));
        Assert.Equal(1, set.Match(Mod)!.Rule.Ordinal);

        var reversed = Compile(("name contains 'hroth' -> B", true), ("name contains 'body' -> A", true));
        Assert.Equal("name contains 'hroth' -> B", reversed.Match(Mod)!.Rule.Text);
    }

    [Fact]
    public void Disabled_and_broken_rules_are_skipped()
    {
        var set = Compile(("name contains 'body' -> A", false), ("nmae = 'x' -> B", true), ("type = 'body' -> C", true));
        var match = set.Match(Mod)!;
        Assert.Equal(3, match.Rule.Ordinal);
        Assert.Single(set.Broken);
        Assert.Equal(2, set.Broken[0].Ordinal);
    }

    [Fact]
    public void No_match_returns_null()
    {
        Assert.Null(Compile(("name = 'zzz' -> A", true)).Match(Mod));
        Assert.Null(RuleSet.Empty.Match(Mod));
    }

    [Fact]
    public void Rules_past_one_hundred_are_flagged_and_never_run()
    {
        var rules = Enumerable.Range(0, 101).Select(i => ($"name = 'n{i}' -> F", true)).ToArray();
        var set = Compile(rules);
        Assert.Equal(101, set.Rules.Count);
        Assert.Null(set.Rules[100].Rule);
        Assert.Equal("Only the first 100 rules run.", set.Rules[100].Error!.Message);
        Assert.Single(set.Broken);
    }

    [Fact]
    public void Equality_follows_every_entry_including_disabled_and_invalid_ones()
    {
        var a = Compile(("nmae = 'x' -> A", false));
        var b = Compile(("nmae = 'y' -> A", false));
        var c = Compile(("nmae = 'x' -> A", false));
        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
        Assert.Equal(a.GetHashCode(), c.GetHashCode());
        Assert.NotEqual(Compile(("name = 'a' -> X", true)), Compile(("name = 'a' -> X", false)));
    }

    [Fact]
    public void Digest_cannot_collide_through_a_separator_character()
    {
        var one = Compile(("name = 'a|b' -> X", true));
        var two = Compile(("name = 'a' -> X", true), ("b' -> X", true));
        Assert.NotEqual(one.Digest, two.Digest);
    }

    [Fact]
    public void UsesRace_reads_the_condition_and_the_folder()
    {
        Assert.True(Compile(("race = 'Viera' -> X", true)).UsesRace);
        Assert.True(Compile(("name = 'a' -> X/{race}", true)).UsesRace);
        Assert.False(Compile(("name = 'a' -> X/{type}", true)).UsesRace);
        Assert.False(Compile(("race = 'Viera' -> X", false)).UsesRace);
    }

    [Fact]
    public void Equality_ignores_race_options_unless_a_rule_uses_race()
    {
        var withRace = RaceSplitOptions.Off with { MultiRaceToMultiRace = false };
        var plain = RuleSet.Compile([new RuleEntry("name = 'a' -> X", true)], RaceSplitOptions.Off);
        var plain2 = RuleSet.Compile([new RuleEntry("name = 'a' -> X", true)], withRace);
        Assert.Equal(plain, plain2);

        var raced = RuleSet.Compile([new RuleEntry("race = 'a' -> X", true)], RaceSplitOptions.Off);
        var raced2 = RuleSet.Compile([new RuleEntry("race = 'a' -> X", true)], withRace);
        Assert.NotEqual(raced, raced2);
    }
}
