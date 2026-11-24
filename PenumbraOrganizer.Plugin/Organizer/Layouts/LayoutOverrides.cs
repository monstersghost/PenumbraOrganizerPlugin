using System.Runtime.CompilerServices;
using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Layouts;

/// <summary>Converts between the saved <see cref="CategoryLayoutEntry"/> list and the model.</summary>
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

    /// <summary>
    /// One entry per category that differs from Recommended, in category order, splits in folder-depth order.
    /// Anything <see cref="Read"/> ignored (unknown or repeated categories, illegal splits) is not written back.
    /// </summary>
    public static List<CategoryLayoutEntry> Write(LayoutProfile profile) =>
        profile.Overrides()
            .OrderBy(pair => pair.Key)
            .Select(pair => new CategoryLayoutEntry
            {
                Category = pair.Key.ToString(),
                Folder = pair.Value.Folder,
                Creator = pair.Value.Creator?.ToString(),
                Splits = LayoutCatalog.SplitsFor(pair.Key)
                    .Where(info => pair.Value.Has(info.Split))
                    .Select(info => info.Split.ToString())
                    .ToList(),
            })
            .ToList();
}

/// <summary>
/// One resolved <see cref="LayoutProfile"/> per configuration, reused until the saved list is replaced or its
/// version is bumped, so nothing parses the config per frame. Keyed per config object (xUnit runs test classes in
/// parallel, and the plugin has one config). A config the layout migration has not run on yet is read from the
/// legacy fields every call: they are plain properties a caller may change at any time, and this path never runs
/// per frame in the plugin, which migrates at startup.
/// </summary>
/// <remarks>
/// Thread safety: each config publishes ONE immutable snapshot (source list, version and profile together)
/// through a single volatile reference, so a reader can never pair one version with another version's profile.
/// A rebuild copies the saved list under the per-config lock before enumerating it, so the resolve never walks a
/// list that is being replaced.
/// </remarks>
public static class LayoutProfileCache
{
    // Source is compared by reference only.
    private sealed record Snapshot(object Source, int Version, LayoutProfile Profile);

    private sealed class Slot
    {
        public readonly object Gate = new();
        public volatile Snapshot? Current;
    }

    private static readonly ConditionalWeakTable<Configuration, Slot> Slots = new();

    public static LayoutProfile For(Configuration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var saved = config.CategoryLayouts;
        if (saved is null)
            return LayoutProfile.FromLegacy(LegacyLayout.ReadFields(config));

        var slot = Slots.GetValue(config, _ => new Slot());
        var version = config.CategoryLayoutsVersion;
        var snapshot = slot.Current;
        if (snapshot is not null && ReferenceEquals(snapshot.Source, saved) && snapshot.Version == version)
            return snapshot.Profile;

        lock (slot.Gate)
        {
            snapshot = slot.Current;
            if (snapshot is not null && ReferenceEquals(snapshot.Source, saved) && snapshot.Version == version)
                return snapshot.Profile;

            // Copied before enumerating: the resolve below never walks the live list. Writers replace the list
            // (SaveLayoutProfile assigns a new one) rather than editing it in place.
            CategoryLayoutEntry?[] copy = [.. saved];
            var fresh = new Snapshot(saved, version, LayoutProfile.Resolve(LayoutOverrides.Read(copy)));
            slot.Current = fresh;
            return fresh.Profile;
        }
    }
}
