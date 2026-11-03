using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests;

public class ConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_IsAtTheCurrentVersionWithAnEmptyProtectedSet()
    {
        var config = new Configuration();

        Assert.Equal(Configuration.CurrentVersion, config.Version);
        Assert.Equal(2, Configuration.CurrentVersion);
        Assert.Empty(config.ProtectedModIdentifiers);
    }

    [Fact]
    public void ProtectedModIdentifiers_IsMutable()
    {
        var config = new Configuration();

        config.ProtectedModIdentifiers.Add("hs-Nightingale-1.0");

        Assert.Contains("hs-Nightingale-1.0", config.ProtectedModIdentifiers);
    }

    [Fact]
    public void DefaultConfiguration_HasNullLastOperationSummaries()
    {
        var config = new Configuration();

        Assert.Null(config.LastApply);
        Assert.Null(config.LastRestore);
        Assert.Null(config.LastFolderCleanup);
        Assert.Null(config.LastFolderCleanupRollback);
    }

    [Fact]
    public void Configuration_RoundTripsOperationSummariesThroughJson()
    {
        var config = new Configuration
        {
            LastApply = new ApplyOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:00:00Z"), OperationCompletionStatus.PartiallySucceeded, 3, 1),
            LastRestore = new RestoreOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:05:00Z"), OperationCompletionStatus.Succeeded, 2, 1, 0, 1, 0),
            LastFolderCleanup = new FolderCleanupOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:10:00Z"), FolderCleanupStatus.Success, 5, 0),
            LastFolderCleanupRollback = new FolderCleanupRollbackOperationSummary(
                DateTimeOffset.Parse("2026-07-20T10:15:00Z"), FolderRollbackStatus.Restored),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<Configuration>(json);

        Assert.Equal(config.LastApply, roundTripped!.LastApply);
        Assert.Equal(config.LastRestore, roundTripped.LastRestore);
        Assert.Equal(config.LastFolderCleanup, roundTripped.LastFolderCleanup);
        Assert.Equal(config.LastFolderCleanupRollback, roundTripped.LastFolderCleanupRollback);
    }

    [Fact]
    public void Configuration_DeserializesLegacyJsonWithoutSummaryFields()
    {
        const string legacyJson = """{"Version":1,"ProtectedModIdentifiers":["a"],"ProtectedFolderPaths":["Gear"]}""";

        var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>(legacyJson);

        Assert.NotNull(config);
        Assert.Contains("a", config!.ProtectedModIdentifiers);
        Assert.Null(config.LastApply);
        Assert.Null(config.LastRestore);
        Assert.Null(config.LastFolderCleanup);
        Assert.Null(config.LastFolderCleanupRollback);
    }

    [Fact]
    public void PreExistingConfig_WithoutTheFirstRunField_ShowsTheWalkthrough()
    {
        // THE upgrade decision, pinned. A config written before 0.6.0 carries no
        // FirstRunTutorialSeen, so it deserialises to false and the walkthrough runs once for
        // existing users too. That is intended: 0.6.0 replaced the sort control outright.
        //
        // If this ever flips to true, someone has changed the property to bool? plus a resolver and
        // silently taken the walkthrough away from every upgrading user.
        const string legacyJson = """{"Version":1,"ProtectedModIdentifiers":[],"ProtectedFolderPaths":[]}""";

        var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>(legacyJson);

        Assert.False(config!.FirstRunTutorialSeen);
    }

    [Fact]
    public void FreshConfig_ShowsTheWalkthrough()
    {
        Assert.False(new Configuration().FirstRunTutorialSeen);
    }

    [Fact]
    public void FirstRunFlag_SurvivesARoundTrip_SoItIsShownExactlyOnce()
    {
        // Written back after the walkthrough closes. If it did not persist, every session would
        // reopen it - the failure a user would actually report.
        var json = System.Text.Json.JsonSerializer.Serialize(new Configuration { FirstRunTutorialSeen = true });

        Assert.True(System.Text.Json.JsonSerializer.Deserialize<Configuration>(json)!.FirstRunTutorialSeen);
    }

    [Fact]
    public void MigrateToCurrentVersion_V1ProtectedFolders_BecomeProtectRules()
    {
        // An upgrading user must not have to re-tick anything: every v1 folder was a protect.
        const string legacyJson =
            """{"Version":1,"ProtectedModIdentifiers":["a"],"ProtectedFolderPaths":["Gear","haloween"]}""";

        var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>(legacyJson)!;
        config.MigrateToCurrentVersion();

        Assert.Equal(Configuration.CurrentVersion, config.Version);
        Assert.True(config.FolderProtectionRules["Gear"]);
        Assert.True(config.FolderProtectionRules["haloween"]);
        Assert.Empty(config.ProtectedFolderPaths);
    }

    [Fact]
    public void MigrateToCurrentVersion_DoesNotOverwriteARuleWrittenByThisVersion()
    {
        // The legacy field is older than any rule map entry, so an exclude the user has since set
        // must survive a migration that still sees that folder listed as protected.
        const string json =
            """
            {"Version":1,"ProtectedFolderPaths":["haloween","haloween/animation"],
             "FolderProtectionRules":{"haloween/animation":false}}
            """;

        var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>(json)!;
        config.MigrateToCurrentVersion();
        config.MigrateToCurrentVersion();

        Assert.True(config.FolderProtectionRules["haloween"]);
        Assert.False(config.FolderProtectionRules["haloween/animation"]);
        Assert.Equal(2, config.FolderProtectionRules.Count);
    }

    [Fact]
    public void FolderProtectionRules_RoundTripThroughJson_IncludingExcludes()
    {
        var config = new Configuration
        {
            FolderProtectionRules = { ["haloween"] = true, ["haloween/animation"] = false },
            ExpandedProtectFolders = { "haloween" },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<Configuration>(json)!;

        Assert.True(roundTripped.FolderProtectionRules["haloween"]);
        Assert.False(roundTripped.FolderProtectionRules["haloween/animation"]);
        Assert.Contains("haloween", roundTripped.ExpandedProtectFolders);
    }

    [Fact]
    public void DefaultConfiguration_HasNoExpandedProtectFolders()
        => Assert.Empty(new Configuration().ExpandedProtectFolders);

    [Fact]
    public void UpgradePath_V1ProtectedFolder_StillProtectsItsModsAfterMigrationAndScan()
    {
        const string legacyJson =
            """{"Version":1,"ProtectedModIdentifiers":[],"ProtectedFolderPaths":["Gear"]}""";
        var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>(legacyJson)!;
        config.MigrateToCurrentVersion();

        var inside = new OrganizerModRow
        {
            Identifier = "m1", Name = "Boots", Author = "A",
            CurrentPath = "Gear/Boots", ProposedPath = "Gear/Boots",
        };
        var outside = new OrganizerModRow
        {
            Identifier = "m2", Name = "Hat", Author = "A",
            CurrentPath = "Other/Hat", ProposedPath = "Other/Hat",
        };
        var state = new OrganizerState();
        state.LoadScan([inside, outside], config.ProtectedModIdentifiers, config.FolderProtectionRules);

        Assert.True(inside.Protected);
        Assert.False(outside.Protected);
    }
}
