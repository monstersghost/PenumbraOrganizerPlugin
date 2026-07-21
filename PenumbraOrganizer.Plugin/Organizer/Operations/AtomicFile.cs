namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Atomic temp-write-flush-replace for small plugin-owned JSON files (operation plans, journals).
/// Design doc 2026-07-21-incremental-operations-design.md section 9: the temp file lives beside
/// the destination so the final move is same-volume, and any temp file left behind by a prior
/// crashed write is cleared before a new attempt rather than left to accumulate.
/// </summary>
public static class AtomicFile
{
    public static void CreateOrReplace(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public static bool TryReadValidated(string path, out string? contents)
    {
        if (!File.Exists(path))
        {
            contents = null;
            return false;
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrEmpty(text))
        {
            contents = null;
            return false;
        }

        contents = text;
        return true;
    }
}
