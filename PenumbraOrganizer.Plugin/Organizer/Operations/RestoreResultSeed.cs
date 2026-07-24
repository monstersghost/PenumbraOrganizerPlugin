using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// The parts of RollbackHistory.BuildRestorePlan's classification that don't fit OperationPlan's
/// schema, persisted into the operation's own bundle directory so a later plan can reconstruct the
/// full Moved/Unchanged/SkippedUninstalled/RootRelocated picture without depending on
/// organizer-history.json still holding the target entry or on Plugin.cs's local state surviving a
/// restart. "Moved" identifiers aren't repeated here - they're every identifier already present in
/// the accompanying OperationPlan's RecoveryTargets. RootRelocatedIdentifiers is a subset of those,
/// marking which moves target Penumbra's plain root rather than the snapshot's exact stored path.
/// TargetSnapshot carries the full RollbackSnapshot (not just its Id) for the same reason
/// OperationSnapshotCodec's own pre-restore snapshot copy does: self-contained, independent of
/// organizer-history.json (whose Delete action could otherwise leave a dangling reference).
/// </summary>
public sealed record RestoreResultSeed(
    RollbackSnapshot TargetSnapshot,
    IReadOnlyList<string> UnchangedIdentifiers,
    IReadOnlyList<string> SkippedUninstalledIdentifiers,
    IReadOnlyList<string> RootRelocatedIdentifiers);

/// <summary>
/// Mirrors OperationSnapshotCodec's shape: atomic write, TryLoad never throws. Validates structural
/// completeness (no null target snapshot or classification list), not cross-field semantics - e.g.
/// "every RootRelocated identifier is also a moved identifier" is a future reader's concern when it
/// interprets this file against the accompanying OperationPlan, not this codec's.
/// </summary>
public static class OperationRestoreResultSeedCodec
{
    public static void Save(string path, RestoreResultSeed seed) =>
        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(seed));

    public static bool TryLoad(string path, out RestoreResultSeed? seed)
    {
        seed = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        RestoreResultSeed? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<RestoreResultSeed>(contents);
        }
        catch (JsonException)
        {
            return false;
        }

        if (candidate is null || candidate.TargetSnapshot is null || candidate.UnchangedIdentifiers is null
            || candidate.SkippedUninstalledIdentifiers is null || candidate.RootRelocatedIdentifiers is null)
            return false;

        seed = candidate;
        return true;
    }
}
