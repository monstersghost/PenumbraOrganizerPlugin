namespace PenumbraOrganizer.Plugin.Organizer.NewMods;

/// <summary>The new-mod banner as data: the question, the warnings that apply right now, the buttons.</summary>
public sealed record NewModBanner(int Count, string Message, IReadOnlyList<string> Warnings);

/// <summary>Every line the new-mod banner and chat use. Plain ASCII.</summary>
public static class NewModPromptText
{
    public const string SortNowLabel = "Sort now";
    public const string ReviewFirstLabel = "Review first";
    public const string NotNowLabel = "Not now";

    private const string Prefix = "Penumbra Organizer: ";
    private const string StagedWarning = "Sorting rescans your library first: your unapplied sort will be discarded.";
    private const string OverrideWarning = "This session's unprotect choices for Heliosphere/Proteus mods will be reset.";
    private const string StillPending = " They stay pending for /porganizer sortnew.";

    public static NewModBanner Banner(int count, bool hasStagedProposals, bool hasSessionOverrides)
    {
        var message = count == 1
            ? "1 new mod is loose in the root. Sort it?"
            : $"{count} new mods are loose in the root. Sort them?";

        var warnings = new List<string>();
        if (hasStagedProposals)
            warnings.Add(StagedWarning);
        if (hasSessionOverrides)
            warnings.Add(OverrideWarning);

        return new NewModBanner(count, message, warnings);
    }

    /// <summary>The one chat line sent instead of the banner when the window is closed.</summary>
    public static string WindowClosed(int count) => count == 1
        ? Prefix + "1 new mod. /porganizer sortnew sorts it, or open /porganizer."
        : Prefix + $"{count} new mods. /porganizer sortnew sorts them, or open /porganizer.";

    /// <summary>After a headless Apply: the Review modal's Rediscover reminder, which is never shown here.</summary>
    public static string Moved(int count, bool newMods, int missing)
    {
        var kind = newMods ? "new" : "loose";
        var noun = count == 1 ? "mod" : "mods";
        return Prefix + $"moved {count} {kind} {noun}." + Missing(missing)
            + " Undo from the History tab. In Penumbra, Settings > Rediscover Mods removes any empty folders left behind.";
    }

    public static string NothingToMove(bool newMods, int missing) =>
        Prefix + $"nothing to move. The {(newMods ? "new" : "loose")} mods are already where a sort would put them."
        + Missing(missing);

    public static string Failed(string reason, bool stillPending) =>
        Prefix + $"could not sort the {(stillPending ? "new mods" : "mods")}. {Sentence(reason)}"
        + (stillPending ? StillPending : string.Empty);

    public static string GaveUpWaiting(string reason, bool stillPending) =>
        Prefix + $"gave up waiting to sort the {(stillPending ? "new mods" : "mods")} after 2 minutes: {Sentence(reason)}"
        + (stillPending ? StillPending : string.Empty);

    /// <summary>Review first: the proposals are staged and the Review tab is open.</summary>
    public static string ReviewOpened(int changed) => changed == 1
        ? "1 new mod is ready to move. Check it on the Review tab, then Apply."
        : $"{changed} new mods are ready to move. Check them on the Review tab, then Apply.";

    public static string NothingPending =>
        "No new mods since the plugin loaded. /porganizer sortroot sorts every loose mod in the root.";

    public static string StagedWithoutForce(CommandKind kind) =>
        "You have an unapplied sort (or unprotect choices) that a rescan would discard. Apply or clear it on the "
        + $"Review tab, or run /porganizer {(kind == CommandKind.SortRoot ? "sortroot" : "sortnew")} force.";

    public static string Busy(string reason) => Sentence(reason) + " Try again when it finishes.";

    public static string AlreadyRunning => Busy("Penumbra Organizer is already sorting new mods.");

    private static string Missing(int missing) => missing switch
    {
        <= 0 => string.Empty,
        1 => " 1 new mod was not sorted: it was removed, moved into a folder, or protected.",
        _ => $" {missing} new mods were not sorted: they were removed, moved into a folder, or protected.",
    };

    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return "Unknown error.";
        return trimmed[^1] is '.' or '!' or '?' ? trimmed : trimmed + ".";
    }
}
