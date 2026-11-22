using PenumbraOrganizer.Plugin.Organizer.Rules;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The optional layout splits on the Sort tab. All default to off, which is the 0.6.4.0 layout.
/// A null <c>LayoutOptions?</c> anywhere means all off.
/// </summary>
/// <remarks>
/// <see cref="SplitHairByRace"/> means "the Hair row of the race split is in effect"; the factory derives it
/// from <see cref="Race"/>. <see cref="Race"/> carries the rest of the Race section; null means no race
/// split beyond the legacy hair flag, with the multi-race toggle on and no priority, which is today's
/// behavior. <see cref="Rules"/> is the Sort tab's keyword rule list; null or empty means none. The record struct compares <see cref="Race"/> by value (it is a record whose priority has
/// sequence equality), which the "selection changed since the last sort" check relies on.
/// </remarks>
public readonly record struct LayoutOptions(
    bool SharedHandsAndFeet = false, bool SplitHairByRace = false, bool SplitEmotes = false,
    RaceSplitOptions? Race = null, RuleSet? Rules = null);
