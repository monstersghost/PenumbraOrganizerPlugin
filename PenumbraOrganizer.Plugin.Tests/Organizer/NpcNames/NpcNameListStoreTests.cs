using PenumbraOrganizer.Plugin;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NpcNames;

public class NpcNameListStoreTests
{
    private const string SeedJson = """
        {"Version":1,"NPCs":["Y'shtola"],"Enemies":[],"Bosses":[],"Excluded":[]}
        """;

    private static string MakeTempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "npc-name-list.json");
    }

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
}
