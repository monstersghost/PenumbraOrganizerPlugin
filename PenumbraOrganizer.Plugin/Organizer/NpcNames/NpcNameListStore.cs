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

    private static int NameCount(NpcNameListDocument document) =>
        document.NPCs.Count + document.Enemies.Count + document.Bosses.Count;

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
