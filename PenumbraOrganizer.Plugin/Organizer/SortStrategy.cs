namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// How a sort groups mods into folders. Deliberately NOT
/// <c>PenumbraOrganizer.Core.Models.OrganizationStrategy</c>: three of that type's members are
/// meaningless here but would be representable, and it is the workbook export dropdown's type.
/// </summary>
/// <remarks>
/// Whether gear and NPC mods are further split into subfolders is orthogonal to this and travels
/// as separate flags, because every combination of the two is valid for the three strategies that
/// consult the category at all.
/// </remarks>
public enum SortStrategy
{
    /// <summary>Creator only. Never consults the category, so both splits are inert.</summary>
    CreatorOnly,

    /// <summary>Mod type only.</summary>
    TypeOnly,

    /// <summary>Mod type, then creator within it.</summary>
    TypeThenCreator,

    /// <summary>Creator, then mod type within it.</summary>
    CreatorThenType,
}
