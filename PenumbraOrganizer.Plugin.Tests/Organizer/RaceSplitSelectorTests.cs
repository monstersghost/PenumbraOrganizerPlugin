using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class RaceSplitSelectorTests
{
    private static ISet<string> S(params string[] v) => new HashSet<string>(v, StringComparer.Ordinal);

    private static RaceSplitOptions Race(
        RaceRows rows = RaceRows.All, bool toMulti = true, string[]? ranked = null) =>
        new(true, rows, toMulti, RacePriority.Normalize(ranked ?? []));

    private static OrganizerModRow Row(
        ModCategory category, RowRaceData? keys = null, BodyPart part = BodyPart.Replacer,
        bool racialScaling = false, RspEvidence? rsp = null) =>
        new()
        {
            Identifier = "m", Name = "Mod", Author = "A", CurrentPath = "Mod", ProposedPath = "Mod",
            Category = category, KeyRaces = keys, BodyPart = part, IsRacialScaling = racialScaling, Rsp = rsp,
        };

    private static RowRaceData Keys(
        string[]? face = null, string[]? iris = null, string[]? skin = null, string[]? tail = null,
        bool irisOnly = false) =>
        new(S(face ?? []).ToHashSet(), S(iris ?? []).ToHashSet(), S(skin ?? []).ToHashSet(),
            S(tail ?? []).ToHashSet(), irisOnly);

    private static string Path(OrganizerModRow row, RaceSplitOptions? race, bool splitBodyMods = true)
    {
        var options = new LayoutOptions(false, race?.InEffect(RaceRows.Hair, splitBodyMods) ?? false, false, race);
        var layout = SortFolderSelectors.BuildBodyLayout([row], splitBodyMods, null);
        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, splitGear: false, splitNpc: true, row,
            splitBodyMods: splitBodyMods, bodyLayout: layout, options: options);
        return primary!;
    }

    [Fact]
    public void NoRaceOptions_ChangesNothing()
    {
        Assert.Equal("Face", Path(Row(ModCategory.Face, Keys(face: ["Viera"])), null));
        Assert.Equal("Skin", Path(Row(ModCategory.Skin, Keys(skin: ["Viera"])), null));
    }

    [Fact]
    public void MasterOff_ChangesNothing() =>
        Assert.Equal("Face", Path(Row(ModCategory.Face, Keys(face: ["Viera"])), Race() with { Enabled = false }));

    [Fact]
    public void Face_OneRace_FilesUnderTheRace() =>
        Assert.Equal("Face/Viera", Path(Row(ModCategory.Face, Keys(face: ["Viera"])), Race()));

    [Fact]
    public void Face_SeveralRaces_ToggleOn_GoesToMultiRace() =>
        Assert.Equal("Face/Multi-race", Path(Row(ModCategory.Face, Keys(face: ["Viera", "Elezen"])), Race()));

    [Fact]
    public void Face_SeveralRaces_ToggleOff_PicksTheTopRankedRace() =>
        Assert.Equal(
            "Face/Elezen",
            Path(Row(ModCategory.Face, Keys(face: ["Viera", "Elezen"])), Race(toMulti: false, ranked: ["Elezen"])));

    [Fact]
    public void Face_RowUnticked_ChangesNothing() =>
        Assert.Equal("Face", Path(Row(ModCategory.Face, Keys(face: ["Viera"])), Race(RaceRows.Skin)));

    [Fact]
    public void RowScannedBeforeUpgrade_HasNoEvidenceAndFilesUnderMultiRace() =>
        Assert.Equal("Face/Multi-race", Path(Row(ModCategory.Face, keys: null), Race()));

    [Fact]
    public void Skin_OneRace() =>
        Assert.Equal("Skin/Miqo'te", Path(Row(ModCategory.Skin, Keys(skin: ["Miqo'te"])), Race()));

    [Fact]
    public void IrisOnly_EyesRowInEffect_FilesUnderFaceEyesRace() =>
        Assert.Equal(
            "Face/Eyes/Viera",
            Path(Row(ModCategory.Face, Keys(iris: ["Viera"], irisOnly: true)), Race()));

    [Fact]
    public void IrisOnly_EyesRowOff_FaceRowOn_StaysInFaceAndUsesTheIrisRaces() =>
        Assert.Equal(
            "Face/Viera",
            Path(Row(ModCategory.Face, Keys(iris: ["Viera"], irisOnly: true)), Race(RaceRows.Face)));

    [Fact]
    public void MixedFaceAndIris_StaysInFaceAndFollowsTheFaceRow() =>
        Assert.Equal(
            "Face/Elezen",
            Path(Row(ModCategory.Face, Keys(face: ["Elezen"], iris: ["Viera"], irisOnly: false)), Race()));

    [Fact]
    public void TailsEarsHorns_FilesUnderTheSharedPartThenTheRace() =>
        Assert.Equal(
            "Body/02. Tails, Ears and Horns/Viera",
            Path(Row(ModCategory.Body, Keys(tail: ["Viera"]), BodyPart.TailsEarsHorns), Race()));

    [Fact]
    public void TailsEarsHorns_NeedsTheBodySplit()
    {
        var row = Row(ModCategory.Body, Keys(tail: ["Viera"]), BodyPart.TailsEarsHorns);
        Assert.Equal("Body", Path(row, Race(), splitBodyMods: false));
    }

    [Fact]
    public void BodyReplacer_IsNeverRaceSplit()
    {
        var row = Row(ModCategory.Body, Keys(tail: ["Viera"]), BodyPart.Replacer);
        Assert.Null(RaceSplitRouting.Resolve(row, Race(), bodySplit: true));
        Assert.Null(RaceSplitRouting.Tail(row, Race(), bodySplit: true));
    }

    private static RspEvidence Rsp(bool pure = true, bool complete = true, string[]? races = null) =>
        new(S(races ?? []).ToHashSet(), complete, pure);

    [Fact]
    public void RacialScaling_RowOn_RoutesGearRowUnderBodyThenTheRace() =>
        Assert.Equal(
            "Body/03. Racial Scaling/Miqo'te",
            Path(Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te"])), Race()));

    [Fact]
    public void RacialScaling_RowOff_StaysInGearExactlyAsToday() =>
        Assert.Equal(
            "Gear",
            Path(Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te"])), Race(RaceRows.Hair)));

    [Fact]
    public void RacialScaling_MasterOff_StaysInGear() =>
        Assert.Equal(
            "Gear",
            Path(Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te"])), RaceSplitOptions.Off));

    [Fact]
    public void RacialScaling_WithoutTheBodySplit_StaysInGear() =>
        Assert.Equal(
            "Gear",
            Path(Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te"])), Race(), splitBodyMods: false));

    [Fact]
    public void RacialScaling_TwoRaces_ToggleOn_MultiRace() =>
        Assert.Equal(
            "Body/03. Racial Scaling/Multi-race",
            Path(Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te", "Viera"])), Race()));

    [Fact]
    public void RacialScaling_IncompleteEvidence_IsMultiRaceEvenWithPriorityAndToggleOff() =>
        Assert.Equal(
            "Body/03. Racial Scaling/Multi-race",
            Path(
                Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(complete: false, races: ["Viera"])),
                Race(toMulti: false, ranked: ["Viera"])));

    [Fact]
    public void RacialScaling_DoesNotShiftBodyNumbers()
    {
        var scaling = Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te"]));
        var layoutWith = SortFolderSelectors.BuildBodyLayout([scaling], true, null)!;
        var layoutWithout = SortFolderSelectors.BuildBodyLayout([], true, null)!;
        Assert.Equal(layoutWithout.BodyNumbers, layoutWith.BodyNumbers);
    }

    [Fact]
    public void HairWithToggleOff_UsesTheResolverThroughTheSelector()
    {
        var row = new OrganizerModRow
        {
            Identifier = "h", Name = "Hair", Author = "A", CurrentPath = "Hair", ProposedPath = "Hair",
            Category = ModCategory.Hair,
            HairFlat = new HashSet<int> { 115, 105 },
            HairByRaceCode = new Dictionary<string, IReadOnlySet<int>>
            {
                ["1801"] = new HashSet<int> { 115 }, ["0801"] = new HashSet<int> { 105 },
            },
        };
        var race = Race(toMulti: false, ranked: ["Viera"]);
        var options = new LayoutOptions(false, true, false, race);

        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, false, true, row, splitHair: false, options: options);

        Assert.Equal("Hair/Viera", primary);
    }

    [Fact]
    public void BodyPart_RacialScaling_IsSharedAndNamed()
    {
        Assert.True(BodyFolderLayout.IsShared(BodyPart.RacialScaling));
        Assert.Equal("03. Racial Scaling", BodyFolderLayout.PartFolder(BodyPart.RacialScaling));
        Assert.Equal("Racial Scaling", BodyPartFolders.Folder(BodyPart.RacialScaling));
    }

    [Fact]
    public void Resolve_ReportsTheReasonForADiagnostic()
    {
        var row = Row(ModCategory.Face, Keys(face: ["Viera", "Elezen"]));
        var result = RaceSplitRouting.Resolve(row, Race(), bodySplit: true);
        Assert.Equal(RaceFolderReason.SeveralToMultiRace, result!.Reason);
        Assert.Null(RaceSplitRouting.Resolve(row, null, bodySplit: true));
        Assert.Null(RaceSplitRouting.Resolve(Row(ModCategory.Weapon), Race(), bodySplit: true));
    }
}
