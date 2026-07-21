namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

public sealed record NpcNameListLoadResult(NpcNameListDocument Document, string? Warning);

public static class NpcNameListStore
{
    // Never throws for a missing/corrupted on-disk file — always returns a usable document,
    // falling back to the bundled seed. Scan-time corruption never touches disk (Warning is
    // reported by the caller via IPluginLog; nothing here writes over an unreadable file).
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
