using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// What a mod's Rsp (racial scaling) manipulations say. <c>Races</c> are the nine race names the sub-races
/// fold to; <c>Complete</c> is false when any entry's SubRace was not one of the sixteen clan enum names;
/// <c>Pure</c> is true when the mod has no other manipulation and no game-file redirect.
/// </summary>
public sealed record RspEvidence(IReadOnlySet<string> Races, bool Complete, bool Pure);

/// <summary>Mutable tally filled by the walker's manipulation callback, then frozen by <see cref="ModRspReader.Finish"/>.</summary>
public sealed class RspCollector
{
    public HashSet<string> Races { get; } = new(StringComparer.Ordinal);
    public int RspCount { get; set; }
    public int OtherManipulations { get; set; }
    public bool Unrecognized { get; set; }
}

/// <summary>
/// Reads Rsp manipulations in the same shape as <see cref="ModHairFileReader"/>: the walker hands each
/// manipulation to <see cref="CollectFromManipulation"/> synchronously. Penumbra writes
/// <c>{"Type":"Rsp","Manipulation":{"SubRace":"SeekerOfTheSun","Attribute":"MaleMinSize","Entry":1.0}}</c>;
/// TexTools racial-scaling packs (.rgsp) are converted to exactly this on import.
/// </summary>
public static class ModRspReader
{
    public static void CollectFromManipulation(JsonElement item, RspCollector collector)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;

        if (!item.TryGetProperty("Type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String
            || typeProp.GetString() != "Rsp")
        {
            collector.OtherManipulations++;
            return;
        }

        collector.RspCount++;
        if (item.TryGetProperty("Manipulation", out var manipulation)
            && manipulation.ValueKind == JsonValueKind.Object
            && manipulation.TryGetProperty("SubRace", out var subRace)
            && subRace.ValueKind == JsonValueKind.String
            && RaceNames.FromSubRaceEnumName(subRace.GetString()) is { } race)
            collector.Races.Add(race);
        else
            collector.Unrecognized = true;
    }

    /// <summary>Null when the mod has no Rsp manipulation at all.</summary>
    public static RspEvidence? Finish(RspCollector collector, int fileKeyCount) =>
        collector.RspCount == 0
            ? null
            : new RspEvidence(
                collector.Races, Complete: !collector.Unrecognized,
                Pure: collector.OtherManipulations == 0 && fileKeyCount == 0);
}
