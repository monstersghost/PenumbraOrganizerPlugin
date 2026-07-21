using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class ChangedItemKeyParserTests
{
    [Theory]
    [InlineData("Emote: Sit on Ground", "Sit on Ground")]
    [InlineData("Emote: 地面に座る", "地面に座る")]
    public void Parse_EmotePrefix_YieldsEmoteShape(string key, string expectedName)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Emote, result.Shape);
        Assert.Equal(expectedName, result.ItemName);
        Assert.Equal(key, result.Raw);
    }

    [Theory]
    [InlineData("Action: Radiant Aegis", "Radiant Aegis")]
    [InlineData("Action: Hissatsu: Guren", "Hissatsu: Guren")]
    [InlineData("Action: 大鷹", "大鷹")]
    public void Parse_ActionPrefix_YieldsActionShape(string key, string expectedName)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Action, result.Shape);
        Assert.Equal(expectedName, result.ItemName);
    }

    [Fact]
    public void Parse_IconPrefix_YieldsIconShape()
    {
        var result = ChangedItemKeyParser.Parse("Icon: 42992");

        Assert.Equal(ChangedItemKeyShape.Icon, result.Shape);
        Assert.Equal("Icon: 42992", result.Raw);
    }

    [Theory]
    [InlineData("Animation")]
    [InlineData("Vfx")]
    [InlineData("Sound")]
    [InlineData("Housing")]
    public void Parse_BareCategoryWord_YieldsCategoryLiteralShape(string key)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.CategoryLiteral, result.Shape);
        Assert.Equal(key, result.CategoryLiteral);
    }

    [Theory]
    [InlineData("Ancient Airship (Mount)", "Ancient Airship")]
    [InlineData("古式魔道船 (Mount)", "古式魔道船")]
    public void Parse_MountSuffix_YieldsMountShape(string key, string expectedName)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Mount, result.Shape);
        Assert.Equal(expectedName, result.ItemName);
    }

    [Theory]
    [InlineData("Beady Eye (Battle NPC)", "Beady Eye")]
    [InlineData("Blue-footed Booby (Companion)", "Blue-footed Booby")]
    [InlineData("Stray Gaelicat (Event NPC)", "Stray Gaelicat")]
    [InlineData("タイニーアイ (Companion)", "タイニーアイ")]
    public void Parse_MinionSuffix_YieldsMinionShape(string key, string expectedName)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Minion, result.Shape);
        Assert.Equal(expectedName, result.ItemName);
    }

    [Fact]
    public void Parse_NpcSuffix_YieldsNpcShape()
    {
        var result = ChangedItemKeyParser.Parse("Smallclothes (NPC, 9903-1, Body)");

        Assert.Equal(ChangedItemKeyShape.Npc, result.Shape);
    }

    [Theory]
    [InlineData("Street Jacket")]
    [InlineData("Moonward Samurai Blade (Sheathe)")]  // parenthetical, but not a recognized suffix
    [InlineData("Dated Canvas Bottom (Auburn)")]       // color variant, still Gear
    [InlineData("Doman Iron Claws (Offhand)")]         // slot qualifier, still Gear
    [InlineData("エンペラーズ・ニューブリーチ")]
    public void Parse_BareItemName_YieldsGearShape(string key)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Gear, result.Shape);
        Assert.Equal(key, result.ItemName);
    }

    [Fact]
    public void Parse_CustomizationPrefix_YieldsCustomizationShape()
    {
        var result = ChangedItemKeyParser.Parse("Customization: Miqo'te Female Face 101");

        Assert.Equal(ChangedItemKeyShape.Customization, result.Shape);
    }

    [Theory]
    // key, race, gender, bodyPart, subtype, number
    [InlineData("Customization: Miqo'te Female Face 101", "Miqo'te", "Female", "Face", null, 101)]
    [InlineData("Customization: Miqo'te Female Face (Iris) 101", "Miqo'te", "Female", "Face", "Iris", 101)]
    [InlineData("Customization: Au Ra Female Body (Skeleton) 1", "Au Ra", "Female", "Body", "Skeleton", 1)]
    [InlineData("Customization: Midlander Female Hair (Accessory) 147", "Midlander", "Female", "Hair", "Accessory", 147)]
    [InlineData("Customization: Miqo'te Male Tail 4", "Miqo'te", "Male", "Tail", null, 4)]
    [InlineData("Customization: Midlander Female Skin Textures", "Midlander", "Female", "Skin Textures", null, null)]
    public void Parse_CustomizationPayload_ExtractsFields(
        string key, string? race, string? gender, string? bodyPart, string? subtype, int? number)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Customization, result.Shape);
        Assert.Equal(race, result.Race);
        Assert.Equal(gender, result.Gender);
        Assert.Equal(bodyPart, result.BodyPart);
        Assert.Equal(subtype, result.Subtype);
        Assert.Equal(number, result.Number);
    }

    [Theory]
    // "(Child)" is a race-variant marker that appears before the body part, not after like
    // every other subtype — confirmed real key shape (Leveilleur Lip Fix by soullesshusk).
    [InlineData("Customization: Elezen Female (Child) Face 201", "Elezen", "Female", "Face", "Child", 201)]
    [InlineData("Customization: Elezen Male (Child) Face 201", "Elezen", "Male", "Face", "Child", 201)]
    public void Parse_CustomizationChildVariant_ExtractsLeadingSubtype(
        string key, string? race, string? gender, string? bodyPart, string? subtype, int? number)
    {
        var result = ChangedItemKeyParser.Parse(key);

        Assert.Equal(ChangedItemKeyShape.Customization, result.Shape);
        Assert.Equal(race, result.Race);
        Assert.Equal(gender, result.Gender);
        Assert.Equal(bodyPart, result.BodyPart);
        Assert.Equal(subtype, result.Subtype);
        Assert.Equal(number, result.Number);
    }

    [Fact]
    public void Parse_CustomizationPlayerPayload_HasNoRaceOrGender()
    {
        var result = ChangedItemKeyParser.Parse("Customization: Player Skin Textures");

        Assert.Null(result.Race);
        Assert.Null(result.Gender);
        Assert.Equal("Skin Textures", result.BodyPart);
    }

    [Fact]
    public void Parse_CustomizationUnknownPayload_KeepsUnknownAsBodyPart()
    {
        var result = ChangedItemKeyParser.Parse("Customization: Unknown");

        Assert.Null(result.Race);
        Assert.Null(result.Gender);
        Assert.Equal("Unknown", result.BodyPart);
    }
}
