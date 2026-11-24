namespace PenumbraOrganizer.Plugin.Organizer.NewMods;

/// <summary>The new-mod popup as data: the question, the warnings that apply right now, the buttons.</summary>
/// <param name="ToolLine">
/// One extra line for new Heliosphere/Proteus mods of the same batch that their Protected mode keeps in place
/// (NewModPromptText.BannerToolLine), or null.
/// </param>
public sealed record NewModBanner(int Count, string Message, IReadOnlyList<string> Warnings, string? ToolLine = null);

/// <summary>
/// The popup's notice when every new mod is one a tool's Protected mode keeps in place: nothing to sort,
/// but the user is told why, with the way to the Protect tab.
/// </summary>
public sealed record NewModToolNotice(ToolHeldCounts Counts, string Message);

/// <summary>Every line the new-mod popup, the confirm window and chat use. Plain ASCII.</summary>
public static class NewModPromptText
{
    public const string SortNowLabel = "Sort now";
    public const string ReviewFirstLabel = "Review first";
    public const string NotNowLabel = "Not now";

    private const string Prefix = "Penumbra Organizer: ";
    private const string StagedWarning = "Sorting rescans your library first: your unapplied sort will be discarded.";
    private const string OverrideWarning = "This session's unprotect choices for Heliosphere/Proteus mods will be reset.";
    private const string StillPending = " They stay pending.";

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

    public const string OpenProtectTabLabel = "Open Protect tab";
    public const string ToolNoticeOkLabel = "OK";
    public const string ToolNoticeTitle = "New tool-managed mods";
    private const string ToolNoticeHint = "You can change this on the Protect tab.";

    /// <summary>The notice for new mods that only a tool's Protected mode holds back. Follows the Protect tab.</summary>
    public static NewModToolNotice ToolNotice(ToolHeldCounts held)
    {
        var parts = new List<string>();
        if (held.Heliosphere > 0)
            parts.Add(ToolNoticeSentence(held.Heliosphere, "Heliosphere"));
        if (held.Proteus > 0)
            parts.Add(ToolNoticeSentence(held.Proteus, "Proteus"));
        parts.Add(ToolNoticeHint);
        return new NewModToolNotice(held, string.Join(" ", parts));
    }

    /// <summary>The banner's one extra line for the held tool mods of a mixed batch, or null when there are none.</summary>
    public static string? BannerToolLine(ToolHeldCounts held)
    {
        if (held.Total <= 0)
            return null;

        var counts = new List<string>();
        if (held.Heliosphere > 0)
            counts.Add(ModCount(held.Heliosphere, "Heliosphere"));
        if (held.Proteus > 0)
            counts.Add(ModCount(held.Proteus, "Proteus"));

        var stays = held.Total == 1
            ? "It is Protected, so it stays where it is."
            : "They are Protected, so they stay where they are.";
        return $"Also added: {string.Join(" and ", counts)}. {stays} {ToolNoticeHint}";
    }

    /// <summary>The confirm window's tag for a tool-managed mod: "Heliosphere", "Proteus", or null.</summary>
    public static string? ToolTag(bool heliosphereManaged, ProteusRole proteusRole) =>
        proteusRole != ProteusRole.None ? "Proteus"
        : heliosphereManaged ? "Heliosphere"
        : null;

    private static string ToolNoticeSentence(int count, string tool) => count == 1
        ? $"1 new {tool} mod added. {tool} mods are Protected, so it stays where it is."
        : $"{count} new {tool} mods added. {tool} mods are Protected, so they stay where they are.";

    private static string ModCount(int count, string tool) => count == 1 ? $"1 {tool} mod" : $"{count} {tool} mods";

    public const string PopupTitle = "New mods";
    public const string DontShowPopupLabel = "Don't show this popup";
    public const string ShowPopupSettingLabel = "Show a popup when new mods are found";
    public const string ConfirmTitle = "Confirm sort";
    public const string ApplyLabel = "Apply";
    public const string CancelLabel = "Cancel";
    public const string Preparing = "Scanning your library before sorting...";

    /// <summary>Why a confirm window was closed without applying: its staged sort no longer matches the library.</summary>
    public const string ConfirmStale = "The library changed before the sort was confirmed, so nothing was applied.";

    /// <summary>
    /// The one chat line sent instead of the popup when the user has turned the popup off. "Auto sort" is the Sort
    /// tab card that holds the popup setting (SortPanel.DrawSortSettings).
    /// </summary>
    public static string PopupOff(int count) => count == 1
        ? Prefix + "1 new mod is loose in the root. Turn the popup back on in Sort > Auto sort to sort it."
        : Prefix + $"{count} new mods are loose in the root. Turn the popup back on in Sort > Auto sort to sort them.";

    /// <summary>After /porganizer sortnew or sortroot staged its sort: the confirm window is open in game.</summary>
    public static string ConfirmOpened(int count, bool newMods)
    {
        var kind = newMods ? "new" : "loose";
        var subject = count == 1 ? $"1 {kind} mod is" : $"{count} {kind} mods are";
        return Prefix + subject + " ready to move. Check the list in the window that opened, then Apply.";
    }

    /// <summary>The confirm window's Cancel: the staged sort was discarded.</summary>
    public static string Cancelled(bool newMods) =>
        Prefix + "sort cancelled. Nothing was moved." + (newMods ? StillPending : string.Empty);

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
