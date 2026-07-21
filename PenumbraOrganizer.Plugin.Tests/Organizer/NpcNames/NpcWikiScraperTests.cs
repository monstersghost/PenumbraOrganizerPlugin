using System.Net;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NpcNames;

public class NpcWikiScraperTests
{
    private const string PageOneHtml = """
        <html><body>
        <div id="mw-pages">
          <div class="mw-category-group">
            <h3>A</h3>
            <ul><li><a href="/wiki/Alphinaud" title="Alphinaud">Alphinaud</a></li></ul>
          </div>
        </div>
        <a href="/mediawiki/index.php?title=Category:NPCs&amp;pagefrom=Alphinaud#mw-pages">(next page)</a>
        </body></html>
        """;

    private const string PageTwoHtml = """
        <html><body>
        <div id="mw-pages">
          <div class="mw-category-group">
            <h3>T</h3>
            <ul><li><a href="/wiki/Thancred" title="Thancred">Thancred</a></li></ul>
          </div>
        </div>
        </body></html>
        """;

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private static HttpClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpMessageHandler(responder));

    [Fact]
    public async Task ScrapeCategoryAsync_SinglePage_ReturnsMembers()
    {
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PageTwoHtml) });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Equal(["Thancred"], result.Names);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_FollowsPaginationAcrossMultiplePages()
    {
        var requestedUrls = new List<string>();
        var client = MakeClient(req =>
        {
            requestedUrls.Add(req.RequestUri!.ToString());
            var html = requestedUrls.Count == 1 ? PageOneHtml : PageTwoHtml;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Equal(["Alphinaud", "Thancred"], result.Names);
        Assert.Equal(2, requestedUrls.Count);
        Assert.Contains("pagefrom=Alphinaud", requestedUrls[1]);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_RepeatedNextPageUrl_StopsWithFailureReason()
    {
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // "next page" link always points back at itself — a pagination loop.
            Content = new StringContent("""
                <html><body>
                <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/X" title="X">X</a></li></div></div>
                <a href="/wiki/Category:NPCs">(next page)</a>
                </body></html>
                """),
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Contains("loop", result.FailureReason);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_NextPageLinkPointsOffHost_StopsWithFailureReason()
    {
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <html><body>
                <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/X" title="X">X</a></li></div></div>
                <a href="https://evil.example.com/steal">(next page)</a>
                </body></html>
                """),
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Contains("off-host", result.FailureReason);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_MissingCategoryContainer_ReturnsFailureReason()
    {
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><p>Not a category page</p></body></html>"),
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.Names);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_HttpRequestFails_ReturnsFailureReason()
    {
        var client = MakeClient(_ => throw new HttpRequestException("connection refused"));
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Contains("connection refused", result.FailureReason);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_EmptyCategoryContainer_SucceedsWithNoNames()
    {
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><div id=\"mw-pages\"></div></body></html>"),
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Empty(result.Names);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_DuplicateMembersAcrossPages_AreBothReturned()
    {
        // MergeAdditive/Codec dedupe on the persistence side (Task 4); the scraper itself is a
        // faithful raw extraction and is not responsible for cross-page dedup.
        var pageOne = """
            <html><body>
            <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/Zenos" title="Zenos">Zenos</a></li></div></div>
            <a href="/mediawiki/index.php?title=Category:Bosses&amp;pagefrom=Zenos#mw-pages">(next page)</a>
            </body></html>
            """;
        var pageTwo = """
            <html><body>
            <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/Zenos" title="Zenos">Zenos</a></li></div></div>
            </body></html>
            """;
        var requestCount = 0;
        var client = MakeClient(_ =>
        {
            requestCount++;
            var html = requestCount == 1 ? pageOne : pageTwo;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:Bosses"), CancellationToken.None);

        Assert.Equal(["Zenos", "Zenos"], result.Names);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_NonSuccessStatusCode_ReturnsFailureReason()
    {
        // Distinct from a connection-level exception: this is a real HTTP response that
        // completes, but with a non-2xx status. HttpClient.GetStringAsync's own
        // EnsureSuccessStatusCode() call turns this into an HttpRequestException, caught by the
        // same branch as a connection failure, but it's worth its own test since it exercises a
        // different path through the stub (a real HttpResponseMessage, not a thrown exception).
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.Names);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_CrossHostRedirectResponse_ReturnsFailureReason()
    {
        // Guards the transport-level redirect gap the design spec calls out: Plugin.cs builds
        // _npcHttpClient with AllowAutoRedirect = false, so a real HTTP 3xx (e.g. a redirect to a
        // different host) is never silently followed. Instead it surfaces to GetStringAsync as a
        // non-success status, whose own EnsureSuccessStatusCode() throws HttpRequestException --
        // the same path already exercised by ScrapeCategoryAsync_NonSuccessStatusCode_*. This test
        // stands in for that end-to-end behavior at the unit level, since a stub HttpMessageHandler
        // (unlike a real HttpClientHandler) never actually follows redirects itself.
        var client = MakeClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example.com/steal");
            return response;
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:NPCs"), CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.Names);
    }

    [Fact]
    public async Task ScrapeCategoryAsync_SubcategoryLinks_AreExcludedFromMemberNames()
    {
        // Real MediaWiki category pages can have a sibling "#mw-subcategories" div listing
        // child categories, separate from "#mw-pages" (the actual member listing). Scoping link
        // extraction to inside "#mw-pages" only means subcategory links are excluded by
        // construction, not by any extra filtering logic — this test documents and guards that.
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <html><body>
                <div id="mw-subcategories"><div class="mw-category-group"><li><a href="/wiki/Category:Raid_Bosses" title="Category:Raid Bosses">Raid Bosses</a></li></div></div>
                <div id="mw-pages"><div class="mw-category-group"><li><a href="/wiki/Zenos" title="Zenos">Zenos</a></li></div></div>
                </body></html>
                """),
        });
        var scraper = new NpcWikiScraper(client);

        var result = await scraper.ScrapeCategoryAsync(new Uri("https://consolegameswiki.com/wiki/Category:Bosses"), CancellationToken.None);

        Assert.Equal(["Zenos"], result.Names);
    }
}
