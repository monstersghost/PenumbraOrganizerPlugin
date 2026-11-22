namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The nine race folder names, spelled exactly as Penumbra's changed-item keys and
/// <see cref="HairRaceCodes"/> spell them. Midlander and Highlander are two names on purpose: hair has
/// always filed them separately, and merging them would rename existing folders.
/// </summary>
public static class RaceNames
{
    public const string MultiRace = HairRaceFolders.MultiRace;

    public static readonly IReadOnlyList<string> All =
    [
        "Midlander", "Highlander", "Elezen", "Miqo'te", "Roegadyn", "Lalafell", "Au Ra", "Hrothgar", "Viera",
    ];

    public static bool IsKnown(string? name) => name is not null && All.Contains(name, StringComparer.Ordinal);

    // Keyed on Penumbra's SubRace ENUM names, which is what an Rsp manipulation stores in its JSON
    // ("SeekerOfTheSun", not the display name "Seeker of the Sun"). Unknown, numbers and any other
    // spelling are deliberately absent: the caller treats them as unreadable evidence.
    private static readonly Dictionary<string, string> BySubRaceEnumName = new(StringComparer.Ordinal)
    {
        ["Midlander"] = "Midlander",
        ["Highlander"] = "Highlander",
        ["Wildwood"] = "Elezen",
        ["Duskwight"] = "Elezen",
        ["Plainsfolk"] = "Lalafell",
        ["Dunesfolk"] = "Lalafell",
        ["SeekerOfTheSun"] = "Miqo'te",
        ["KeeperOfTheMoon"] = "Miqo'te",
        ["Seawolf"] = "Roegadyn",
        ["Hellsguard"] = "Roegadyn",
        ["Raen"] = "Au Ra",
        ["Xaela"] = "Au Ra",
        ["Helion"] = "Hrothgar",
        ["Lost"] = "Hrothgar",
        ["Rava"] = "Viera",
        ["Veena"] = "Viera",
    };

    /// <summary>The race name for a SubRace enum name, or null when it is not one of the sixteen clans.</summary>
    public static string? FromSubRaceEnumName(string? subRace) =>
        subRace is not null && BySubRaceEnumName.TryGetValue(subRace, out var race) ? race : null;
}
