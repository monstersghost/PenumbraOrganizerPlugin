using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateSortBodyTests
{
    // Its own fixture, for the same reason OrganizerStateSortHairTests has one: that file's
    // 16-row theory holds expectations captured from the original SortBy* methods, and a splitBody
    // axis would double it again.
    //
    // Two rows: one body whose type resolved, one whose did not. The second is load-bearing -
    // without it, a "split off" assertion would pass against a flattener that only ever sees null.
    private static OrganizerState WithBodyMods()
    {
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "typed", Name = "Bibo+ Body", Author = "Ann",
                CurrentPath = "Bibo+ Body", ProposedPath = "Bibo+ Body",
                Category = ModCategory.Body, SubCategory = "Bibo+" },

            new OrganizerModRow { Identifier = "untyped", Name = "Some Body", Author = "Bob",
                CurrentPath = "Some Body", ProposedPath = "Some Body",
                Category = ModCategory.Body, SubCategory = null },
        ], new HashSet<string>());
        return state;
    }

    private static string Canon(string s) => s;

    private static string PathOf(OrganizerState state, string identifier) =>
        state.Mods.Single(m => m.Identifier == identifier).ProposedPath;

    [Theory]
    [InlineData(SortStrategy.TypeOnly, "Body/Bibo+/Bibo+ Body", "Body/Some Body")]
    [InlineData(SortStrategy.TypeThenCreator, "Body/Bibo+/Ann/Bibo+ Body", "Body/Bob/Some Body")]
    [InlineData(SortStrategy.CreatorThenType, "Ann/Body/Bibo+/Bibo+ Body", "Bob/Body/Some Body")]
    public void Sort_BodySplitOn_NestsTheTypedModOnly(
        SortStrategy strategy, string expectedTyped, string expectedUntyped)
    {
        var state = WithBodyMods();

        state.Sort(strategy, splitGear: false, splitNpc: false, splitHair: false, Canon, splitBody: true);

        Assert.Equal(expectedTyped, PathOf(state, "typed"));
        Assert.Equal(expectedUntyped, PathOf(state, "untyped"));
    }

    [Theory]
    [InlineData(SortStrategy.TypeOnly, "Body/Bibo+ Body", "Body/Some Body")]
    [InlineData(SortStrategy.TypeThenCreator, "Body/Ann/Bibo+ Body", "Body/Bob/Some Body")]
    [InlineData(SortStrategy.CreatorThenType, "Ann/Body/Bibo+ Body", "Bob/Body/Some Body")]
    public void Sort_BodySplitOff_FlattensBothToBody(
        SortStrategy strategy, string expectedTyped, string expectedUntyped)
    {
        var state = WithBodyMods();

        state.Sort(strategy, splitGear: false, splitNpc: false, splitHair: false, Canon, splitBody: false);

        Assert.Equal(expectedTyped, PathOf(state, "typed"));
        Assert.Equal(expectedUntyped, PathOf(state, "untyped"));
    }

    [Fact]
    public void Sort_BodySplitDefaultsOff_SoAnUnchangedInstallDoesNotMove()
    {
        // The overload without splitBody must behave as it did before this feature existed. A body
        // library silently re-foldering itself on upgrade would be the worst outcome here.
        var state = WithBodyMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: false, splitHair: false, Canon);

        Assert.Equal("Body/Bibo+ Body", PathOf(state, "typed"));
        Assert.Equal("Body/Some Body", PathOf(state, "untyped"));
    }

    [Fact]
    public void Sort_CreatorOnly_IgnoresTheBodySplit()
    {
        // By Creator never consults the category, so the split must be inert - the same contract
        // the gear, NPC and hair splits hold.
        var state = WithBodyMods();

        state.Sort(SortStrategy.CreatorOnly, splitGear: false, splitNpc: false, splitHair: false,
            Canon, splitBody: true);

        Assert.Equal("Ann/Bibo+ Body", PathOf(state, "typed"));
        Assert.Equal("Bob/Some Body", PathOf(state, "untyped"));
    }

    [Fact]
    public void Sort_BodySplit_DoesNotTouchOtherCategories()
    {
        // FlattenBodySubCategory keys on Category == Body, so a Gear row's slot subcategory must
        // survive the body split untouched.
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "gear", Name = "A Top", Author = "Ann",
                CurrentPath = "A Top", ProposedPath = "A Top",
                Category = ModCategory.Gear, SubCategory = "Top" },
            new OrganizerModRow { Identifier = "hair", Name = "A Hair", Author = "Ann",
                CurrentPath = "A Hair", ProposedPath = "A Hair",
                Category = ModCategory.Hair, SubCategory = "115" },
        ], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: true,
            Canon, splitBody: true);

        Assert.Equal("Gear/Top/A Top", PathOf(state, "gear"));
        Assert.Equal("Hair/115/A Hair", PathOf(state, "hair"));
    }
}
