using System.Net;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NpcNames;

public class NpcNameRefreshServiceTests
{
    private const string SeedJson = """
        {"Version":1,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":[]}
        """;

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MakeTempPath() => Path.Combine(MakeTempDir(), "npc-name-list.json");

    private static string CategoryPageHtml(params string[] names)
    {
        var items = string.Concat(names.Select(n => $"""<li><a href="/wiki/{n}" title="{n}">{n}</a></li>"""));
        return $"""
            <html><body>
            <div id="mw-pages"><div class="mw-category-group">{items}</div></div>
            </body></html>
            """;
    }

    private static NpcNameRefreshService MakeService(Func<Uri, string> htmlForUrl)
    {
        var handler = new StubHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(htmlForUrl(req.RequestUri!)) });
        return new NpcNameRefreshService(new NpcWikiScraper(new HttpClient(handler)));
    }

    private static NpcNameRefreshService MakeServiceReturning(string[] npcs, string[] enemies, string[] bosses) =>
        MakeService(url => url.ToString().Contains("Bosses")
            ? CategoryPageHtml(bosses)
            : url.ToString().Contains("Enemies")
                ? CategoryPageHtml(enemies)
                : CategoryPageHtml(npcs));

    [Fact]
    public async Task RefreshAsync_MergesNewlyScrapedNamesIntoEachCategory()
    {
        var path = MakeTempPath();
        var service = MakeService(url => url.ToString().Contains("Bosses")
            ? CategoryPageHtml("Zenos")
            : url.ToString().Contains("Enemies")
                ? CategoryPageHtml("Garuda")
                : CategoryPageHtml("Alphinaud"));

        var result = await service.RefreshAsync(path, SeedJson, CancellationToken.None);

        Assert.False(result.RecoveredFromCorruption);
        Assert.All(result.Categories, c => Assert.Null(c.FailureReason));
        Assert.Equal(3, result.Categories.Count);
        Assert.All(result.Categories, c => Assert.Equal(1, c.NameCount));

        var written = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;
        Assert.Contains("Alphinaud", written.NPCs);
        Assert.Contains("Garuda", written.Enemies);
        Assert.Contains("Zenos", written.Bosses);
    }

    [Fact]
    public async Task RefreshAsync_OneCategoryFailing_StillMergesTheOthers()
    {
        var path = MakeTempPath();
        var service = MakeService(url => url.ToString().Contains("Enemies")
            ? throw new HttpRequestException("timed out")
            : CategoryPageHtml("Alphinaud"));

        var result = await service.RefreshAsync(path, SeedJson, CancellationToken.None);

        var enemies = result.Categories.Single(c => c.CategoryName == "Enemies");
        var npcs = result.Categories.Single(c => c.CategoryName == "NPCs");
        Assert.NotNull(enemies.FailureReason);
        Assert.Null(npcs.FailureReason);
        Assert.Equal(1, npcs.NameCount);

        var written = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;
        Assert.Contains("Alphinaud", written.NPCs);
        Assert.Empty(written.Enemies);
    }

    [Fact]
    public async Task RefreshAsync_ExcludedName_IsNeverReAdded()
    {
        var path = MakeTempPath();
        // Excluded blocks a name from being *re-added* by a future scrape; it does not
        // retroactively remove an already-present entry (that's still a manual edit). Simulate
        // the real "already removed by hand" state: excluded, and NOT present in NPCs.
        var seed = """{"Version":1,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":["Excluded Guy"]}""";
        var service = MakeService(_ => CategoryPageHtml("Excluded Guy"));

        var result = await service.RefreshAsync(path, seed, CancellationToken.None);

        var written = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;
        Assert.DoesNotContain("Excluded Guy", written.NPCs);
    }

    [Fact]
    public async Task RefreshAsync_CorruptedExistingFile_PreservesBackupAndRecoversFromSeed()
    {
        var path = MakeTempPath();
        File.WriteAllText(path, "{ not json");
        var service = MakeService(_ => CategoryPageHtml("Alphinaud"));

        var result = await service.RefreshAsync(path, SeedJson, CancellationToken.None);

        Assert.True(result.RecoveredFromCorruption);
        var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*.json");
        Assert.Single(backups);
        Assert.Equal("{ not json", File.ReadAllText(backups[0]));

        var written = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;
        Assert.Contains("Alphinaud", written.NPCs);
    }

    [Fact]
    public async Task RefreshAsync_MissingFile_StartsFromSeedNotRecoveryFlag()
    {
        var path = MakeTempPath(); // MakeTempPath only creates the directory, not the file
        var service = MakeService(_ => CategoryPageHtml("Alphinaud"));

        var result = await service.RefreshAsync(path, SeedJson, CancellationToken.None);

        Assert.False(result.RecoveredFromCorruption); // missing file is a normal first run, not corruption
    }

    // ---- Snapshot semantics: a refresh writes what the wiki returned, nothing more (Task 5) ----

    [Fact]
    public async Task Refresh_ReplacesRatherThanGrowing()
    {
        // The unbounded growth this whole piece exists to stop. Two refreshes returning identical
        // wiki data must leave the file the same size, not double it.
        var dir = MakeTempDir();
        var path = Path.Combine(dir, "npc-name-list-scraped.json");
        var service = MakeServiceReturning(npcs: ["Alpha", "Beta"], enemies: [], bosses: []);

        await service.RefreshAsync(path, SeedJson, CancellationToken.None);
        var afterFirst = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;

        await service.RefreshAsync(path, SeedJson, CancellationToken.None);
        var afterSecond = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;

        Assert.Equal(afterFirst.NPCs.Count, afterSecond.NPCs.Count);
        Assert.Equal(["Alpha", "Beta"], afterSecond.NPCs.Order());
    }

    [Fact]
    public async Task Refresh_DropsNamesTheWikiNoLongerReturns()
    {
        // The other half of snapshot semantics, and the direction MergeAdditive could never do.
        var dir = MakeTempDir();
        var path = Path.Combine(dir, "npc-name-list-scraped.json");

        await MakeServiceReturning(npcs: ["Alpha", "Beta"], [], []).RefreshAsync(path, SeedJson, CancellationToken.None);
        await MakeServiceReturning(npcs: ["Alpha"], [], []).RefreshAsync(path, SeedJson, CancellationToken.None);

        Assert.Equal(["Alpha"], NpcNameListCodec.Parse(File.ReadAllText(path)).Data!.NPCs);
    }

    [Fact]
    public async Task Refresh_NeverInjectsTheBundledSeedIntoTheScrapedFile()
    {
        // LoadForRefresh used to fall back to the seed document. Under snapshot semantics that
        // would write bundled names into the scraped file and they would never leave again, since
        // no later refresh removes what the wiki does not return - it would just be the growth bug
        // with a smaller constant. A refresh's output is the wiki's contents and nothing else.
        var dir = MakeTempDir();
        var path = Path.Combine(dir, "npc-name-list-scraped.json");
        var seedWithNames = """{"Version":1,"NPCs":["Seed Only Person"],"Enemies":[],"Bosses":[],"Excluded":[]}""";

        await MakeServiceReturning(npcs: ["Alpha"], [], []).RefreshAsync(path, seedWithNames, CancellationToken.None);

        Assert.Equal(["Alpha"], NpcNameListCodec.Parse(File.ReadAllText(path)).Data!.NPCs);
    }

    [Fact]
    public async Task Refresh_CarriesExclusionsForwardFromThePreviousScrapedFile()
    {
        // Excluded is user state and lives in the scraped file, so a snapshot write must preserve
        // it - losing it would let the next refresh re-add every name the user had removed.
        var dir = MakeTempDir();
        var path = Path.Combine(dir, "npc-name-list-scraped.json");
        File.WriteAllText(path, """
            {"Version":1,"NPCs":[],"Enemies":[],"Bosses":[],"Excluded":["Beta"]}
            """);

        await MakeServiceReturning(npcs: ["Alpha", "Beta"], [], []).RefreshAsync(path, SeedJson, CancellationToken.None);

        var written = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;
        Assert.Equal(["Alpha"], written.NPCs);
        Assert.Equal(["Beta"], written.Excluded);
    }

    [Fact]
    public async Task Refresh_FailedCategory_KeepsWhatWasAlreadyOnDisk()
    {
        // Snapshot semantics are per category AND conditional on a clean scrape. NpcWikiScraper
        // returns the names it gathered before failing alongside the FailureReason, so replacing a
        // category from a partial scrape would delete everything past the failure point - a timeout
        // on page 3 of 50 would silently discard 47 pages' worth of names.
        var dir = MakeTempDir();
        var path = Path.Combine(dir, "npc-name-list-scraped.json");
        File.WriteAllText(path, """
            {"Version":1,"NPCs":["Kept Alpha","Kept Beta"],"Enemies":[],"Bosses":[],"Excluded":[]}
            """);
        var service = MakeService(url => url.ToString().Contains("NPCs")
            ? throw new HttpRequestException("timed out")
            : CategoryPageHtml("Placeholder"));

        var result = await service.RefreshAsync(path, SeedJson, CancellationToken.None);

        Assert.NotNull(result.Categories.Single(c => c.CategoryName == "NPCs").FailureReason);
        Assert.Equal(["Kept Alpha", "Kept Beta"], NpcNameListCodec.Parse(File.ReadAllText(path)).Data!.NPCs);
    }

    [Fact]
    public async Task Refresh_ReportsTheSnapshotTotal_NotADelta()
    {
        // AddedCount was merged-minus-existing, which under snapshots is meaningless and can go
        // negative. NameCount is the count in the file that was just written.
        var dir = MakeTempDir();
        var path = Path.Combine(dir, "npc-name-list-scraped.json");

        await MakeServiceReturning(npcs: ["Alpha", "Beta"], [], []).RefreshAsync(path, SeedJson, CancellationToken.None);
        var second = await MakeServiceReturning(npcs: ["Alpha", "Beta"], [], []).RefreshAsync(
            path, SeedJson, CancellationToken.None);

        // A delta would report 0 on the second identical run; a total reports 2 both times.
        Assert.Equal(2, second.Categories.Single(c => c.CategoryName == "NPCs").NameCount);
    }
}
