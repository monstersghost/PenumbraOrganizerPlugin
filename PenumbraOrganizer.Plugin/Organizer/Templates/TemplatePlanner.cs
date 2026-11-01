using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Row counts and entry counts are separate fields because they are separate numbers: 214 matched
/// rows can come from 190 matched entries.
/// </summary>
public sealed record TemplateApplyReport(
    int ConsideredRows,
    int ProtectedRows,
    int RowsMatchedByEntry,
    int RowsPlacedByFallback,
    int TemplateEntriesMatched,
    int TemplateEntriesUnmatched,
    int AmbiguousLocalMatchGroups,
    int InvalidEntriesSkipped);

public sealed record TemplateApplicationPlan(
    IReadOnlyDictionary<string, string> DestinationFolders,
    IReadOnlyDictionary<string, int> FolderCounts,
    TemplateApplyReport Report,
    IReadOnlyList<TemplateWarning> Warnings);

/// <summary>
/// Pure, non-mutating. Produces everything the preview shows AND everything the apply writes, so
/// the two cannot be different computations of the same answer — an approximate preview is
/// structurally impossible.
/// </summary>
public static class TemplatePlanner
{
    /// <summary>
    /// Plans directly from a decode result, so the decode warnings cannot be dropped. Plan's own
    /// decodeWarnings parameter is optional, and a caller that omits it silently loses every
    /// warning and reports InvalidEntriesSkipped as 0 -- a plausible-looking but incomplete plan
    /// with no signal that anything is missing. UI callers use this entry point.
    /// </summary>
    public static TemplateApplicationPlan PlanFromDecoded(
        TemplateDecodeResult decoded,
        IReadOnlyCollection<OrganizerModRow> rows,
        Func<string, string> canonicalizeCreator)
    {
        if (!decoded.Succeeded)
        {
            throw new ArgumentException(
                "Cannot plan from a template that failed to decode; surface the error instead.",
                nameof(decoded));
        }

        return Plan(decoded.Template!, rows, canonicalizeCreator, decoded.Warnings);
    }

    public static TemplateApplicationPlan Plan(
        ValidatedOrganizationTemplate template,
        IReadOnlyCollection<OrganizerModRow> rows,
        Func<string, string> canonicalizeCreator,
        IReadOnlyList<TemplateWarning>? decodeWarnings = null)
    {
        var renameFolder = TemplateFolderLabels.Create(template.FolderLabels);
        var warnings = new List<TemplateWarning>(decodeWarnings ?? []);

        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        var matchedEntryKeys = new HashSet<string>(StringComparer.Ordinal);
        var rowsPerNormalizedName = new Dictionary<string, int>(StringComparer.Ordinal);

        var protectedRows = 0;
        var matchedRows = 0;
        var fallbackRows = 0;

        foreach (var row in rows)
        {
            // Protected rows are excluded here for reporting, and again by OrganizerState.Sort's
            // own !m.Protected filter when the plan is applied.
            if (row.Protected)
            {
                protectedRows++;
                continue;
            }

            var key = ModNameNormalizer.Normalize(row.Name);
            if (key.Length > 0)
                rowsPerNormalizedName[key] = rowsPerNormalizedName.GetValueOrDefault(key) + 1;

            if (key.Length > 0 && template.EntriesByNormalizedName.TryGetValue(key, out var folder))
            {
                destinations[row.Identifier] = folder;
                matchedEntryKeys.Add(key);
                matchedRows++;
                continue;
            }

            var fallback = template.Fallback;
            var (primary, secondary) = SortFolderSelectors.Select(
                fallback.Strategy, fallback.SplitGear, fallback.SplitNpc, row, canonicalizeCreator, renameFolder);
            destinations[row.Identifier] = SortFolderSelectors.FlattenToFolder(primary, secondary);
            fallbackRows++;
        }

        // An entry matching several local rows is the most likely source of a surprising result,
        // so it is surfaced rather than silently multiplied out.
        var ambiguousGroups = 0;
        foreach (var key in matchedEntryKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (rowsPerNormalizedName.GetValueOrDefault(key) > 1)
            {
                ambiguousGroups++;
                warnings.Add(new TemplateWarning(TemplateWarningCode.AmbiguousLocalMatch, key));
            }
        }

        var unmatchedEntries = 0;
        foreach (var key in template.EntriesByNormalizedName.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (matchedEntryKeys.Contains(key))
                continue;

            unmatchedEntries++;
            warnings.Add(new TemplateWarning(TemplateWarningCode.UnmatchedTemplateEntry, key));
        }

        var folderCounts = destinations.Values
            .GroupBy(folder => folder, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var report = new TemplateApplyReport(
            ConsideredRows: rows.Count,
            ProtectedRows: protectedRows,
            RowsMatchedByEntry: matchedRows,
            RowsPlacedByFallback: fallbackRows,
            TemplateEntriesMatched: matchedEntryKeys.Count,
            TemplateEntriesUnmatched: unmatchedEntries,
            AmbiguousLocalMatchGroups: ambiguousGroups,
            InvalidEntriesSkipped: decodeWarnings?.Count(w => w.Code == TemplateWarningCode.InvalidEntryPath) ?? 0);

        return new TemplateApplicationPlan(destinations, folderCounts, report, warnings);
    }
}
