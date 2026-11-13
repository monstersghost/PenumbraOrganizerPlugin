using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// Builds the Sort tab's selection from saved configuration, so the panel's first frame and a headless
/// run (the new-mod pipeline, chat commands) read the same settings the same way.
/// </summary>
public static class SortSelectionFactory
{
    /// <summary>
    /// Never writes the configuration: a body priority that needed repair against the label list is
    /// repaired in the returned selection only (the panel saves its own repair). An empty label list
    /// leaves the priority at its default rather than dropping every saved entry.
    /// </summary>
    public static SortSelection FromConfig(Configuration config, IReadOnlyList<string> bodyLabels)
    {
        var strategy = SortPanel.Groupings[SortPanel.GroupingIndexFor(config.SortGrouping)].Strategy;
        var priority = bodyLabels.Count == 0
            ? BodyPriority.Default([])
            : BodyPriority.Repair(config.BodyPriority, bodyLabels, out _);

        return new SortSelection(
            strategy, config.SortSplitGear, config.SortSplitNpc, config.SortSplitHair,
            SortPanel.HairRaceCodeAt(SortPanel.HairRaceIndexFor(config.HairRaceCode)),
            config.MidlanderFallback, config.SortSplitGearBody, config.SortSplitBodyMods,
            priority,
            new LayoutOptions(config.SortSharedHandsFeet, config.SortSplitHairByRace, config.SortSplitEmotes),
            RootOnly: false);
    }
}

/// <summary>The one place a SortSelection becomes a call to <see cref="OrganizerState.Sort"/>.</summary>
public static class SortRunner
{
    public static int Run(
        OrganizerState state, SortSelection selection, Func<string, string> canonicalizeCreator, SortScope? scope) =>
        state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, selection.SplitHair,
            canonicalizeCreator, selection.HairPriority, selection.SplitGearBody, selection.SplitBodyMods,
            selection.BodyPriority, selection.Layout, scope);
}
