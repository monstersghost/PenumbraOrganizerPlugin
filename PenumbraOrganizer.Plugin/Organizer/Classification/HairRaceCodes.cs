namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>One race and gender folder code, as Penumbra names them.</summary>
public sealed record HairRace(string Code, string Race, string Gender);

/// <summary>
/// The eighteen race and gender folder codes a hair mod can ship under, from the mods in the test set.
/// </summary>
/// <remarks>
/// The race names are Penumbra's own key names, so a priority chosen in the dropdown matches the
/// same names the changed-item keys use. Every code here is confirmed by at least one mod; see the
/// race-priority spec. Adding a code means adding a test row for it.
/// </remarks>
public static class HairRaceCodes
{
    public static readonly IReadOnlyList<HairRace> All =
    [
        new("0101", "Midlander", "Male"),
        new("0201", "Midlander", "Female"),
        new("0301", "Highlander", "Male"),
        new("0401", "Highlander", "Female"),
        new("0501", "Elezen", "Male"),
        new("0601", "Elezen", "Female"),
        new("0701", "Miqo'te", "Male"),
        new("0801", "Miqo'te", "Female"),
        new("0901", "Roegadyn", "Male"),
        new("1001", "Roegadyn", "Female"),
        new("1101", "Lalafell", "Male"),
        new("1201", "Lalafell", "Female"),
        new("1301", "Au Ra", "Male"),
        new("1401", "Au Ra", "Female"),
        new("1501", "Hrothgar", "Male"),
        new("1601", "Hrothgar", "Female"),
        new("1701", "Viera", "Male"),
        new("1801", "Viera", "Female"),
    ];

    /// <summary>The Midlander folder codes, used by the Midlander fallback.</summary>
    public const string MidlanderMale = "0101";
    public const string MidlanderFemale = "0201";

    public static bool TryGetByCode(string code, out HairRace race)
    {
        foreach (var entry in All)
        {
            if (entry.Code == code)
            {
                race = entry;
                return true;
            }
        }

        race = null!;
        return false;
    }
}
