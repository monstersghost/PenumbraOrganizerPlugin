namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The optional layout splits on the Sort tab. All default to off, which is the 0.6.4.0 layout.
/// A null <c>LayoutOptions?</c> anywhere means all off.
/// </summary>
public readonly record struct LayoutOptions(
    bool SharedHandsAndFeet = false, bool SplitHairByRace = false, bool SplitEmotes = false);
