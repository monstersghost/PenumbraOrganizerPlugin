using PenumbraOrganizer.Plugin;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NpcNames;

public class NpcNameListStoreTests
{
    private const string SeedJson = """
        {"Version":1,"NPCs":["Y'shtola"],"Enemies":[],"Bosses":[],"Excluded":[]}
        """;

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MakeTempPath() => Path.Combine(MakeTempDir(), "npc-name-list.json");

    [Fact]
    public void Load_MissingFile_SeedsExactlyOnce()
    {
        var path = MakeTempPath();

        var first = NpcNameListStore.Load(path, SeedJson);
        var writtenAfterFirst = File.ReadAllText(path);
        var second = NpcNameListStore.Load(path, SeedJson);

        Assert.True(File.Exists(path));
        Assert.Contains("Y'shtola", first.Document.NPCs);
        Assert.Null(first.Warning);
        Assert.Equal(writtenAfterFirst, File.ReadAllText(path)); // second Load() didn't rewrite it
        Assert.Contains("Y'shtola", second.Document.NPCs);
    }

    [Fact]
    public void Load_ValidExistingFile_IsNeverOverwritten()
    {
        var path = MakeTempPath();
        var customContent = """{"Version":1,"NPCs":["Custom Entry"],"Enemies":[],"Bosses":[],"Excluded":[]}""";
        File.WriteAllText(path, customContent);

        var result = NpcNameListStore.Load(path, SeedJson);

        Assert.Contains("Custom Entry", result.Document.NPCs);
        Assert.DoesNotContain("Y'shtola", result.Document.NPCs);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Load_CorruptedFile_FallsBackToSeedInMemoryWithoutTouchingDisk()
    {
        var path = MakeTempPath();
        File.WriteAllText(path, "{ not json");
        var onDiskBefore = File.ReadAllText(path);

        var result = NpcNameListStore.Load(path, SeedJson);

        Assert.Contains("Y'shtola", result.Document.NPCs);
        Assert.NotNull(result.Warning);
        Assert.Equal(onDiskBefore, File.ReadAllText(path)); // disk untouched
    }

    [Fact]
    public void Load_UnsupportedVersion_FallsBackToSeedWithWarning()
    {
        var path = MakeTempPath();
        File.WriteAllText(path, """{"Version":99,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":[]}""");

        var result = NpcNameListStore.Load(path, SeedJson);

        Assert.Contains("Y'shtola", result.Document.NPCs);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void WriteAtomic_LeavesOriginalIntactIfDirectoryMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "nested", "npc-name-list.json");

        NpcNameListStore.WriteAtomic(path, SeedJson);

        Assert.Equal(SeedJson, File.ReadAllText(path));
    }

    [Fact]
    public void BuildMatcher_ProducesAMatcherThatMatchesLoadedNames()
    {
        var document = NpcNameListStore.Load(MakeTempPath(), SeedJson).Document;

        var matcher = NpcNameListStore.BuildMatcher(document);

        Assert.NotNull(matcher.Match("Y'shtola Overhaul"));
    }

    private static string ListJson(int npcs, int enemies = 0, int bosses = 0)
    {
        static string Block(string prefix, int count) =>
            string.Join(",", Enumerable.Range(0, count).Select(i => $"\"{prefix} Name {i}\""));
        return $$"""
            {"Version":1,"NPCs":[{{Block("Npc", npcs)}}],"Enemies":[{{Block("Enemy", enemies)}}],"Bosses":[{{Block("Boss", bosses)}}],"Excluded":[]}
            """;
    }

    [Fact]
    public void Load_OversizedList_FallsBackToTheSeedAndWarns()
    {
        var path = MakeTempPath();
        File.WriteAllText(path, ListJson(NpcNameListStore.MaxSafeNameCount + 1));

        var result = NpcNameListStore.Load(path, SeedJson);

        Assert.Equal(["Y'shtola"], result.Document.NPCs);
        Assert.NotNull(result.Warning);
        Assert.Contains("bundled", result.Warning);
    }

    [Fact]
    public void Load_OversizedList_ReplacesTheFileAndKeepsABackup()
    {
        var path = MakeTempPath();
        File.WriteAllText(path, ListJson(NpcNameListStore.MaxSafeNameCount + 1));

        NpcNameListStore.Load(path, SeedJson);

        // Replaced rather than ignored: leaving the oversized file in place would re-arm the
        // problem on every later scan and index build.
        var reloaded = NpcNameListStore.Load(path, SeedJson);
        Assert.Null(reloaded.Warning);
        Assert.Equal(["Y'shtola"], reloaded.Document.NPCs);

        var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.oversized-*.json");
        Assert.Single(backups);
        Assert.Contains("Npc Name 0", File.ReadAllText(backups[0]));
    }

