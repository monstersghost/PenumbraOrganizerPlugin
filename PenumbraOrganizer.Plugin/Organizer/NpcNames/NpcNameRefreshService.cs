namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

// NameCount is the number of names in the category as just written, NOT a delta. It was AddedCount
// (merged-minus-existing) under additive merging; under snapshots that is meaningless and can go
// negative, since a refresh that finds fewer names now removes the rest.
public sealed record NpcNameRefreshCategoryResult(string CategoryName, int NameCount, string? FailureReason);

public sealed record NpcNameRefreshResult(IReadOnlyList<NpcNameRefreshCategoryResult> Categories, bool RecoveredFromCorruption);

// Orchestrates the only network-touching action in the plugin: scrape all three wiki categories
// independently (one failing category doesn't block the others), then write the result as a
// SNAPSHOT - this run's names minus exclusions, with no reference to what the file held before.
//
// Snapshot rather than additive merge is what stops the file growing without bound: under merging
// a name the wiki stopped returning could never leave, so every refresh could only ever make the
// list larger, and a large enough list is what this whole piece exists to avoid building a matcher
// from. Corrupted-file recovery is distinct from missing-file first-run: a corrupted file is
// preserved as a timestamped backup, so nothing already there is silently lost.
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

        List<string> Snapshot(string categoryName)
        {
            var result = scraped[categoryName];

            // A category that stopped early keeps whatever was already on disk. Its Names holds
            // only the pages it got through before failing, so replacing from it would silently
            // delete every name past the failure point - a transient network error partway through
            // a 50-page category would shrink the file to the pages that happened to load. Only a
            // clean scrape is a complete picture, and only a complete picture may replace.
            if (result.FailureReason is not null)
                return [.. CategoryNames(existing, categoryName)];

            return [.. result.Names.Where(n => !excluded.Contains(n))];
        }

        var snapshot = new NpcNameListDocument
        {
            Version = NpcNameListCodec.CurrentVersion,
            NPCs = Snapshot("NPCs"),
            Enemies = Snapshot("Enemies"),
            Bosses = Snapshot("Bosses"),
            Excluded = existing.Excluded,
        };

        // Serialize sanitizes (trims, de-dups, sorts), so the counts reported to the UI are read
        // back from what was actually written rather than from the pre-sanitized lists.
        var json = NpcNameListCodec.Serialize(snapshot);
        NpcNameListStore.WriteAtomic(path, json);
        var written = NpcNameListCodec.Parse(json).Data!;

        var categoryResults = Categories
            .Select(c => new NpcNameRefreshCategoryResult(
                c.CategoryName,
                NameCount: CategoryNames(written, c.CategoryName).Count,
                scraped[c.CategoryName].FailureReason))
            .ToList();

        return new NpcNameRefreshResult(categoryResults, recovered);
    }

    // Falls back to an EMPTY document, never to the seed. Under snapshot semantics, seeding here
    // would write bundled names into the scraped file, and no later refresh would ever remove them
    // (a refresh only replaces from what the wiki returned) - the growth bug again, with a smaller
    // constant. A refresh's output is the wiki's contents and nothing else.
    //
    // Excluded is the exception, because it is user state rather than names: it is carried forward
    // from the previous scraped file when one parsed, otherwise taken from the bundled seed, which
    // ships none today. Losing it would let the next refresh re-add every name the user removed.
    private static (NpcNameListDocument Document, bool Recovered) LoadForRefresh(string path, string embeddedSeedJson)
    {
        var seedExclusions = NpcNameListCodec.Parse(embeddedSeedJson).Data?.Excluded ?? [];
        NpcNameListDocument Empty() => new() { Excluded = [.. seedExclusions] };

        if (!File.Exists(path))
            return (Empty(), false);

        var parse = NpcNameListCodec.Parse(File.ReadAllText(path));
        if (parse.Status == NpcNameListParseStatus.Ok)
            return (parse.Data!, false);

        var backupPath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
        File.Copy(path, backupPath, overwrite: true);
        return (Empty(), true);
    }

    private static List<string> CategoryNames(NpcNameListDocument document, string categoryName) => categoryName switch
    {
        "NPCs" => document.NPCs,
        "Enemies" => document.Enemies,
        "Bosses" => document.Bosses,
        _ => throw new ArgumentOutOfRangeException(nameof(categoryName), categoryName, null),
    };
}
