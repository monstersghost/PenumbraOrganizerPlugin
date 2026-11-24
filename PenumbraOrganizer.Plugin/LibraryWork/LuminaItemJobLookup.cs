using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.LibraryWork;

/// <summary>
/// The real <see cref="IItemJobLookup"/>: the game's Item sheet, in the client language (the language Penumbra's
/// changed-item names are in), indexed once by name. Only weapons, shields and tools are indexed (an EquipSlotCategory
/// with a main or off hand); each name maps to the abbreviations its ClassJobCategory allows, unfolded.
/// </summary>
/// <remarks>
/// Built lazily on first use, which is on the scan's background thread, so the framework thread never pays for the
/// ~50,000-row walk; the index is then shared by every later scan. A failure to read the sheet is thrown from
/// <see cref="JobsFor"/> every time it is asked, and the scan turns that into one warning and "no job". Can only be
/// verified in game.
/// </remarks>
public sealed class LuminaItemJobLookup : IItemJobLookup
{
    private readonly Lazy<Dictionary<string, IReadOnlySet<string>>> _index;

    public LuminaItemJobLookup(IDataManager dataManager)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        _index = new(() => Build(dataManager), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlySet<string>? JobsFor(string itemName) =>
        _index.Value.TryGetValue(itemName, out var jobs) ? jobs : null;

    private static Dictionary<string, IReadOnlySet<string>> Build(IDataManager dataManager)
    {
        var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var item in dataManager.GetExcelSheet<Item>())
        {
            if (!item.EquipSlotCategory.IsValid || !item.ClassJobCategory.IsValid)
                continue;
            var slots = item.EquipSlotCategory.Value;
            if (slots.MainHand == 0 && slots.OffHand == 0)
                continue;

            var name = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var jobs = Abbreviations(item.ClassJobCategory.Value);
            if (jobs.Count == 0)
                continue;

            // Two items with one name (a reissue, a PvP copy) count as one: the union of their jobs.
            if (index.TryGetValue(name, out var known))
                known.UnionWith(jobs);
            else
                index[name] = jobs;
        }

        return index.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal);
    }

    // Every class and job column of ClassJobCategory (Lumina.Excel 7), by its own abbreviation. Folding classes into
    // jobs is WeaponJobs.Fold's job, which is tested; this only reads the sheet.
    private static HashSet<string> Abbreviations(ClassJobCategory c)
    {
        var all = new (string Abbreviation, bool Allowed)[]
        {
            ("ADV", c.ADV), ("GLA", c.GLA), ("PGL", c.PGL), ("MRD", c.MRD), ("LNC", c.LNC), ("ARC", c.ARC),
            ("CNJ", c.CNJ), ("THM", c.THM), ("CRP", c.CRP), ("BSM", c.BSM), ("ARM", c.ARM), ("GSM", c.GSM),
            ("LTW", c.LTW), ("WVR", c.WVR), ("ALC", c.ALC), ("CUL", c.CUL), ("MIN", c.MIN), ("BTN", c.BTN),
            ("FSH", c.FSH), ("PLD", c.PLD), ("MNK", c.MNK), ("WAR", c.WAR), ("DRG", c.DRG), ("BRD", c.BRD),
            ("WHM", c.WHM), ("BLM", c.BLM), ("ACN", c.ACN), ("SMN", c.SMN), ("SCH", c.SCH), ("ROG", c.ROG),
            ("NIN", c.NIN), ("MCH", c.MCH), ("DRK", c.DRK), ("AST", c.AST), ("SAM", c.SAM), ("RDM", c.RDM),
            ("BLU", c.BLU), ("GNB", c.GNB), ("DNC", c.DNC), ("RPR", c.RPR), ("SGE", c.SGE), ("VPR", c.VPR),
            ("PCT", c.PCT),
        };

        return all.Where(x => x.Allowed).Select(x => x.Abbreviation).ToHashSet(StringComparer.Ordinal);
    }
}
