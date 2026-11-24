using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SplitHelpTests
{
    [Theory]
    [InlineData(ModCategory.Gear, LayoutSplits.BodyType, "sort.split-gear-body")]
    [InlineData(ModCategory.Gear, LayoutSplits.Slot, "sort.split-gear")]
    [InlineData(ModCategory.Gear, LayoutSplits.SharedHandsFeet, "sort.shared-hands-feet")]
    [InlineData(ModCategory.NPC, LayoutSplits.NpcKind, "sort.split-npc")]
    [InlineData(ModCategory.Hair, LayoutSplits.Race, "sort.split-hair-by-race")]
    [InlineData(ModCategory.Hair, LayoutSplits.HairNumber, "sort.split-hair")]
    [InlineData(ModCategory.Body, LayoutSplits.PartAndBodyType, "sort.split-body-mods")]
    [InlineData(ModCategory.Body, LayoutSplits.Race, "sort.race-rows")]
    [InlineData(ModCategory.Body, LayoutSplits.RacialScalingRace, "sort.race-rows")]
    [InlineData(ModCategory.Face, LayoutSplits.Race, "sort.race-rows")]
    [InlineData(ModCategory.Face, LayoutSplits.EyesRace, "sort.race-rows")]
    [InlineData(ModCategory.Skin, LayoutSplits.Race, "sort.race-rows")]
    [InlineData(ModCategory.Animation, LayoutSplits.Emote, "sort.split-emotes")]
    public void EachSplit_KeepsTheTopicOfTheCheckboxItReplaces(ModCategory category, LayoutSplits split, string id) =>
        Assert.Equal(id, SplitHelp.TopicFor(category, split).Id);

    [Fact]
    public void EveryCatalogSplit_HasATopicWithATooltip() =>
        Assert.All(LayoutCatalog.Categories.SelectMany(c => LayoutCatalog.SplitsFor(c).Select(s => (c, s.Split))),
            pair => Assert.False(string.IsNullOrEmpty(Help.Short(SplitHelp.TopicFor(pair.c, pair.Split)))));
}
