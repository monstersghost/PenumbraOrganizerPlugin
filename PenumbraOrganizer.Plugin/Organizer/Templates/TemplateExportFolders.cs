namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// <paramref name="OrganizationJsonUnavailable"/> is surfaced rather than swallowed: the folder list
/// is then incomplete in a specific way -- empty folders are missing -- and the author deserves to
/// know that before publishing a template that silently omits their empty buckets.
/// </summary>
public sealed record TemplateExportFolderSeed(
    IReadOnlyList<string> Folders,
    bool OrganizationJsonUnavailable);

/// <summary>
/// Seeds the export screen's folder list.
/// </summary>
/// <remarks>
/// <see cref="OrganizerState.KnownFolders"/> alone is not enough. It is derived from the virtual
/// parents of scanned mods, so every folder it contains holds at least one mod and a deliberately
/// empty bucket is invisible to it -- yet an empty bucket is exactly the kind of structure an author
/// wants to share. Penumbra's own <c>organization.json</c> lists every folder it knows, empty ones
/// included, and this plugin already parses it for orphaned-folder cleanup.
/// <para>
/// Degrades rather than fails. A missing, malformed, or future-versioned file leaves the author with
/// the mods-derived list and a stated caveat, which is strictly better than refusing to export.
/// </para>
/// </remarks>
public static class TemplateExportFolders
{
    public static TemplateExportFolderSeed Seed(IReadOnlyList<string> knownFolders, string? organizationJson)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);

        foreach (var folder in knownFolders)
        {
            var normalized = OrganizationCleanupPlanner.NormalizeFolderPath(folder);
            if (normalized is not null)
                folders.Add(normalized);
        }

        var unavailable = true;
        if (organizationJson is not null)
        {
            var parsed = OrganizationJsonCodec.Parse(organizationJson);
            if (parsed.Status == OrganizationJsonParseStatus.Ok && parsed.Data is not null)
            {
                unavailable = false;
                foreach (var folder in parsed.Data.Folders.Keys)
                {
                    var normalized = OrganizationCleanupPlanner.NormalizeFolderPath(folder);
                    if (normalized is not null)
                        folders.Add(normalized);
                }
            }
        }

        return new TemplateExportFolderSeed(
            [.. folders.OrderBy(f => f, StringComparer.Ordinal)],
            unavailable);
    }
}
