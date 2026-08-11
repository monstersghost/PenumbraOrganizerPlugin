namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

public sealed record NpcNameListLoadResult(NpcNameListDocument Document, string? Warning);

public static class NpcNameListStore
{
    // A wiki scrape produces roughly 21,000 names. Building the matcher from a list that size is
    // implicated in reports of the game closing instantly during Scan and during the Search index
    // build, both of which build a matcher before touching a single mod. The exact mechanism is
    // not established; what is established is that a small list works and that list does not.
    // 2,000 is far above the bundled seed and far below the scale that fails.
    //
    // This is a stopgap for 0.5.3.1, not the fix. The matcher's one-giant-compiled-alternation-
    // per-category structure is what needs replacing.
    public const int MaxSafeNameCount = 2_000;

    // The file the wiki refresh wrote before 0.6.0, and the file it writes now. The rename is what
    // demotes the scrape from "the name list" to "an opt-in list beside the bundled one".
    public const string LegacyFileName = "npc-name-list.json";
    public const string ScrapedFileName = "npc-name-list-scraped.json";

    // The opted-in path's own ceiling, deliberately NOT MaxSafeNameCount. That guard is 2,000 and
    // does not merely warn - it backs the file up and overwrites it with the seed. A full scrape is
    // roughly 20,115 names, so reusing it here would destroy the user's file on every opted-in
    // load, for a list they deliberately enabled. This one warns and falls back in memory only,
    // writing nothing, and sits above a full scrape so it trips on runaway growth rather than on
    // normal use.
    public const int MaxScrapedNameCount = 25_000;

    public static string ScrapedListPath(string configDirectory) =>
        Path.Combine(configDirectory, ScrapedFileName);

    private static int NameCount(NpcNameListDocument document) =>
        document.NPCs.Count + document.Enemies.Count + document.Bosses.Count;

