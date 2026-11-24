namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The classes and jobs that can equip an item, looked up by the item's name in the client language (the name
/// Penumbra uses as a weapon's changed-item key). Injected into the scan so tests use a fake; the real one reads the
/// game's Item sheet and can only be checked in game.
/// </summary>
public interface IItemJobLookup
{
    /// <summary>
    /// The class and job abbreviations the item's ClassJobCategory allows (for example GLA and PLD), unfolded, or
    /// null when no weapon, shield or tool has this name. May throw when the game data cannot be read; the scan
    /// treats that as "no job" and warns once.
    /// </summary>
    IReadOnlySet<string>? JobsFor(string itemName);
}

/// <summary>Turns the jobs of a weapon mod's items into its one job folder: a job, Multiclass, or none.</summary>
public static class WeaponJobs
{
    /// <summary>The folder for a weapon several jobs can use, or whose items disagree.</summary>
    public const string Multiclass = "Multiclass";

    // A class files with the job it becomes. Arcanist becomes both Summoner and Scholar, so an Arcanist book folds
    // to two jobs and is Multiclass. ADV (every adventurer) is not a job of its own and is dropped.
    private static readonly Dictionary<string, string[]> ClassToJobs = new(StringComparer.Ordinal)
    {
        ["GLA"] = ["PLD"],
        ["PGL"] = ["MNK"],
        ["MRD"] = ["WAR"],
        ["LNC"] = ["DRG"],
        ["ARC"] = ["BRD"],
        ["CNJ"] = ["WHM"],
        ["THM"] = ["BLM"],
        ["ACN"] = ["SMN", "SCH"],
        ["ROG"] = ["NIN"],
        ["ADV"] = [],
    };

    /// <summary>The jobs these abbreviations stand for once every class is folded into its job.</summary>
    public static IReadOnlySet<string> Fold(IEnumerable<string> abbreviations)
    {
        var jobs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var abbreviation in abbreviations)
        {
            if (ClassToJobs.TryGetValue(abbreviation, out var folded))
                jobs.UnionWith(folded);
            else if (!string.IsNullOrWhiteSpace(abbreviation))
                jobs.Add(abbreviation);
        }

        return jobs;
    }

    /// <summary>
    /// The job folder for a weapon mod whose changed items have these names: the one job every matched item belongs
    /// to; <see cref="Multiclass"/> when an item allows several jobs or two items disagree; null when no item matched
    /// (the mod then stays directly under the Weapon folder, never in an "Unknown" folder).
    /// </summary>
    /// <remarks>
    /// A name that does not match as given is tried once more without a trailing parenthesised suffix, in case the
    /// key carries one (for example an off-hand marker); this is unverified against Penumbra and costs nothing when
    /// the plain name matches.
    /// </remarks>
    public static string? Resolve(IEnumerable<string> itemNames, IItemJobLookup lookup)
    {
        string? single = null;
        foreach (var name in itemNames)
        {
            var raw = lookup.JobsFor(name) ?? (WithoutSuffix(name) is { } bare ? lookup.JobsFor(bare) : null);
            if (raw is null)
                continue;

            var jobs = Fold(raw);
            if (jobs.Count == 0)
                continue;
            if (jobs.Count > 1)
                return Multiclass;

            var job = jobs.First();
            if (single is not null && !string.Equals(single, job, StringComparison.Ordinal))
                return Multiclass;
            single = job;
        }

        return single;
    }

    private static string? WithoutSuffix(string name)
    {
        if (!name.EndsWith(')'))
            return null;
        var open = name.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 ? name[..open] : null;
    }
}
