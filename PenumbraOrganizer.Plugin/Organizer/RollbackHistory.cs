namespace PenumbraOrganizer.Plugin.Organizer;

public sealed record RollbackSnapshot(
    Guid Id,
    DateTimeOffset CreatedAt,
    string? Label,
    string AutoDescription,
    IReadOnlyDictionary<string, string> ModPaths);

public sealed record LiveMod(string Identifier, string Name, string FullPath, bool HeliosphereManaged);

public enum RestoreOutcome { Moved, Unchanged, SkippedUninstalled, RootRelocated, Failed }

public sealed record RestoreResult(string Identifier, RestoreOutcome Outcome, string? FailureReason);

public sealed record RestorePlan(
    IReadOnlyList<ModMove> Moves,
    IReadOnlyList<string> UnchangedIdentifiers,
    IReadOnlyList<string> SkippedUninstalledIdentifiers,
    IReadOnlyList<string> RootRelocatedIdentifiers);

public static class RollbackHistory
{
    public static IReadOnlyList<RollbackSnapshot> Load(string historyFilePath)
    {
        if (!File.Exists(historyFilePath))
            return [];

        var json = File.ReadAllText(historyFilePath);
        return System.Text.Json.JsonSerializer.Deserialize<List<RollbackSnapshot>>(json) ?? [];
    }

    public static void Save(string historyFilePath, IReadOnlyList<RollbackSnapshot> snapshots)
    {
        var directory = Path.GetDirectoryName(historyFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = historyFilePath + ".tmp";
        File.WriteAllText(tempPath, System.Text.Json.JsonSerializer.Serialize(snapshots));
        File.Move(tempPath, historyFilePath, overwrite: true);
    }

    // ToDictionary throws ArgumentException if two live mods share an identifier - deliberate:
    // a capture must fail loudly on a duplicate identity rather than silently keep one and drop
    // the other (design spec, Data Model & Storage: "capture fails if Penumbra reports duplicate
    // identifiers").
    public static RollbackSnapshot CaptureSnapshot(
        IReadOnlyList<LiveMod> currentMods, string? label, string autoDescription)
    {
        var modPaths = currentMods.ToDictionary(m => m.Identifier, m => m.FullPath, StringComparer.Ordinal);
        return new RollbackSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, label, autoDescription, modPaths);
    }

    public static IReadOnlyList<RollbackSnapshot> AppendSnapshot(string historyFilePath, RollbackSnapshot snapshot)
    {
        var updated = Load(historyFilePath).Append(snapshot).ToList();
        Save(historyFilePath, updated);
        return updated;
    }

    public static IReadOnlyList<RollbackSnapshot> DeleteSnapshot(string historyFilePath, Guid id)
    {
        var updated = Load(historyFilePath).Where(s => s.Id != id).ToList();
        Save(historyFilePath, updated);
        return updated;
    }

    // A mod present in both the target snapshot and the current library is always moved to its
    // historical path here - current protection state (individual, folder, or Heliosphere-
    // managed) is deliberately NOT consulted. A snapshot captured while a mod was movable must
    // remain restorable even if the user has since protected it (tester report, Bug 3: "Restore
    // should operate from snapshot data, not current sorting protection policy"). A move is only
    // withheld when the mod isn't present in both sets (SkippedUninstalledIdentifiers, below) or
    // when Penumbra's own SetModPath rejects it at execution time - that failure is surfaced by
    // the caller (Plugin.Restore) as RestoreOutcome.Failed, not by this method.
    //
    // A mod present in the current library but absent from the target snapshot is root-relocated
    // (rootRelocated, below) - this is PRE-EXISTING, UNCHANGED behavior, not new to this method:
    // it predates the protection-removal change above and is out of scope for this plan (the
    // tester never reported it, and changing it is a distinct product decision - see the plan's
    // Revision Note). It is documented here only so a future reader doesn't mistake it for a
    // consequence of the change directly above it.
    //
    // Mods present only in the snapshot (uninstalled since capture) are reported, never moved.
    public static RestorePlan BuildRestorePlan(RollbackSnapshot target, IReadOnlyList<LiveMod> currentMods)
    {
        var moves = new List<ModMove>();
        var unchanged = new List<string>();
        var rootRelocated = new List<string>();

        foreach (var mod in currentMods)
        {
            if (target.ModPaths.TryGetValue(mod.Identifier, out var historicalPath))
            {
                if (PenumbraPathSemantics.AreEquivalent(mod.FullPath, historicalPath, mod.Name))
                    unchanged.Add(mod.Identifier);
                else
                    moves.Add(new ModMove(mod.Identifier, mod.FullPath, historicalPath));
            }
            else
            {
                var rootPath = PenumbraPathSemantics.FixName(mod.Name);
                if (PenumbraPathSemantics.AreEquivalent(mod.FullPath, rootPath, mod.Name))
                {
                    unchanged.Add(mod.Identifier);
                }
                else
                {
                    moves.Add(new ModMove(mod.Identifier, mod.FullPath, rootPath));
                    rootRelocated.Add(mod.Identifier);
                }
            }
        }

        var currentIdentifiers = currentMods.Select(m => m.Identifier).ToHashSet(StringComparer.Ordinal);
        var skippedUninstalled = target.ModPaths.Keys
            .Where(id => !currentIdentifiers.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return new RestorePlan(moves, unchanged, skippedUninstalled, rootRelocated);
    }
}
