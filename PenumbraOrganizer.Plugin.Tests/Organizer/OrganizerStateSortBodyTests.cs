using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Templates;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

/// <summary>
/// The body split end to end. GEAR is the case that matters: a garment filed under the body it was
/// made for, with its slot below.
/// </summary>
public class OrganizerStateSortBodyTests
{
    // Its own fixture, for the same reason OrganizerStateSortHairTests has one.
    //
    // Four rows: gear with a body and a slot, gear with a body and no slot resolved, gear with no
    // body at all, and a body mod. The third is load-bearing - without it a "split on" assertion
    // would pass against code that blindly inserted a segment for every row.
    private static OrganizerState WithMods()
    {
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "dress", Name = "Bibo+ Medieval Dress", Author = "Ann",
                CurrentPath = "Bibo+ Medieval Dress", ProposedPath = "Bibo+ Medieval Dress",
                Category = ModCategory.Gear, SubCategory = "Top", BodyType = "Bibo+" },

            new OrganizerModRow { Identifier = "unslotted", Name = "TBSE Outfit", Author = "Ann",
                CurrentPath = "TBSE Outfit", ProposedPath = "TBSE Outfit",
                Category = ModCategory.Gear, SubCategory = null, BodyType = "TBSE" },

            new OrganizerModRow { Identifier = "bodiless", Name = "Plain Robe", Author = "Bob",
                CurrentPath = "Plain Robe", ProposedPath = "Plain Robe",
                Category = ModCategory.Gear, SubCategory = "Top", BodyType = null },

