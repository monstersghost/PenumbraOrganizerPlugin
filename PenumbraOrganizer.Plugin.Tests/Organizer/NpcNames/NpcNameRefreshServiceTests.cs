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

    private static string MakeTempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "npc-name-list.json");
    }

    private static string CategoryPageHtml(string name) => $"""
        <html><body>
        <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/{name}" title="{name}">{name}</a></li></div></div>
        </body></html>
        """;

    private static NpcNameRefreshService MakeService(Func<Uri, string> htmlForUrl)
    {
        var handler = new StubHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(htmlForUrl(req.RequestUri!)) });
        return new NpcNameRefreshService(new NpcWikiScraper(new HttpClient(handler)));
    }

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
        Assert.All(result.Categories, c => Assert.Equal(1, c.AddedCount));

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
        Assert.Equal(1, npcs.AddedCount);

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
    public async Task RefreshAsync_NeverRemovesExistingNames()
    {
        var path = MakeTempPath();
        var seed = """{"Version":1,"NPCs":["Y'shtola"],"Enemies":[],"Bosses":[],"Excluded":[]}""";
        // The scrape this run finds nothing under NPCs (simulating a temporarily-empty/changed page).
        var service = MakeService(url => url.ToString().Contains("NPCs")
            ? "<html><body><div id=\"mw-pages\"></div></body></html>"
            : CategoryPageHtml("Placeholder"));

        await service.RefreshAsync(path, seed, CancellationToken.None);

        var written = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;
        Assert.Contains("Y'shtola", written.NPCs); // still present, not silently dropped
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
}
