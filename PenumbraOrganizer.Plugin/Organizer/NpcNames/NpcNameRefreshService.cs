namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

public sealed record NpcNameRefreshCategoryResult(string CategoryName, int AddedCount, string? FailureReason);

public sealed record NpcNameRefreshResult(IReadOnlyList<NpcNameRefreshCategoryResult> Categories, bool RecoveredFromCorruption);

// Orchestrates the only network-touching action in the plugin: scrape all three wiki categories
// independently (one failing category doesn't block the others), additively merge whatever
// succeeded into the on-disk list (respecting Excluded, never removing anything already
// present), and write atomically. Corrupted-file recovery is distinct from missing-file
// first-run: a corrupted file is preserved as a timestamped backup before starting fresh from
// the seed, so nothing already there is silently lost.
public sealed class NpcNameRefreshService
{
    private static readonly (string CategoryName, Uri Url)[] Categories =
    [
        ("NPCs", new Uri("https://consolegameswiki.com/wiki/Category:NPCs")),
        ("Enemies", new Uri("https://consolegameswiki.com/wiki/Category:Enemies")),
        ("Bosses", new Uri("https://consolegameswiki.com/wiki/Category:Bosses")),
    ];

    private readonly NpcWikiScraper _scraper;

    public NpcNameRefreshService(NpcWikiScraper scraper) => _scraper = scraper;

    public async Task<NpcNameRefreshResult> RefreshAsync(
        string path, string embeddedSeedJson, CancellationToken cancellationToken)
    {
        var (existing, recovered) = LoadForRefresh(path, embeddedSeedJson);

        var scraped = new Dictionary<string, NpcWikiScrapeResult>();
        foreach (var (categoryName, url) in Categories)
            scraped[categoryName] = await _scraper.ScrapeCategoryAsync(url, cancellationToken);

        var excluded = new HashSet<string>(existing.Excluded, StringComparer.OrdinalIgnoreCase);
        var merged = NpcNameListCodec.MergeAdditive(
            existing,
            newNpcs: scraped["NPCs"].Names.Where(n => !excluded.Contains(n)).ToList(),
            newEnemies: scraped["Enemies"].Names.Where(n => !excluded.Contains(n)).ToList(),
            newBosses: scraped["Bosses"].Names.Where(n => !excluded.Contains(n)).ToList());

        NpcNameListStore.WriteAtomic(path, NpcNameListCodec.Serialize(merged));

        var categoryResults = Categories
            .Select(c => new NpcNameRefreshCategoryResult(
                c.CategoryName,
                AddedCount: CategoryCount(merged, c.CategoryName) - CategoryCount(existing, c.CategoryName),
                scraped[c.CategoryName].FailureReason))
            .ToList();

        return new NpcNameRefreshResult(categoryResults, recovered);
    }

    private static (NpcNameListDocument Document, bool Recovered) LoadForRefresh(string path, string embeddedSeedJson)
    {
        var seed = NpcNameListCodec.Parse(embeddedSeedJson).Data!;

        if (!File.Exists(path))
            return (seed, false);

        var parse = NpcNameListCodec.Parse(File.ReadAllText(path));
        if (parse.Status == NpcNameListParseStatus.Ok)
            return (parse.Data!, false);

        var backupPath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
        File.Copy(path, backupPath, overwrite: true);
        return (seed, true);
    }

    private static int CategoryCount(NpcNameListDocument document, string categoryName) => categoryName switch
    {
        "NPCs" => document.NPCs.Count,
        "Enemies" => document.Enemies.Count,
        "Bosses" => document.Bosses.Count,
        _ => throw new ArgumentOutOfRangeException(nameof(categoryName), categoryName, null),
    };
}
