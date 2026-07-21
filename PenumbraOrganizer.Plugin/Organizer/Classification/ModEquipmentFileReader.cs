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

        if (!modDirectory.Exists)
            return slots; // no directory, no evidence — not a read failure, just nothing to find

        IReadOnlyList<FileInfo> configFiles;
        try
        {
            configFiles = DiscoverConfigFiles(modDirectory);
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            return null;
        }

        foreach (var file in configFiles)
        {
            if (!TryCollectSlots(file, slots))
                return null;
        }

        return slots;
    }

    // Counts the config file(s) DiscoverConfigFiles would read, without reading or parsing them.
    // ReadEquipmentSlots collapses "no config files were found here at all" and "config files
    // were found and read but had no recognized equipment data" into the same empty result - this
    // lets a caller (diagnostics) tell the two apart, since they point at very different root
    // causes (a naming/location mismatch for this mod's config files vs. genuinely no equipment
    // data in files that were actually read).
    public static int CountConfigFiles(DirectoryInfo modDirectory)
    {
        if (!modDirectory.Exists)
            return 0;

        try
        {
            return DiscoverConfigFiles(modDirectory).Count;
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            return 0;
        }
    }

    // Penumbra 1.7.0 (2026-07-20) replaced the older default_mod.json + group_*.json layout with
    // a single meta.json per mod ("This version gets rid of the group- and default option JSON
    // files and instead stores all this information in the meta.json again" - Penumbra's own
    // changelog). The two layouts are mutually exclusive after Penumbra's own migration, but both
    // are checked here so a plugin build spanning the update doesn't regress for a tester still on
    // the older Penumbra version.
    private static IReadOnlyList<FileInfo> DiscoverConfigFiles(DirectoryInfo modDirectory)
    {
        // Materialized eagerly: DirectoryInfo.EnumerateFiles is lazy, so a permission error during
        // enumeration would otherwise surface outside the caller's catch.
        var metaJson = new FileInfo(Path.Combine(modDirectory.FullName, "meta.json"));
        if (metaJson.Exists)
            return [metaJson];

        var files = new List<FileInfo>();
        var defaultMod = new FileInfo(Path.Combine(modDirectory.FullName, "default_mod.json"));
        if (defaultMod.Exists)
            files.Add(defaultMod);
        files.AddRange(modDirectory.EnumerateFiles("group_*.json"));
        return files;
    }

    private static bool TryCollectSlots(FileInfo file, HashSet<EquipmentSlot> slots)
    {
        try
        {
            using var stream = File.OpenRead(file.FullName);
            using var document = JsonDocument.Parse(stream);
            CollectSlotsFromElement(document.RootElement, slots);
            return true;
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex) || ex is JsonException)
        {
            return false;
        }
    }

    private static bool IsExpectedFileSystemException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException; // UnauthorizedAccessException does NOT
                                                            // derive from IOException in .NET

    private static void CollectSlotsFromElement(JsonElement element, HashSet<EquipmentSlot> slots)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return; // guards every TryGetProperty call below against a non-object element

        CollectFromFilesAndManipulations(element, slots);
        CollectFromChildArray(element, "Options", slots);
        CollectFromChildArray(element, "Containers", slots);

        // Penumbra 1.7.0's meta.json shape: the mod's default option's Files/Manipulations live
        // nested one level under "DefaultData" (an object, not an array like Options/Containers),
        // and every group that used to be its own group_*.json file is now one entry in a
        // top-level "Groups" array - each group entry has the same Options/Containers shape
        // already handled above, so recursing into it needs no further special-casing.
        if (element.TryGetProperty("DefaultData", out var defaultData) && defaultData.ValueKind == JsonValueKind.Object)
            CollectSlotsFromElement(defaultData, slots);
        CollectFromChildArray(element, "Groups", slots);
    }

    private static void CollectFromChildArray(JsonElement element, string propertyName, HashSet<EquipmentSlot> slots)
    {
        if (!element.TryGetProperty(propertyName, out var children) || children.ValueKind != JsonValueKind.Array)
            return;

        // Genuinely recursive (calls CollectSlotsFromElement, not just the Files/Manipulations
        // step) — real mods checked across ~2,280 combined mods never nest beyond one level,
        // but this doesn't silently stop early if a future/unusual mod does.
        foreach (var child in children.EnumerateArray())
            CollectSlotsFromElement(child, slots);
    }

    private static void CollectFromFilesAndManipulations(JsonElement element, HashSet<EquipmentSlot> slots)
    {
        if (element.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in files.EnumerateObject())
            {
                var path = entry.Name.Replace('\\', '/');
                if (!path.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith("chara/accessory/", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileName = Path.GetFileName(path);
                if (EquipmentSlotMapper.ExtractSlotFromFileName(fileName) is { } slot)
                    slots.Add(slot);
            }
        }

        if (element.TryGetProperty("Manipulations", out var manipulations) && manipulations.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in manipulations.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!item.TryGetProperty("Type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                    continue;
                if (!item.TryGetProperty("Manipulation", out var manipulation) || manipulation.ValueKind != JsonValueKind.Object)
                    continue;

                // Real shape, confirmed against two independent real mod libraries: the slot
                // lives nested under "Manipulation", not as a direct property of the array
                // element, and the field name depends on "Type" — Eqp/Eqdp use "Slot", Imc uses
                // "EquipSlot". "Est" also has a "Slot", but it names a customization slot
                // (Hair/Face), not equipment — deliberately excluded by only recognizing the
                // three equipment-relevant types.
                var slotFieldName = typeProp.GetString() switch
                {
                    "Eqp" or "Eqdp" => "Slot",
                    "Imc" => "EquipSlot",
                    _ => null,
                };
                if (slotFieldName is null)
                    continue;

                if (manipulation.TryGetProperty(slotFieldName, out var slotProp)
                    && slotProp.ValueKind == JsonValueKind.String
                    && EquipmentSlotMapper.MapManipulationSlot(slotProp.GetString()!) is { } slot)
                    slots.Add(slot);
            }
        }
    }
}
