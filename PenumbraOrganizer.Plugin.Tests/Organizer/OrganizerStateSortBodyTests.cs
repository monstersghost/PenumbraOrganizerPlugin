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
            Canon, splitGearBody: true, splitBodyMods: true);

        Assert.Equal("Gear/10. Bibo+/02. Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Gear/11. TBSE/00. Sets/TBSE Outfit", PathOf(state, "unslotted"));
        Assert.Equal("Gear/02. Top/Plain Robe", PathOf(state, "bodiless"));
        Assert.Equal("Body/Rue+/Replacer/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Fact]
    public void Sort_BodySplitOnAndSlotSplitOff_FilesGearUnderBodyAlone()
    {
        // The two splits are independent: a body folder without a slot below it.
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: false, splitHair: false,
            Canon, splitGearBody: true, splitBodyMods: true);

        Assert.Equal("Gear/Bibo+/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Gear/TBSE/TBSE Outfit", PathOf(state, "unslotted"));
        Assert.Equal("Gear/Plain Robe", PathOf(state, "bodiless"));
    }

    [Fact]
    public void Sort_BodySplitOff_IsExactlyThePreExistingBehaviour()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false,
            Canon, splitGearBody: false, splitBodyMods: false);

        Assert.Equal("Gear/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Gear/TBSE Outfit", PathOf(state, "unslotted"));
        Assert.Equal("Gear/Top/Plain Robe", PathOf(state, "bodiless"));
        Assert.Equal("Body/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Fact]
    public void Sort_BodySplitDefaultsOff_SoAnUnchangedInstallDoesNotMove()
    {
        // The overload without the body flags must mean what it meant before this feature existed.
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon);

        Assert.Equal("Gear/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Body/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Theory]
    [InlineData(SortStrategy.TypeThenCreator, "Gear/10. Bibo+/02. Top/Ann/Bibo+ Medieval Dress")]
    [InlineData(SortStrategy.CreatorThenType, "Ann/Gear/10. Bibo+/02. Top/Bibo+ Medieval Dress")]
    public void Sort_BodySplitComposesWithTheCreatorStrategies(SortStrategy strategy, string expected)
    {
        var state = WithMods();

        state.Sort(strategy, splitGear: true, splitNpc: false, splitHair: false, Canon, splitGearBody: true, splitBodyMods: true);

        Assert.Equal(expected, PathOf(state, "dress"));
    }

    [Fact]
    public void Sort_CreatorOnly_IgnoresTheBodySplit()
    {
        // By Creator never consults the category, so the split must be inert - the same contract
        // the gear, NPC and hair splits hold.
        var state = WithMods();

        state.Sort(SortStrategy.CreatorOnly, splitGear: false, splitNpc: false, splitHair: false,
            Canon, splitGearBody: true, splitBodyMods: true);

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
            Canon, splitGearBody: true, splitBodyMods: true);

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

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: true, splitBodyMods: true,
            bodyPriority: BodyPriority.Normalize(["Rue+"], Bodies));
        Assert.Equal("Gear/10. Rue+/02. Top/Succubus Lifestyle", PathOf(state, "multi"));

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: true, splitBodyMods: true,
            bodyPriority: BodyPriority.Normalize(["YAB"], Bodies));
        Assert.Equal("Gear/10. YAB/02. Top/Succubus Lifestyle", PathOf(state, "multi"));
    }

    [Fact]
    public void Sort_WithoutAPriority_UsesTheRowsStoredBodyType()
    {
        var state = WithMultiBodyMod();

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: true, splitBodyMods: true);

        Assert.Equal("Gear/10. YAB/02. Top/Succubus Lifestyle", PathOf(state, "multi"));
    }

    [Fact]
    public void Sort_BodySplitOff_IgnoresThePriorityEntirely()
    {
        var state = WithMultiBodyMod();

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: false, splitBodyMods: false,
            bodyPriority: BodyPriority.Normalize(["Rue+"], Bodies));

        Assert.Equal("Gear/Top/Succubus Lifestyle", PathOf(state, "multi"));
    }

    [Fact]
    public void Sort_RecordsTheResolutionAndPriorityItActuallyUsed_AcrossTwoSorts()
    {
        var state = WithMultiBodyMod();
        var row = state.Mods.Single();

        var rueFirst = BodyPriority.Normalize(["Rue+"], Bodies);
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: true, splitBodyMods: true, bodyPriority: rueFirst);
        Assert.Equal(new BodyResolution("Rue+", BodyEvidenceTier.Content, BodyEvidenceSource.Option), row.StagedBodyResolution);
        Assert.Equal(rueFirst, state.StagedBodyPriority);
        Assert.Equal("YAB", row.BodyType);

        var yabFirst = BodyPriority.Normalize(["YAB"], Bodies);
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: true, splitBodyMods: true, bodyPriority: yabFirst);
        Assert.Equal("YAB", row.StagedBodyResolution!.Label);
        Assert.Equal(yabFirst, state.StagedBodyPriority);
    }

    [Fact]
    public void Sort_BodySplitOff_ClearsTheStagedResolutionAndPriority()
    {
        var state = WithMultiBodyMod();
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: true, splitBodyMods: true,
            bodyPriority: BodyPriority.Default(Bodies));

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: false, splitBodyMods: false);

        Assert.Null(state.Mods.Single().StagedBodyResolution);
        Assert.Null(state.StagedBodyPriority);
    }

    [Fact]
    public void Sort_RowsWithoutCandidates_KeepTheirStoredBodyType_EvenWithAPriority()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: true, splitBodyMods: true,
            bodyPriority: BodyPriority.Default(Bodies));

        Assert.Equal("Gear/10. Bibo+/02. Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
    }

    [Fact]
    public void LoadScan_AfterASortWithAPriority_ClearsTheStagedPriorityAndTheExportLine()
    {
        var state = WithMultiBodyMod();
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: true, splitBodyMods: true,
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
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: true, splitBodyMods: true,
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
        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: true, splitBodyMods: true,
            bodyPriority: BodyPriority.Default(Bodies));

        Assert.True(state.AssignManual("multi", "Manual/Spot"));

        Assert.Null(state.Mods.Single().StagedBodyResolution);
        Assert.NotNull(state.StagedBodyPriority);
    }

    [Fact]
    public void Sort_GearBodyFlagOnly_FilesGearUnderItsBody_AndLeavesBodyModsFlat()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon,
            splitGearBody: true, splitBodyMods: false);

        Assert.Equal("Gear/10. Bibo+/02. Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Body/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Fact]
    public void Sort_BodyModsFlagOnly_FilesBodyModsUnderTheirBody_AndLeavesGearWithoutOne()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon,
            splitGearBody: false, splitBodyMods: true);

        Assert.Equal("Gear/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Body/Rue+/Replacer/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Fact]
    public void Sort_BothFlags_FilesBothCategories()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon,
            splitGearBody: true, splitBodyMods: true);

        Assert.Equal("Gear/10. Bibo+/02. Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Body/Rue+/Replacer/Rue+ Body", PathOf(state, "bodymod"));
    }

    [Fact]
    public void Sort_GearBodyFlagWithoutTheSlotSplit_FilesGearAtGearBodyMod()
    {
        // The two splits are independent: the body flag does not switch the slot split on.
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: false, splitNpc: false, splitHair: false, Canon,
            splitGearBody: true, splitBodyMods: false);

        Assert.Equal("Gear/Bibo+/Bibo+ Medieval Dress", PathOf(state, "dress"));
    }

    [Fact]
    public void Sort_NeitherFlag_UsesNoBodySegmentAnywhere()
    {
        var state = WithMods();

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon);

        Assert.Equal("Gear/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
        Assert.Equal("Body/Rue+ Body", PathOf(state, "bodymod"));
    }

    private static OrganizerState WithGearAndBodyCandidates()
    {
        var state = new OrganizerState();
        var both = new BodyCandidates
        {
            Content = [new BodyCandidate("YAB", BodyEvidenceSource.Option),
                       new BodyCandidate("Rue+", BodyEvidenceSource.Option)],
        };
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "g", Name = "Gear Multi", Author = "Ann",
                CurrentPath = "Gear Multi", ProposedPath = "Gear Multi",
                Category = ModCategory.Gear, SubCategory = "Top", BodyType = "YAB", BodyCandidates = both },
            new OrganizerModRow { Identifier = "b", Name = "Body Multi", Author = "Ann",
                CurrentPath = "Body Multi", ProposedPath = "Body Multi",
                Category = ModCategory.Body, BodyType = "YAB", BodyCandidates = both },
        ], new HashSet<string>());
        return state;
    }

    [Fact]
    public void Sort_StagedResolution_IsRecordedOnlyForRowsWhoseCategoryFlagIsOn()
    {
        var state = WithGearAndBodyCandidates();
        var rueFirst = BodyPriority.Normalize(["Rue+"], ["YAB", "Rue+"]);

        state.Sort(SortStrategy.TypeOnly, splitGear: true, splitNpc: false, splitHair: false, Canon,
            splitGearBody: true, splitBodyMods: false, bodyPriority: rueFirst);

        Assert.Equal("Rue+", state.Mods.Single(m => m.Identifier == "g").StagedBodyResolution!.Label);
        Assert.Null(state.Mods.Single(m => m.Identifier == "b").StagedBodyResolution);
        Assert.Equal(rueFirst, state.StagedBodyPriority);
        Assert.Equal("Body/Body Multi", PathOf(state, "b"));
    }

    [Fact]
    public void Sort_StagedPriority_IsNullOnlyWhenBothFlagsAreOff()
    {
        var state = WithGearAndBodyCandidates();
        var p = BodyPriority.Default(["YAB", "Rue+"]);

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: false, splitBodyMods: true, bodyPriority: p);
        Assert.Equal(p, state.StagedBodyPriority);

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: false, splitBodyMods: false, bodyPriority: p);
        Assert.Null(state.StagedBodyPriority);
    }

    [Fact]
    public void Sort_BodyMod_StagedResolutionAgreesWithThePathItWasFiledUnder()
    {
        // "Yet Another Body+" carries a Bibo+ option group but its own name says YAB. The folder uses
        // the name-first rule for Body mods; the diagnostic record must describe the same choice.
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "yab", Name = "Yet Another Body+", Author = "Aleks",
                CurrentPath = "Body/Yet Another Body+", ProposedPath = "Body/Yet Another Body+",
                Category = ModCategory.Body, BodyType = "Bibo+",
                BodyCandidates = new BodyCandidates
                {
                    Content = [new BodyCandidate("Bibo+", BodyEvidenceSource.Option)],
                    Name = [new BodyCandidate("YAB", BodyEvidenceSource.Name)],
                } },
        ], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitGearBody: false, splitBodyMods: true,
            bodyPriority: BodyPriority.Normalize(["Bibo+"], Bodies));

        Assert.Equal("Body/YAB/Replacer/Yet Another Body+", PathOf(state, "yab"));
        Assert.Equal("YAB", state.Mods.Single().StagedBodyResolution!.Label);
    }

    private static OrganizerState WithGearLibrary()
    {
        OrganizerModRow G(string id, string? slot, string? body) => new()
        {
            Identifier = id, Name = id, Author = "A", CurrentPath = id, ProposedPath = id,
            Category = ModCategory.Gear, SubCategory = slot, BodyType = body,
        };
        var state = new OrganizerState();
        state.LoadScan(
        [
            G("rue-top", "Top", "Rue+"), G("bibo-set", null, "Bibo+"), G("head", "Head", null),
            G("set", null, null), G("rue-set", null, "Rue+"),
        ], new HashSet<string>());
        return state;
    }

    [Fact]
    public void Sort_BothGearSplitsOn_LaysGearOutNumbered()
    {
        var state = WithGearLibrary();

        state.Sort(SortStrategy.TypeOnly, true, true, false, Canon, splitGearBody: true, splitBodyMods: false,
            bodyPriority: BodyPriority.Normalize(["Rue+"], Bodies));

        Assert.Equal("Gear/10. Rue+/02. Top/rue-top", PathOf(state, "rue-top"));
        Assert.Equal("Gear/10. Rue+/00. Sets/rue-set", PathOf(state, "rue-set"));
        Assert.Equal("Gear/11. Bibo+/00. Sets/bibo-set", PathOf(state, "bibo-set"));
        Assert.Equal("Gear/01. Head/head", PathOf(state, "head"));
        Assert.Equal("Gear/00. Sets/set", PathOf(state, "set"));
    }

    [Fact]
    public void Sort_BothGearSplitsOn_NullPriority_IsStillNumbered()
    {
        var state = WithGearLibrary();

        state.Sort(SortStrategy.TypeOnly, true, true, false, Canon, splitGearBody: true, splitBodyMods: false);

        Assert.Equal("Gear/10. Bibo+/00. Sets/bibo-set", PathOf(state, "bibo-set"));
        Assert.Equal("Gear/11. Rue+/02. Top/rue-top", PathOf(state, "rue-top"));
    }

    [Fact]
    public void Sort_OnlyTheSlotSplit_IsUnchanged()
    {
        var state = WithGearLibrary();

        state.Sort(SortStrategy.TypeOnly, true, true, false, Canon, splitGearBody: false, splitBodyMods: false);

        Assert.Equal("Gear/Top/rue-top", PathOf(state, "rue-top"));
        Assert.Equal("Gear/Head/head", PathOf(state, "head"));
        Assert.Equal("Gear/set", PathOf(state, "set"));
    }

    [Fact]
    public void Sort_OnlyTheBodySplit_IsUnchanged()
    {
        var state = WithGearLibrary();

        state.Sort(SortStrategy.TypeOnly, false, true, false, Canon, splitGearBody: true, splitBodyMods: false,
            bodyPriority: BodyPriority.Normalize(["Rue+"], Bodies));

        Assert.Equal("Gear/Rue+/rue-top", PathOf(state, "rue-top"));
        Assert.Equal("Gear/head", PathOf(state, "head"));
    }

    [Fact]
    public void Sort_AProtectedGearRow_DoesNotConsumeABodyNumber()
    {
        // Bibo+ is ranked first but its only gear mod is protected and will not move, so the one
        // body being placed (Rue+) must still be 10, not 11.
        OrganizerModRow G(string id, string body) => new()
        {
            Identifier = id, Name = id, Author = "A", CurrentPath = id, ProposedPath = id,
            Category = ModCategory.Gear, SubCategory = "Top", BodyType = body,
        };
        var state = new OrganizerState();
        state.LoadScan([G("prot", "Bibo+"), G("free", "Rue+")], new HashSet<string> { "prot" });

        state.Sort(SortStrategy.TypeOnly, true, true, false, Canon, splitGearBody: true, splitBodyMods: false,
            bodyPriority: BodyPriority.Normalize(["Bibo+", "Rue+"], Bodies));

        Assert.Equal("Gear/10. Rue+/02. Top/free", PathOf(state, "free"));
        Assert.Equal("prot", PathOf(state, "prot"));
    }
}
