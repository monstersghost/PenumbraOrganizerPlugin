using System.Reflection;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// Every help topic the code refers to. Pieces 3, 4 and 5 add to this; piece 2 only needs the
/// sort controls.
/// </summary>
public static class HelpTopics
{
    // Scan
    public static readonly HelpTopic ScanRefreshModList = new("scan.refresh-mod-list");
    public static readonly HelpTopic ScanEventLog = new("scan.event-log");

    // Protect
    public static readonly HelpTopic ProtectToggleAll = new("protect.toggle-all");
    public static readonly HelpTopic ProtectToggleHeliosphere = new("protect.toggle-heliosphere");
    public static readonly HelpTopic ProtectFolders = new("protect.folders");
    public static readonly HelpTopic ProtectMods = new("protect.mods");
    public static readonly HelpTopic ProtectHeliosphereNote = new("protect.heliosphere-note");

    // Sort
    public static readonly HelpTopic SortGrouping = new("sort.grouping");
    public static readonly HelpTopic SortSplitGear = new("sort.split-gear");
    public static readonly HelpTopic SortSplitNpc = new("sort.split-npc");
    public static readonly HelpTopic SortButton = new("sort.button");
    public static readonly HelpTopic SortScrapedNpcList = new("sort.scraped-npc-list");
    public static readonly HelpTopic SortImportWorkbook = new("sort.import-workbook");
    public static readonly HelpTopic SortManualAssign = new("sort.manual-assign");

    // Review Changes
    public static readonly HelpTopic ReviewApply = new("review.apply");
    public static readonly HelpTopic ReviewExport = new("review.export");
    public static readonly HelpTopic ReviewShowConfigFile = new("review.show-config-file");
    public static readonly HelpTopic ReviewDiagnosticDump = new("review.diagnostic-dump");
    public static readonly HelpTopic ReviewWorkbookDestinations = new("review.workbook-destinations");
    public static readonly HelpTopic ReviewExportWorkbook = new("review.export-workbook");
    public static readonly HelpTopic ReviewProtectAndSkip = new("review.protect-and-skip");
    public static readonly HelpTopic ReviewRereadOrganizationJson = new("review.reread-organization-json");
    public static readonly HelpTopic ReviewCleanUpFolders = new("review.clean-up-folders");
    public static readonly HelpTopic ReviewRollbackFolderCleanup = new("review.rollback-folder-cleanup");

    // History
    public static readonly HelpTopic HistoryCreateBackup = new("history.create-backup");
    public static readonly HelpTopic HistoryRestore = new("history.restore");
    public static readonly HelpTopic HistoryDeleteSnapshot = new("history.delete-snapshot");

    // Search
    public static readonly HelpTopic SearchBuildIndex = new("search.build-index");
    public static readonly HelpTopic SearchNameFilter = new("search.name-filter");
    public static readonly HelpTopic SearchItemFilter = new("search.item-filter");
    public static readonly HelpTopic SearchCategories = new("search.categories");
    public static readonly HelpTopic SearchSlots = new("search.slots");

    // The recovery panel, above the tab bar. Two mutually exclusive branches: one interrupted
    // operation, or several blocked roots. Both are covered - these are the most consequential
    // controls in the plugin and the least self-explanatory.
    public static readonly HelpTopic RecoveryKeepCurrent = new("recovery.keep-current");
    public static readonly HelpTopic RecoveryContinue = new("recovery.continue");
    public static readonly HelpTopic RecoveryRestorePrevious = new("recovery.restore-previous");
    public static readonly HelpTopic RecoveryKeepCurrentOne = new("recovery.keep-current-one");
    public static readonly HelpTopic RecoveryCloseAll = new("recovery.close-all");

    /// <summary>
    /// Every constant declared above, found by reflection rather than repeated in a hand-written
    /// array. The tests assert properties of "all topics", and a hand-maintained list would let a
    /// new constant be added without ever being checked - which is the exact failure the tests
    /// exist to catch.
    /// </summary>
    public static IReadOnlyList<HelpTopic> All { get; } = typeof(HelpTopics)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(HelpTopic))
        .Select(f => (HelpTopic)f.GetValue(null)!)
        .ToList();
}
