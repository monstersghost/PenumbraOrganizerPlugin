using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;

namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

public sealed record WorkbookCategoryDefinition(
    int Code,
    string Name,
    string Description,
    string ExampleDestination);

public sealed record WorkbookExportResult(
    string WorkbookPath,
    string SourceExportId,
    DateTimeOffset GeneratedAtUtc,
    string ScanIdentity,
    string InstallationIdentity,
    string StrategyLabel,
    int RowCount,
    string Summary);

public sealed record WorkbookImportRow(
    int RowNumber,
    string StableScanId,
    string ModName,
    string Author,
    string CurrentVirtualFolder,
    string ModType,
    bool Protected,
    string Destination,
    string ResolvedModType,
    string? ResolvedDestination);

public sealed record WorkbookImportResult(
    string WorkbookPath,
    string SourceExportId,
    DateTimeOffset GeneratedAtUtc,
    string ScanIdentity,
    string InstallationIdentity,
    IReadOnlyList<WorkbookImportRow> Rows,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string Summary);

public static class WorkbookCategoryCatalog
{
    public const int CurrentFormatVersion = 2;
    public const string BlankDestinationRule = "Blank destination leaves the mod in its current folder.";
    public const string ReviewRule = "Use mod type Others or destination 16/Others when a row still needs manual attention.";

    public static IReadOnlyList<WorkbookCategoryDefinition> Definitions { get; } =
    [
        new(1, "Gear", "Equipment and accessories: head, body, hands, legs, feet, ears, neck, wrists, and rings.", "1/Creator"),
        new(2, "Weapon", "Weapon mods.", "2/Creator"),
        new(3, "Face", "Face mesh and texture replacements.", "3/Creator"),
        new(4, "Hair", "Hair mesh and texture replacements.", "4/Creator"),
        new(5, "Body", "Body mesh replacements, plus tail and ear meshes.", "5/Creator"),
        new(6, "Skin", "Body texture-only retextures, no mesh replacement.", "6/Creator"),
        new(7, "NPC", "Mods that reskin or reshape a specific NPC rather than a playable character.", "7/Creator"),
        new(8, "Minion", "Minion companion mods.", "8/Creator"),
        new(9, "Mount", "Mount mods.", "9/Creator"),
        new(10, "Pet", "Battle pet mods: fairies, egis, turrets, and similar.", "10/Creator"),
        new(11, "Ornament", "Worn fashion accessories such as wings and halos.", "11/Creator"),
        new(12, "Furniture", "Housing furniture and fixtures.", "12/Creator"),
        new(13, "VFX", "Visual effects.", "13/Creator"),
        new(14, "Sound", "Sound replacements.", "14/Creator"),
        new(15, "Animation", "Animation and emote replacements.", "15/Creator"),
        new(16, "Others", "Anything that doesn't match a specific category, or needs manual review.", "16/Review"),
    ];

    /// <summary>
    /// Classification is computed structurally at scan time by <c>ModPathClassifier</c> from the
    /// mod's own game paths and manipulation slots (see <c>ModScanResult.DetectedCategory</c>).
    /// This is a thin lookup from that already-computed category to its workbook definition.
    /// </summary>
    public static WorkbookCategoryDefinition Detect(ModScanResult mod)
        => GetRequiredByCode((int)mod.DetectedCategory);

    public static bool TryGetByCode(int code, out WorkbookCategoryDefinition category)
    {
        category = Definitions.FirstOrDefault(item => item.Code == code) ?? GetRequiredByCode((int)ModCategory.Others);
        return Definitions.Any(item => item.Code == code);
    }

    public static bool TryGetByName(string name, out WorkbookCategoryDefinition category)
    {
        category = Definitions.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? GetRequiredByCode((int)ModCategory.Others);
        return Definitions.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static WorkbookCategoryDefinition GetRequiredByCode(int code)
        => Definitions.First(item => item.Code == code);
}
