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
}
