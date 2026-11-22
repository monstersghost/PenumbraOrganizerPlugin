namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>The races a mod's evidence names, and whether that read was complete.</summary>
public sealed record RaceEvidence(IReadOnlySet<string> Races, bool Complete)
{
    public static readonly RaceEvidence NoEvidence = new(new HashSet<string>(StringComparer.Ordinal), true);

    public static readonly RaceEvidence Unreadable = new(new HashSet<string>(StringComparer.Ordinal), false);

    public static RaceEvidence Of(IEnumerable<string> races, bool complete = true) =>
        new(races.ToHashSet(StringComparer.Ordinal), complete);
}

public enum RaceFolderReason
{
    OneRace,
    SeveralToMultiRace,
    PriorityPick,
    SeveralNoRankedRace,
    NoEvidence,
    Unreadable,
}

public sealed record RaceFolderResult(string Folder, RaceFolderReason Reason);

/// <summary>
/// The one pure rule that turns race evidence into a folder name (race spec, section 2). The workbook's
/// {race} token calls this same function so Sort and the workbook cannot disagree.
/// </summary>
public static class RaceFolderResolver
{
    public static RaceFolderResult Resolve(
        RaceEvidence evidence, bool multiRaceToMultiRace, RacePriority priority)
    {
        // Incomplete evidence never becomes a single race and is never picked by priority.
        if (!evidence.Complete)
            return new(RaceNames.MultiRace, RaceFolderReason.Unreadable);
        if (evidence.Races.Count == 0)
            return new(RaceNames.MultiRace, RaceFolderReason.NoEvidence);
        if (evidence.Races.Count == 1)
            return new(evidence.Races.Single(), RaceFolderReason.OneRace);
        if (multiRaceToMultiRace)
            return new(RaceNames.MultiRace, RaceFolderReason.SeveralToMultiRace);

        var pick = evidence.Races
            .Where(race => priority.RankOf(race) >= 0)
            .OrderBy(priority.RankOf)
            .FirstOrDefault();
        return pick is null
            ? new(RaceNames.MultiRace, RaceFolderReason.SeveralNoRankedRace)
            : new(pick, RaceFolderReason.PriorityPick);
    }
}
