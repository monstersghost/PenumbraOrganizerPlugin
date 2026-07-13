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
}
