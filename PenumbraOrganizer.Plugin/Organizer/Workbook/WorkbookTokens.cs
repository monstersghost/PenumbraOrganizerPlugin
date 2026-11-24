using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Layouts;

namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

/// <summary>Builds the values of {creator}, {type} and {race} for one mod from the plugin's own state.</summary>
public static class WorkbookTokens
{
    /// <param name="placement">The renamed category folders; null means none, and {type} is today's name.</param>
    public static DestinationTokens For(
        OrganizerModRow row, Func<string, string> canonicalizeCreator, RaceSplitOptions race,
        CategoryPlacement? placement = null)
    {
        var creator = SortFolderSelectors.KnownSegment(canonicalizeCreator(row.Author)) ?? string.Empty;
        var raceFolder = RaceSplitRouting.ResolveForToken(row, race)?.Folder ?? string.Empty;
        return new DestinationTokens(creator, TypeFolderFor(row.Category, placement), raceFolder);
    }

    /// <summary>
    /// The category's folder as the Sort tab names it, and only the category (never a sub-folder or a
    /// numbered tail), taken from the version 3 catalog so there is one table. Animation and VFX share one
    /// parent; an unclassified mod and Others go to Review. A category the user renamed gives its whole
    /// configured folder (which may be nested, such as Equipment/Gear); an unclassified mod follows Others.
    /// The rules' <c>type</c> field calls this WITHOUT a placement: it stays the built-in name whatever the rename.
    /// </summary>
    public static string TypeFolderFor(ModCategory? category, CategoryPlacement? placement = null)
    {
        if (placement?.FolderFor(category ?? ModCategory.Others) is { } configured)
            return configured;
        return category is null
            ? WorkbookCatalog.FolderFor((int)ModCategory.Others)
            : WorkbookCatalog.FolderFor((int)category.Value).Split('/')[0];
    }
}
