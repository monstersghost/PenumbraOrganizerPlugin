using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.NewMods;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NewMods;

public class NewModPromptTextTests
{
    [Fact]
    public void Banner_Plural()
    {
        var banner = NewModPromptText.Banner(3, false, false);
        Assert.Equal(3, banner.Count);
        Assert.Equal("3 new mods are loose in the root. Sort them?", banner.Message);
        Assert.Empty(banner.Warnings);
    }

    [Fact]
    public void Banner_Singular()
    {
        Assert.Equal("1 new mod is loose in the root. Sort it?", NewModPromptText.Banner(1, false, false).Message);
    }

    [Fact]
    public void Banner_StagedWarningOnlyWhenTrue()
    {
        var banner = NewModPromptText.Banner(2, true, false);
        Assert.Equal(
            ["Sorting rescans your library first: your unapplied sort will be discarded."],
            banner.Warnings);
    }

    [Fact]
    public void Banner_OverrideWarningOnlyWhenTrue()
    {
        var banner = NewModPromptText.Banner(2, false, true);
        Assert.Equal(
            ["This session's unprotect choices for Heliosphere/Proteus mods will be reset."],
            banner.Warnings);
    }

    [Fact]
    public void Banner_BothWarnings()
    {
        Assert.Equal(2, NewModPromptText.Banner(2, true, true).Warnings.Count);
    }

    [Fact]
    public void ButtonLabels()
    {
        Assert.Equal("Sort now", NewModPromptText.SortNowLabel);
        Assert.Equal("Review first", NewModPromptText.ReviewFirstLabel);
        Assert.Equal("Not now", NewModPromptText.NotNowLabel);
    }

    [Fact]
    public void PopupOff_ChatLine()
    {
        Assert.Equal(
            "Penumbra Organizer: 3 new mods are loose in the root. Turn the popup back on in Sort > Advanced to sort them.",
            NewModPromptText.PopupOff(3));
        Assert.Equal(
            "Penumbra Organizer: 1 new mod is loose in the root. Turn the popup back on in Sort > Advanced to sort it.",
            NewModPromptText.PopupOff(1));
    }

    [Fact]
    public void PopupAndConfirmLabels()
    {
        Assert.Equal("New mods", NewModPromptText.PopupTitle);
        Assert.Equal("Don't show this popup", NewModPromptText.DontShowPopupLabel);
        Assert.Equal("Show a popup when new mods are found", NewModPromptText.ShowPopupSettingLabel);
        Assert.Equal("Confirm sort", NewModPromptText.ConfirmTitle);
        Assert.Equal("Apply", NewModPromptText.ApplyLabel);
        Assert.Equal("Cancel", NewModPromptText.CancelLabel);
        Assert.Equal("Scanning your library before sorting...", NewModPromptText.Preparing);
    }

    [Fact]
    public void ConfirmOpened_ChatLine()
    {
        Assert.Equal(
            "Penumbra Organizer: 2 new mods are ready to move. Check the list in the window that opened, then Apply.",
            NewModPromptText.ConfirmOpened(2, newMods: true));
        Assert.Equal(
            "Penumbra Organizer: 1 loose mod is ready to move. Check the list in the window that opened, then Apply.",
            NewModPromptText.ConfirmOpened(1, newMods: false));
    }

    [Fact]
    public void Cancelled_AndStale()
    {
        Assert.Equal(
            "Penumbra Organizer: sort cancelled. Nothing was moved. They stay pending.",
            NewModPromptText.Cancelled(newMods: true));
        Assert.Equal("Penumbra Organizer: sort cancelled. Nothing was moved.", NewModPromptText.Cancelled(newMods: false));
        Assert.Equal(
            "The library changed before the sort was confirmed, so nothing was applied.",
            NewModPromptText.ConfirmStale);
    }

    [Fact]
    public void Moved_IncludesUndoAndRediscoverNote()
    {
        Assert.Equal(
            "Penumbra Organizer: moved 3 new mods. Undo from the History tab. In Penumbra, Settings > " +
            "Rediscover Mods removes any empty folders left behind.",
            NewModPromptText.Moved(3, newMods: true, missing: 0));
    }

