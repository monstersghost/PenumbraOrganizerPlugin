using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The Sort tab's split choices as one value, so "Select all" is a pure function the tests can pin.
/// Race priority, the multi-race toggle, the body priority and the hair priority are deliberately not
/// here: "Select all" never changes them.
/// </summary>
public readonly record struct SplitToggles(
    bool SplitGear, bool SplitNpc, bool SplitHair, bool SplitGearBody, bool SplitBodyMods,
    bool SharedHandsFeet, bool SplitEmotes, bool RaceEnabled, RaceRows RaceRows)
{
    /// <summary>
    /// The deepest layout the tab can produce: every split on, every race row on with the race master on, and
    /// hands and feet NOT shared (sharing puts them in one folder, which is less detailed).
    /// </summary>
    public static SplitToggles MostDetailed(SplitToggles current) => current with
    {
        SplitGear = true,
        SplitNpc = true,
        SplitHair = true,
        SplitGearBody = true,
        SplitBodyMods = true,
        SharedHandsFeet = false,
        SplitEmotes = true,
        RaceEnabled = true,
        RaceRows = RaceRows.All,
    };
}
