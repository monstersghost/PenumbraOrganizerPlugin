namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The topics a control is intended to show as a tooltip. Hand-maintained: adding a
/// <c>Help.Tooltip</c> call means adding the topic here too. See the comment on
/// <c>EveryTopicWithAShort_IsDeclaredAsUsedByAControl</c> for what this does and does not prove.
/// </summary>
/// <remarks>
/// public because the test project reads it and this repo has no <c>InternalsVisibleTo</c>.
/// </remarks>
public static class HelpTopicUsage
{
    // In tab order, matching the order the controls are drawn.
    public static IReadOnlyList<HelpTopic> ReferencedByControls { get; } =
    [
        // Scan - MainWindow.ScanTab.cs
        HelpTopics.ScanRefreshModList,
        HelpTopics.ScanEventLog,

        // Protect - MainWindow.ProtectTab.cs
        HelpTopics.ProtectToggleAll,
        HelpTopics.ProtectToggleHeliosphere,
        HelpTopics.ProtectFolders,
        HelpTopics.ProtectMods,
        HelpTopics.ProtectHeliosphereNote,

        // Sort - SortPanel.cs and MainWindow.SortTab.cs
        HelpTopics.SortGrouping,
        HelpTopics.SortSplitGear,
        HelpTopics.SortSplitNpc,
        HelpTopics.SortButton,
        HelpTopics.SortScrapedNpcList,
        HelpTopics.SortImportWorkbook,
        HelpTopics.SortManualAssign,
        HelpTopics.SortRefreshNpcList,

        // Templates - MainWindow.TemplatesExport.cs
        HelpTopics.TemplatesExport,
        HelpTopics.TemplatesExportName,
        HelpTopics.TemplatesExportFallback,
        HelpTopics.TemplatesExportFolders,
        HelpTopics.TemplatesExportSearch,
        HelpTopics.TemplatesExportSave,
        HelpTopics.TemplatesExportShareCode,

        // Review Changes - MainWindow.ReviewTab.cs
        HelpTopics.ReviewApply,
        HelpTopics.ReviewExport,
        HelpTopics.ReviewShowConfigFile,
        HelpTopics.ReviewDiagnosticDump,
        HelpTopics.ReviewWorkbookDestinations,
        HelpTopics.ReviewExportWorkbook,
        HelpTopics.ReviewProtectAndSkip,
        HelpTopics.ReviewRereadOrganizationJson,
        HelpTopics.ReviewCleanUpFolders,
        HelpTopics.ReviewRollbackFolderCleanup,

        // History - MainWindow.HistoryTab.cs
        HelpTopics.HistoryCreateBackup,
        HelpTopics.HistoryRestore,
        HelpTopics.HistoryDeleteSnapshot,

        // Search - MainWindow.SearchTab.cs
        HelpTopics.SearchBuildIndex,
        HelpTopics.SearchNameFilter,
        HelpTopics.SearchItemFilter,
        HelpTopics.SearchCategories,
        HelpTopics.SearchSlots,

        // Recovery panel - MainWindow.Recovery.cs, above the tab bar
        HelpTopics.RecoveryKeepCurrent,
        HelpTopics.RecoveryContinue,
        HelpTopics.RecoveryRestorePrevious,
        HelpTopics.RecoveryKeepCurrentOne,
        HelpTopics.RecoveryCloseAll,
    ];
}
