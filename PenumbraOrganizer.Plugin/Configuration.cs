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
    /// Pre-v2 protected folders. <see cref="MigrateToCurrentVersion"/> reads it once and empties it.
    /// It is then maintained as a write-only compatibility projection of the protect rules (excludes
    /// are not representable), so a rollback to a pre-v2 build keeps its folder protection. The
    /// version guard stops a v2 build ever reading it back.
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
    /// The user's body ranking, first is highest. Set once and left: most people use one body per
    /// character, so unlike the sort strategy this is a preference, not a per-sort choice. Repaired
    /// against the current body list on load by <c>BodyPriority.Normalize</c>, so a stale entry or a
    /// body added by a later release never breaks it.
    /// </summary>
    public List<string> BodyPriority { get; set; } = [];

    /// <summary>The hair priority race code ("1801"), or null for None. A set-once preference.</summary>
    public string? HairRaceCode { get; set; }

    /// <summary>The Midlander hair fallback. A set-once preference.</summary>
    public bool MidlanderFallback { get; set; }

    /// <summary>The Sort tab's Group by, as a <c>SortStrategy</c> name; null means the default.</summary>
    public string? SortGrouping { get; set; }

    /// <summary>The Sort tab's split checkboxes, kept between sessions.</summary>
    public bool SortSplitGear { get; set; }
    public bool SortSplitNpc { get; set; } = true;
    public bool SortSplitHair { get; set; }
    public bool SortSplitGearBody { get; set; }
    public bool SortSplitBodyMods { get; set; }

    /// <summary>Gear: hands and feet in the plain slot folders, not under a body. Off keeps 0.6.4.0 behavior.</summary>
    public bool SortSharedHandsFeet { get; set; }

    /// <summary>Hair: split by race, with a Multi-race folder. Off keeps 0.6.4.0 behavior.</summary>
    public bool SortSplitHairByRace { get; set; }

    /// <summary>Animation: split Emotes by the emote a mod replaces. Off keeps 0.6.5.0 behavior.</summary>
    public bool SortSplitEmotes { get; set; }

    /// <summary>The template creator's fallback grouping (a <c>SortStrategy</c> name) and its two splits.</summary>
    public string? TemplateExportGrouping { get; set; }
    public bool TemplateExportSplitGear { get; set; }
    public bool TemplateExportSplitNpc { get; set; } = true;
    public bool TemplateExportSplitHair { get; set; }
    public bool TemplateExportSplitGearBody { get; set; }
    public bool TemplateExportSplitBodyMods { get; set; }

    /// <summary>What the sorter does with Heliosphere-managed mods. Protected until the user says otherwise.</summary>
    public Organizer.ToolMode HeliosphereMode { get; set; } = Organizer.ToolMode.Protected;

    /// <summary>What the sorter does with mods Proteus made. Protected until the user says otherwise.</summary>
    public Organizer.ToolMode ProteusMode { get; set; } = Organizer.ToolMode.Protected;

    /// <summary>
    /// Place Proteus's own managed mod at the root instead of keeping it protected. It is plumbing the
    /// user did not choose, so it is protected by default and outside the three modes.
    /// </summary>
    public bool ProteusManagedAtRoot { get; set; }

    /// <summary>Top-level folders kept as the top level while their mods are sorted beneath them.</summary>
    public List<string> SortInsideFolders { get; set; } = [];

    /// <summary>What happens when mods are installed. Ask until the user opts into automatic.</summary>
    public Organizer.Collections.NewModAction NewModAction { get; set; } = Organizer.Collections.NewModAction.Ask;

    /// <summary>The folder unused mods are moved under.</summary>
    public string UnusedFolderName { get; set; } = "Unused";

    /// <summary>Sort into one root folder per Penumbra collection first.</summary>
    public bool SortByCollection { get; set; }

    public Organizer.Collections.CollectionSharedRule CollectionSharedRule { get; set; } =
        Organizer.Collections.CollectionSharedRule.SharedFolder;

    public string CollectionSharedFolderName { get; set; } = "Shared";

    public Organizer.Collections.CollectionUnusedRule CollectionUnusedRule { get; set; } =
        Organizer.Collections.CollectionUnusedRule.UnusedFolder;

    /// <summary>Collection folder names the last collection sort used, so a later sort can still strip them.</summary>
    public List<string> CollectionFolderNamesInUse { get; set; } = [];

    /// <summary>The three settings above, with an unknown stored mode read as Protected.</summary>
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public Organizer.ToolPolicy ToolPolicy =>
        Organizer.ToolPolicy.From((int)HeliosphereMode, (int)ProteusMode, ProteusManagedAtRoot, SortInsideFolders ?? []);

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
    /// Brings a config loaded from disk up to <see cref="CurrentVersion"/>. Safe to call on a
    /// fresh config (which is already current), and idempotent by way of the DATA rather than of
    /// a version check.
    /// </summary>
    /// <remarks>
    /// Every entry of <see cref="ProtectedFolderPaths"/> becomes a protect rule, which is exactly
    /// what it meant, so behaviour after an upgrade is identical and nothing needs re-ticking.
    /// TryAdd, not an assignment: a rule already in the map was written by this version and is
    /// newer than the legacy field - notably an exclude, which the legacy field cannot express.
    /// <para>
    /// Deliberately NOT guarded on <see cref="Version"/>, and the legacy field is deliberately NOT
    /// cleared. This plugin ships testing builds that users opt into and can roll back from, so
    /// the round trip has to survive: a pre-v2 build keeps protecting folders because the legacy
    /// field is still there, keeps writing that field itself, and re-saves Version as 2 - so a
    /// version-guarded migration would skip on the way back up and drop every folder the user
    /// protected while on the old build. Reading the field every time costs nothing, because
    /// <c>SaveProtectionState</c> rewrites it from the live protect rules on every save, which
    /// makes it a mirror rather than a second source of truth: a folder the user unprotects here
    /// leaves both, and TryAdd can only ever re-add what the mirror still lists.
    /// </para>
    /// </remarks>
    public void MigrateToCurrentVersion()
    {
        foreach (var folder in ProtectedFolderPaths)
            FolderProtectionRules.TryAdd(folder, true);

        Version = CurrentVersion;
    }
}
