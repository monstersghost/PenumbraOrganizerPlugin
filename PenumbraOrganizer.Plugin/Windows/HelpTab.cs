using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// One collapsible block of the Help tab: its own explanation, then the controls it covers.
/// </summary>
/// <remarks>
/// An ordered list of topics rather than a flat lookup, because the order is the content - "the Sort
/// section is these controls, in the order you meet them" is not expressible in a dictionary.
/// </remarks>
public sealed record HelpSection(HelpTopic Topic, IReadOnlyList<HelpTopic> Controls)
{
    public HelpSection(HelpTopic topic) : this(topic, []) { }
}

/// <summary>
/// The Help tab: every explanation the plugin holds, at reading length rather than tooltip length.
/// </summary>
/// <remarks>
/// A standalone type rather than a <c>MainWindow.HelpTab.cs</c> partial, deliberately breaking
/// piece 0's one-partial-per-tab convention. This tab's content is the only tab content worth unit
/// testing, and a partial of <c>MainWindow</c> cannot be reached from a test without constructing
/// <c>MainWindow</c> - which needs a live Dalamud. Do not "restore consistency" by folding it back in.
/// <para>
/// public, and its data members with it: this repo has no <c>InternalsVisibleTo</c>.
/// </para>
/// <para>
/// It touches nothing outside the plugin - no Penumbra IPC, no mod-library read or write, no file
/// write. Drawing it can never change anything.
/// </para>
/// </remarks>
public static class HelpTab
{
    /// <summary>
    /// Pinned to a release tag, never to main: main serves the newest guide to someone on an older
    /// build, describing controls their version does not have.
    /// </summary>
    /// <remarks>
    /// This 404s until the matching tag is pushed, which happens at publish time. No test can catch
    /// that - <c>TheGitHubLink_CarriesAVersionTag_NotMain</c> passes against a tag that does not
    /// exist yet - so "push the tag before announcing" is on the release checklist instead.
    /// </remarks>
    public const string GuideUrl =
        "https://github.com/monstersghost/PenumbraOrganizerPlugin/blob/0.6.0.1/docs/USER_GUIDE.md";

    /// <summary>
    /// The support Discord. A permanent invite, deliberately - a `discord.gg` code that can expire
    /// is baked into every shipped binary, and a dead one cannot be fixed without a new release.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GuideUrl"/> this is NOT version-pinned and must not be: an older build
    /// pointing at a still-valid invite is exactly what you want.
    /// </remarks>
    public const string DiscordUrl = "https://discord.gg/NHssF8ux6B";

    /// <summary>
    /// Reading order, which is not tab order: what the plugin does and what is safe come first,
    /// because they are what a new user needs before touching anything.
    /// </summary>
    public static IReadOnlyList<HelpSection> Sections { get; } =
    [
        new(HelpTopics.HelpWhatThisDoes),
        new(HelpTopics.HelpSafetyRules),

        new(HelpTopics.HelpTabScan,
        [
            HelpTopics.ScanRefreshModList,
            HelpTopics.ScanEventLog,
        ]),
        new(HelpTopics.HelpTabProtect,
        [
            HelpTopics.ProtectToggleAll,
            HelpTopics.ProtectToggleHeliosphere,
            HelpTopics.ProtectFolders,
            HelpTopics.ProtectMods,
            HelpTopics.ProtectHeliosphereNote,
            HelpTopics.ProtectStrandedAtRoot,
        ]),
        new(HelpTopics.HelpTabSort,
        [
            HelpTopics.SortGrouping,
            HelpTopics.SortSplitGear,
            HelpTopics.SortSplitNpc,
            HelpTopics.SortButton,
            HelpTopics.SortScrapedNpcList,
            HelpTopics.SortRefreshNpcList,
            HelpTopics.SortImportWorkbook,
            HelpTopics.SortManualAssign,
        ]),
        new(HelpTopics.HelpTabReview,
        [
            HelpTopics.ReviewApply,
            HelpTopics.ReviewProtectAndSkip,
            HelpTopics.ReviewExport,
            HelpTopics.ReviewWorkbookDestinations,
            HelpTopics.ReviewExportWorkbook,
            HelpTopics.ReviewRereadOrganizationJson,
            HelpTopics.ReviewCleanUpFolders,
            HelpTopics.ReviewRollbackFolderCleanup,
            HelpTopics.ReviewShowConfigFile,
            HelpTopics.ReviewDiagnosticDump,
        ]),
        new(HelpTopics.HelpTabHistory,
        [
            HelpTopics.HistoryCreateBackup,
            HelpTopics.HistoryRestore,
            HelpTopics.HistoryDeleteSnapshot,
        ]),
        new(HelpTopics.HelpTabSearch,
        [
            HelpTopics.SearchBuildIndex,
            HelpTopics.SearchNameFilter,
            HelpTopics.SearchItemFilter,
            HelpTopics.SearchCategories,
            HelpTopics.SearchSlots,
        ]),
        new(HelpTopics.HelpTabTemplates,
        [
            HelpTopics.TemplatesRefreshList,
            HelpTopics.TemplatesOpenFolder,
            HelpTopics.TemplatesImportFile,
            HelpTopics.TemplatesPreview,
            HelpTopics.TemplatesApply,
            HelpTopics.TemplatesExport,
            HelpTopics.TemplatesExportName,
            HelpTopics.TemplatesExportFallback,
            HelpTopics.TemplatesExportFolders,
            HelpTopics.TemplatesExportSearch,
            HelpTopics.TemplatesExportSave,
            HelpTopics.TemplatesExportShareCode,
        ]),

        new(HelpTopics.HelpWhenThingsGoWrong,
        [
            HelpTopics.RecoveryKeepCurrent,
            HelpTopics.RecoveryContinue,
            HelpTopics.RecoveryRestorePrevious,
            HelpTopics.RecoveryKeepCurrentOne,
            HelpTopics.RecoveryCloseAll,
        ]),
        new(HelpTopics.HelpWhereYourFilesAre),
    ];

