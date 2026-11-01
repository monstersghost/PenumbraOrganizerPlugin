using Dalamud.Configuration;

namespace PenumbraOrganizer.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public HashSet<string> ProtectedModIdentifiers { get; set; } = [];

    public HashSet<string> ProtectedFolderPaths { get; set; } = [];

    /// <summary>
    /// Every mod identifier any scan has ever resolved as Heliosphere-managed.
    /// </summary>
    /// <remarks>
    /// Persisted so a mod is not silently unprotected during the window where its
    /// <c>heliosphere.json</c> is absent - which is what a Heliosphere update looks like on disk.
    /// Only ever grows: an entry that is no longer managed costs one unnecessary protection the user
    /// can untick, whereas dropping one costs a mod being moved out from under Heliosphere.
    /// </remarks>
    public HashSet<string> KnownHeliosphereIdentifiers { get; set; } = [];

    public Organizer.ApplyOperationSummary? LastApply { get; set; }

    public Organizer.RestoreOperationSummary? LastRestore { get; set; }

    public Organizer.FolderCleanupOperationSummary? LastFolderCleanup { get; set; }

    public Organizer.FolderCleanupRollbackOperationSummary? LastFolderCleanupRollback { get; set; }

    /// <summary>
    /// Set once the first-run walkthrough has been shown and dismissed.
    /// </summary>
    /// <remarks>
    /// A plain <c>bool</c>, and that is a decision rather than an oversight. A config written before
    /// 0.6.0 has no such property, so it deserialises to <c>false</c> and an upgrading user sees the
    /// walkthrough once - which is the intent: 0.6.0 replaced the entire sort control, moved NPC
    /// name handling and added a Help tab, so existing users have as much to be shown as new ones.
    /// <para>
    /// The plan this came from specified <c>bool?</c> plus a resolver, precisely to tell "old config
    /// predating this field" from "fresh config" and to spare upgraders the walkthrough. That was
    /// reversed deliberately (maintainer decision, 2026-08-10). To restore it, make this nullable
    /// and resolve it against whether <c>GetPluginConfig</c> returned non-null.
    /// </para>
    /// </remarks>
    public bool FirstRunTutorialSeen { get; set; }

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
