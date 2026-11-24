using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Layouts;

/// <summary>Converts the saved <see cref="CategoryLayoutEntry"/> list into the model.</summary>
public static class LayoutOverrides
{
    /// <summary>
    /// The saved overrides, forgiving of a hand-edited file:
    /// <list type="bullet">
    /// <item>a null entry is skipped, an unknown category name (matched by exact member name only, so a typo, a
    /// number or a different case is unknown) is ignored, and a repeated category keeps its first entry;</item>
    /// <item>an invalid folder falls back to the category's default folder, a valid one is stored normalized, and a
    /// folder that equals the default folder ignoring case reads as the default (a case-only change is not a
    /// rename);</item>
    /// <item>an unknown creator level follows Group by;</item>
    /// <item>missing or null splits take Recommended's splits for the category (an empty list is "all off"), and
    /// unknown split names or splits the category cannot use are dropped.</item>
    /// </list>
    /// Whatever is ignored here is gone once the profile is written back.
    /// </summary>
    public static Dictionary<ModCategory, CategoryLayout> Read(IEnumerable<CategoryLayoutEntry?>? entries)
    {
        var result = new Dictionary<ModCategory, CategoryLayout>();
        foreach (var entry in entries ?? [])
        {
            if (entry is null || !TryName<ModCategory>(entry.Category, out var category) || result.ContainsKey(category))
                continue;

            result[category] = new CategoryLayout(FolderOf(entry, category), CreatorOf(entry), SplitsOf(entry, category));
        }

        return result;
    }

    private static string FolderOf(CategoryLayoutEntry entry, ModCategory category)
    {
        var fallback = LayoutCatalog.DefaultFolder(category);
        if (LayoutFolderName.Error(entry.Folder) is not null)
            return fallback;

        var folder = LayoutFolderName.Normalize(entry.Folder);
        return string.Equals(folder, fallback, StringComparison.OrdinalIgnoreCase) ? fallback : folder;
    }

    private static CreatorLevel? CreatorOf(CategoryLayoutEntry entry) =>
        TryName<CreatorLevel>(entry.Creator, out var level) ? level : null;

    private static LayoutSplits SplitsOf(CategoryLayoutEntry entry, ModCategory category)
    {
        if (entry.Splits is null)
            return LayoutCatalog.Recommended(category).Splits;

        var splits = LayoutSplits.None;
        foreach (var info in LayoutCatalog.SplitsFor(category))
        {
            if (entry.Splits.Contains(info.Split.ToString(), StringComparer.Ordinal))
                splits |= info.Split;
        }

        return splits;
    }

    // By member name only: Enum.TryParse would also accept "5" or "Gear, Hair".
    private static bool TryName<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        return text is not null && Enum.GetNames<T>().Contains(text, StringComparer.Ordinal)
               && Enum.TryParse(text, ignoreCase: false, out value);
    }
}