    /// <param name="onShowWalkthrough">
    /// Piece 5's reserved slot. Null until the walkthrough exists, and the button is not drawn at
    /// all rather than drawn disabled - a control that does nothing yet is worse than no control.
    /// </param>
    public static void Draw(Action? onShowWalkthrough = null)
    {
        using var tab = ImRaii.TabItem("Help");
        if (!tab)
            return;

        // No tooltips on any of these: each label already says exactly what it does, which is the
        // same reason the coverage list skips Cancel and the Yes/No confirmations.
        //
        // Support first and visually distinct, following Penumbra's own Help layout - someone who
        // opens this tab because something went wrong should not have to read past the reference
        // material to find a human.
        using (PluginTheme.PrimaryButton())
        {
            if (ImGui.Button("Join the Discord for support"))
                OpenUrl(DiscordUrl);
        }

        ImGui.SameLine();
        if (ImGui.Button("Open the full guide in your browser"))
            OpenUrl(GuideUrl);

        if (onShowWalkthrough is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Show the walkthrough"))
                onShowWalkthrough();
        }

        ImGui.Spacing();

        using var child = ImRaii.Child("HelpSections", new Vector2(0, -1), border: true);
        if (!child)
            return;

        // Wrapped at the child's own width rather than a fixed position: this is body text, and the
        // window is resizable.
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);

        foreach (var section in Sections)
        {
            if (!ImGui.CollapsingHeader(Help.Title(section.Topic) ?? section.Topic.Id))
                continue;

            if (Help.Body(section.Topic) is { } body)
                ImGui.TextUnformatted(body);

            foreach (var control in section.Controls)
            {
                ImGui.Spacing();
                ImGui.TextColored(PluginTheme.ChangedGood, Help.Title(control) ?? control.Id);
                // Falls back to short: a control's tooltip line is a perfectly good reference entry,
                // and requiring a separate body for all 38 would mean writing the same sentence
                // twice or leaving the entry blank.
                // Parenthesised deliberately: `a ?? b is { } t` binds as `a ?? (b is { } t)`, which
                // is a different expression entirely and does not compile against a string.
                if ((Help.Body(control) ?? Help.Short(control)) is { } text)
                    ImGui.TextUnformatted(text);
            }

            ImGui.Spacing();
        }

        ImGui.PopTextWrapPos();
    }

    // Uses the OS handler rather than any in-game browser: Dalamud plugins have no way to open a
    // page in game, and silently doing nothing would look like a broken button.
    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // A failed browser launch must not take the draw call with it - this runs inside the
            // frame, and an exception here would tear down the whole window.
            Plugin.Log.Warning(ex, $"Could not open {url} in a browser.");
        }
    }
}