    [Fact]
    public void Load_ListExactlyAtTheLimit_IsAccepted()
    {
        var path = MakeTempPath();
        File.WriteAllText(path, ListJson(NpcNameListStore.MaxSafeNameCount));

        var result = NpcNameListStore.Load(path, SeedJson);

        Assert.Null(result.Warning);
        Assert.Equal(NpcNameListStore.MaxSafeNameCount, result.Document.NPCs.Count);
    }

    [Fact]
    public void Load_CountsAllThreeCategories_NotJustNpcs()
    {
        var path = MakeTempPath();
        var perCategory = NpcNameListStore.MaxSafeNameCount / 3 + 10;
        File.WriteAllText(path, ListJson(perCategory, perCategory, perCategory));

        var result = NpcNameListStore.Load(path, SeedJson);

        Assert.NotNull(result.Warning);
        Assert.Equal(["Y'shtola"], result.Document.NPCs);
    }

    // ---- The bundled static list (piece 1 Task 4) ----

    [Fact]
    public void StaticList_LoadsFromTheEmbeddedResource_AndHasTheExpectedShape()
    {
        var json = Plugin.ReadEmbeddedStaticNpcNameList();
        var parsed = NpcNameListCodec.Parse(json);

        Assert.Equal(NpcNameListParseStatus.Ok, parsed.Status);
        Assert.Equal(133, parsed.Data!.NPCs.Count);
        Assert.Equal(15, parsed.Data.Enemies.Count);
        Assert.Equal(679, parsed.Data.Bosses.Count);
    }

    [Fact]
    public void StaticList_HasNoByteOrderMark()
    {
        // Parse never throws - it reports MalformedJson - so a BOM would not fail loudly. It would
        // make the bundled list silently unavailable and leave every scan with no NPC names. This
        // asserts the specific failure rather than trusting the shape test above to imply it.
        Assert.False(Plugin.ReadEmbeddedStaticNpcNameList().StartsWith('﻿'));
    }

    [Fact]
    public void StaticList_ContainsBothScionsAndPrimals()
    {
        var doc = NpcNameListCodec.Parse(Plugin.ReadEmbeddedStaticNpcNameList()).Data!;

        Assert.Contains("Y'shtola", doc.NPCs);
        Assert.Contains("Alphinaud", doc.NPCs);
        Assert.Contains("Leveilleur", doc.NPCs);   // surname carries the whole family
        Assert.Contains("Titan", doc.Bosses);
        Assert.Contains("Shiva", doc.Bosses);
    }

    [Fact]
    public void StaticList_IsWellUnderTheLegacyOversizeGuard()
    {
        // The curated list is the default, and the default must never trip the 2,000-name guard
        // 0.5.3.1 added - if it did, every load would back it up and replace it with the seed.
        var doc = NpcNameListCodec.Parse(Plugin.ReadEmbeddedStaticNpcNameList()).Data!;

        Assert.True(doc.NPCs.Count + doc.Enemies.Count + doc.Bosses.Count
                    < NpcNameListStore.MaxSafeNameCount);
    }

    // ---- Migrating the legacy list to the opt-in scraped list (piece 1 Task 5) ----

    private static string LegacyPath(string dir) => Path.Combine(dir, "npc-name-list.json");

    private static string ScrapedPath(string dir) => Path.Combine(dir, "npc-name-list-scraped.json");

    [Fact]
    public void Migration_LegacyPresentScrapedAbsent_RenamesLegacy()
    {
        var dir = MakeTempDir();
        File.WriteAllText(LegacyPath(dir), ListJson(5));

        NpcNameListStore.MigrateLegacyList(dir);

        Assert.False(File.Exists(LegacyPath(dir)));
        Assert.True(File.Exists(ScrapedPath(dir)));
    }

    [Fact]
    public void Migration_LegacyPresentScrapedAbsent_PreservesTheContents()
    {
        // The rename must carry the user's names across. A migration that produced an empty or
        // seeded file would silently discard whatever the refresh had accumulated.
        var dir = MakeTempDir();
        File.WriteAllText(LegacyPath(dir), ListJson(5));

        var warning = NpcNameListStore.MigrateLegacyList(dir);

        Assert.Null(warning);
        Assert.Contains("Npc Name 4", NpcNameListCodec.Parse(File.ReadAllText(ScrapedPath(dir))).Data!.NPCs);
    }