            new OrganizerModRow { Identifier = "bodymod", Name = "Rue+ Body", Author = "Bob",
                CurrentPath = "Rue+ Body", ProposedPath = "Rue+ Body",
                Category = ModCategory.Body, SubCategory = null, BodyType = "Rue+" },
        ], new HashSet<string>());
        return state;
    }

    private static string Canon(string s) => s;

    private static string PathOf(OrganizerState state, string identifier) =>
        state.Mods.Single(m => m.Identifier == identifier).ProposedPath;

    [Fact]
    public void Sort_BodyAndSlotSplitsOn_FilesGearUnderBodyThenSlot()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false,
            Canon, splitBody: true);

        Assert.Equal("Gear/Bibo+/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Gear/TBSE/TBSE Outfit", PathOf(state, "unslotted"));
        Assert.Equal("Gear/Top/Plain Robe", PathOf(state, "bodiless"));
        Assert.Equal("Body/Rue+/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Fact]
    public void Sort_BodySplitOnAndSlotSplitOff_FilesGearUnderBodyAlone()
    {
        // The two splits are independent: a body folder without a slot below it.
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: false, splitHair: false,
            Canon, splitBody: true);

        Assert.Equal("Gear/Bibo+/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Gear/TBSE/TBSE Outfit", PathOf(state, "unslotted"));
        Assert.Equal("Gear/Plain Robe", PathOf(state, "bodiless"));
    }

    [Fact]
    public void Sort_BodySplitOff_IsExactlyThePreExistingBehaviour()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false,
            Canon, splitBody: false);

        Assert.Equal("Gear/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Gear/TBSE Outfit", PathOf(state, "unslotted"));
        Assert.Equal("Gear/Top/Plain Robe", PathOf(state, "bodiless"));
        Assert.Equal("Body/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Fact]
    public void Sort_BodySplitDefaultsOff_SoAnUnchangedInstallDoesNotMove()
    {
        // The overload without splitBody must mean what it meant before this feature existed.
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon);

        Assert.Equal("Gear/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Body/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Theory]
    [InlineData(SortStrategy.TypeThenCreator, "Gear/Bibo+/Top/Ann/Bibo+ Medieval Dress")]
    [InlineData(SortStrategy.CreatorThenType, "Ann/Gear/Bibo+/Top/Bibo+ Medieval Dress")]
    public void Sort_BodySplitComposesWithTheCreatorStrategies(SortStrategy strategy, string expected)
    {
        var state = WithMods();

        state.Sort(strategy, splitGear: true, splitNpc: false, splitHair: false, Canon, splitBody: true);

        Assert.Equal(expected, PathOf(state, "dress"));
    }

    [Fact]
    public void Sort_CreatorOnly_IgnoresTheBodySplit()
    {
        // By Creator never consults the category, so the split must be inert - the same contract
        // the gear, NPC and hair splits hold.
        var state = WithMods();

        state.Sort(SortStrategy.CreatorOnly, splitGear: false, splitNpc: false, splitHair: false,
            Canon, splitBody: true);

        Assert.Equal("Ann/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Bob/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Fact]
    public void Sort_BodySplit_DoesNotTouchOtherCategories()
    {
        // Only Gear and Body ever carry a BodyType, so a hair row's number must come through the
        // body split untouched - and must not throw on the way, which it would if a body segment
        // were built for an ineligible category.
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "hair", Name = "Bibo+ Hair", Author = "Ann",
                CurrentPath = "Bibo+ Hair", ProposedPath = "Bibo+ Hair",
                Category = ModCategory.Hair, SubCategory = "115", BodyType = null },
            new OrganizerModRow { Identifier = "npc", Name = "Some Boss", Author = "Ann",
                CurrentPath = "Some Boss", ProposedPath = "Some Boss",
                Category = ModCategory.NPC, SubCategory = "Bosses", BodyType = null },
        ], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: true, splitHair: true,
            Canon, splitBody: true);

        Assert.Equal("Hair/115/Bibo+ Hair", PathOf(state, "hair"));
        Assert.Equal("NPC/Bosses/Some Boss", PathOf(state, "npc"));
    }

    private static OrganizerState WithMultiBodyMod()
    {
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "multi", Name = "Succubus Lifestyle", Author = "Ann",
                CurrentPath = "Succubus Lifestyle", ProposedPath = "Succubus Lifestyle",
                Category = ModCategory.Gear, SubCategory = "Top", BodyType = "YAB",
                BodyCandidates = new BodyCandidates
                {
                    Content = [new BodyCandidate("YAB", BodyEvidenceSource.Option),
                               new BodyCandidate("Rue+", BodyEvidenceSource.Option)],
                } },
        ], new HashSet<string>());
        return state;
    }

    private static readonly string[] Bodies = ["YAB", "Rue+", "Bibo+"];

    [Fact]
    public void Sort_ReorderingThePriority_RefilesTheSameRowsWithoutARescan()
    {
        var state = WithMultiBodyMod();

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true,
            bodyPriority: BodyPriority.Normalize(["Rue+"], Bodies));
        Assert.Equal("Gear/Rue+/Top/Succubus Lifestyle", PathOf(state, "multi"));

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true,
            bodyPriority: BodyPriority.Normalize(["YAB"], Bodies));
        Assert.Equal("Gear/YAB/Top/Succubus Lifestyle", PathOf(state, "multi"));
    }

    [Fact]
    public void Sort_WithoutAPriority_UsesTheRowsStoredBodyType()
    {
        var state = WithMultiBodyMod();

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true);

        Assert.Equal("Gear/YAB/Top/Succubus Lifestyle", PathOf(state, "multi"));
    }

    [Fact]
    public void Sort_BodySplitOff_IgnoresThePriorityEntirely()
    {
        var state = WithMultiBodyMod();

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: false,
            bodyPriority: BodyPriority.Normalize(["Rue+"], Bodies));

        Assert.Equal("Gear/Top/Succubus Lifestyle", PathOf(state, "multi"));
    }

    [Fact]
    public void Sort_RecordsTheResolutionAndPriorityItActuallyUsed_AcrossTwoSorts()
    {
        var state = WithMultiBodyMod();
        var row = state.Mods.Single();

        var rueFirst = BodyPriority.Normalize(["Rue+"], Bodies);
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true, bodyPriority: rueFirst);
        Assert.Equal(new BodyResolution("Rue+", BodyEvidenceTier.Content, BodyEvidenceSource.Option), row.StagedBodyResolution);
        Assert.Equal(rueFirst, state.StagedBodyPriority);
        Assert.Equal("YAB", row.BodyType);

        var yabFirst = BodyPriority.Normalize(["YAB"], Bodies);
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true, bodyPriority: yabFirst);
        Assert.Equal("YAB", row.StagedBodyResolution!.Label);
        Assert.Equal(yabFirst, state.StagedBodyPriority);
    }

    [Fact]
    public void Sort_BodySplitOff_ClearsTheStagedResolutionAndPriority()
    {
        var state = WithMultiBodyMod();
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true,
            bodyPriority: BodyPriority.Default(Bodies));

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: false);

        Assert.Null(state.Mods.Single().StagedBodyResolution);
        Assert.Null(state.StagedBodyPriority);
    }

    [Fact]
    public void Sort_RowsWithoutCandidates_KeepTheirStoredBodyType_EvenWithAPriority()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true,
            bodyPriority: BodyPriority.Default(Bodies));

        Assert.Equal("Gear/Bibo+/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
    }

    [Fact]
    public void LoadScan_AfterASortWithAPriority_ClearsTheStagedPriorityAndTheExportLine()
    {
        var state = WithMultiBodyMod();
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true,
            bodyPriority: BodyPriority.Default(Bodies));
        Assert.NotNull(state.StagedBodyPriority);

        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "multi", Name = "Succubus Lifestyle", Author = "Ann",
                CurrentPath = "Succubus Lifestyle", ProposedPath = "Succubus Lifestyle",
                Category = ModCategory.Gear, SubCategory = "Top", BodyType = "YAB" },
        ], new HashSet<string>());

        Assert.Null(state.StagedBodyPriority);
        var export = OrganizerExportFormatter.Format(state.Mods, state.Validate(), state.StagedBodyPriority);
        Assert.DoesNotContain("Body priority used for the last sort", export);
    }

    [Fact]
    public void ApplyTemplate_AfterASortWithAPriority_ClearsTheTouchedRowsAndThePriority()
    {
        var state = WithMultiBodyMod();
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true,
            bodyPriority: BodyPriority.Default(Bodies));
        Assert.NotNull(state.Mods.Single().StagedBodyResolution);

        var template = new ValidatedOrganizationTemplate(
            "T", "A", null, new TemplateFallback(SortStrategy.TypeOnly, false, true),
            new Dictionary<string, string>(), [], new Dictionary<string, string> { ["succubus lifestyle"] = "Looks" });
        state.ApplyTemplate(TemplatePlanner.Plan(template, state.Mods, Canon));

        Assert.StartsWith("Looks/", state.Mods.Single().ProposedPath);
        Assert.Null(state.Mods.Single().StagedBodyResolution);
        Assert.Null(state.StagedBodyPriority);
    }

    [Fact]
    public void AssignManual_ClearsThatRowsStagedResolutionButNotThePriority()
    {
        var state = WithMultiBodyMod();
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true,
            bodyPriority: BodyPriority.Default(Bodies));

        Assert.True(state.AssignManual("multi", "Manual/Spot"));

        Assert.Null(state.Mods.Single().StagedBodyResolution);
        Assert.NotNull(state.StagedBodyPriority);
    }
}
