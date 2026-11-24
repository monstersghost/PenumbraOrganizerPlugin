using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Organizer.Layouts;

/// <summary>One split a category may use. <paramref name="IsFolder"/> is false for a switch that adds no folder level.</summary>
public sealed record SplitInfo(LayoutSplits Split, string Name, bool IsFolder, bool NeedsScanEvidence);

/// <summary>
/// The fixed table of what each category may split by, in folder-depth order (the order the engine builds the
/// path in today), plus each category's default folder and its Recommended layout. Spec 3 adds rows here.
/// </summary>
public static class LayoutCatalog
{
    /// <summary>The sixteen categories in enum order (Gear = 1 ... Others = 16), which is also workbook code order.</summary>
    public static IReadOnlyList<ModCategory> Categories { get; } = Enum.GetValues<ModCategory>();

    private static readonly SplitInfo Race = new(LayoutSplits.Race, "Race", true, true);

    private static readonly Dictionary<ModCategory, SplitInfo[]> Table = new()
    {
        [ModCategory.Gear] =
        [
            new(LayoutSplits.BodyType, "Body type", true, false),
            new(LayoutSplits.Slot, "Equipment slot", true, false),
            new(LayoutSplits.SharedHandsFeet, "Hands and feet in one shared folder", false, false),
        ],
        [ModCategory.NPC] = [new(LayoutSplits.NpcKind, "Kind", true, false)],
        [ModCategory.Hair] = [Race, new(LayoutSplits.HairNumber, "Hair number", true, false)],
        [ModCategory.Body] =
        [
            new(LayoutSplits.PartAndBodyType, "Part and body type", true, false),
            Race,
            new(LayoutSplits.RacialScalingRace, "Racial scaling by race", true, true),
        ],
        [ModCategory.Animation] = [new(LayoutSplits.Emote, "Emote", true, false)],
        [ModCategory.Face] = [Race, new(LayoutSplits.EyesRace, "Eyes by race", true, true)],
        [ModCategory.Skin] = [Race],
        // Needs scan evidence twice over: the Weapon category itself and the job both come from a scan made with it on.
        [ModCategory.Weapon] = [new(LayoutSplits.Job, "Job", true, true)],
    };

    /// <summary>True for the sixteen categories (1 to 16); false for any other value cast to the enum.</summary>
    public static bool IsKnown(ModCategory category) => category is >= ModCategory.Gear and <= ModCategory.Others;

    /// <summary>The splits this category may use, in folder-depth order. Empty for a category with none.</summary>
    public static IReadOnlyList<SplitInfo> SplitsFor(ModCategory category) =>
        Table.TryGetValue(category, out var splits) ? splits : [];

    public static LayoutSplits LegalSplits(ModCategory category) =>
        SplitsFor(category).Aggregate(LayoutSplits.None, (all, info) => all | info.Split);

    /// <summary>
    /// The category's folder before any rename: where the Sort files it today. That is the workbook's built-in
    /// folder for the same code (<c>Animation and VFX/VFX</c> for VFX), except Others: the Sort has always filed
    /// it under <c>Others</c>, while workbook code 16 and <c>{type}</c> say <c>Review</c>. Code 16 keeps saying
    /// <c>Review</c> until Others is actually renamed.
    /// </summary>
    public static string DefaultFolder(ModCategory category) =>
        category == ModCategory.Others ? nameof(ModCategory.Others) : WorkbookCatalog.FolderFor((int)category);

    /// <summary>
    /// The leading folder(s) the Sort engine writes for this category today, which a rename replaces. The same as
    /// <see cref="DefaultFolder"/> for every category.
    /// </summary>
    public static string SortRoot(ModCategory category) => DefaultFolder(category);

    /// <summary>
    /// Replaces the category's <see cref="SortRoot"/> at the start of <paramref name="path"/> with
    /// <paramref name="folder"/>, on a segment boundary. A path that does not start with the root (an Animation or
    /// VFX row with no subcategory, which a scan never produces) is returned unchanged.
    /// </summary>
    public static string ReplaceRoot(string path, ModCategory category, string folder)
    {
        var root = SortRoot(category);
        if (string.Equals(path, root, StringComparison.Ordinal))
            return folder;
        return path.Length > root.Length && path[root.Length] == '/' && path.StartsWith(root, StringComparison.Ordinal)
            ? folder + path[root.Length..]
            : path;
    }

    /// <summary>
    /// The Recommended layout: default folder, creator level follows Group by, and the splits that clearly help.
    /// </summary>
    public static CategoryLayout Recommended(ModCategory category) => new(
        DefaultFolder(category),
        null,
        category switch
        {
            ModCategory.Gear => LayoutSplits.BodyType | LayoutSplits.Slot | LayoutSplits.SharedHandsFeet,
            ModCategory.NPC => LayoutSplits.NpcKind,
            // LOAD-BEARING: Recommended carries EVERY race bit (Hair Race, Face Race | EyesRace,
            // Body Race | RacialScalingRace, Skin Race), exactly the rows a fresh install has ticked today. The
            // spec's "race off" is delivered by the global race master switch (RaceSplitEnabled, off by default),
            // not by clearing these bits. Do not remove them: the legacy mirror of Recommended would then untick
            // every race row, and a user who turns the master on would get no race folders at all.
            ModCategory.Hair => LayoutSplits.Race | LayoutSplits.HairNumber,
            ModCategory.Body => LayoutSplits.PartAndBodyType | LayoutSplits.Race | LayoutSplits.RacialScalingRace,
            ModCategory.Animation => LayoutSplits.Emote,
            ModCategory.Face => LayoutSplits.Race | LayoutSplits.EyesRace,
            ModCategory.Skin => LayoutSplits.Race,
            // Opt-in: Job also turns on weapon classification at scan time, which moves weapon mods out of Gear.
            ModCategory.Weapon => LayoutSplits.None,
            _ => LayoutSplits.None,
        });

    /// <summary>The Group by a creator level stands for, for a category that overrides it.</summary>
    public static SortStrategy StrategyFor(CreatorLevel level) => level switch
    {
        CreatorLevel.None => SortStrategy.TypeOnly,
        CreatorLevel.BeforeType => SortStrategy.CreatorThenType,
        CreatorLevel.AfterType => SortStrategy.TypeThenCreator,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown creator level."),
    };
}
