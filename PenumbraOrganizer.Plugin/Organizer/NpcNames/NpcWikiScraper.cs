using AngleSharp.Html.Parser;

namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

public sealed record NpcWikiScrapeResult(IReadOnlyList<string> Names, string? FailureReason);

// The only piece of this feature that touches the network. Each call scrapes one paginated
// MediaWiki category page end to end, with defensive termination: a visited-URL set (catches
// pagination loops), a hard page ceiling, and same-host/HTTPS-only link following. A null
// FailureReason with a populated Names list means the category was fully and successfully
// scraped to its last page; a non-null FailureReason means something stopped early (the caller
// still gets whatever names were gathered before the failure).
public sealed class NpcWikiScraper
{
    private const int MaxPagesPerCategory = 100;
    private static readonly HtmlParser Parser = new();

    private readonly HttpClient _httpClient;

    public NpcWikiScraper(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<NpcWikiScrapeResult> ScrapeCategoryAsync(Uri startUrl, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var visited = new HashSet<Uri>();
        var host = startUrl.Host;
        Uri? currentUrl = startUrl;
        var pagesFetched = 0;

        while (currentUrl is not null)
        {
            if (pagesFetched >= MaxPagesPerCategory)
                return new NpcWikiScrapeResult(names, $"Stopped after reaching the {MaxPagesPerCategory}-page limit.");

            if (!visited.Add(currentUrl))
                return new NpcWikiScrapeResult(names, $"Pagination loop detected at {currentUrl}.");

            if (!string.Equals(currentUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentUrl.Host, host, StringComparison.OrdinalIgnoreCase))
                return new NpcWikiScrapeResult(names, $"Refused to follow off-host or non-HTTPS link: {currentUrl}.");

            string html;
            try
            {
                html = await _httpClient.GetStringAsync(currentUrl, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new NpcWikiScrapeResult(names, $"Failed to fetch {currentUrl}: {ex.Message}");
            }

            pagesFetched++;

            var document = Parser.ParseDocument(html);
            var container = document.QuerySelector("#mw-pages");
            if (container is null)
                return new NpcWikiScrapeResult(names, $"Category-member container not found on {currentUrl}.");

            foreach (var link in container.QuerySelectorAll("a"))
            {
                var name = link.GetAttribute("title") ?? link.TextContent;
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name.Trim());
            }

            // A missing "next page" link is the normal, successful termination condition for
            // the last page of a category — not a failure. Only the conditions above (loop,
            // off-host, ceiling, fetch/parse error, missing container) are failures.
            var nextHref = document
                .QuerySelectorAll("a")
                .FirstOrDefault(a => a.TextContent.Contains("next page", StringComparison.OrdinalIgnoreCase))
                ?.GetAttribute("href");

            currentUrl = nextHref is null
                ? null
                : (Uri.TryCreate(currentUrl, nextHref, out var resolved) ? resolved : null);
        }

        return new NpcWikiScrapeResult(names, null);
    }
}
