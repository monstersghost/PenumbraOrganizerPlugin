using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Workbook;

public class WorkbookTokensTests
{
    private static HashSet<string> S(params string[] v) => new(v, StringComparer.Ordinal);

    private static OrganizerModRow Row(
        ModCategory? category, string author = "Thor", RowRaceData? keys = null,
        BodyPart part = BodyPart.Replacer, bool scaling = false, RspEvidence? rsp = null,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? hair = null) =>
        new()
        {
            Identifier = "m", Name = "Mod", Author = author, CurrentPath = "Mod", ProposedPath = "Mod",
            Category = category, KeyRaces = keys, BodyPart = part, IsRacialScaling = scaling, Rsp = rsp,
            HairByRaceCode = hair,
        };

    private static RowRaceData Keys(string[]? face = null, string[]? skin = null, string[]? tail = null) =>
        new(S(face ?? []), S(), S(skin ?? []), S(tail ?? []), false);

    private static readonly RaceSplitOptions MasterOff = RaceSplitOptions.Off;

    private static DestinationTokens Tokens(OrganizerModRow row, RaceSplitOptions? race = null) =>
        WorkbookTokens.For(row, name => name, race ?? MasterOff);

    [Fact]
    public void Creator_IsTheCanonicalAuthorAsASortSegment()
    {
        var row = Row(ModCategory.Hair, author: "  Thor  ");
        var tokens = WorkbookTokens.For(row, name => name.Trim().ToUpperInvariant(), MasterOff);
        Assert.Equal("THOR", tokens.Creator);
    }

    [Fact]
    public void ANoAuthorMod_HasNoCreatorValue() =>
        Assert.Equal(string.Empty, Tokens(Row(ModCategory.Hair, author: "")).Creator);

    [Theory]
    [InlineData(ModCategory.Gear, "Gear")]
    [InlineData(ModCategory.Hair, "Hair")]
    [InlineData(ModCategory.Face, "Face")]
    [InlineData(ModCategory.Animation, "Animation and VFX")]
    [InlineData(ModCategory.VFX, "Animation and VFX")]
    [InlineData(ModCategory.NPC, "NPC")]
    [InlineData(ModCategory.Others, "Review")]
    public void Type_IsTheCategoryFolderAsSortNamesIt(ModCategory category, string expected) =>
        Assert.Equal(expected, Tokens(Row(category)).Type);

    [Fact]
    public void Type_IsDefinedForEveryCategory()
    {
        foreach (var category in Enum.GetValues<ModCategory>())
            Assert.False(string.IsNullOrEmpty(WorkbookTokens.TypeFolderFor(category)));
    }

    [Fact]
    public void AnUnclassifiedMod_IsTypeReview() =>
        Assert.Equal("Review", Tokens(Row(null)).Type);

    [Fact]
    public void Race_ForHairWithOneRace_IsThatRace_EvenWithTheMasterOff()
    {
        var hair = new Dictionary<string, IReadOnlySet<int>> { ["1801"] = new HashSet<int> { 115 } };
        Assert.Equal("Viera", Tokens(Row(ModCategory.Hair, hair: hair)).Race);
    }

    [Fact]
    public void Race_ForFaceUsesItsKeys() =>
        Assert.Equal("Elezen", Tokens(Row(ModCategory.Face, keys: Keys(face: ["Elezen"]))).Race);

    [Fact]
    public void Race_ForSkinAndTailsUsesTheirKeys()
    {
        Assert.Equal("Miqo'te", Tokens(Row(ModCategory.Skin, keys: Keys(skin: ["Miqo'te"]))).Race);
        Assert.Equal(
            "Au Ra",
            Tokens(Row(ModCategory.Body, keys: Keys(tail: ["Au Ra"]), part: BodyPart.TailsEarsHorns)).Race);
    }

    [Fact]
    public void Race_ForAModWithNoRaceRow_IsEmptySoItCollapses()
    {
        Assert.Equal(string.Empty, Tokens(Row(ModCategory.Gear)).Race);
        Assert.Equal(string.Empty, Tokens(Row(ModCategory.Weapon)).Race);
        Assert.Equal(string.Empty, Tokens(Row(ModCategory.Body, part: BodyPart.Replacer)).Race);
    }

    [Fact]
    public void Race_ForAnApplicableModWithNoEvidence_IsMultiRace() =>
        Assert.Equal("Multi-race", Tokens(Row(ModCategory.Face, keys: null)).Race);

    [Fact]
    public void Race_ForRacialScaling_UsesTheRspRaces()
    {
        var one = new RspEvidence(S("Miqo'te"), true, true);
        var two = new RspEvidence(S("Miqo'te", "Viera"), true, true);
        Assert.Equal("Miqo'te", Tokens(Row(ModCategory.Gear, scaling: true, rsp: one)).Race);
        Assert.Equal("Multi-race", Tokens(Row(ModCategory.Gear, scaling: true, rsp: two)).Race);
    }

    [Fact]
    public void Race_FollowsTheMultiRaceToggleAndThePriority()
    {
        var two = new RspEvidence(S("Miqo'te", "Viera"), true, true);
        var row = Row(ModCategory.Gear, scaling: true, rsp: two);
        var toggleOff = new RaceSplitOptions(false, RaceRows.All, false, RacePriority.Empty.Add("Viera"));
        Assert.Equal("Viera", Tokens(row, toggleOff).Race);
    }

    [Fact]
    public void ApplicableRow_IsIndependentOfEverySwitch()
    {
        Assert.Equal(RaceRows.Hair, RaceSplitRouting.ApplicableRow(Row(ModCategory.Hair)));
        Assert.Equal(RaceRows.Face, RaceSplitRouting.ApplicableRow(Row(ModCategory.Face, keys: Keys())));
        Assert.Equal(RaceRows.Skin, RaceSplitRouting.ApplicableRow(Row(ModCategory.Skin)));
        Assert.Equal(
            RaceRows.TailsEarsHorns,
            RaceSplitRouting.ApplicableRow(Row(ModCategory.Body, part: BodyPart.TailsEarsHorns)));
        Assert.Equal(RaceRows.RacialScaling, RaceSplitRouting.ApplicableRow(Row(ModCategory.Gear, scaling: true)));
        Assert.Null(RaceSplitRouting.ApplicableRow(Row(ModCategory.Gear)));
    }

    [Fact]
    public void ApplicableRow_ForAnIrisOnlyFaceMod_IsEyes()
    {
        var iris = new RowRaceData(S(), S("Viera"), S(), S(), true);
        Assert.Equal(RaceRows.Eyes, RaceSplitRouting.ApplicableRow(Row(ModCategory.Face, keys: iris)));
        Assert.Equal("Viera", Tokens(Row(ModCategory.Face, keys: iris)).Race);
    }
}
