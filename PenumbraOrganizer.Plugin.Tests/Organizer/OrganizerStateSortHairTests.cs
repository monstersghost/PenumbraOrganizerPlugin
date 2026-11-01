using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateSortHairTests
{
    // Its own fixture, deliberately not OrganizerStateSortTests'. That file's 16-row theory holds
    // expectations CAPTURED from the original SortBy* methods; adding a hair row would change all
    // 16 of them and a splitHair axis would double it to 32 hand-written rows.
    //
    // Two rows: one hair mod whose number resolved, one whose number did not. The second is
    // load-bearing - without it, "split off" assertions would pass against a flattener that only
    // ever sees null anyway.
    private static OrganizerState WithHairMods()
    {
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "numbered", Name = "Numbered Hair", Author = "Ann",
                CurrentPath = "Numbered Hair", ProposedPath = "Numbered Hair",
                Category = ModCategory.Hair, SubCategory = "115" },

            new OrganizerModRow { Identifier = "ambiguous", Name = "Ambiguous Hair", Author = "Bob",
                CurrentPath = "Ambiguous Hair", ProposedPath = "Ambiguous Hair",
                Category = ModCategory.Hair, SubCategory = null },
        ], new HashSet<string>());
        return state;
    }

    private static string Canon(string s) => s;

    private static string PathOf(OrganizerState state, string identifier) =>
        state.Mods.Single(m => m.Identifier == identifier).ProposedPath;

    [Theory]
    [InlineData(SortStrategy.TypeOnly, "Hair/115/Numbered Hair", "Hair/Ambiguous Hair")]
    [InlineData(SortStrategy.TypeThenCreator, "Hair/115/Ann/Numbered Hair", "Hair/Bob/Ambiguous Hair")]
    [InlineData(SortStrategy.CreatorThenType, "Ann/Hair/115/Numbered Hair", "Bob/Hair/Ambiguous Hair")]
    public void Sort_HairSplitOn_NestsTheNumberedModOnly(
        SortStrategy strategy, string expectedNumbered, string expectedAmbiguous)
    {
        var state = WithHairMods();

        state.Sort(strategy, splitGear: false, splitNpc: false, splitHair: true, Canon);

        Assert.Equal(expectedNumbered, PathOf(state, "numbered"));
        Assert.Equal(expectedAmbiguous, PathOf(state, "ambiguous"));
    }

    [Theory]
    [InlineData(SortStrategy.TypeOnly, "Hair/Numbered Hair", "Hair/Ambiguous Hair")]
    [InlineData(SortStrategy.TypeThenCreator, "Hair/Ann/Numbered Hair", "Hair/Bob/Ambiguous Hair")]
    [InlineData(SortStrategy.CreatorThenType, "Ann/Hair/Numbered Hair", "Bob/Hair/Ambiguous Hair")]
    public void Sort_HairSplitOff_LeavesEveryHairModFlat(
        SortStrategy strategy, string expectedNumbered, string expectedAmbiguous)
    {
        var state = WithHairMods();

        state.Sort(strategy, splitGear: false, splitNpc: false, splitHair: false, Canon);

        Assert.Equal(expectedNumbered, PathOf(state, "numbered"));
        Assert.Equal(expectedAmbiguous, PathOf(state, "ambiguous"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sort_CreatorOnly_IgnoresTheHairSplitEntirely(bool splitHair)
    {
        // CreatorOnly never consults the category, which is why SplitsApply disables all three
        // checkboxes for it.
        var state = WithHairMods();

        state.Sort(SortStrategy.CreatorOnly, splitGear: false, splitNpc: false, splitHair, Canon);

        Assert.Equal("Ann/Numbered Hair", PathOf(state, "numbered"));
        Assert.Equal("Bob/Ambiguous Hair", PathOf(state, "ambiguous"));
    }

    [Fact]
    public void Sort_HairSplitDoesNotDisturbGearOrNpc()
    {
        // The three flatteners are independent; each must touch only its own category.
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "hair", Name = "Hair Mod", Author = "Ann",
                CurrentPath = "Hair Mod", ProposedPath = "Hair Mod",
                Category = ModCategory.Hair, SubCategory = "115" },
            new OrganizerModRow { Identifier = "gear", Name = "Gear Mod", Author = "Ann",
                CurrentPath = "Gear Mod", ProposedPath = "Gear Mod",
                Category = ModCategory.Gear, SubCategory = "Feet" },
            new OrganizerModRow { Identifier = "npc", Name = "Npc Mod", Author = "Bob",
                CurrentPath = "Npc Mod", ProposedPath = "Npc Mod",
                Category = ModCategory.NPC, SubCategory = "Bosses" },
        ], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: true, splitHair: true, Canon);

        Assert.Equal("Hair/115/Hair Mod", PathOf(state, "hair"));
        Assert.Equal("Gear/Gear Mod", PathOf(state, "gear"));
        Assert.Equal("NPC/Bosses/Npc Mod", PathOf(state, "npc"));
    }

    [Fact]
    public void Sort_HairSplitOff_FlattensHairWithoutTouchingGearOrNpc()
    {
        // The mirror of the test above, and the one that actually exercises
        // FlattenHairSubCategory - the other runs with splitHair: true, so the flattener never
        // fires there at all. Without this, a flattener that nulled EVERY category's subcategory,
        // or that tested the wrong category, would pass this fixture.
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "hair", Name = "Hair Mod", Author = "Ann",
                CurrentPath = "Hair Mod", ProposedPath = "Hair Mod",
                Category = ModCategory.Hair, SubCategory = "115" },
            new OrganizerModRow { Identifier = "gear", Name = "Gear Mod", Author = "Ann",
                CurrentPath = "Gear Mod", ProposedPath = "Gear Mod",
                Category = ModCategory.Gear, SubCategory = "Feet" },
            new OrganizerModRow { Identifier = "npc", Name = "Npc Mod", Author = "Bob",
                CurrentPath = "Npc Mod", ProposedPath = "Npc Mod",
                Category = ModCategory.NPC, SubCategory = "Bosses" },
        ], new HashSet<string>());

        // Hair off, the other two ON: the hair subfolder must disappear while the gear and NPC
        // ones survive untouched.
        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: true, splitHair: false, Canon);

        Assert.Equal("Hair/Hair Mod", PathOf(state, "hair"));
        Assert.Equal("Gear/Feet/Gear Mod", PathOf(state, "gear"));
        Assert.Equal("NPC/Bosses/Npc Mod", PathOf(state, "npc"));
    }
}
