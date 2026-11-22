using PenumbraOrganizer.Plugin;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Rules;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Rules;

public class RulesConfigTests
{
    private static Configuration WithRule(string text = "name = 'a' -> X")
    {
        var config = new Configuration();
        config.SortRules.Add(new SortRuleEntry { Text = text });
        config.SortRulesVersion++;
        return config;
    }

    [Fact]
    public void Sort_rules_default_empty_and_survive_a_serializer_round_trip()
    {
        var config = new Configuration();
        Assert.Empty(config.SortRules);

        config.SortRules.Add(new SortRuleEntry { Text = "name = 'a' -> X", Enabled = false });
        config.SortRulesVersion = 5;
        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var back = System.Text.Json.JsonSerializer.Deserialize<Configuration>(json)!;
        Assert.Equal("name = 'a' -> X", back.SortRules.Single().Text);
        Assert.False(back.SortRules.Single().Enabled);
        Assert.DoesNotContain("SortRulesVersion", json);
        Assert.Equal(0, back.SortRulesVersion);
    }

    [Fact]
    public void Sort_rules_version_is_not_written_by_newtonsoft()
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new Configuration { SortRulesVersion = 3 });
        Assert.DoesNotContain("SortRulesVersion", json);
    }

    [Fact]
    public void A_legacy_config_without_the_property_loads_an_empty_list()
    {
        var back = System.Text.Json.JsonSerializer.Deserialize<Configuration>("{}")!;
        Assert.Empty(back.SortRules);
    }

    [Fact]
    public void Cache_returns_the_same_instance_until_the_version_or_race_changes()
    {
        var config = WithRule();
        var race = RaceSplitOptions.Off;
        var first = RuleSetCache.For(config, race);
        Assert.Same(first, RuleSetCache.For(config, race));

        config.SortRules.Add(new SortRuleEntry { Text = "name = 'b' -> Y" });
        config.SortRulesVersion++;
        var second = RuleSetCache.For(config, race);
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Rules.Count);

        Assert.NotSame(second, RuleSetCache.For(config, race with { MultiRaceToMultiRace = false }));
    }

    [Fact]
    public void Cache_is_kept_per_configuration()
    {
        var a = WithRule("name = 'a' -> A");
        var b = WithRule("name = 'b' -> B");
        var setA = RuleSetCache.For(a, RaceSplitOptions.Off);
        var setB = RuleSetCache.For(b, RaceSplitOptions.Off);
        Assert.NotSame(setA, setB);
        Assert.Same(setA, RuleSetCache.For(a, RaceSplitOptions.Off));
        Assert.Same(setB, RuleSetCache.For(b, RaceSplitOptions.Off));
    }

    [Fact]
    public void FromConfig_carries_the_rules_and_an_empty_list_leaves_layout_rules_null()
    {
        var config = new Configuration();
        var empty = SortSelectionFactory.FromConfig(config, []);
        Assert.Null(empty.Layout.Rules);
        Assert.Null(SortSelectionFactory.RulesFor(config));

        config.SortRules.Add(new SortRuleEntry { Text = "name = 'a' -> X" });
        config.SortRulesVersion++;
        var withRules = SortSelectionFactory.FromConfig(config, []);
        Assert.Equal(1, withRules.Layout.Rules!.Rules.Count);
        Assert.NotEqual(empty.Fingerprint(), withRules.Fingerprint());
    }

    [Fact]
    public void FromConfig_called_twice_reuses_the_compiled_set()
    {
        var config = WithRule();
        Assert.Same(SortSelectionFactory.FromConfig(config, []).Layout.Rules,
            SortSelectionFactory.FromConfig(config, []).Layout.Rules);
    }

    [Fact]
    public void Panel_built_selection_and_FromConfig_share_a_fingerprint_when_rules_exist()
    {
        var config = WithRule();
        var fromConfig = SortSelectionFactory.FromConfig(config, []);
        var race = RaceSplitOptions.FromConfig(config);
        var panel = SortPanel.BuildSelection(
            config, fromConfig.Strategy, fromConfig.SplitGear, fromConfig.SplitNpc, fromConfig.SplitHair,
            fromConfig.HairRaceCode, fromConfig.MidlanderFallback, fromConfig.SplitGearBody,
            fromConfig.SplitBodyMods, fromConfig.BodyPriority, config.SortSharedHandsFeet,
            config.SortSplitEmotes, race, rootOnly: false);

        Assert.NotNull(panel.Layout.Rules);
        Assert.Equal(fromConfig.Fingerprint(), panel.Fingerprint());
    }
}
