namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The Sort tab's hair priority. A null <see cref="RaceCode"/> is None.
/// </summary>
public readonly record struct HairPriority(string? RaceCode, bool MidlanderFallback);

/// <summary>
/// Resolves the hair number a hair mod files under, from the race-priority spec's steps 1-5.
/// </summary>
/// <remarks>
/// Pure: no disk, no state. Null in means "no trustworthy evidence", and the answer is then null, the
/// same flat <c>Hair</c> as 0.6.1.0. Two numbers within one race never produce an arbitrary pick: such a
/// race is "not exactly one", so the rule falls through.
/// </remarks>
public static class HairPriorityResolver
{
    /// <summary>The number to file under, or null for flat <c>Hair</c>.</summary>
    public static int? Resolve(
        IReadOnlySet<int>? flat,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? byRace,
        HairPriority priority)
    {
        if (flat is null || byRace is null)
            return null;

        // Step 1: the priority race has exactly one number. Other races are ignored.
        if (priority.RaceCode is { } code
            && byRace.TryGetValue(code, out var chosen)
            && chosen.Count == 1)
            return chosen.Single();

        // Step 2 is the fall-through: a priority that is absent or ambiguous reaches here unchanged.

        // Step 3: the mod's detected set has one number.
        if (flat.Count == 1)
            return flat.Single();

        // Step 4: different numbers across races, and the Midlander rule yields one.
        if (flat.Count > 1 && priority.MidlanderFallback)
            return MidlanderNumber(byRace);

        // Step 5: no single number. Flat.
        return null;
    }

    // Both genders present and agreeing on one number: that number. Both present and disagreeing:
    // no answer, since picking one gender would be arbitrary. One gender present with exactly one
    // number: that number. Anything else, including no Midlander entries at all: no answer.
    private static int? MidlanderNumber(IReadOnlyDictionary<string, IReadOnlySet<int>> byRace)
    {
        var hasMale = byRace.TryGetValue(HairRaceCodes.MidlanderMale, out var male);
        var hasFemale = byRace.TryGetValue(HairRaceCodes.MidlanderFemale, out var female);

        if (hasMale && hasFemale)
        {
            if (male!.Count == 1 && female!.Count == 1 && male.Single() == female.Single())
                return male.Single();
            return null;
        }

        if (hasMale)
            return male!.Count == 1 ? male.Single() : null;
        if (hasFemale)
            return female!.Count == 1 ? female.Single() : null;
        return null;
    }
}
