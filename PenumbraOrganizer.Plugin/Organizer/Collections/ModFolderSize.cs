namespace PenumbraOrganizer.Plugin.Organizer.Collections;

public static class ModFolderSize
{
    /// <summary>
    /// Total bytes of every file under the directory, or null when it does not exist. Unreadable
    /// entries are skipped. Throws OperationCanceledException when cancelled.
    /// </summary>
    public static long? Measure(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory))
            return null;

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try { total += new FileInfo(file).Length; }
                    catch (Exception) { }
                }

                foreach (var sub in Directory.EnumerateDirectories(current))
                    pending.Push(sub);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return total;
    }
}
