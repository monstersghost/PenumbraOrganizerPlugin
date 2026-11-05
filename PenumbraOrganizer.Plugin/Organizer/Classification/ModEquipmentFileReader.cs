using System.Text.Json;
using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public static class ModEquipmentFileReader
{
    // Returns every resolved equipment/accessory slot found across the mod's files.
    // - null: some file could not be read, parsed, or enumerated — an untrustworthy partial
    //   result, treated as "no subcategory," never as a confident (possibly wrong) answer.
    // - non-null (possibly empty): every file was read successfully; empty means none of them
    //   carried recognized equipment/accessory path or manipulation data.
    public static IReadOnlySet<EquipmentSlot>? ReadEquipmentSlots(DirectoryInfo modDirectory)
    {
        var slots = new HashSet<EquipmentSlot>();

        var ok = ModConfigFileWalker.TryWalk(
            modDirectory,
            path => CollectFromFileKey(path, slots),
            item => CollectFromManipulation(item, slots));

        return ok ? slots : null;
    }

    // Counts the config file(s) the walker would read, without reading or parsing them.
    // ReadEquipmentSlots collapses "no config files were found here at all" and "config files
    // were found and read but had no recognized equipment data" into the same empty result - this
    // lets a caller (diagnostics) tell the two apart, since they point at very different root
    // causes (a naming/location mismatch for this mod's config files vs. genuinely no equipment
    // data in files that were actually read).
    public static int CountConfigFiles(DirectoryInfo modDirectory) =>
        ModConfigFileWalker.CountConfigFiles(modDirectory);

    internal static void CollectFromFileKey(string path, HashSet<EquipmentSlot> slots)
    {
        // Already '/'-normalised by the walker.
        if (!path.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("chara/accessory/", StringComparison.OrdinalIgnoreCase))
            return;

        var fileName = Path.GetFileName(path);
        if (EquipmentSlotMapper.ExtractSlotFromFileName(fileName) is { } slot)
            slots.Add(slot);
    }

    internal static void CollectFromManipulation(JsonElement item, HashSet<EquipmentSlot> slots)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;
        if (!item.TryGetProperty("Type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
            return;
        if (!item.TryGetProperty("Manipulation", out var manipulation) || manipulation.ValueKind != JsonValueKind.Object)
            return;

        // Real shape, confirmed against two independent real mod libraries: the slot
        // lives nested under "Manipulation", not as a direct property of the array
        // element, and the field name depends on "Type" — Eqp/Eqdp use "Slot", Imc uses
        // "EquipSlot". "Est" also has a "Slot", but it names a customization slot
        // (Hair/Face), not equipment — deliberately excluded here by only recognizing the
        // three equipment-relevant types. Est is NOT useless, though: ModHairFileReader
        // reads exactly those entries to resolve a hair number.
        var slotFieldName = typeProp.GetString() switch
        {
            "Eqp" or "Eqdp" => "Slot",
            "Imc" => "EquipSlot",
            _ => null,
        };
        if (slotFieldName is null)
            return;

        if (manipulation.TryGetProperty(slotFieldName, out var slotProp)
            && slotProp.ValueKind == JsonValueKind.String
            && EquipmentSlotMapper.MapManipulationSlot(slotProp.GetString()!) is { } slot)
            slots.Add(slot);
    }
}
