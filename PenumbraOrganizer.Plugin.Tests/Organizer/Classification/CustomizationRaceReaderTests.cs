using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class CustomizationRaceReaderTests
{
    private static RowRaceData From(params string[] keys) =>
        CustomizationRaceReader.From(keys.Select(ChangedItemKeyParser.Parse));

    [Fact]
    public void FaceKeys_GoToFaceNotIris()
    {
        var data = From("Customization: Viera Female Face 3", "Customization: Viera Female Face (Iris) 3");
        Assert.Equal(new[] { "Viera" }, data.Face);
        Assert.Equal(new[] { "Viera" }, data.Iris);
        Assert.False(data.IrisOnly);
    }

    [Fact]
    public void IrisOnlyMod_IsFlaggedAndHasNoFaceRaces()
    {
        var data = From("Customization: Midlander Male Face (Iris) 219");
        Assert.True(data.IrisOnly);
        Assert.Empty(data.Face);
        Assert.Equal(new[] { "Midlander" }, data.Iris);
    }

    [Fact]
    public void IncidentalSkinKeys_NeverAddToFaceRaces()
    {
        var data = From("Customization: Viera Female Face 3", "Customization: Au Ra Female Skin Textures");
        Assert.Equal(new[] { "Viera" }, data.Face);
        Assert.Equal(new[] { "Au Ra" }, data.Skin);
    }

    [Fact]
    public void SkinTexturesKey_IsMatchedByContainingSkin() =>
        Assert.Equal(new[] { "Midlander" }, From("Customization: Midlander Female Skin Textures").Skin);

    [Fact]
    public void TailAndEarsKeys_GoToTailEars()
    {
        var data = From("Customization: Miqo'te Male Tail 4", "Customization: Viera Female Ears 2");
        Assert.Equal(new[] { "Miqo'te", "Viera" }, data.TailEars.OrderBy(r => r, StringComparer.Ordinal));
    }

    [Fact]
    public void KeyWithNoRace_IsNoEvidenceButStillCountsForIrisOnly()
    {
        var data = From("Customization: Player Face (Iris) 3");
        Assert.Empty(data.Iris);
        Assert.True(data.IrisOnly);
    }

    [Fact]
    public void UnknownRaceWord_IsIgnored() =>
        Assert.Empty(From("Customization: Hyur Male Face 3").Face);

    [Fact]
    public void NonCustomizationKeys_AreIgnored()
    {
        var data = From("Midlander Bust Minimum X-Axis", "Emote: Dance 74");
        Assert.Empty(data.Face);
        Assert.Empty(data.Skin);
        Assert.False(data.IrisOnly);
    }
}
