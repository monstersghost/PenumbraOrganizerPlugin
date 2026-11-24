using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Layouts;

namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

public sealed record BuiltInCategory(int Code, string Name, string Description, string Folder);

/// <summary>
/// The version 3 built-in category table. Each folder is where the Sort tab files that category, so a sheet
/// that mixes codes and {type} builds one tree. Version 2 files keep the older table in
/// <see cref="WorkbookCategoryCatalog"/>.
/// </summary>
public static class WorkbookCatalog
{
    public const int FormatVersion = 3;

    public static readonly IReadOnlyList<BuiltInCategory> BuiltIn =
    [
        new(1, "Gear", "Equipment and accessories: head, body, hands, legs, feet, ears, neck, wrists, and rings.", "Gear"),
        new(2, "Weapon", "Weapon mods.", "Weapon"),
        new(3, "Face", "Face mesh and texture replacements.", "Face"),
        new(4, "Hair", "Hair mesh and texture replacements.", "Hair"),
        new(5, "Body", "Body mesh replacements, plus tail and ear meshes.", "Body"),
        new(6, "Skin", "Body texture-only retextures, no mesh replacement.", "Skin"),
        new(7, "NPC", "Mods that reskin or reshape a specific NPC rather than a playable character.", "NPC"),
        new(8, "Minion", "Minion companion mods.", "Minion"),
        new(9, "Mount", "Mount mods.", "Mount"),
        new(10, "Pet", "Battle pet mods: fairies, egis, turrets, and similar.", "Pet"),
        new(11, "Ornament", "Worn fashion accessories such as wings and halos.", "Ornament"),
        new(12, "Furniture", "Housing furniture and fixtures.", "Furniture"),
        new(13, "VFX", "Visual effects.", "Animation and VFX/VFX"),
        new(14, "Sound", "Sound replacements.", "Sound"),
        new(15, "Animation", "Animation and emote replacements.", "Animation and VFX"),
        new(16, "Others", "Anything that doesn't match a specific category, or needs manual review.", "Review"),
    ];

    public static string FolderFor(int code) => BuiltIn.First(c => c.Code == code).Folder;

    /// <summary>
    /// The folder a built-in code (1 to 16, the category's enum value) files under: the category's configured
    /// folder when the user renamed it, otherwise the table's (so code 16 stays Review and code 13 stays
    /// Animation and VFX/VFX until that category itself is renamed).
    /// </summary>
    public static string FolderFor(int code, CategoryPlacement? placement) =>
        placement?.FolderFor((ModCategory)code) ?? FolderFor(code);

    public static bool TryGetByName(string name, out BuiltInCategory category)
    {
        var found = BuiltIn.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        category = found ?? BuiltIn[^1];
        return found is not null;
    }
}