    [Fact]
    public void Migration_BothPresent_LeavesBothUntouched()
    {
        var dir = MakeTempDir();
        File.WriteAllText(LegacyPath(dir), ListJson(5));
        File.WriteAllText(ScrapedPath(dir), ListJson(7));

        var warning = NpcNameListStore.MigrateLegacyList(dir);

        // Nothing is overwritten or deleted: both-present is reachable via an interrupted migration
        // or a downgrade cycle, which is exactly when the user has no backup.
        Assert.True(File.Exists(LegacyPath(dir)));
        Assert.Contains("\"Npc Name 6\"", File.ReadAllText(ScrapedPath(dir)));
        Assert.NotNull(warning); // reported, not silent - the leftover legacy file is now inert
    }

    [Fact]
    public void Migration_NeitherPresent_IsANoOpAndCreatesNothing()
    {
        var dir = MakeTempDir();

        var warning = NpcNameListStore.MigrateLegacyList(dir);

        Assert.Null(warning);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void Migration_ScrapedOnly_IsANoOp()
    {
        // The already-migrated steady state. It runs on every plugin load, so it must not churn.
        var dir = MakeTempDir();
        File.WriteAllText(ScrapedPath(dir), ListJson(5));
        var before = File.ReadAllText(ScrapedPath(dir));

        var warning = NpcNameListStore.MigrateLegacyList(dir);

        Assert.Null(warning);
        Assert.Equal(before, File.ReadAllText(ScrapedPath(dir)));
        Assert.False(File.Exists(LegacyPath(dir)));
    }

    [Fact]
    public void Migration_MissingConfigDirectory_DoesNotThrow()
    {
        // Runs during plugin construction, before anything has necessarily created the directory.
        // Throwing here would take the whole plugin down over a diagnostic concern.
        var dir = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));