    /// <summary>
    /// Renames a pre-0.6.0 <c>npc-name-list.json</c> to <c>npc-name-list-scraped.json</c>. Returns a
    /// warning to log, or null when there was nothing to do. Never throws: it runs during plugin
    /// construction, where an exception would take the whole plugin down over a diagnostic concern.
    /// </summary>
    public static string? MigrateLegacyList(string configDirectory)
    {
        var legacyPath = Path.Combine(configDirectory, LegacyFileName);
        var scrapedPath = Path.Combine(configDirectory, ScrapedFileName);

        // Neither present (fresh install) and scraped-only (already migrated, the steady state that
        // runs on every load) are both no-ops. Only a legacy file left on disk needs anything.
        if (!File.Exists(legacyPath))
            return null;

        if (File.Exists(scrapedPath))
        {
            // Nothing is overwritten or deleted. Both-present is reachable through an interrupted
            // migration or a downgrade-then-upgrade cycle, which is precisely the situation where
            // the user has no backup of either file.
            return $"Both {LegacyFileName} and {ScrapedFileName} are present in {configDirectory}. "
                + $"{ScrapedFileName} is the one in use; {LegacyFileName} has been left untouched "
                + "and is no longer read.";
        }

        try
        {
            File.Move(legacyPath, scrapedPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not rename {LegacyFileName} to {ScrapedFileName} ({ex.GetType().Name}: "
                + $"{ex.Message}). The opt-in scraped NPC name list will be unavailable until this "
                + "is resolved; the bundled list is unaffected.";
        }
    }

    /// <summary>
    /// The matcher's name source: the bundled static list, plus the opt-in scraped list when
    /// <paramref name="useScraped"/> is true. Never throws for on-disk problems and never writes.
    /// </summary>
    /// <summary>
    /// The bundled curated list, read from this assembly's embedded resources.
    /// </summary>
    /// <remarks>
    /// Lives here rather than on <c>Plugin</c> so that the background library phase never names
    /// <c>Plugin</c> at all. <c>ScanProcessor.Prepare</c> and <c>IndexProcessor.Prepare</c> call
    /// <see cref="LoadForMatching"/> off the framework thread, and <c>LibraryWorkPurityTests</c>
    /// checks signatures only - so a call graph reaching the type that holds every Dalamud static is
    /// one careless edit away from a real violation the test could not see. Resolving the assembly
    /// from <c>typeof(NpcNameListStore)</c> keeps that impossible.
    /// <para>
    /// The file must be saved WITHOUT a UTF-8 BOM: <c>NpcNameListCodec.Parse</c> never throws, so a
    /// BOM would silently make the bundled list unavailable and leave every scan with no NPC names.
    /// </para>
    /// </remarks>
    public static string ReadEmbeddedStaticList()
    {
        const string resourceName = "PenumbraOrganizer.Plugin.Organizer.NpcNames.npc-name-list-static.json";
        using var stream = typeof(NpcNameListStore).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static NpcNameListLoadResult LoadForMatching(string configDirectory, bool useScraped)
    {
        var staticParse = NpcNameListCodec.Parse(ReadEmbeddedStaticList());
        if (staticParse.Status != NpcNameListParseStatus.Ok)
            throw new InvalidOperationException(
                "Bundled static NPC name list is not valid JSON - this is a packaging bug, not a runtime condition.");
        var staticList = staticParse.Data!;

        // Opted out: the scraped file is not opened, not created, and not touched. A corrupt file
        // the user is not using must not produce a warning about a list that is not in play.
        if (!useScraped)
            return new NpcNameListLoadResult(staticList, null);

        var scrapedPath = ScrapedListPath(configDirectory);

        // Deliberately NOT Load's missing-file branch, which seeds the file. Writing here would
        // create the very file MigrateLegacyList's four-case table assumes is absent, turning
        // "never refreshed" into "already migrated" on the next load.
        if (!File.Exists(scrapedPath))
            return new NpcNameListLoadResult(staticList, null);

        // Exactly one thing can be wrong with this file per load, so the warning is a single string
        // rather than an accumulated list. The plan called for joining two warnings - a failed
        // migration plus a corrupt file - but migration runs at a different call site with its own
        // return value, so no path here can produce two. An accumulate-and-join that no input can
        // exercise reads as though it does something.
        string contents;
        try
        {
            contents = File.ReadAllText(scrapedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new NpcNameListLoadResult(staticList,
                $"{ScrapedFileName} could not be read ({ex.GetType().Name}); "
                + "using the bundled NPC name list for this session.");
        }

        var parse = NpcNameListCodec.Parse(contents);
        switch (parse.Status)
        {
            case NpcNameListParseStatus.MalformedJson:
                return new NpcNameListLoadResult(staticList,
                    $"{scrapedPath} is not valid JSON; using the bundled NPC name list for this session.");

            case NpcNameListParseStatus.UnsupportedVersion:
                return new NpcNameListLoadResult(staticList,
                    $"{scrapedPath} has an unsupported Version; using the bundled NPC name list for this session.");
        }

        var scraped = parse.Data!;
        var count = NameCount(scraped);
        if (count > MaxScrapedNameCount)
        {
            // Warn and fall back in memory only. The file is left exactly as it is - see the
            // comment on MaxScrapedNameCount for why this must not reuse Load's replace-and-back-up
            // behaviour.
            return new NpcNameListLoadResult(staticList,
                $"{ScrapedFileName} holds {count:N0} names, more than the "
                + $"{MaxScrapedNameCount:N0} this version will build a matcher from. Using the "
                + "bundled list for this session; the file has not been changed.");
        }

        return new NpcNameListLoadResult(Union(staticList, scraped), null);
    }

    private static NpcNameListDocument Union(NpcNameListDocument staticList, NpcNameListDocument scraped)
    {
        // Excluded applies to the SCRAPED names only. It is the set the user removed through the
        // refresh UI, and the static list's contents were never offered there; letting a stale
        // exclusion silently drop "Shiva" (which the bundled list carries in both Enemies and
        // Bosses) would change classification for a name the user never touched.
        var excluded = new HashSet<string>(scraped.Excluded, StringComparer.OrdinalIgnoreCase);

        // Duplicates across the two sources are harmless and need no de-dup pass: Sanitize de-dups
        // within an array but not across, and NpcNameMatcher merges same-name entries into a single
        // entry carrying both category flags.
        return new NpcNameListDocument
        {
            Version = NpcNameListCodec.CurrentVersion,
            NPCs = [.. staticList.NPCs, .. scraped.NPCs.Where(n => !excluded.Contains(n))],
            Enemies = [.. staticList.Enemies, .. scraped.Enemies.Where(n => !excluded.Contains(n))],
            Bosses = [.. staticList.Bosses, .. scraped.Bosses.Where(n => !excluded.Contains(n))],
            Excluded = scraped.Excluded,
        };
    }

    // Never throws for a missing/corrupted on-disk file — always returns a usable document,
    // falling back to the bundled seed. A merely unreadable file is left alone on disk (the
    // warning is reported by the caller via IPluginLog). An oversized one is the exception: it
    // is backed up and replaced, because leaving it would re-arm the crash on every later run.
    public static NpcNameListLoadResult Load(string path, string embeddedSeedJson)
    {
        var seedParse = NpcNameListCodec.Parse(embeddedSeedJson);
        if (seedParse.Status != NpcNameListParseStatus.Ok)
            throw new InvalidOperationException(
                "Bundled NPC name-list seed is not valid JSON — this is a packaging bug, not a runtime condition.");
        var seed = seedParse.Data!;

        if (!File.Exists(path))
        {
            WriteAtomic(path, NpcNameListCodec.Serialize(seed));
            return new NpcNameListLoadResult(seed, null);
        }

        var parse = NpcNameListCodec.Parse(File.ReadAllText(path));
        if (parse.Status == NpcNameListParseStatus.Ok && NameCount(parse.Data!) > MaxSafeNameCount)
        {
            var count = NameCount(parse.Data!);
            var backupPath = $"{path}.oversized-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
            var warning =
                $"The NPC name list held {count:N0} names, more than the {MaxSafeNameCount:N0} this "
                + "version can safely build a matcher from. It has been replaced with the bundled "
                + $"list and the old one kept as {Path.GetFileName(backupPath)}.";

            // Replacing rather than ignoring: an oversized file left in place re-arms the problem
            // on every later scan and index build. A failure here must not take the run with it -
            // falling back to the seed in memory is still correct, the file just stays oversized.
            try
            {
                File.Copy(path, backupPath, overwrite: true);
                WriteAtomic(path, NpcNameListCodec.Serialize(seed));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning =
                    $"The NPC name list holds {count:N0} names, more than the {MaxSafeNameCount:N0} "
                    + "this version can safely build a matcher from, and it could not be replaced "
                    + $"({ex.GetType().Name}). Using the bundled list for this session.";
            }

            return new NpcNameListLoadResult(seed, warning);
        }

        return parse.Status switch
        {
            NpcNameListParseStatus.Ok => new NpcNameListLoadResult(parse.Data!, null),
            NpcNameListParseStatus.MalformedJson => new NpcNameListLoadResult(
                seed, $"{path} is not valid JSON; using the bundled NPC name list for this session."),
            NpcNameListParseStatus.UnsupportedVersion => new NpcNameListLoadResult(
                seed, $"{path} has an unsupported Version; using the bundled NPC name list for this session."),
            _ => new NpcNameListLoadResult(seed, "Unrecognized NPC name-list state; using the bundled list."),
        };
    }

    // Shared by scan-time seeding (first run) and refresh-time writes (Task 7) — temp-file then
    // atomic replace, the same pattern Plugin.cs already uses for ExportWorkbook/WriteBackup.
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    public static Classification.NpcNameMatcher BuildMatcher(NpcNameListDocument document) =>
        new(document.NPCs, document.Enemies, document.Bosses);
}
