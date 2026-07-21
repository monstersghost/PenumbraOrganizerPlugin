using System.Text;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

public static class OrganizerExportFormatter
{
    public static string Format(IReadOnlyList<OrganizerModRow> mods, ReviewResult validation)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Penumbra Organizer Export ===");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Total mods: {mods.Count}");
        sb.AppendLine($"Protected: {mods.Count(m => m.Protected)}");
        sb.AppendLine($"Collisions: {validation.PathCollisions.Count}");

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
            if (mod.GearSlotDiagnostic != GearSlotDiagnostic.NotApplicable)
                sb.AppendLine($"GearSlotDiagnostic: {mod.GearSlotDiagnostic}");
            sb.AppendLine($"HeliosphereManaged: {mod.HeliosphereManaged}");
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
