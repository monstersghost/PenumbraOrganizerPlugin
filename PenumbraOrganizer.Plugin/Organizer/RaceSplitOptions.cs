namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>The rows of the Race section. Flags so a set of ticked rows is one comparable value.</summary>
[Flags]
public enum RaceRows
{
    None = 0,
    Hair = 1,
    Face = 2,
    Eyes = 4,
    Skin = 8,
    TailsEarsHorns = 16,
    RacialScaling = 32,
    All = 63,
}

/// <summary>Row names as stored in the config, and the labels the Sort tab shows.</summary>
public static class RaceRowNames
{
    private static readonly (RaceRows Row, string Name, string Label)[] Table =
    [
        (RaceRows.Hair, "Hair", "Hair"),
        (RaceRows.Face, "Face", "Face"),
        (RaceRows.Eyes, "Eyes", "Eyes"),
        (RaceRows.Skin, "Skin", "Skin"),
        (RaceRows.TailsEarsHorns, "TailsEarsHorns", "Tails, ears and horns"),
        (RaceRows.RacialScaling, "RacialScaling", "Racial scaling"),
    ];

    public static IReadOnlyList<(RaceRows Row, string Name, string Label)> All => Table;

    /// <summary>Null means "never touched": every row ticked. An empty list means the user unticked all.</summary>
    public static RaceRows Parse(IEnumerable<string>? names)
    {
        if (names is null)
            return RaceRows.All;

        var rows = RaceRows.None;
        foreach (var name in names)
        {
            foreach (var entry in Table)
            {
                if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                    rows |= entry.Row;
            }
        }

        return rows;
    }

    public static List<string> ToNames(RaceRows rows) =>
        Table.Where(entry => rows.HasFlag(entry.Row)).Select(entry => entry.Name).ToList();
}

/// <summary>
/// The Race section of the Sort tab as one value. Null anywhere a <c>RaceSplitOptions?</c> is accepted
/// means today's behavior: no race split beyond the legacy hair option.
/// </summary>
public sealed record RaceSplitOptions(
    bool Enabled, RaceRows Rows, bool MultiRaceToMultiRace, RacePriority Priority)
{
    public static readonly RaceSplitOptions Off = new(false, RaceRows.All, true, RacePriority.Empty);

    /// <summary>The Tails/Ears/Horns and Racial scaling folders only exist while body mods are split.</summary>
    public static bool NeedsBodySplit(RaceRows row) => row is RaceRows.TailsEarsHorns or RaceRows.RacialScaling;

    /// <summary>A row applies only when the master is on, the row is ticked and its dependencies hold.</summary>
    public bool InEffect(RaceRows row, bool bodySplit) =>
        Enabled && Rows.HasFlag(row) && (!NeedsBodySplit(row) || bodySplit);
}
