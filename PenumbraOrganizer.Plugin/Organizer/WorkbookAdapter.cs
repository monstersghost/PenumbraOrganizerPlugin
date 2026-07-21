namespace PenumbraOrganizer.Plugin.Organizer;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;

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

    public static IReadOnlyList<OrganizerModProposal> ToProposals(OrganizerState state)
        => state.Mods.Select(row =>
        {
            var (folder, _) = SplitPath(row.CurrentPath);
            return new OrganizerModProposal
            {
                StableScanId = row.Identifier,
                Name = row.Name,
                CurrentVirtualFolder = folder,
                ProposedVirtualFolder = folder,
                OriginalCreator = row.Author,
                Protected = row.Protected,
                OriginalProtected = row.Protected,
            };
        }).ToList();

    public static OrganizationPreferences ToOrganizationPreferences(OrganizationStrategy strategy)
        => OrganizationPreferences.DefaultManual with { Strategy = strategy };

    public static void ApplyImportResult(OrganizerState state, WorkbookImportResult result)
    {
        var rowsById = state.Mods.ToDictionary(row => row.Identifier, StringComparer.Ordinal);
        foreach (var importedRow in result.Rows)
        {
            if (!rowsById.TryGetValue(importedRow.StableScanId, out var row))
                continue;

            state.SetProtected(row.Identifier, importedRow.Protected);

            if (importedRow.ResolvedDestination is not null)
                state.AssignManual(row.Identifier, JoinPath(importedRow.ResolvedDestination, row.Name));
        }
    }
}
