using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class ModTypeClassifierTests
{
    [Fact] // "Carlotta's Outfit": 30 gear items + one incidental mount — Gear wins
    public void Classify_GearBeatsIncidentalMount()
    {
        var result = ModTypeClassifier.Classify(
            ["Appointed Gloves", "Archon Throne (Mount)", "Animation"]);

        Assert.Equal(ModCategory.Gear, result.Category);
        Assert.Null(result.SubCategory);
    }

    [Fact] // "Kigu - Face 001": customization keys + one bare item — Gear wins
    public void Classify_GearBeatsCustomization()
    {
        var result = ModTypeClassifier.Classify(
            ["Customization: Lalafell Female Face 1", "Moogle Legs"]);

        Assert.Equal(ModCategory.Gear, result.Category);
    }

    [Fact] // "Yacht_V1.0": Animation + Sound + mount key, no gear — Mount
    public void Classify_PureMountMod_IsMount()
    {
        var result = ModTypeClassifier.Classify(
            ["Ancient Airship (Mount)", "Animation", "Sound"]);

        Assert.Equal(ModCategory.Mount, result.Category);
    }

    [Fact] // "Red-Footed Booby": Battle NPC + Companion pair — Minion
    public void Classify_MinionSuffixes_AreMinion()
    {
        var result = ModTypeClassifier.Classify(
            ["Blue-footed Booby (Battle NPC)", "Blue-footed Booby (Companion)"]);

        Assert.Equal(ModCategory.Minion, result.Category);
    }

    [Fact] // Mount beats Minion when both present and no gear
    public void Classify_MountBeatsMinion()
    {
        var result = ModTypeClassifier.Classify(
            ["Spectral Statice (Mount)", "Ghido (Companion)"]);

        Assert.Equal(ModCategory.Mount, result.Category);
    }

    [Fact] // NPC-only mod (hypothetical isolation of the Smallclothes shape)
    public void Classify_NpcSuffix_IsNpc()
    {
        var result = ModTypeClassifier.Classify(["Smallclothes (NPC, 9903-1, Body)"]);

        Assert.Equal(ModCategory.NPC, result.Category);
    }

    [Fact] // "[Bard Lb3] Pashupata": Action + Animation + Vfx — Battle Animation
    public void Classify_ActionKey_IsBattleAnimation()
    {
        var result = ModTypeClassifier.Classify(
            ["Action: Arrow of Fortitude", "Animation", "Vfx"]);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Battle Animation", result.SubCategory);
    }

    [Fact] // "Toothless Dance": Emote + Sound — Emotes
    public void Classify_EmoteKey_IsEmotes()
    {
        var result = ModTypeClassifier.Classify(["Emote: Bee's Knees", "Sound"]);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Emotes", result.SubCategory);
    }

    [Fact] // Vfx + Animation, no Action/Emote — ambiguous, Other
    public void Classify_VfxAndAnimationTogether_IsOther()
    {
        var result = ModTypeClassifier.Classify(["Animation", "Vfx"]);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Other", result.SubCategory);
    }

    [Fact] // solo Vfx — VFX
    public void Classify_VfxAlone_IsVfx()
    {
        var result = ModTypeClassifier.Classify(["Vfx"]);

        Assert.Equal(ModCategory.VFX, result.Category);
        Assert.Equal("VFX", result.SubCategory);
    }

    [Fact] // "[NX] Thicc Viera Walkin For All F": bare Animation only
    public void Classify_AnimationAlone_IsAnimation()
    {
        var result = ModTypeClassifier.Classify(["Animation"]);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Animation", result.SubCategory);
    }

    [Fact] // "cleaned up phasmascapes": single Housing literal — Furniture
    public void Classify_Housing_IsFurniture()
    {
        var result = ModTypeClassifier.Classify(["Housing"]);

        Assert.Equal(ModCategory.Furniture, result.Category);
    }

    [Fact] // Sound alone — Sound
    public void Classify_SoundAlone_IsSound()
    {
        var result = ModTypeClassifier.Classify(["Sound"]);

        Assert.Equal(ModCategory.Sound, result.Category);
    }

    [Fact] // "Akako's Files 3.1.1": Face+Hair+Skin+Tail body parts — Face wins
    public void Classify_CustomizationFaceBeatsHairBodySkin()
    {
        var result = ModTypeClassifier.Classify(
        [
            "Customization: Miqo'te Female Face 101",
            "Customization: Miqo'te Female Hair 115",
            "Customization: Miqo'te Female Skin Textures",
            "Customization: Miqo'te Female Tail 3",
        ]);

        Assert.Equal(ModCategory.Face, result.Category);
    }

    [Fact] // "tail": Tail + Skin Textures — Body wins over Skin
    public void Classify_CustomizationTailBeatsSkin()
    {
        var result = ModTypeClassifier.Classify(
        [
            "Customization: Miqo'te Female Skin Textures",
            "Customization: Miqo'te Female Tail 3",
        ]);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // "akako skin": skin textures only — Skin
    public void Classify_CustomizationSkinOnly_IsSkin()
    {
        var result = ModTypeClassifier.Classify(
        [
            "Customization: Midlander Female Skin Textures",
            "Customization: Player Skin Textures",
        ]);

        Assert.Equal(ModCategory.Skin, result.Category);
    }

    [Fact] // "Akako's Glowy Eyes": Face + literal Unknown — Unknown key doesn't block Face
    public void Classify_CustomizationUnknownKeyDoesNotBlockOthers()
    {
        var result = ModTypeClassifier.Classify(
        [
            "Customization: Miqo'te Female Face (Iris) 101",
            "Customization: Unknown",
        ]);

        Assert.Equal(ModCategory.Face, result.Category);
    }

    [Fact] // only unrecognizable customization — Unknown
    public void Classify_OnlyUnknownCustomization_IsUnknown()
    {
        var result = ModTypeClassifier.Classify(["Customization: Unknown"]);

        Assert.Equal(ClassificationResult.Unknown, result);
    }

    [Fact] // "higanbana [bibo]": empty key set — Unknown
    public void Classify_EmptyKeys_IsUnknown()
    {
        var result = ModTypeClassifier.Classify([]);

        Assert.Equal(ClassificationResult.Unknown, result);
    }

    [Fact] // Icon: alone (never observed with no companion key) — Unknown, never a guess
    public void Classify_IconAlone_IsUnknown()
    {
        var result = ModTypeClassifier.Classify(["Icon: 42992"]);

        Assert.Equal(ClassificationResult.Unknown, result);
    }

    [Theory]
    [InlineData(ModCategory.Gear, null, "Gear")]
    [InlineData(ModCategory.NPC, null, "NPC")]
    [InlineData(ModCategory.Animation, "Battle Animation", "Animation and VFX/Battle Animation")]
    [InlineData(ModCategory.VFX, "VFX", "Animation and VFX/VFX")]
    public void GetFolder_MapsCategoryAndSubCategory(ModCategory category, string? sub, string expected)
    {
        Assert.Equal(expected, ModTypeFolders.GetFolder(category, sub));
    }
}