    [Fact]
    public void Moved_SingularAndRootWording()
    {
        Assert.StartsWith("Penumbra Organizer: moved 1 new mod. ", NewModPromptText.Moved(1, true, 0));
        Assert.StartsWith("Penumbra Organizer: moved 4 loose mods. ", NewModPromptText.Moved(4, false, 0));
    }

    [Fact]
    public void Moved_CountsMissing()
    {
        var text = NewModPromptText.Moved(2, true, missing: 1);
        Assert.Contains("1 new mod was not sorted: it was removed, moved into a folder, or protected.", text);
        Assert.Contains("Rediscover Mods", text);

        Assert.Contains(
            "2 new mods were not sorted: they were removed, moved into a folder, or protected.",
            NewModPromptText.Moved(2, true, missing: 2));
    }

    [Fact]
    public void NothingToMove()
    {
        Assert.Equal(
            "Penumbra Organizer: nothing to move. The new mods are already where a sort would put them.",
            NewModPromptText.NothingToMove(true, 0));
        Assert.Equal(
            "Penumbra Organizer: nothing to move. The loose mods are already where a sort would put them.",
            NewModPromptText.NothingToMove(false, 0));
    }

    [Fact]
    public void Failed_KeepsPendingHint()
    {
        Assert.Equal(
            "Penumbra Organizer: could not sort the new mods. Boom. They stay pending.",
            NewModPromptText.Failed("Boom.", stillPending: true));
        Assert.Equal(
            "Penumbra Organizer: could not sort the mods. Boom.",
            NewModPromptText.Failed("Boom.", stillPending: false));
    }

    [Fact]
    public void GaveUpWaiting()
    {
        Assert.Equal(
            "Penumbra Organizer: gave up waiting to sort the new mods after 2 minutes: Busy. " +
            "They stay pending.",
            NewModPromptText.GaveUpWaiting("Busy.", stillPending: true));
    }

    [Fact]
    public void CommandLines()
    {
        Assert.Equal(
            "No new mods since the plugin loaded. /porganizer sortroot sorts every loose mod in the root.",
            NewModPromptText.NothingPending);
        Assert.Equal(
            "You have an unapplied sort (or unprotect choices) that a rescan would discard. Apply or clear it " +
            "on the Review tab, or run /porganizer sortnew force.",
            NewModPromptText.StagedWithoutForce(CommandKind.SortNew));
        Assert.EndsWith("/porganizer sortroot force.", NewModPromptText.StagedWithoutForce(CommandKind.SortRoot));
        Assert.Equal(
            "An operation is running. Try again when it finishes.",
            NewModPromptText.Busy("An operation is running."));
        Assert.Equal(
            "An operation is running. Try again when it finishes.",
            NewModPromptText.Busy("  An operation is running  "));
    }

    [Fact]
    public void EveryLine_IsAscii()
    {
        var banner = NewModPromptText.Banner(3, true, true);
        string[] lines =
        [
            banner.Message, .. banner.Warnings,
            NewModPromptText.SortNowLabel, NewModPromptText.ReviewFirstLabel, NewModPromptText.NotNowLabel,
            NewModPromptText.PopupOff(3), NewModPromptText.Moved(3, true, 2), NewModPromptText.Moved(1, false, 1),
            NewModPromptText.NothingToMove(true, 1), NewModPromptText.Failed("x", true),
            NewModPromptText.GaveUpWaiting("x", true), NewModPromptText.ReviewOpened(3),
            NewModPromptText.NothingPending, NewModPromptText.StagedWithoutForce(CommandKind.SortNew),
            NewModPromptText.Busy("x"),
            NewModPromptText.PopupTitle, NewModPromptText.DontShowPopupLabel, NewModPromptText.ShowPopupSettingLabel,
            NewModPromptText.ConfirmTitle, NewModPromptText.ApplyLabel, NewModPromptText.CancelLabel,
            NewModPromptText.Preparing, NewModPromptText.ConfirmOpened(2, true), NewModPromptText.Cancelled(true),
            NewModPromptText.ConfirmStale,
        ];
        foreach (var line in lines)
            Assert.All(line, c => Assert.True(c < 128, $"non-ASCII code point {(int)c} in: {line}"));
    }
}
