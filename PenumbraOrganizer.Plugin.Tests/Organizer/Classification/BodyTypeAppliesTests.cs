namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The gate on consulting a mod's name for a body type at all. A body type is only meaningful for
/// something cut to a body's shape, and the equipment slots are what say so.
/// </summary>
public class BodyTypeAppliesTests
{
    private static IReadOnlySet<EquipmentSlot> Slots(params EquipmentSlot[] slots) => new HashSet<EquipmentSlot>(slots);

    [Theory]
    [InlineData(EquipmentSlot.Top)]
    [InlineData(EquipmentSlot.Hands)]
    [InlineData(EquipmentSlot.Legs)]
    [InlineData(EquipmentSlot.Feet)]
    public void Gear_InABodyShapedSlot_Applies(EquipmentSlot slot) =>
        // The four halves a body replacer authors, and the only four that are deformed to it.
        Assert.True(ModTypeClassifier.BodyTypeApplies(ModCategory.Gear, Slots(slot)));

    [Theory]
    [InlineData(EquipmentSlot.Head)]
    [InlineData(EquipmentSlot.Ears)]
    [InlineData(EquipmentSlot.Neck)]
    [InlineData(EquipmentSlot.Wrists)]
    [InlineData(EquipmentSlot.Rings)]
    public void Gear_InAnAccessorySlotOnly_DoesNotApply(EquipmentSlot slot) =>
        // A helmet, earring, necklace, bracelet or ring is not cut to a body's shape, so a body
        // name in its title is about the glamour, not about fit.
        Assert.False(ModTypeClassifier.BodyTypeApplies(ModCategory.Gear, Slots(slot)));

    [Fact]
    public void Gear_WithNoSlotAtAll_DoesNotApply() =>
        // THE WEAPON CASE. A weapon mod replaces chara/weapon/..., so it resolves no equipment
        // slot, yet it classifies as Gear like any other item. Without this gate an "Artemis Bow"
        // would be filed under the Artemis body.
        Assert.False(ModTypeClassifier.BodyTypeApplies(ModCategory.Gear, Slots()));

    [Fact]
    public void Gear_WithAMixOfSlots_AppliesOnTheBodyShapedOne() =>
        // A full outfit covering a body-shaped slot and an accessory still gets a body folder; one
        // body-shaped slot anywhere in the set is enough.
        Assert.True(ModTypeClassifier.BodyTypeApplies(
            ModCategory.Gear, Slots(EquipmentSlot.Rings, EquipmentSlot.Top)));

    [Fact]
    public void Gear_WithUnreadableSlots_DoesNotApply() =>
        // Null is a read that could not be trusted. Treated as no evidence rather than as
        // permission, which is how the gear and hair passes already treat the same answer.
        Assert.False(ModTypeClassifier.BodyTypeApplies(ModCategory.Gear, null));

    [Fact]
    public void Body_AppliesWithoutAnySlotEvidence() =>
        // The category already establishes the mod is a body; there is nothing for slots to add.
        Assert.True(ModTypeClassifier.BodyTypeApplies(ModCategory.Body, null));

    [Theory]
    [InlineData(ModCategory.Hair)]
    [InlineData(ModCategory.Face)]
    [InlineData(ModCategory.Skin)]
    [InlineData(ModCategory.NPC)]
    [InlineData(ModCategory.Mount)]
    [InlineData(ModCategory.Minion)]
    [InlineData(ModCategory.Animation)]
    [InlineData(ModCategory.VFX)]
    [InlineData(ModCategory.Furniture)]
    [InlineData(ModCategory.Sound)]
    public void EveryOtherCategory_DoesNotApply(ModCategory category) =>
        // Even handed a body-shaped slot, which cannot happen, these never get a body folder -
        // ModTypeFolders.GetFolder would throw for them anyway.
        Assert.False(ModTypeClassifier.BodyTypeApplies(category, Slots(EquipmentSlot.Top)));

    [Fact]
    public void NullCategory_DoesNotApply() =>
        // An unclassified row goes to Review, not to a body folder.
        Assert.False(ModTypeClassifier.BodyTypeApplies(null, Slots(EquipmentSlot.Top)));

    [Fact]
    public void BodyShapedSlots_AreExactlyTheFourModelHalves() =>
        // Pins the set itself: Head, Ears, Neck, Wrists and Rings being absent is the whole point,
        // and an "obvious" addition of Head would quietly start filing hats under bodies.
        Assert.Equal(
            [EquipmentSlot.Top, EquipmentSlot.Hands, EquipmentSlot.Legs, EquipmentSlot.Feet],
            ModTypeClassifier.BodyShapedSlots.OrderBy(s => s));
}
