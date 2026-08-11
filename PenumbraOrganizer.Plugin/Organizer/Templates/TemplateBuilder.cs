using System.Globalization;
using System.Reflection;

namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Everything the author chooses about the document, other than which mods are in it.
/// </summary>
public sealed record TemplateMetadata(
    string Name,
    string? Author,
    string? Description,
    TemplateFallback Fallback,
    IReadOnlyDictionary<string, string> FolderLabels);

/// <summary>
/// <paramref name="RootLevelSkipped"/> is a count rather than a warning per row: a library with two
/// hundred mods at its root would otherwise produce two hundred warnings saying the same thing. The
/// export screen states it once.
/// </summary>
public sealed record TemplateBuildResult(
    OrganizationTemplate Document,
    IReadOnlyList<TemplateWarning> Warnings,
    int RootLevelSkipped);

/// <summary>
/// Turns the author's library plus their inclusion choices into a document. Pure and non-mutating:
/// the export screen calls this on every frame it needs a preview or a length estimate, and calls it
/// once more to emit, so the reviewed document and the written one cannot be different computations.
/// </summary>
public static class TemplateBuilder
{
    /// <param name="includedFolders">
    /// Required rather than derived, because an empty folder is invisible in the rows: every folder
    /// reachable from a row is that row's own parent. See TemplateExportFolders for the seeding.
    /// </param>
    /// <param name="createdWithVersion">Null takes the running assembly's version.</param>
    /// <param name="createdAtUtc">Null takes the current time. Injected so tests are not clock-dependent.</param>
    public static TemplateBuildResult Build(
        IReadOnlyCollection<OrganizerModRow> rows,
        IReadOnlySet<string> includedIdentifiers,
        IReadOnlyCollection<string> includedFolders,
        TemplateMetadata metadata,
        string? createdWithVersion = null,
        DateTimeOffset? createdAtUtc = null)
    {
        var warnings = new List<TemplateWarning>();
        var rootLevelSkipped = 0;

        // Keyed on the normalized name because that is what the importer matches on, so a collision
        // here is a collision there.
        var foldersByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!includedIdentifiers.Contains(row.Identifier))
                continue;

            // Export reflects the APPLIED organization, never a pending proposal. Otherwise a user
            // could sort, review a new structure, export, and unknowingly share the old layout.
            var folder = OrganizationCleanupPlanner.GetVirtualParent(row.CurrentPath);
            if (folder is null)
            {
                // A destination is folder-only and the importer rejects an empty one, so a mod at
                // the library root carries no folder this format can express.
                rootLevelSkipped++;
                continue;
            }

            var key = ModNameNormalizer.Normalize(row.Name);
            if (key.Length == 0)
                continue;

            if (!foldersByKey.TryGetValue(key, out var folders))
                foldersByKey[key] = folders = new HashSet<string>(StringComparer.Ordinal);

            folders.Add(folder);
        }

        var entries = new List<TemplateEntry>();
        foreach (var (key, folders) in foldersByKey.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            // Several rows normalizing alike but already sitting in ONE folder is not a conflict:
            // the entry is identical whichever row produced it. Only disagreement is a conflict, and
            // it drops the whole group rather than picking a winner -- the same rule
            // TemplateDuplicateResolver applies to a conflicting duplicate on the way in.
            if (folders.Count > 1)
            {
                warnings.Add(new TemplateWarning(TemplateWarningCode.ExportNameCollision, key));
                continue;
            }

            entries.Add(new TemplateEntry(key, folders.Single()));
        }

        var document = new OrganizationTemplate
        {
            FormatVersion = TemplateCodec.SupportedFormatVersion,
            Name = metadata.Name,
            Author = metadata.Author,
            Description = metadata.Description,
            CreatedWithVersion = createdWithVersion ?? RunningVersion(),
            CreatedAtUtc = Timestamp(createdAtUtc ?? DateTimeOffset.UtcNow),
            FallbackStrategy = metadata.Fallback.Strategy.ToString(),
            FallbackSplitGear = metadata.Fallback.SplitGear,
            FallbackSplitNpc = metadata.Fallback.SplitNpc,
            FolderLabels = new Dictionary<string, string>(metadata.FolderLabels, StringComparer.Ordinal),
            Folders = [.. includedFolders.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)],
            Entries = entries,
        };

        return new TemplateBuildResult(document, warnings, rootLevelSkipped);
    }

    // Provenance only, never validated on import, so an assembly with no version is not an error.
    private static string? RunningVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString();

    private static string Timestamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
