using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>Why the workbook's "As suggested by Sort" export is unavailable right now, or null when it is.</summary>
public static class WorkbookExportGate
{
    public static string? SuggestedUnavailableReason(OrganizerState state, SortSelection current)
    {
        if (!state.HasScanned)
            return "Scan first.";

        if (state.SortFingerprint is null)
            return state.LastSortSummary is null
                ? "Press Sort first."
                : "Staged paths changed since the last whole-library sort, press Sort again.";

        return string.Equals(state.SortFingerprint, current.Fingerprint(), StringComparison.Ordinal)
            ? null
            : "Sort choices changed since the last sort, press Sort again.";
    }
}
