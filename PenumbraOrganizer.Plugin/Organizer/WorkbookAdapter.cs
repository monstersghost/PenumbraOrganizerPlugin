namespace PenumbraOrganizer.Plugin.Organizer;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

public static class WorkbookAdapter
{
    public static (string Folder, string Leaf) SplitPath(string fullPath)
    {
        var lastSeparator = fullPath.LastIndexOf('/');
        return lastSeparator < 0
            ? (string.Empty, fullPath)
            : (fullPath[..lastSeparator], fullPath[(lastSeparator + 1)..]);
    }

    public static string JoinPath(string folder, string leaf)
        => folder.Length == 0 ? leaf : $"{folder}/{leaf}";

    public static ScanInventory ToScanInventory(OrganizerState state, PenumbraInstallation installation)
        => new()
        {
            Installation = installation,
            ScannedAtUtc = DateTimeOffset.UtcNow,
            Mods = state.Mods.Select(ToModScanResult).ToList(),
            CurrentFolderTree = [],
            Collections = [],
            Warnings = [],
        };

    private static ModScanResult ToModScanResult(OrganizerModRow row)
    {
        var (folder, _) = SplitPath(row.CurrentPath);
        return new ModScanResult
        {
            StableScanId = row.Identifier,
            PhysicalDirectory = string.Empty,
            PhysicalDirectoryName = row.Identifier,
            CurrentVirtualFolder = folder,
            Name = row.Name,
            Author = row.Author,
            Protected = row.Protected,
            DetectedCategory = row.Category ?? ModCategory.Others,
        };
    }

    /// <summary>
    /// The proposals the export reads. With <paramref name="staged"/> each proposal's folder is the folder part
    /// of the row's staged path (what the last Sort proposed); otherwise it is the current folder.
    /// </summary>
    public static IReadOnlyList<OrganizerModProposal> ToProposals(OrganizerState state, bool staged = false)
        => state.Mods.Select(row =>
        {
            var (folder, _) = SplitPath(row.CurrentPath);
            var proposed = staged ? SplitPath(row.ProposedPath).Folder : folder;
            return new OrganizerModProposal
            {
                StableScanId = row.Identifier,
                Name = row.Name,
                CurrentVirtualFolder = folder,
                ProposedVirtualFolder = proposed,
                OriginalCreator = row.Author,
                Protected = row.Protected,
                OriginalProtected = row.Protected,
            };
        }).ToList();

    /// <summary>
    /// Each row's real last path segment: the staged one for the staged Sort, the current one otherwise. It can
    /// differ from the name (a duplicate install is "Name (2)").
    /// </summary>
    public static IReadOnlyDictionary<string, string> Leaves(OrganizerState state, bool staged)
        => state.Mods.ToDictionary(
            row => row.Identifier,
            row => SplitPath(staged ? row.ProposedPath : row.CurrentPath).Leaf,
            StringComparer.Ordinal);

    /// <summary>What applying an import did. <see cref="NotStaged"/> counts moves refused because the mod is protected.</summary>
    public sealed record ImportApplyReport(int Rows, int Staged, int NotStaged);

    public static ImportApplyReport ApplyImportResult(OrganizerState state, WorkbookImportResult result)
    {
        var rowsById = state.Mods.ToDictionary(row => row.Identifier, StringComparer.Ordinal);
        var rows = 0;
        var staged = 0;
        var notStaged = 0;
        foreach (var importedRow in result.Rows)
        {
            if (!rowsById.TryGetValue(importedRow.StableScanId, out var row))
                continue;

            rows++;

            // Only write protection that changed: an unchanged TRUE must not turn inherited protection into an
            // explicit per-mod rule, and an unchanged FALSE has nothing to do.
            if (importedRow.Protected != row.Protected)
                state.SetProtected(row.Identifier, importedRow.Protected);

            if (importedRow.ResolvedDestination is null)
                continue;

            var leaf = importedRow.Leaf is { Length: > 0 } importedLeaf ? importedLeaf : PenumbraPathSemantics.FixName(row.Name);
            var path = JoinPath(importedRow.ResolvedDestination, leaf);
            if (string.Equals(path, row.ProposedPath, StringComparison.Ordinal))
                continue;

            if (state.AssignManual(row.Identifier, path))
                staged++;
            else
                notStaged++;
        }

        return new ImportApplyReport(rows, staged, notStaged);
    }
}