        Assert.Null(NpcNameListStore.MigrateLegacyList(dir));
    }

    // ---- LoadForMatching: the static list, and the opt-in scraped list beside it ----

    [Fact]
    public void LoadForMatching_OptedOut_IgnoresTheScrapedFileEntirely()
    {
        var dir = MakeTempDir();
        File.WriteAllText(ScrapedPath(dir), ListJson(5000));

        var result = NpcNameListStore.LoadForMatching(dir, useScraped: false);

        Assert.DoesNotContain("Npc Name 0", result.Document.NPCs);
        Assert.Contains("Y'shtola", result.Document.NPCs);   // static list only
        Assert.Null(result.Warning);
    }

    [Fact]
    public void LoadForMatching_OptedOut_DoesNotEvenReadACorruptScrapedFile()
    {
        // "not opened, not created, not touched": an unreadable file the user is not opted in to
        // must not produce a warning about a list that is not in use.
        var dir = MakeTempDir();
        File.WriteAllText(ScrapedPath(dir), "{ not json");

        var result = NpcNameListStore.LoadForMatching(dir, useScraped: false);

        Assert.Null(result.Warning);
        Assert.Contains("Y'shtola", result.Document.NPCs);
    }

    [Fact]
    public void LoadForMatching_OptedIn_UnionsStaticAndScraped()
    {
        var dir = MakeTempDir();
        File.WriteAllText(ScrapedPath(dir), ListJson(5));

        var result = NpcNameListStore.LoadForMatching(dir, useScraped: true);

        Assert.Contains("Y'shtola", result.Document.NPCs);
        Assert.Contains("Npc Name 0", result.Document.NPCs);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void LoadForMatching_OptedIn_MissingScrapedFile_IsStaticOnlyAndWritesNothing()
    {
        // Load's missing-file branch seeds the file. Reusing it here would create the very file
        // the migration table assumes is absent, turning "never refreshed" into "already migrated".
        var dir = MakeTempDir();

        var result = NpcNameListStore.LoadForMatching(dir, useScraped: true);

        Assert.Null(result.Warning);
        Assert.Contains("Y'shtola", result.Document.NPCs);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void LoadForMatching_CorruptScrapedFile_DegradesToStaticWithAWarning()
    {
        var dir = MakeTempDir();
        File.WriteAllText(ScrapedPath(dir), "{ not json");

        var result = NpcNameListStore.LoadForMatching(dir, useScraped: true);

        Assert.NotNull(result.Warning);
        Assert.Contains("Y'shtola", result.Document.NPCs);
    }

    [Fact]
    public void LoadForMatching_UnsupportedVersionScrapedFile_DegradesToStaticWithAWarning()
    {
        var dir = MakeTempDir();
        File.WriteAllText(ScrapedPath(dir), """{"Version":99,"NPCs":["Npc Name 0"],"Enemies":[],"Bosses":[],"Excluded":[]}""");

        var result = NpcNameListStore.LoadForMatching(dir, useScraped: true);

        Assert.NotNull(result.Warning);
        Assert.DoesNotContain("Npc Name 0", result.Document.NPCs);
        Assert.Contains("Y'shtola", result.Document.NPCs);
    }

    [Fact]
    public void LoadForMatching_OversizedScrapedFile_WarnsAndLeavesTheFileExactlyAsItWas()
    {
        // THE trap this ceiling exists to avoid. Load's 2,000-name guard does not merely warn: it
        // backs the file up and overwrites it with the seed. The scraped list is ~20,000 names, so
        // reusing that guard would destroy the user's file on every single opted-in load.
        var dir = MakeTempDir();
        File.WriteAllText(ScrapedPath(dir), ListJson(NpcNameListStore.MaxScrapedNameCount + 1));
        var before = File.ReadAllText(ScrapedPath(dir));

        var result = NpcNameListStore.LoadForMatching(dir, useScraped: true);

        Assert.NotNull(result.Warning);
        Assert.Contains("Y'shtola", result.Document.NPCs);
        Assert.Equal(before, File.ReadAllText(ScrapedPath(dir)));           // not rewritten
        Assert.Empty(Directory.GetFiles(dir, "*.oversized-*.json"));        // and not backed up
    }

    [Fact]
    public void LoadForMatching_ScrapedListAtTheCeiling_IsAccepted()
    {
        var dir = MakeTempDir();
        File.WriteAllText(ScrapedPath(dir), ListJson(NpcNameListStore.MaxScrapedNameCount));

        var result = NpcNameListStore.LoadForMatching(dir, useScraped: true);

        Assert.Null(result.Warning);
        Assert.Contains("Npc Name 0", result.Document.NPCs);
    }

    [Fact]
    public void LoadForMatching_ScrapedListIsWellAboveTheLegacy2000Guard()
    {
        // A full scrape is roughly 20,115 names. If the ceiling were ever lowered back towards
        // MaxSafeNameCount the opt-in feature would be unusable rather than merely gated.
        Assert.True(NpcNameListStore.MaxScrapedNameCount > 20_115);
    }

    [Fact]
    public void LoadForMatching_ExcludedFiltersScrapedNamesOnly_NeverTheStaticList()
    {
        // Excluded is what the user removed through the refresh UI, so it applies to the scraped
        // names. The static list is curated and was never chosen through that UI; letting a stale
        // exclusion silently drop a bundled name would change classification for a name the user
        // never touched. Shiva is the live example - it is in the bundled list twice.
        var dir = MakeTempDir();
        File.WriteAllText(ScrapedPath(dir), """
            {"Version":1,"NPCs":["Npc Name 0"],"Enemies":[],"Bosses":["Shiva"],"Excluded":["Npc Name 0","Shiva"]}
            """);

        var result = NpcNameListStore.LoadForMatching(dir, useScraped: true);

        Assert.DoesNotContain("Npc Name 0", result.Document.NPCs);  // scraped name, excluded
        Assert.Contains("Shiva", result.Document.Bosses);           // static name, kept
    }

    [Fact]
    public void LoadForMatching_UnionFeedsTheMatcherWithBothSources()
    {
        // The union only matters if it reaches the matcher, and duplicates across the two lists
        // must merge into one entry rather than fighting over precedence.
        var dir = MakeTempDir();
        File.WriteAllText(ScrapedPath(dir), """
            {"Version":1,"NPCs":["Wholly Invented Person"],"Enemies":[],"Bosses":[],"Excluded":[]}
            """);

        var matcher = NpcNameListStore.BuildMatcher(
            NpcNameListStore.LoadForMatching(dir, useScraped: true).Document);

        Assert.Equal(NpcNameKind.Npc, matcher.Match("Wholly Invented Person Hair")!.Kind);
        Assert.Equal(NpcNameKind.Npc, matcher.Match("Y'shtola Hair")!.Kind);
    }

    // ---- The compile-time feature gate ----

    [Fact]
    public void ScrapedListFeature_ShipsDisabled()
    {
        // 0.6.0 ships with the scraped list unavailable. Greying out a checkbox enforces nothing:
        // a true left in config by testing or a hand edit would otherwise load the full list while
        // the UI claims the feature is off. Flipping this is a deliberate release decision.
        Assert.False(Configuration.ScrapedNpcListFeatureEnabled);
    }

    [Fact]
    public void UseScrapedNpcNameList_DefaultsToOff()
    {
        Assert.False(new Configuration().UseScrapedNpcNameList);
    }
}
