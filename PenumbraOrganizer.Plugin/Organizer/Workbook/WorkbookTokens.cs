using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

/// <summary>Builds the values of {creator}, {type} and {race} for one mod from the plugin's own state.</summary>
public static class WorkbookTokens
{
    public static DestinationTokens For(
        OrganizerModRow row, Func<string, string> canonicalizeCreator, RaceSplitOptions race)
    {
        var creator = SortFolderSelectors.KnownSegment(canonicalizeCreator(row.Author)) ?? string.Empty;
        var raceFolder = RaceSplitRouting.ResolveForToken(row, race)?.Folder ?? string.Empty;
        return new DestinationTokens(creator, TypeFolderFor(row.Category), raceFolder);
    }

    /// <summary>
    /// The category's folder as the Sort tab names it, and only the category (never a sub-folder or a
    /// numbered tail), taken from the version 3 catalog so there is one table. Animation and VFX share one
    /// parent; an unclassified mod and Others go to Review.
    /// </summary>
    public static string TypeFolderFor(ModCategory? category) =>
        category is null
            ? WorkbookCatalog.FolderFor((int)ModCategory.Others)
            : WorkbookCatalog.FolderFor((int)category.Value).Split('/')[0];
}
