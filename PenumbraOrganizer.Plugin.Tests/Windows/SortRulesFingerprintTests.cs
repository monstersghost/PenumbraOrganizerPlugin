using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Rules;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SortRulesFingerprintTests
{
    private static SortSelection Selection(RuleSet? rules, RaceSplitOptions? race = null) =>
        new(SortStrategy.TypeThenCreator, true, true, false, Layout: new LayoutOptions(Rules: rules, Race: race));

    private static RuleSet Set(params (string, bool)[] r) =>
        RuleSet.Compile(r.Select(x => new RuleEntry(x.Item1, x.Item2)).ToList(), RaceSplitOptions.Off);

    [Fact]
    public void Empty_or_null_rules_keep_todays_exact_fingerprint()
    {
        var today = Selection(null).Fingerprint();
        Assert.Equal(today, Selection(RuleSet.Empty).Fingerprint());
        Assert.DoesNotContain("rules", today);
    }

    [Fact]
    public void Any_edit_reorder_or_toggle_changes_the_fingerprint()
    {
        var baseline = Selection(Set(("name = 'a' -> X", true), ("name = 'b' -> Y", true))).Fingerprint();
        Assert.NotEqual(baseline, Selection(Set(("name = 'a' -> X", true), ("name = 'c' -> Y", true))).Fingerprint());
        Assert.NotEqual(baseline, Selection(Set(("name = 'b' -> Y", true), ("name = 'a' -> X", true))).Fingerprint());
        Assert.NotEqual(baseline, Selection(Set(("name = 'a' -> X", false), ("name = 'b' -> Y", true))).Fingerprint());
        Assert.NotEqual(Selection(Set(("zzz", false))).Fingerprint(), Selection(Set(("zzy", false))).Fingerprint());
    }

    [Fact]
    public void Race_options_count_only_when_a_rule_uses_race_even_with_the_master_off()
    {
        var off = RaceSplitOptions.Off;
        var changed = off with { MultiRaceToMultiRace = false };

        var usesRace = RuleSet.Compile([new RuleEntry("race = 'Viera' -> X", true)], off);
        var usesRace2 = RuleSet.Compile([new RuleEntry("race = 'Viera' -> X", true)], changed);
        Assert.NotEqual(Selection(usesRace).Fingerprint(), Selection(usesRace2).Fingerprint());

        var plain = RuleSet.Compile([new RuleEntry("name = 'a' -> X", true)], off);
        var plain2 = RuleSet.Compile([new RuleEntry("name = 'a' -> X", true)], changed);
        Assert.Equal(Selection(plain).Fingerprint(), Selection(plain2).Fingerprint());
    }

    [Fact]
    public void A_pipe_in_a_rule_cannot_make_two_rule_sets_collide()
    {
        Assert.NotEqual(
            Selection(Set(("name = 'a|b' -> X", true))).Fingerprint(),
            Selection(Set(("name = 'a' -> X", true), ("b' -> X", true))).Fingerprint());
    }
}
