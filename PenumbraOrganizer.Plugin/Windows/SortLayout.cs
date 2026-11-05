using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>Whether a control is drawn at all, and whether it can be used when it is.</summary>
public readonly record struct ControlState(bool Visible, bool Enabled);

/// <summary>The state of every control on the Sort tab's first section and Body priority section.</summary>
public readonly record struct SortControlStates(
    ControlState GroupBy,
    ControlState SplitGear,
    ControlState SplitNpc,
    ControlState SplitHair,
    ControlState PriorityRace,
    ControlState MidlanderFallback,
    ControlState SplitGearBody,
    ControlState SplitBodyMods,
    ControlState FirstChoiceBody,
    ControlState FullPriorityList,
    ControlState ScrapedNpcCheckbox,
    ControlState WikiRefresh,
    ControlState SortButton);

/// <summary>
/// Which Sort tab controls show, and which are enabled, as one pure function so the "hidden until
/// ticked" and "greyed under Creator grouping" rules are tested rather than read off ImGui calls.
/// </summary>
/// <remarks>
/// public, not internal: this repo has no InternalsVisibleTo.
/// <para>
/// HIDDEN means not drawn: a dependent of an unticked box, and a feature that is switched off.
/// GREYED means drawn but disabled: the splits under Creator grouping (they never change a
/// by-creator path), and any dependent whose own box is ticked, so its value is preserved and it
/// comes back usable when the strategy changes back. A closed activity gate disables everything
/// that acts, and hides nothing.
/// </para>
/// </remarks>
public static class SortLayout
{
    public static SortControlStates Compute(
        SortStrategy strategy, bool splitHair, bool scrapedFeatureEnabled, bool activityGateOpen)
    {
        var splitsApply = strategy != SortStrategy.CreatorOnly;
        var splitEnabled = splitsApply && activityGateOpen;

        return new SortControlStates(
            GroupBy: new(true, activityGateOpen),
            SplitGear: new(true, splitEnabled),
            SplitNpc: new(true, splitEnabled),
            SplitHair: new(true, splitEnabled),
            PriorityRace: new(splitHair, splitEnabled),
            MidlanderFallback: new(splitHair, splitEnabled),
            SplitGearBody: new(true, splitEnabled),
            SplitBodyMods: new(true, splitEnabled),
            FirstChoiceBody: new(true, activityGateOpen),
            FullPriorityList: new(true, activityGateOpen),
            ScrapedNpcCheckbox: new(scrapedFeatureEnabled, activityGateOpen),
            WikiRefresh: new(scrapedFeatureEnabled, activityGateOpen),
            SortButton: new(true, activityGateOpen));
    }
}
