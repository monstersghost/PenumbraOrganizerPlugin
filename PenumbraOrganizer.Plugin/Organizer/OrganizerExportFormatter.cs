using System.Text;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

public static class OrganizerExportFormatter
{
    private static string ManagedBy(OrganizerModRow mod) => mod.ProteusRole switch
    {
        ProteusRole.Managed => "ProteusManaged",
        ProteusRole.Made => "Proteus",
        _ => mod.HeliosphereManaged ? "Heliosphere" : "(none)",
    };

    public static string Format(IReadOnlyList<OrganizerModRow> mods, ReviewResult validation,
                                BodyPriority? stagedBodyPriority = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Penumbra Organizer Export ===");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Total mods: {mods.Count}");
        sb.AppendLine($"Protected: {mods.Count(m => m.Protected)}");
        sb.AppendLine($"Collisions: {validation.PathCollisions.Count}");
        if (stagedBodyPriority is not null)
            sb.AppendLine($"Body priority used for the last sort: {string.Join(" > ", stagedBodyPriority.Labels)}");

        // Only shown when at least one Gear mod exists - added after a tester report where 0 of
        // ~1500 Gear mods got a subcategory, to distinguish "config files failed to read" from
        // "these are all legitimately ambiguous multi-piece outfits" without the Dalamud log.
        var gearMods = mods.Where(m => m.GearSlotDiagnostic != GearSlotDiagnostic.NotApplicable).ToList();
        if (gearMods.Count > 0)
        {
            var single = gearMods.Count(m => m.GearSlotDiagnostic == GearSlotDiagnostic.Single);
            var ambiguous = gearMods.Count(m => m.GearSlotDiagnostic == GearSlotDiagnostic.Ambiguous);
            var zeroEvidence = gearMods.Count(m => m.GearSlotDiagnostic == GearSlotDiagnostic.ZeroEvidence);
            var directoryMissing = gearMods.Count(m => m.GearSlotDiagnostic == GearSlotDiagnostic.DirectoryMissing);
            var readFailure = gearMods.Count(m => m.GearSlotDiagnostic == GearSlotDiagnostic.ReadFailure);
            sb.AppendLine(
                $"Gear slot detection: {single} single-slot, {ambiguous} multi-slot (ambiguous), "
                + $"{zeroEvidence} zero-evidence, {directoryMissing} directory-missing, {readFailure} read/parse failures");
        }

        sb.AppendLine();
        sb.AppendLine("--- Mods ---");

        foreach (var mod in mods)
        {
            sb.AppendLine($"Identifier: {mod.Identifier}");
            sb.AppendLine($"Name: {mod.Name}");
            sb.AppendLine($"Author: {mod.Author}");
            sb.AppendLine($"Category: {(mod.Category is null ? "(none)" : mod.Category.Value.ToString())}");
            sb.AppendLine($"SubCategory: {mod.SubCategory ?? "(none)"}");
            // Always emitted, including "(none)", unlike GearSlotDiagnostic below which is
            // suppressed when not applicable. A body type is read from the mod's NAME, so it is
            // the classification signal most likely to be wrong, and "this mod matched no body"
            // is exactly as diagnostic as "it matched Bibo+" - a dump that showed only the hits
            // could not answer how many mods went unmatched.
            sb.AppendLine($"BodyType: {mod.BodyType ?? "(none)"}");
            // Why this mod landed where it did: every candidate with its tier and source. The
            // BodyType line above is the DEFAULT-order winner; a sort with another priority can pick
            // a different candidate from this same list.
            if (mod.BodyCandidates is { } candidates)
                sb.AppendLine($"BodyEvidence: {candidates.Describe()}");
            if (mod.StagedBodyResolution is { } staged)
                sb.AppendLine($"StagedBody: {staged.Label} ({staged.Tier}/{staged.Source})");
            if (mod.GearSlotDiagnostic != GearSlotDiagnostic.NotApplicable)
                sb.AppendLine($"GearSlotDiagnostic: {mod.GearSlotDiagnostic}");
            sb.AppendLine($"HeliosphereManaged: {mod.HeliosphereManaged}");
            sb.AppendLine($"ManagedBy: {ManagedBy(mod)}");
            sb.AppendLine($"Protected: {mod.Protected}");
            sb.AppendLine($"CurrentPath: {mod.CurrentPath}");
            sb.AppendLine($"ProposedPath: {mod.ProposedPath}");
            sb.AppendLine();
        }

        sb.AppendLine("--- Validate() ---");
        sb.AppendLine($"Protected violations: {(validation.ProtectedViolations.Count == 0 ? "(none)" : string.Join(", ", validation.ProtectedViolations))}");

        if (validation.PathCollisions.Count == 0)
        {
            sb.AppendLine("Path collisions: (none)");
        }
        else
        {
            sb.AppendLine("Path collisions:");
            foreach (var (path, identifiers) in validation.PathCollisions)
                sb.AppendLine($"  '{path}': {string.Join(", ", identifiers)}");
        }

        return sb.ToString();
    }
}
