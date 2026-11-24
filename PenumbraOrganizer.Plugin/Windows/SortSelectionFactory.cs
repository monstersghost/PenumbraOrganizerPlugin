using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Organizer.Rules;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// Builds the Sort tab's selection from saved configuration, so the panel's first frame and a headless
/// run (the new-mod pipeline, chat commands) read the same settings the same way.
/// </summary>
public static class SortSelectionFactory
{
    /// <summary>
    /// The compiled rule set for this configuration, or null when it has no rules (so a rule-free layout stays
    /// equal to the default). Shared by the headless path and the Sort panel so their fingerprints agree.
    /// </summary>
    public static RuleSet? RulesFor(Configuration config)
    {
        var set = RuleSetCache.For(config, RaceSplitOptions.FromConfig(config));
        return set.HasRules ? set : null;
    }

    /// <summary>
    /// The category folders and creator levels that differ from the defaults, or null when none do. Shared by the
    /// headless path and the Sort panel so their fingerprints agree; cached with the profile, so free per frame.
    /// </summary>
    public static CategoryPlacement? PlacementFor(Configuration config) => LayoutProfileCache.For(config).Placement;

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

        // The splits come from the category layouts (the legacy fields are only their rollback mirror).
        var layout = LayoutProfileCache.For(config).ToLegacy();
        var race = RaceSplitOptions.FromConfig(config);
        return new SortSelection(
            strategy, layout.SplitGear, layout.SplitNpc, layout.SplitHair,
            SortPanel.HairRaceCodeAt(SortPanel.HairRaceIndexFor(config.HairRaceCode)),
            config.MidlanderFallback, layout.SplitGearBody, layout.SplitBodyMods,
            priority,
            new LayoutOptions(
                layout.SharedHandsFeet, race.InEffect(RaceRows.Hair, layout.SplitBodyMods),
                layout.SplitEmotes, race.NullIfNoEffect(),
                Rules: RulesFor(config),
                Placement: PlacementFor(config)),
            RootOnly: false);
    }
}

/// <summary>The one place a SortSelection becomes a call to <see cref="OrganizerState.Sort"/>.</summary>
public static class SortRunner
{
    public static int Run(
        OrganizerState state, SortSelection selection, Func<string, string> canonicalizeCreator, SortScope? scope)
    {
        var count = state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, selection.SplitHair,
            canonicalizeCreator, selection.HairPriority, selection.SplitGearBody, selection.SplitBodyMods,
            selection.BodyPriority, selection.Layout, scope);

        // A sort of part of the library (a root-only pass or a chosen set) leaves the rest at its old paths, so
        // the staged paths are not one Sort's result and "As suggested by Sort" must stay unavailable.
        if (scope is null || scope.Identifiers is null && !scope.RootOnly)
            state.MarkSortedWith(selection.Fingerprint());
        else
            state.InvalidateSortFingerprint();
        return count;
    }
}
