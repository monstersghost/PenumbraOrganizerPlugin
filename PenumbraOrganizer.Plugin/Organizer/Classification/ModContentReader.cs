using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>Everything the scan reads out of one mod's config, from one walk.</summary>
public sealed record ModContent(
    IReadOnlySet<EquipmentSlot> Slots,
    ModHairFileReader.HairEvidence Hair,
    IReadOnlyList<string> GroupNames,
    IReadOnlyList<string> OptionNames,
    IReadOnlyList<string> FileValues);

/// <summary>
/// One walk per mod feeding the equipment-slot, hair and body logic. Before this, each reader
/// walked the mod's config itself; adding a third would have read a Gear mod's files twice.
/// </summary>
/// <remarks>
/// Null means the walk could not be trusted and everything collected is discarded - the contract
/// <see cref="ModConfigFileWalker.TryWalk(DirectoryInfo, ConfigVisitor)"/> imposes on every caller.
/// </remarks>
public static class ModContentReader
{
    public static ModContent? Read(DirectoryInfo modDirectory)
    {
        var slots = new HashSet<EquipmentSlot>();
        var flat = new HashSet<int>();
        var byRace = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var groups = new List<string>();
        var options = new List<string>();
        var values = new List<string>();

        var ok = ModConfigFileWalker.TryWalk(modDirectory, new ConfigVisitor
        {
            OnFileKey = path =>
            {
                ModEquipmentFileReader.CollectFromFileKey(path, slots);
                ModHairFileReader.CollectFromFileKey(path, flat, byRace);
            },
            OnFileValue = values.Add,
            OnManipulation = item =>
            {
                ModEquipmentFileReader.CollectFromManipulation(item, slots);
                ModHairFileReader.CollectFromManipulation(item, flat);
            },
            OnName = (kind, name) => (kind == ConfigNameKind.Group ? groups : options).Add(name),
        });

        return ok ? new ModContent(slots, ModHairFileReader.Freeze(flat, byRace), groups, options, values) : null;
    }
}
