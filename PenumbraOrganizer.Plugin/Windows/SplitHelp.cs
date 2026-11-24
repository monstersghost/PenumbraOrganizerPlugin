using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Layouts;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The help topic each Advanced split row shows: the topic of the old Sort tab checkbox the split replaces, so its
/// text keeps explaining the same behaviour (spec 2, section 7).
/// </summary>
public static class SplitHelp
{
    public static HelpTopic TopicFor(ModCategory category, LayoutSplits split) => (category, split) switch
    {
        (_, LayoutSplits.BodyType) => HelpTopics.SortSplitGearBody,
        (_, LayoutSplits.Slot) => HelpTopics.SortSplitGear,
        (_, LayoutSplits.SharedHandsFeet) => HelpTopics.SortSharedHandsFeet,
        (_, LayoutSplits.NpcKind) => HelpTopics.SortSplitNpc,
        (ModCategory.Hair, LayoutSplits.Race) => HelpTopics.SortSplitHairByRace,
        (_, LayoutSplits.HairNumber) => HelpTopics.SortSplitHair,
        (_, LayoutSplits.PartAndBodyType) => HelpTopics.SortSplitBodyMods,
        (_, LayoutSplits.Emote) => HelpTopics.SortSplitEmotes,
        // New with the split itself: there was never a checkbox for it.
        (_, LayoutSplits.Job) => HelpTopics.SortSplitWeaponJob,
        // Face, Skin and Body race, Eyes by race, Racial scaling by race: the race rows.
        _ => HelpTopics.SortRaceRows,
    };
}
