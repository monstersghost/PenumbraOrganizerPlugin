using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class ModTypeClassifierTests
{
    [Fact] // "Carlotta's Outfit": 30 gear items + one incidental mount — Gear wins
    public void Classify_GearBeatsIncidentalMount()
    {
        var result = ModTypeClassifier.Classify(
            "Carlotta's Outfit",
            ["Appointed Gloves", "Archon Throne (Mount)", "Animation"],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Gear, result.Category);
        Assert.Null(result.SubCategory);
    }

    [Fact] // "Kigu - Face 001": customization keys + one bare item — Gear wins
    public void Classify_GearBeatsCustomization()
    {
        var result = ModTypeClassifier.Classify(
            "Kigu - Face 001",
            ["Customization: Lalafell Female Face 1", "Moogle Legs"],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Gear, result.Category);
    }

    [Fact] // Bibo+-style body mesh mod: bare Smallclothes item — Body, not Gear
    public void Classify_SmallclothesAlone_IsBody()
    {
        var result = ModTypeClassifier.Classify("Bibo+ Body Mesh", ["Smallclothes"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
        Assert.Null(result.SubCategory);
    }

    [Theory] // Emperor's New Clothes body-slot pieces — each alone is Body
    [InlineData("The Emperor's New Hat")]
    [InlineData("The Emperor's New Robe")]
    [InlineData("The Emperor's New Gloves")]
    [InlineData("The Emperor's New Breeches")]
    [InlineData("The Emperor's New Boots")]
    public void Classify_EmperorsNewClothesBodySlotAlone_IsBody(string itemName)
    {
        var result = ModTypeClassifier.Classify("Test Mod", [itemName], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + real named Gear together — Body still wins (absolute override)
    public void Classify_SmallclothesBeatsRealGear()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Smallclothes", "Appointed Gloves"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + Face customization — Body still wins, not just a soft merge
    public void Classify_SmallclothesBeatsFaceCustomization()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Smallclothes", "Customization: Miqo'te Female Face 101"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + a Mount key — Body still wins
    public void Classify_SmallclothesBeatsMount()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Smallclothes", "Archon Throne (Mount)"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Smallclothes + an NPC-suffixed key — Body wins; accepted trade-off, NPC is deferred
    public void Classify_SmallclothesBeatsNpcSuffix()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Smallclothes", "Smallclothes (NPC, 9903-1, Legs)"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // Child race-variant customization is an unconditional NPC signal — no playable
            // character can be a child model, only NPCs use child-sized customization.
    public void Classify_ChildRaceVariantCustomization_IsNpc()
    {
        var result = ModTypeClassifier.Classify(
            "Leveilleur Lip Fix",
            ["Customization: Elezen Female (Child) Face 201", "Customization: Elezen Male (Child) Face 201"],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.NPC, result.Category);
        Assert.Null(result.SubCategory);
    }

    [Fact] // Excluded ENC accessory literal, deliberately not in the table — stays ordinary Gear
    public void Classify_EmperorsNewClothesAccessory_IsStillGear()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["The Emperor's New Earrings"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Gear, result.Category);
    }

    [Fact] // "Yacht_V1.0": Animation + Sound + mount key, no gear — Mount
    public void Classify_PureMountMod_IsMount()
    {
        var result = ModTypeClassifier.Classify(
            "Yacht_V1.0", ["Ancient Airship (Mount)", "Animation", "Sound"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Mount, result.Category);
    }

    [Fact] // "Red-Footed Booby": Battle NPC + Companion pair — Minion
    public void Classify_MinionSuffixes_AreMinion()
    {
        var result = ModTypeClassifier.Classify(
            "Red-Footed Booby",
            ["Blue-footed Booby (Battle NPC)", "Blue-footed Booby (Companion)"],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Minion, result.Category);
    }

    [Fact] // Mount beats Minion when both present and no gear
    public void Classify_MountBeatsMinion()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Spectral Statice (Mount)", "Ghido (Companion)"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Mount, result.Category);
    }

    [Fact] // NPC-only mod (hypothetical isolation of the Smallclothes shape)
    public void Classify_NpcSuffix_IsNpc()
    {
        var result = ModTypeClassifier.Classify(
            "Test Mod", ["Smallclothes (NPC, 9903-1, Body)"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.NPC, result.Category);
        Assert.Equal(ClassificationSource.Structural, result.Source);
    }

    [Fact] // "[Bard Lb3] Pashupata": Action + Animation + Vfx — Battle Animation
    public void Classify_ActionKey_IsBattleAnimation()
    {
        var result = ModTypeClassifier.Classify(
            "[Bard Lb3] Pashupata", ["Action: Arrow of Fortitude", "Animation", "Vfx"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Battle Animation", result.SubCategory);
    }

    [Fact] // "Toothless Dance": Emote + Sound — Emotes
    public void Classify_EmoteKey_IsEmotes()
    {
        var result = ModTypeClassifier.Classify(
            "Toothless Dance", ["Emote: Bee's Knees", "Sound"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Emotes", result.SubCategory);
    }

    [Fact] // Vfx + Animation, no Action/Emote — ambiguous, Other
    public void Classify_VfxAndAnimationTogether_IsOther()
    {
        var result = ModTypeClassifier.Classify("Test Mod", ["Animation", "Vfx"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Other", result.SubCategory);
    }

    [Fact] // solo Vfx — VFX
    public void Classify_VfxAlone_IsVfx()
    {
        var result = ModTypeClassifier.Classify("Test Mod", ["Vfx"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.VFX, result.Category);
        Assert.Equal("VFX", result.SubCategory);
    }

    [Fact] // "[NX] Thicc Viera Walkin For All F": bare Animation only
    public void Classify_AnimationAlone_IsAnimation()
    {
        var result = ModTypeClassifier.Classify(
            "[NX] Thicc Viera Walkin For All F", ["Animation"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Animation, result.Category);
        Assert.Equal("Animation", result.SubCategory);
    }

    [Fact] // "cleaned up phasmascapes": single Housing literal — Furniture
    public void Classify_Housing_IsFurniture()
    {
        var result = ModTypeClassifier.Classify("cleaned up phasmascapes", ["Housing"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Furniture, result.Category);
    }

    [Fact] // Sound alone — Sound
    public void Classify_SoundAlone_IsSound()
    {
        var result = ModTypeClassifier.Classify("Test Mod", ["Sound"], NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Sound, result.Category);
    }

    [Fact] // "Akako's Files 3.1.1": Face+Hair+Skin+Tail body parts — Face wins
    public void Classify_CustomizationFaceBeatsHairBodySkin()
    {
        var result = ModTypeClassifier.Classify(
            "Akako's Files 3.1.1",
            [
                "Customization: Miqo'te Female Face 101",
                "Customization: Miqo'te Female Hair 115",
                "Customization: Miqo'te Female Skin Textures",
                "Customization: Miqo'te Female Tail 3",
            ],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Face, result.Category);
    }

    [Fact] // "tail": Tail + Skin Textures — Body wins over Skin
    public void Classify_CustomizationTailBeatsSkin()
    {
        var result = ModTypeClassifier.Classify(
            "tail",
            [
                "Customization: Miqo'te Female Skin Textures",
                "Customization: Miqo'te Female Tail 3",
            ],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Body, result.Category);
    }

    [Fact] // "akako skin": skin textures only — Skin
    public void Classify_CustomizationSkinOnly_IsSkin()
    {
        var result = ModTypeClassifier.Classify(
            "akako skin",
            [
                "Customization: Midlander Female Skin Textures",
                "Customization: Player Skin Textures",
            ],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Skin, result.Category);
    }

    [Fact] // "Akako's Glowy Eyes": Face + literal Unknown — Unknown key doesn't block Face
    public void Classify_CustomizationUnknownKeyDoesNotBlockOthers()
    {
        var result = ModTypeClassifier.Classify(
            "Akako's Glowy Eyes",
            [
                "Customization: Miqo'te Female Face (Iris) 101",
                "Customization: Unknown",
            ],
            NpcNameMatcher.Empty);

        Assert.Equal(ModCategory.Face, result.Category);
    }

    [Fact] // only unrecognizable customization — Unknown
    public void Classify_OnlyUnknownCustomization_IsUnknown()
    {
        var result = ModTypeClassifier.Classify("Test Mod", ["Customization: Unknown"], NpcNameMatcher.Empty);

        Assert.Equal(ClassificationResult.Unknown, result);
    }

    [Fact] // "higanbana [bibo]": empty key set — Unknown
    public void Classify_EmptyKeys_IsUnknown()
    {
        var result = ModTypeClassifier.Classify("higanbana [bibo]", [], NpcNameMatcher.Empty);

        Assert.Equal(ClassificationResult.Unknown, result);
    }

    [Fact] // Icon: alone (never observed with no companion key) — Unknown, never a guess
    public void Classify_IconAlone_IsUnknown()
    {
        var result = ModTypeClassifier.Classify("Test Mod", ["Icon: 42992"], NpcNameMatcher.Empty);

        Assert.Equal(ClassificationResult.Unknown, result);
    }

    // --- New: name-heuristic behavior ---

    [Fact] // Confirmed real case: a Y'shtola-named mod with only generic customization keys —
           // structurally this would be Face, the name heuristic must override that to NPC.
    public void Classify_NameMatchOverridesCustomizationFace()
    {
        var matcher = new NpcNameMatcher(["Y'shtola"], [], []);

        var result = ModTypeClassifier.Classify(
            "Rhul of Cool: A Y'shtola Overhaul",
            ["Customization: Miqo'te Female Face 201", "Customization: Miqo'te Female Skin Textures"],
            matcher);

        Assert.Equal(ModCategory.NPC, result.Category);
        Assert.Equal("NPCs", result.SubCategory);
        Assert.Equal(ClassificationSource.NameHeuristic, result.Source);
    }

    [Fact] // Confirmed accepted trade-off: name match overrides even a real, structurally-correct
           // Gear classification (e.g. a shared-coat mod named after an NPC).
    public void Classify_NameMatchOverridesGear()
    {
        var matcher = new NpcNameMatcher(["Alphinaud"], [], []);

        var result = ModTypeClassifier.Classify(
            "Slightly Better Alphinaud", ["Didact's Coat (696-1)"], matcher);

        Assert.Equal(ModCategory.NPC, result.Category);
        Assert.Equal(ClassificationSource.NameHeuristic, result.Source);
    }

    [Fact] // Name match overrides the Smallclothes placeholder too — outranks even Rule 0.
    public void Classify_NameMatchOverridesSmallclothesPlaceholder()
    {
        var matcher = new NpcNameMatcher([], [], ["Titania"]);

        var result = ModTypeClassifier.Classify("Titania Smallclothes Replacer", ["Smallclothes"], matcher);

        Assert.Equal(ModCategory.NPC, result.Category);
        Assert.Equal("Bosses", result.SubCategory);
    }

    [Fact] // Name match overrides a structural NPC-suffix result too (still NPC, but the
           // subcategory and Source now reflect the heuristic, not the structural signal).
    public void Classify_NameMatchOverridesStructuralNpcSuffix()
    {
        var matcher = new NpcNameMatcher(["Zenos"], [], []);

        var result = ModTypeClassifier.Classify(
            "Zenos Custom NPC Body", ["Smallclothes (NPC, 9903-1, Body)"], matcher);

        Assert.Equal(ModCategory.NPC, result.Category);
        Assert.Equal("NPCs", result.SubCategory);
        Assert.Equal(ClassificationSource.NameHeuristic, result.Source);
    }

    [Fact] // A mod with zero GetChangedItems entries must still run the name check.
    public void Classify_EmptyChangedItemsStillChecksName()
    {
        var matcher = new NpcNameMatcher(["Y'shtola"], [], []);

        var result = ModTypeClassifier.Classify("Y'shtola Portrait", [], matcher);

        Assert.Equal(ModCategory.NPC, result.Category);
    }

    [Fact] // A non-matching mod's classification is completely unaffected by a non-empty matcher.
    public void Classify_NoNameMatch_StructuralClassificationUnaffected()
    {
        var matcher = new NpcNameMatcher(["Y'shtola", "Thancred"], ["Titania"], ["Zenos"]);

        var result = ModTypeClassifier.Classify("Carlotta's Outfit", ["Appointed Gloves"], matcher);

        Assert.Equal(ModCategory.Gear, result.Category);
        Assert.Equal(ClassificationSource.Structural, result.Source);
    }

    [Theory]
    [InlineData(ModCategory.Gear, null, "Gear")]
    [InlineData(ModCategory.NPC, null, "NPC")]
    [InlineData(ModCategory.Animation, "Battle Animation", "Animation and VFX/Battle Animation")]
    [InlineData(ModCategory.VFX, "VFX", "Animation and VFX/VFX")]
    [InlineData(ModCategory.NPC, "NPCs", "NPC/NPCs")]
    [InlineData(ModCategory.NPC, "Enemies", "NPC/Enemies")]
    [InlineData(ModCategory.NPC, "Bosses", "NPC/Bosses")]
    [InlineData(ModCategory.Gear, "Head", "Gear/Head")]
    [InlineData(ModCategory.Gear, "Top", "Gear/Top")]
    [InlineData(ModCategory.Gear, "Hands", "Gear/Hands")]
    [InlineData(ModCategory.Gear, "Legs", "Gear/Legs")]
    [InlineData(ModCategory.Gear, "Feet", "Gear/Feet")]
    [InlineData(ModCategory.Gear, "Ears", "Gear/Ears")]
    [InlineData(ModCategory.Gear, "Neck", "Gear/Neck")]
    [InlineData(ModCategory.Gear, "Wrists", "Gear/Wrists")]
    [InlineData(ModCategory.Gear, "Rings", "Gear/Rings")]
    public void GetFolder_MapsCategoryAndSubCategory(ModCategory category, string? sub, string expected)
    {
        Assert.Equal(expected, ModTypeFolders.GetFolder(category, sub));
    }

    [Fact]
    public void GetFolder_UnsupportedSubCategoryPairing_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModTypeFolders.GetFolder(ModCategory.Gear, "Bosses"));
    }

    [Fact]
    public void GetFolder_GearWithUnsupportedSubCategory_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModTypeFolders.GetFolder(ModCategory.Gear, "NotARealSlot"));
    }

    // --- EnrichGearSubCategory ---

    [Fact]
    public void EnrichGearSubCategory_GearResultWithOneSlot_AssignsSubCategory()
    {
        var baseResult = new ClassificationResult(ModCategory.Gear, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichGearSubCategory(baseResult, new HashSet<EquipmentSlot> { EquipmentSlot.Feet });

        Assert.Equal(ModCategory.Gear, result.Category);
        Assert.Equal("Feet", result.SubCategory);
    }

    [Fact]
    public void EnrichGearSubCategory_GearResultWithNullRead_LeavesSubCategoryNull()
    {
        var baseResult = new ClassificationResult(ModCategory.Gear, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichGearSubCategory(baseResult, null);

        Assert.Equal(ModCategory.Gear, result.Category);
        Assert.Null(result.SubCategory);
    }

    [Fact]
    public void EnrichGearSubCategory_GearResultWithEmptySet_LeavesSubCategoryNull()
    {
        var baseResult = new ClassificationResult(ModCategory.Gear, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichGearSubCategory(baseResult, new HashSet<EquipmentSlot>());

        Assert.Null(result.SubCategory);
    }

    [Fact]
    public void EnrichGearSubCategory_GearResultWithMultipleSlots_LeavesSubCategoryNull()
    {
        var baseResult = new ClassificationResult(ModCategory.Gear, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichGearSubCategory(
            baseResult, new HashSet<EquipmentSlot> { EquipmentSlot.Top, EquipmentSlot.Legs });

        Assert.Null(result.SubCategory);
    }

    [Fact]
    public void EnrichGearSubCategory_NonGearResult_ReturnedUnchangedRegardlessOfSlots()
    {
        var baseResult = new ClassificationResult(ModCategory.Face, null, ClassificationSource.Structural);

        var result = ModTypeClassifier.EnrichGearSubCategory(
            baseResult, new HashSet<EquipmentSlot> { EquipmentSlot.Feet });

        Assert.Equal(baseResult, result); // completely untouched — proves the gating is real
    }

    // --- ClassifyKeyFacet ---

    [Fact] // Real named Gear item, no placeholder match — plain Gear facet
    public void ClassifyKeyFacet_RealGearItem_IsGear()
    {
        var key = ChangedItemKeyParser.Parse("Appointed Gloves");
        Assert.Equal(ModCategory.Gear, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact] // Placeholder override applies at the per-key level too, not just mod-level Classify
    public void ClassifyKeyFacet_SmallclothesPlaceholder_IsBody()
    {
        var key = ChangedItemKeyParser.Parse("Smallclothes");
        Assert.Equal(ModCategory.Body, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact]
    public void ClassifyKeyFacet_MountSuffix_IsMount()
    {
        var key = ChangedItemKeyParser.Parse("Archon Throne (Mount)");
        Assert.Equal(ModCategory.Mount, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact]
    public void ClassifyKeyFacet_MinionSuffix_IsMinion()
    {
        var key = ChangedItemKeyParser.Parse("Wind-up Bahamut (Companion)");
        Assert.Equal(ModCategory.Minion, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact]
    public void ClassifyKeyFacet_NpcSuffix_IsNpc()
    {
        var key = ChangedItemKeyParser.Parse("Smallclothes (NPC, 9903-1, Legs)");
        Assert.Equal(ModCategory.NPC, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact] // (Child) race-variant customization is an unconditional NPC signal
    public void ClassifyKeyFacet_ChildCustomization_IsNpc()
    {
        var key = ChangedItemKeyParser.Parse("Customization: Elezen Female (Child) Face 201");
        Assert.Equal(ModCategory.NPC, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Theory]
    [InlineData("Action: Sample Action", ModCategory.Animation)]
    [InlineData("Emote: Sample Emote", ModCategory.Animation)]
    [InlineData("Vfx", ModCategory.VFX)]
    [InlineData("Animation", ModCategory.Animation)]
    [InlineData("Housing", ModCategory.Furniture)]
    [InlineData("Sound", ModCategory.Sound)]
    public void ClassifyKeyFacet_LiteralAndPrefixedShapes_MapCorrectly(string rawKey, ModCategory expected)
    {
        var key = ChangedItemKeyParser.Parse(rawKey);
        Assert.Equal(expected, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Theory]
    [InlineData("Customization: Miqo'te Female Face 101", ModCategory.Face)]
    [InlineData("Customization: Midlander Female Hair 157", ModCategory.Hair)]
    [InlineData("Customization: Miqo'te Female Tail 3", ModCategory.Body)]
    [InlineData("Customization: Midlander Female Skin Textures", ModCategory.Skin)]
    public void ClassifyKeyFacet_CustomizationBodyPart_MapsCorrectly(string rawKey, ModCategory expected)
    {
        var key = ChangedItemKeyParser.Parse(rawKey);
        Assert.Equal(expected, ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact] // Body part present but unrecognized ("Unknown") — never guess, return null
    public void ClassifyKeyFacet_UnrecognizedCustomizationBodyPart_IsNull()
    {
        var key = ChangedItemKeyParser.Parse("Customization: Unknown");
        Assert.Null(ModTypeClassifier.ClassifyKeyFacet(key));
    }

    [Fact] // Icon shape has no facet mapping at all — null, not a guess
    public void ClassifyKeyFacet_IconShape_IsNull()
    {
        var key = ChangedItemKeyParser.Parse("Icon: Something");
        Assert.Null(ModTypeClassifier.ClassifyKeyFacet(key));
    }
}
