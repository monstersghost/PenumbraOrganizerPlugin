namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// An immutable read of Penumbra's live mod list, with duplicate identifiers surfaced rather than
/// thrown. Consumers treat any non-empty DuplicateIdentifiers as "live state can't be trusted"
/// (verification/recovery force RecoveryRequired), so the read-side guard is non-throwing - unlike
/// RollbackHistory.CaptureSnapshot's deliberate throw on the write side. Reuses the existing LiveMod
/// record so there is one live-mod shape, not two.
/// </summary>
public sealed record LiveModSnapshot(
    IReadOnlyDictionary<string, LiveMod> Mods,
    IReadOnlySet<string> DuplicateIdentifiers);

public static class LiveModSnapshotBuilder
{
    public static LiveModSnapshot Build(IEnumerable<LiveMod> mods)
    {
        var byIdentifier = new Dictionary<string, LiveMod>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mod in mods)
            if (!byIdentifier.TryAdd(mod.Identifier, mod))
                duplicates.Add(mod.Identifier);

        return new LiveModSnapshot(byIdentifier, duplicates);
    }
}
