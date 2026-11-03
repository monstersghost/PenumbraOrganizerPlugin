using Dalamud.Configuration;

namespace PenumbraOrganizer.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    /// <summary>
    /// 2: folder protection is a rule map (protect/exclude) rather than a set of protected
    /// folders. <see cref="MigrateToCurrentVersion"/> carries a v1 file across.
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public HashSet<string> ProtectedModIdentifiers { get; set; } = [];

    /// <summary>
    /// Pre-v2 protected folders. Read only by <see cref="MigrateToCurrentVersion"/>, which empties
    /// it; nothing writes it again. Still declared so a v1 file on disk deserialises into it.
    /// </summary>
    public HashSet<string> ProtectedFolderPaths { get; set; } = [];

    /// <summary>Folders whose node is open in the Protect tab's tree.</summary>
    /// <remarks>
    /// Persisted because the tree defaults to collapsed, and re-digging to the same hair subfolder
    /// every session is the complaint the tree exists to answer. Holds paths that no longer exist
    /// harmlessly: a path with no node is simply never consulted.
    /// </remarks>
    public HashSet<string> ExpandedProtectFolders { get; set; } = [];

    /// <summary>Folder path -> true (protect) / false (exclude). Replaces ProtectedFolderPaths.</summary>
    public Dictionary<string, bool> FolderProtectionRules { get; set; } = [];

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

    /// <summary>
    /// Brings a config loaded from disk up to <see cref="CurrentVersion"/>. Idempotent, and safe to
    /// call on a fresh config (which is already current).
    /// </summary>
    /// <remarks>
    /// v1 -> v2: every entry of <see cref="ProtectedFolderPaths"/> becomes a protect rule, which is
    /// exactly what it meant, so behaviour after upgrade is identical and nothing needs re-ticking.
    /// TryAdd, not an assignment: if a rule for that folder already exists it was written by this
    /// version and is newer than the legacy field.
    /// </remarks>
    public void MigrateToCurrentVersion()
    {
        if (Version >= CurrentVersion)
            return;

        foreach (var folder in ProtectedFolderPaths)
            FolderProtectionRules.TryAdd(folder, true);

        ProtectedFolderPaths.Clear();
        Version = CurrentVersion;
    }
}
