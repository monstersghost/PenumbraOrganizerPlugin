namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The folder below <c>Animation and VFX/Emotes</c> when emotes are split: the emote a mod replaces,
/// exactly as Penumbra names it. Facial expressions are usually bundled with a real emote, so they
/// only count when a mod has nothing else.
/// </summary>
public static class EmoteFolders
{
    public const string Multiple = "Multiple";
    public const string ExpressionsFolder = "Expressions";
    public const string Undetermined = "Undetermined";

    /// <summary>The game's Expressions emote category. An emote not listed here counts as a real emote.</summary>
    public static readonly IReadOnlySet<string> Expressions = new HashSet<string>(
        ["Endure", "Ponder", "Reflect", "Big Grin"], StringComparer.OrdinalIgnoreCase);

    public static string Tail(IReadOnlySet<string>? names)
    {
        if (names is null || names.Count == 0)
            return Undetermined;

        var primary = names.Where(n => !Expressions.Contains(n)).ToList();
        if (primary.Count == 0)
            return ExpressionsFolder;
        if (primary.Count > 1)
            return Multiple;
        return PenumbraPathSemantics.FixName(primary[0]);
    }
}
