using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Layouts;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Layouts;

public class LayoutOverridesReadTests
{
    private static CategoryLayout Recommended(ModCategory category) => LayoutProfile.Recommended[category];

    [Fact]
    public void NoList_ReadsAsNoOverrides()
    {
        Assert.Empty(LayoutOverrides.Read(null));
        Assert.Empty(LayoutOverrides.Read([]));
    }

    [Fact]
    public void AValidEntry_IsReadAsWritten()
    {
        var read = LayoutOverrides.Read(
        [
            new() { Category = "Gear", Folder = "Equipment/Gear", Creator = "AfterType", Splits = ["BodyType", "Slot"] },
        ]);

        Assert.Equal(
            new CategoryLayout("Equipment/Gear", CreatorLevel.AfterType, LayoutSplits.BodyType | LayoutSplits.Slot),
            read[ModCategory.Gear]);
    }

    [Fact]
    public void NullEntries_AreSkipped()
    {
        var read = LayoutOverrides.Read(
        [
            null!,
            new() { Category = "Gear", Folder = "Kit", Splits = ["Slot"] },
            null!,
        ]);

        Assert.Equal([ModCategory.Gear], read.Keys);
        Assert.Equal(new CategoryLayout("Kit", null, LayoutSplits.Slot), read[ModCategory.Gear]);
    }

    [Fact]
    public void AMisspelledCategory_IsIgnored_AndTheValidGearEntryNextToItIsKept()
    {
        var read = LayoutOverrides.Read(
        [
            new() { Category = "Gaer", Folder = "Typo", Splits = [] },
            new() { Category = "Gear", Folder = "Kit", Splits = ["Slot"] },
            new() { Category = "gear", Folder = "Lowercase", Splits = [] },
        ]);

        Assert.Equal([ModCategory.Gear], read.Keys);
        Assert.Equal(new CategoryLayout("Kit", null, LayoutSplits.Slot), read[ModCategory.Gear]);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("0")]
    [InlineData("Gear, Hair")]
    [InlineData("Nonsense")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownCategoryNames_AreIgnored(string? name) =>
        Assert.Empty(LayoutOverrides.Read([new() { Category = name!, Folder = "X", Splits = [] }]));

    [Fact]
    public void ARepeatedCategory_KeepsTheFirstEntry()
    {
        var read = LayoutOverrides.Read(
        [
            new() { Category = "Gear", Folder = "First", Splits = ["Slot"] },
            new() { Category = "Gear", Folder = "Second", Splits = ["BodyType"] },
        ]);

        Assert.Equal(new CategoryLayout("First", null, LayoutSplits.Slot), read[ModCategory.Gear]);
    }

    [Fact]
    public void NullSplits_TakeRecommendedsSplits_NotAllOff()
    {
        var read = LayoutOverrides.Read(
        [
            new() { Category = "Hair", Folder = "Looks/Hair", Splits = null },
            new() { Category = "Gear", Folder = "Kit", Splits = null },
        ]);

        Assert.Equal(Recommended(ModCategory.Hair) with { Folder = "Looks/Hair" }, read[ModCategory.Hair]);
        Assert.Equal(Recommended(ModCategory.Gear) with { Folder = "Kit" }, read[ModCategory.Gear]);
    }

    [Fact]
    public void AnEmptySplitsList_IsAllOff()
    {
        var read = LayoutOverrides.Read([new() { Category = "Gear", Folder = "Gear", Splits = [] }]);
        Assert.Equal(new CategoryLayout("Gear", null, LayoutSplits.None), read[ModCategory.Gear]);
    }

    [Fact]
    public void MissingSplitsInTheSavedFile_TakeRecommendedsSplits()
    {
        const string json = "[{\"Category\":\"Hair\",\"Folder\":\"Looks/Hair\"},{\"Category\":\"Gear\",\"Folder\":\"Kit\",\"Splits\":null}]";

        var fromSystemText = System.Text.Json.JsonSerializer.Deserialize<List<CategoryLayoutEntry>>(json)!;
        var fromNewtonsoft = Newtonsoft.Json.JsonConvert.DeserializeObject<List<CategoryLayoutEntry>>(json)!;

        foreach (var entries in new[] { fromSystemText, fromNewtonsoft })
        {
            var read = LayoutOverrides.Read(entries);
            Assert.Equal(Recommended(ModCategory.Hair).Splits, read[ModCategory.Hair].Splits);
            Assert.Equal(Recommended(ModCategory.Gear).Splits, read[ModCategory.Gear].Splits);
        }
    }

    [Fact]
    public void UnknownAndIllegalSplitNames_AreDropped()
    {
        var read = LayoutOverrides.Read(
            [new() { Category = "Gear", Folder = "Gear", Splits = ["Slot", "Emote", "Bogus", "slot", "BodyType, Slot"] }]);
        Assert.Equal(LayoutSplits.Slot, read[ModCategory.Gear].Splits);
    }

    [Theory]
    [InlineData("None", CreatorLevel.None)]
    [InlineData("BeforeType", CreatorLevel.BeforeType)]
    [InlineData("AfterType", CreatorLevel.AfterType)]
    [InlineData("Sideways", null)]
    [InlineData("1", null)]
    [InlineData("aftertype", null)]
    [InlineData(null, null)]
    public void TheCreatorLevel_IsReadByMemberName_AndAnythingElseFollowsGroupBy(string? raw, CreatorLevel? expected) =>
        Assert.Equal(expected, LayoutOverrides.Read([new() { Category = "NPC", Folder = "NPC", Creator = raw }])[ModCategory.NPC].Creator);

    [Theory]
    [InlineData("../Up")]
    [InlineData("C:/Gear")]
    [InlineData("Mods/{creator}")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void AnInvalidFolder_FallsBackToTheDefaultFolder(string? folder) =>
        Assert.Equal("Gear", LayoutOverrides.Read([new() { Category = "Gear", Folder = folder!, Splits = [] }])[ModCategory.Gear].Folder);

    [Theory]
    [InlineData(@" Looks\Hair ", "Looks/Hair")]
    [InlineData(" Looks / Hair ", "Looks/Hair")]
    [InlineData("/Looks/Hair/", "Looks/Hair")]
    public void AValidFolder_IsStoredNormalized(string folder, string expected) =>
        Assert.Equal(expected, LayoutOverrides.Read([new() { Category = "Hair", Folder = folder, Splits = [] }])[ModCategory.Hair].Folder);

    [Theory]
    [InlineData("Gear", "gear", "Gear")]
    [InlineData("Gear", " GEAR ", "Gear")]
    [InlineData("VFX", "animation and vfx/vfx", "Animation and VFX/VFX")]
    [InlineData("VFX", "Animation And VFX / VFX", "Animation and VFX/VFX")]
    [InlineData("Animation", "ANIMATION AND VFX", "Animation and VFX")]
    [InlineData("Others", "others", "Others")]
    public void ACaseOnlyChange_IsNotARename_AndReadsAsTheDefaultFolder(string category, string folder, string expected) =>
        Assert.Equal(expected, LayoutOverrides.Read([new() { Category = category, Folder = folder, Splits = [] }])
            .Single().Value.Folder);

    [Fact]
    public void ACaseChangeOfAnotherCategorysFolder_IsStillARename()
    {
        // "review" is workbook code 16's name but not where the Sort files Others, so it is a real rename.
        var read = LayoutOverrides.Read([new() { Category = "Others", Folder = "review", Splits = [] }]);
        Assert.Equal("review", read[ModCategory.Others].Folder);
    }
}
