using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Persists the pre-mutation RollbackSnapshot into an operation's own bundle directory
/// (OperationBundlePaths.SnapshotPath) - a durable copy independent of organizer-history.json, so
/// an operation's own recovery data never depends on that separate file staying available or
/// consistent (design doc section 4a). Mirrors OperationJournalCodec/OperationPlanCodec's shape:
/// atomic write, TryLoad never throws.
/// </summary>
public static class OperationSnapshotCodec
{
    public static void Save(string path, RollbackSnapshot snapshot) =>
        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(snapshot));

    public static bool TryLoad(string path, out RollbackSnapshot? snapshot)
    {
        snapshot = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        RollbackSnapshot? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<RollbackSnapshot>(contents);
        }
        catch (JsonException)
        {
            return false;
        }

        if (candidate is null)
            return false;

        snapshot = candidate;
        return true;
    }
}
