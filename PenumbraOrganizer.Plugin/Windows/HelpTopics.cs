using System.Reflection;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// Every help topic the code refers to. Pieces 3, 4 and 5 add to this; piece 2 only needs the
/// sort controls.
/// </summary>
public static class HelpTopics
{
    // First-run walkthrough. These carry a step and nothing else. Order lives in FirstRunSteps,
    // not here - a step added to the resource and not listed there renders nowhere.
    public static readonly HelpTopic FirstRunWelcome = new("firstrun.welcome");
    public static readonly HelpTopic FirstRunScan = new("firstrun.scan");
    public static readonly HelpTopic FirstRunProtect = new("firstrun.protect");
    public static readonly HelpTopic FirstRunSort = new("firstrun.sort");
    public static readonly HelpTopic FirstRunReview = new("firstrun.review");
    public static readonly HelpTopic FirstRunApply = new("firstrun.apply");
    public static readonly HelpTopic FirstRunHelp = new("firstrun.help");
    public static readonly HelpTopic FirstRunPenumbraUnavailable = new("firstrun.penumbra-unavailable");

    // Help tab sections. These carry a body and no short: they are read, not hovered.
    public static readonly HelpTopic HelpWhatThisDoes = new("help.what-this-does");
    public static readonly HelpTopic HelpSafetyRules = new("help.safety-rules");
    public static readonly HelpTopic HelpTabScan = new("help.tab-scan");
    public static readonly HelpTopic HelpTabProtect = new("help.tab-protect");
    public static readonly HelpTopic HelpTabSort = new("help.tab-sort");
    public static readonly HelpTopic HelpTabReview = new("help.tab-review");
    public static readonly HelpTopic HelpTabHistory = new("help.tab-history");
    public static readonly HelpTopic HelpTabSearch = new("help.tab-search");
    public static readonly HelpTopic HelpTabTemplates = new("help.tab-templates");
    public static readonly HelpTopic HelpWhenThingsGoWrong = new("help.when-things-go-wrong");
    public static readonly HelpTopic HelpWhereYourFilesAre = new("help.where-your-files-are");

    // Scan
    public static readonly HelpTopic ScanRefreshModList = new("scan.refresh-mod-list");
    public static readonly HelpTopic ScanEventLog = new("scan.event-log");

    // Protect
    public static readonly HelpTopic ProtectToggleAll = new("protect.toggle-all");
    public static readonly HelpTopic ProtectToggleHeliosphere = new("protect.toggle-heliosphere");
    public static readonly HelpTopic ProtectFolders = new("protect.folders");
    public static readonly HelpTopic ProtectMods = new("protect.mods");
    public static readonly HelpTopic ProtectHeliosphereNote = new("protect.heliosphere-note");
    public static readonly HelpTopic ProtectStrandedAtRoot = new("protect.stranded-at-root");

    // Sort
    public static readonly HelpTopic SortGrouping = new("sort.grouping");
    public static readonly HelpTopic SortSplitGear = new("sort.split-gear");
    public static readonly HelpTopic SortSplitNpc = new("sort.split-npc");
    public static readonly HelpTopic SortButton = new("sort.button");
    public static readonly HelpTopic SortScrapedNpcList = new("sort.scraped-npc-list");
    public static readonly HelpTopic SortImportWorkbook = new("sort.import-workbook");
    public static readonly HelpTopic SortManualAssign = new("sort.manual-assign");
    public static readonly HelpTopic SortRefreshNpcList = new("sort.refresh-npc-list");

    // Templates
    public static readonly HelpTopic TemplatesRefreshList = new("templates.refresh-list");
    public static readonly HelpTopic TemplatesOpenFolder = new("templates.open-folder");
    public static readonly HelpTopic TemplatesImportFile = new("templates.import-file");
    public static readonly HelpTopic TemplatesPreview = new("templates.preview");
    public static readonly HelpTopic TemplatesApply = new("templates.apply");
    public static readonly HelpTopic TemplatesExport = new("templates.export");
    public static readonly HelpTopic TemplatesExportName = new("templates.export-name");
    public static readonly HelpTopic TemplatesExportFallback = new("templates.export-fallback");
    public static readonly HelpTopic TemplatesExportFolders = new("templates.export-folders");
    public static readonly HelpTopic TemplatesExportSearch = new("templates.export-search");
    public static readonly HelpTopic TemplatesExportSave = new("templates.export-save");
    public static readonly HelpTopic TemplatesExportShareCode = new("templates.export-share-code");

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
