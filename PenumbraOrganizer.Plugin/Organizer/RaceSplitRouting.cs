using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Which Race-section row governs a mod, what race evidence that row reads, and the folder it adds.
/// One place, used by the folder selector and by diagnostics, so a staged path and its explanation
/// cannot disagree.
/// </summary>
public static class RaceSplitRouting
{
    /// <summary>The row that applies to this mod right now, or null when none is in effect for it.</summary>
    public static RaceRows? Governing(OrganizerModRow row, RaceSplitOptions race, bool bodySplit)
    {
        if (row.IsRacialScaling)
            return race.InEffect(RaceRows.RacialScaling, bodySplit) ? RaceRows.RacialScaling : null;

        switch (row.Category)
        {
            case ModCategory.Hair:
                return race.InEffect(RaceRows.Hair, bodySplit) ? RaceRows.Hair : null;
            case ModCategory.Face:
                if (row.KeyRaces is { IrisOnly: true } && race.InEffect(RaceRows.Eyes, bodySplit))
                    return RaceRows.Eyes;
                return race.InEffect(RaceRows.Face, bodySplit) ? RaceRows.Face : null;
            case ModCategory.Skin:
                return race.InEffect(RaceRows.Skin, bodySplit) ? RaceRows.Skin : null;
            case ModCategory.Body when row.BodyPart == BodyPart.TailsEarsHorns:
                return race.InEffect(RaceRows.TailsEarsHorns, bodySplit) ? RaceRows.TailsEarsHorns : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The row a race-applicable mod belongs to, whatever the switches say (race spec, section 2). Null for a
    /// mod with no race row at all (gear, weapons, a body replacer).
    /// </summary>
    public static RaceRows? ApplicableRow(OrganizerModRow row)
    {
        if (row.IsRacialScaling)
            return RaceRows.RacialScaling;

        return row.Category switch
        {
            ModCategory.Hair => RaceRows.Hair,
            ModCategory.Face => row.KeyRaces is { IrisOnly: true } ? RaceRows.Eyes : RaceRows.Face,
            ModCategory.Skin => RaceRows.Skin,
            ModCategory.Body when row.BodyPart == BodyPart.TailsEarsHorns => RaceRows.TailsEarsHorns,
            _ => null,
        };
    }

    /// <summary>
    /// The race folder the workbook's {race} token uses: the same rule Sort applies, but for any applicable
    /// mod whether or not its row or the master switch is on, because the user typed the token on purpose.
    /// Null when the mod is not race-applicable.
    /// </summary>
    public static RaceFolderResult? ResolveForToken(OrganizerModRow row, RaceSplitOptions options)
    {
        if (ApplicableRow(row) is not { } applicable)
            return null;

        return RaceFolderResolver.Resolve(
            EvidenceFor(row, applicable), options.MultiRaceToMultiRace, options.Priority);
    }

    public static RaceEvidence EvidenceFor(OrganizerModRow row, RaceRows governing) => governing switch
    {
        RaceRows.Hair => row.HairByRaceCode is null
            ? RaceEvidence.Unreadable
            : RaceEvidence.Of(HairRaces(row.HairByRaceCode)),
        // An iris-only mod has no other Face keys, so under the Face row it uses its iris keys.
        RaceRows.Face => FromKeys(row.KeyRaces is { IrisOnly: true } ? row.KeyRaces.Iris : row.KeyRaces?.Face),
        RaceRows.Eyes => FromKeys(row.KeyRaces?.Iris),
        RaceRows.Skin => FromKeys(row.KeyRaces?.Skin),
        RaceRows.TailsEarsHorns => FromKeys(row.KeyRaces?.TailEars),
        RaceRows.RacialScaling => row.Rsp is null
            ? RaceEvidence.Unreadable
            : RaceEvidence.Of(row.Rsp.Races, row.Rsp.Complete),
        _ => RaceEvidence.NoEvidence,
    };

    private static RaceEvidence FromKeys(IReadOnlySet<string>? races) =>
        races is null ? RaceEvidence.NoEvidence : RaceEvidence.Of(races);

    private static IEnumerable<string> HairRaces(IReadOnlyDictionary<string, IReadOnlySet<int>> byRaceCode)
    {
        foreach (var code in byRaceCode.Keys)
        {
            if (HairRaceCodes.TryGetByCode(code, out var hairRace))
                yield return hairRace.Race;
        }
    }

    /// <summary>The folder result for any governed row, hair included. Null when no row governs the mod.</summary>
    public static RaceFolderResult? Resolve(OrganizerModRow row, RaceSplitOptions? race, bool bodySplit)
    {
        if (race is not { Enabled: true })
            return null;
        if (Governing(row, race, bodySplit) is not { } governing)
            return null;
        return RaceFolderResolver.Resolve(
            EvidenceFor(row, governing), race.MultiRaceToMultiRace, race.Priority);
    }

    /// <summary>True when a scan-flagged racial-scaling mod is routed under Body (instead of staying in Gear).</summary>
    public static bool IsRoutedToRacialScaling(OrganizerModRow row, RaceSplitOptions? race, bool bodySplit) =>
        race is { Enabled: true }
        && row.IsRacialScaling
        && race.InEffect(RaceRows.RacialScaling, bodySplit);

    /// <summary>
    /// The folders to append for a non-hair row: the race, with an <c>Eyes/</c> level first for the Eyes row.
    /// Hair appends its own tail inside <c>HairRaceFolders</c>, so it returns null here.
    /// </summary>
    public static string? Tail(OrganizerModRow row, RaceSplitOptions? race, bool bodySplit)
    {
        if (race is not { Enabled: true })
            return null;
        if (Governing(row, race, bodySplit) is not { } governing || governing == RaceRows.Hair)
            return null;

        var folder = RaceFolderResolver.Resolve(
            EvidenceFor(row, governing), race.MultiRaceToMultiRace, race.Priority).Folder;
        return governing == RaceRows.Eyes ? $"Eyes/{folder}" : folder;
    }
}
