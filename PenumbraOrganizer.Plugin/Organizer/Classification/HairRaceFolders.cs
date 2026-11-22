namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The folder below <c>Hair/</c> when hair is split by race: the race for a mod made for one race,
/// <c>Multi-race</c> for a mod made for several or one with no race evidence. With the race split's
/// multi-race toggle off, a mod made for several races files under the top-ranked race it covers.
/// </summary>
public static class HairRaceFolders
{
    public const string MultiRace = "Multi-race";

    /// <param name="flat">Every hair number the mod uses; passed to <see cref="HairPriorityResolver"/> only.</param>
    /// <param name="byRace">Hair numbers per race and gender folder code, or null when none were read.</param>
    /// <param name="priority">Null means no priority; the stored scan-time number is used for Multi-race.</param>
    /// <param name="storedNumber">The number the scan stored on the row, used for Multi-race with no priority.</param>
    /// <param name="splitByNumber">Whether the hair number split is also on.</param>
    /// <param name="race">Null means today's behavior: several races always go to Multi-race.</param>
    public static string Tail(
        IReadOnlySet<int>? flat,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? byRace,
        HairPriority? priority,
        string? storedNumber,
        bool splitByNumber,
        RaceSplitOptions? race = null)
    {
        var raceNumbers = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        if (byRace is not null)
        {
            foreach (var (code, numbers) in byRace)
            {
                if (!HairRaceCodes.TryGetByCode(code, out var hairRace))
                    continue;
                if (!raceNumbers.TryGetValue(hairRace.Race, out var set))
                    raceNumbers[hairRace.Race] = set = [];
                set.UnionWith(numbers);
            }
        }

        // One race, or several races with the toggle off and one of them ranked: that race's own folder.
        // The priority race and the Midlander rule never apply here, and a picked race is numbered as if
        // it were the mod's only race.
        var result = RaceFolderResolver.Resolve(
            RaceEvidence.Of(raceNumbers.Keys),
            race?.MultiRaceToMultiRace ?? true,
            race?.Priority ?? RacePriority.Empty);
        if (result.Reason is RaceFolderReason.OneRace or RaceFolderReason.PriorityPick)
        {
            var numbers = raceNumbers[result.Folder];
            return Join(result.Folder, splitByNumber && numbers.Count == 1 ? numbers.Single() : null);
        }

        // Several races, or none: Multi-race, numbered by the existing priority rule.
        int? number = null;
        if (splitByNumber)
            number = priority is { } p ? HairPriorityResolver.Resolve(flat, byRace, p) : ParseStored(storedNumber);
        return Join(MultiRace, number);
    }

    private static string Join(string folder, int? number) =>
        number is { } n ? $"{folder}/{n.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(3, '0')}" : folder;

    // Same range the hair reader accepts: 1..9999.
    private static int? ParseStored(string? stored) =>
        int.TryParse(stored, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
        && n is >= 1 and <= 9999 ? n : null;
}
