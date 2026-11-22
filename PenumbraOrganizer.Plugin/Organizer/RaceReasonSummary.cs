using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// One line counting why rows got their race folder. Several races, no evidence and unreadable evidence all
/// file under Multi-race for compatibility, so the counts are kept apart: a detection failure must never
/// look like a genuine multi-race mod.
/// </summary>
public static class RaceReasonSummary
{
    public static string? Describe(IEnumerable<OrganizerModRow> rows)
    {
        var staged = rows.Where(r => r.StagedRace is not null).Select(r => r.StagedRace!.Reason).ToList();
        if (staged.Count == 0)
            return null;

        int Count(RaceFolderReason reason) => staged.Count(r => r == reason);

        var parts = new List<string>();
        void Add(RaceFolderReason reason, string text)
        {
            if (Count(reason) is > 0 and var n)
                parts.Add($"{n} {text}");
        }

        Add(RaceFolderReason.OneRace, "one race");
        Add(RaceFolderReason.SeveralToMultiRace, "several races (Multi-race)");
        Add(RaceFolderReason.PriorityPick, "several races (priority pick)");
        Add(RaceFolderReason.SeveralNoRankedRace, "several races (none ranked, Multi-race)");
        Add(RaceFolderReason.NoEvidence, "no race evidence");
        Add(RaceFolderReason.Unreadable, "unreadable evidence");
        return "Race split: " + string.Join(", ", parts);
    }
}
