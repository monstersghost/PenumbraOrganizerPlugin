namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The two optional layout splits on the Sort tab. Both default to off, which is the 0.6.4.0 layout.
/// A null <c>LayoutOptions?</c> anywhere means both off.
/// </summary>
public readonly record struct LayoutOptions(bool SharedHandsAndFeet = false, bool SplitHairByRace = false);
