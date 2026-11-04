namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The body-type segment of a folder. The point of the feature is a GEAR mod filed under the body
/// it was made for, with its equipment slot below that.
/// </summary>
public class ModTypeFoldersBodyTypeTests
{
    [Fact]
    public void GetFolder_GearWithBodyAndSlot_PutsTheBodyAboveTheSlot()
    {
        // The whole shape of the feature in one assertion: one body's gear gathers in one place
        // instead of scattering across the nine slot folders.
        Assert.Equal("Gear/Bibo+/Top", ModTypeFolders.GetFolder(ModCategory.Gear, "Top", "Bibo+"));
        Assert.Equal("Gear/Bibo+/Legs", ModTypeFolders.GetFolder(ModCategory.Gear, "Legs", "Bibo+"));
    }

    [Fact]
    public void GetFolder_GearWithBodyAndNoSlot_IsBodyOnly() =>
        // Slot split off, body split on.
        Assert.Equal("Gear/Bibo+", ModTypeFolders.GetFolder(ModCategory.Gear, null, "Bibo+"));

    [Fact]
    public void GetFolder_GearWithSlotAndNoBody_IsUnchangedFromBefore() =>
        // The pre-existing behaviour must survive untouched: body split off changes nothing.
        Assert.Equal("Gear/Top", ModTypeFolders.GetFolder(ModCategory.Gear, "Top", null));

    [Fact]
    public void GetFolder_BodyCategoryWithBodyType_IsOneLevel() =>
        Assert.Equal("Body/Bibo+", ModTypeFolders.GetFolder(ModCategory.Body, null, "Bibo+"));

    [Fact]
    public void GetFolder_BodyCategoryWithoutBodyType_IsFlatBody() =>
        Assert.Equal("Body", ModTypeFolders.GetFolder(ModCategory.Body, null, null));

    [Theory]
    [InlineData("Bibo+", "Gear/Bibo+/Top")]
    [InlineData("YAB Buff", "Gear/YAB Buff/Top")]
    [InlineData("Tight&Firm", "Gear/Tight&Firm/Top")]
    [InlineData("TBSE-X", "Gear/TBSE-X/Top")]
    [InlineData("FreyjaAF", "Gear/FreyjaAF/Top")]
    public void GetFolder_RealBodyLabels_AllSurvive(string bodyType, string expected) =>
        Assert.Equal(expected, ModTypeFolders.GetFolder(ModCategory.Gear, "Top", bodyType));

    [Theory]
    [InlineData(ModCategory.Hair)]
    [InlineData(ModCategory.Skin)]
    [InlineData(ModCategory.Face)]
    [InlineData(ModCategory.NPC)]
    [InlineData(ModCategory.Animation)]
    [InlineData(ModCategory.Furniture)]
    public void GetFolder_BodyTypeOnAnIneligibleCategory_Throws(ModCategory category) =>
        // Only Gear and Body are matched for a body type, so one arriving on any other category is
        // a bug, not a folder. This is what stops an "Animation/Bibo+" from ever existing.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModTypeFolders.GetFolder(category, null, "Bibo+"));

    [Theory]
    [InlineData("Bibo+/Nested")]     // would escape the body folder
    [InlineData("Bibo+\\Nested")]
    [InlineData(" Leading")]
    [InlineData("Trailing ")]
    [InlineData("")]
    [InlineData("Bad*Char")]
    [InlineData("Bad:Char")]
    public void GetFolder_MalformedBodyType_Throws(string bodyType) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModTypeFolders.GetFolder(ModCategory.Gear, "Top", bodyType));

    [Fact]
    public void GetFolder_OverLongBodyType_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModTypeFolders.GetFolder(ModCategory.Gear, "Top", new string('x', 41)));

    [Fact]
    public void GetFolder_InvalidSlotStillThrows_EvenWithAValidBody() =>
        // The subcategory is validated by the two-argument overload first, so adding a body type
        // cannot smuggle a nonsense slot past it.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModTypeFolders.GetFolder(ModCategory.Gear, "Bosses", "Bibo+"));

    [Fact]
    public void GetFolder_ArbitraryWordAsBodyType_IsAcceptedByShape()
    {
        // Pins the LIMIT of the shape check rather than pretending it is stricter. A real body
        // label is an arbitrary word, so this cannot reject one; membership is guaranteed upstream,
        // where only a match against the loaded list ever sets a row's BodyType.
        Assert.Equal("Gear/Bosses/Top", ModTypeFolders.GetFolder(ModCategory.Gear, "Top", "Bosses"));
    }
}
