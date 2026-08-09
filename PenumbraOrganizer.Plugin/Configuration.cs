using Dalamud.Configuration;

namespace PenumbraOrganizer.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public HashSet<string> ProtectedModIdentifiers { get; set; } = [];

    public HashSet<string> ProtectedFolderPaths { get; set; } = [];

    public Organizer.ApplyOperationSummary? LastApply { get; set; }

    public Organizer.RestoreOperationSummary? LastRestore { get; set; }

    public Organizer.FolderCleanupOperationSummary? LastFolderCleanup { get; set; }

    public Organizer.FolderCleanupRollbackOperationSummary? LastFolderCleanupRollback { get; set; }

    /// <summary>
    /// Opt in to unioning the wiki-scraped NPC name list with the bundled static one. Off by
    /// default, and inert on its own - every consumer reads the conjunction with
    /// <see cref="ScrapedNpcListFeatureEnabled"/>, never this value alone.
    /// </summary>
    public bool UseScrapedNpcNameList { get; set; }

    /// <summary>
    /// 0.6.0 ships with the scraped list unavailable: the crash whose correlation motivated this
    /// work has not been reproduced, so nothing may load a 20,000-name list yet. Flipping this to
    /// true is a deliberate release decision made after that verification, not a config edit.
    /// </summary>
    /// <remarks>
    /// public, not internal: this repo has no InternalsVisibleTo, so an internal const is
    /// unreachable from the test project and the test asserting the feature ships disabled would
    /// not compile.
    /// </remarks>
    public const bool ScrapedNpcListFeatureEnabled = false;
}
